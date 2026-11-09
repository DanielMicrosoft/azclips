#requires -Version 7
[CmdletBinding()]
param(
    [string] $AzclipsExe = $env:AZCLIPS_EXE,
    [string] $OutputPath,
    [string] $TracePath,
    [ValidateSet('automatic', 'manual')]
    [string] $RecoveryMode = 'automatic',
    [ValidateRange(1, 2147483647)]
    [int] $Repeat = 1,
    [ValidateSet('refresh', 'existing')]
    [string] $ResourceCache = 'refresh',
    [ValidateRange(0, 10)]
    [int] $WarmupObservations = 1,
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ExpectedPackageSha256,
    [switch] $Upload,
    [string] $ServiceUrl = 'https://azclis-evaluation-web-6b085460.azurewebsites.net'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if ([string]::IsNullOrWhiteSpace($AzclipsExe) -or -not (Test-Path $AzclipsExe -PathType Leaf)) {
    throw 'Pass -AzclipsExe or set AZCLIPS_EXE to an installed azclips executable.'
}
# Setup commands run from the caller's directory and measured cases from this script's directory,
# so a relative home could select two different configurations.
if ($env:AZCLIPS_HOME -and -not [IO.Path]::IsPathFullyQualified($env:AZCLIPS_HOME)) {
    throw "AZCLIPS_HOME must be an absolute path, not '$env:AZCLIPS_HOME'; no Agent evaluation was started."
}

$AzclipsExe = (Resolve-Path $AzclipsExe).Path
# Default artifacts go to agent/evaluation/<run-id> under the home root (HomePaths.GetDefaultAgentEvaluationDirectory).
$homeRoot = if ($env:AZCLIPS_HOME) {
    $env:AZCLIPS_HOME
}
else {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.azclips'
}
$runDirectory = Join-Path $homeRoot 'agent' 'evaluation' ([guid]::NewGuid().ToString('N'))
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $runDirectory 'result.json' }
if ([string]::IsNullOrWhiteSpace($TracePath)) { $TracePath = Join-Path $runDirectory 'traces' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$TracePath = [IO.Path]::GetFullPath($TracePath)
if (Test-Path -LiteralPath $OutputPath) {
    throw "Output already exists: $OutputPath. Choose a new path so a previous artifact cannot be uploaded as this run."
}
$warmupOutputPath = [IO.Path]::ChangeExtension($OutputPath, '.warmup.json')
if ($WarmupObservations -gt 0 -and (Test-Path -LiteralPath $warmupOutputPath)) {
    throw "Warm-up output already exists: $warmupOutputPath. Choose a new output path."
}
& node (Join-Path $PSScriptRoot 'judge\judge-azure.js') --check-config
if ($LASTEXITCODE -ne 0) {
    throw 'Semantic judge configuration is incomplete; no Agent evaluation was started. Set endpoint/deployment in Agent/eval/judge.config.local.json or configure AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_DEPLOYMENT.'
}
$configuration = if ($RecoveryMode -eq 'manual') {
    [ordered]@{ 'agent.recovery.trigger' = 'manual' }
}
else {
    [ordered]@{
        'agent.recovery.trigger' = 'auto'
        'agent.recovery.style' = 'oneshot'
    }
}
$configuration['agent.trace.enabled'] = 'true'
$configuration['agent.trace.path'] = $TracePath

function Invoke-WithFileRetry {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,

        [int]$MaxAttempts = 10,

        [int]$DelayMilliseconds = 200
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            & $Action
            return
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                throw
            }
            Write-Verbose "File operation attempt $attempt failed: $($_.Exception.Message)"
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }
}

function Invoke-AzclipsCommand {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)

    # A failed command starts AI recovery when the configuration enables it. The product's recursion
    # guard keeps the wrapper's own setup commands from starting a model session; measured cases run
    # through the provider, which removes the guard.
    $previousAgentMode = $env:AZCLIPS_IN_AGENT_MODE
    $env:AZCLIPS_IN_AGENT_MODE = '1'
    try {
        & $AzclipsExe @Arguments
    }
    finally {
        $env:AZCLIPS_IN_AGENT_MODE = $previousAgentMode
    }
}

function Invoke-Azclips {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)

    # Keep stderr diagnostics out of structured stdout such as agent tool env show.
    $output = Invoke-AzclipsCommand @Arguments | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "azclips exited with $LASTEXITCODE while running: $($Arguments -join ' ')`n$output"
    }
    return $output
}

function ConvertTo-RecordedSetting {
    param([string] $Value, $Source)

    # Results are uploaded, and a configured URL may carry credentials in its user info, query or fragment.
    $setting = [ordered]@{ value = $Value; source = $Source }
    $uri = $null
    if ([Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -and -not $uri.IsFile -and
        ($uri.UserInfo -or $uri.Query -or $uri.Fragment)) {
        $builder = [UriBuilder]::new($uri)
        $builder.UserName = ''
        $builder.Password = ''
        $builder.Query = ''
        $builder.Fragment = ''
        $setting.value = $builder.Uri.AbsoluteUri
        $setting.redacted = $true
    }
    return $setting
}

function Get-JsonObjectText {
    param([string] $Text)

    # Yields each top-level JSON object in the text. Braces inside JSON strings do not count.
    $depth = 0
    $start = -1
    $inString = $false
    $escaped = $false
    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        if ($inString) {
            if ($escaped) { $escaped = $false }
            elseif ($character -eq [char]'\') { $escaped = $true }
            elseif ($character -eq [char]'"') { $inString = $false }
        }
        elseif ($character -eq [char]'"') { $inString = $depth -gt 0 }
        elseif ($character -eq [char]'{') {
            if ($depth -eq 0) { $start = $index }
            $depth++
        }
        elseif ($character -eq [char]'}' -and $depth -gt 0) {
            $depth--
            if ($depth -eq 0) { $Text.Substring($start, $index - $start + 1) }
        }
    }
}

function ConvertFrom-ConfigGetOutput {
    param([string] $Text, [string] $Key)

    # Older targets print `key = value [source]`. Newer targets print a JSON object with key, value and source,
    # which may be followed by other JSON objects such as warnings. Both forms record the same value text,
    # following the older formatter: null as `null`, booleans in lowercase and strings unchanged.
    foreach ($objectText in @(Get-JsonObjectText $Text)) {
        try { $entry = $objectText | ConvertFrom-Json -AsHashtable -ErrorAction Stop } catch { continue }
        if ($entry -isnot [System.Collections.IDictionary] -or $entry['key'] -ne $Key -or -not $entry.Contains('value')) { continue }
        $value = $entry['value']
        $valueText = if ($null -eq $value) { 'null' }
        elseif ($value -is [bool]) { $value.ToString().ToLowerInvariant() }
        elseif ($value -is [string]) { $value }
        else { ConvertTo-Json -InputObject $value -Compress -Depth 4 }
        return @{ Value = $valueText; Source = $entry['source'] }
    }
    $settingLine = @($Text -split "\r?\n" | Where-Object { $_.StartsWith("$Key = ") }) | Select-Object -Last 1
    if ($settingLine -match '^\S+ = (?<value>.*?)(?: \[(?<source>[^\]]+)\])?\s*$') {
        return @{ Value = $Matches.value; Source = $Matches.source }
    }
    return $null
}

function Get-PackageFingerprint {
    param([string] $Root)

    # Framework-dependent packages keep product code beside a generic app host, so hash every file.
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        $relativePath = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $lines.Add("$relativePath`t$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())")
    }
    $lines.Sort([StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    return [ordered]@{
        packageSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        packageFiles = $lines.Count
    }
}

function Get-LastJsonLine {
    param([string] $Text, [string] $Property)

    $lines = @($Text -split "\r?\n" | Where-Object { $_.Trim().StartsWith('{') -and $_.Trim().EndsWith('}') })
    [array]::Reverse($lines)
    foreach ($line in $lines) {
        try {
            $value = $line | ConvertFrom-Json
        }
        catch {
            continue
        }
        if ($null -ne $value.PSObject.Properties[$Property]) { return $value }
    }
    return $null
}

function Set-ConfigurationTelemetryOff {
    param([string] $Path)

    # Same result as `config set telemetry.enabled false`: the nested key, with legacy flat spellings removed.
    # Comments and trailing commas are accepted because the product's configuration reader accepts them.
    $text = if (Test-Path -LiteralPath $Path -PathType Leaf) { [IO.File]::ReadAllText($Path) } else { '' }
    $root = [Text.Json.Nodes.JsonObject]::new()
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        $documentOptions = [Text.Json.JsonDocumentOptions]@{
            CommentHandling = [Text.Json.JsonCommentHandling]::Skip
            AllowTrailingCommas = $true
        }
        try {
            $root = [Text.Json.Nodes.JsonNode]::Parse($text, $null, $documentOptions)
        }
        catch {
            throw "Agent configuration '$Path' is not valid JSON; no Agent evaluation was started: $(($_.Exception.InnerException ?? $_.Exception).Message)"
        }
        if ($root -isnot [Text.Json.Nodes.JsonObject]) {
            throw "Agent configuration '$Path' is not a JSON object; no Agent evaluation was started."
        }
    }
    [void]$root.Remove('telemetry.enabled')
    [void]$root.Remove('telemetry:enabled')
    $telemetry = $root['telemetry']
    if ($telemetry -isnot [Text.Json.Nodes.JsonObject]) {
        $telemetry = [Text.Json.Nodes.JsonObject]::new()
        $root['telemetry'] = $telemetry
    }
    $telemetry['enabled'] = [Text.Json.Nodes.JsonValue]::Create($false)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $root.ToJsonString([Text.Json.JsonSerializerOptions]@{ WriteIndented = $true }))
}

$configurationPath = [IO.Path]::GetFullPath((Join-Path $homeRoot 'config.json'), $PWD.ProviderPath)
$configurationExisted = Test-Path -LiteralPath $configurationPath
$configurationBackupPath = "$configurationPath.evaluation-$([guid]::NewGuid().ToString('N')).bak"
if ($configurationExisted) {
    try {
        [IO.File]::Copy($configurationPath, $configurationBackupPath)
    }
    catch {
        throw "Failed to back up Agent configuration '$configurationPath'; no temporary settings were applied: $($_.Exception.Message)"
    }
    Write-Host "Agent configuration backup: $configurationBackupPath"
}

$evaluationExitCode = 1
$previousAzclipsExe = $env:AZCLIPS_EXE
$previousPromptfooTelemetry = $env:PROMPTFOO_DISABLE_TELEMETRY
$previousPromptfooUpdate = $env:PROMPTFOO_DISABLE_UPDATE
$previousSignedIn = $env:AZCLIPS_EVALUATION_SIGNED_IN
$previousSubscriptionId = $env:AZCLIPS_EVALUATION_SUBSCRIPTION_ID
$previousRecoveryMode = $env:AZCLIPS_EVALUATION_RECOVERY_MODE
$previousTarget = $env:AZCLIPS_EVALUATION_TARGET
$previousResourceCache = $env:AZCLIPS_EVALUATION_RESOURCE_CACHE
$previousSettings = $env:AZCLIPS_EVALUATION_SETTINGS
$previousWarmupObservations = $env:AZCLIPS_EVALUATION_WARMUP_OBSERVATIONS
$runFailure = $null
$restoreFailure = $null
try {
    # Evaluation runs must not report product telemetry. Write the switch into the file before any azclips
    # process starts: `config set` would itself start with telemetry on and report that command.
    Set-ConfigurationTelemetryOff $configurationPath

    $runtimeEnvironment = Invoke-Azclips agent tool env show | ConvertFrom-Json
    $subscriptionId = [guid]::Empty
    if ($runtimeEnvironment.signedIn -ne $true -or
        -not [guid]::TryParse($runtimeEnvironment.subscriptionId, [ref]$subscriptionId) -or
        $subscriptionId -eq [guid]::Empty) {
        throw 'The reference suite requires an Azure-signed-in active profile with a subscription.'
    }

    $targetVersion = [string]$runtimeEnvironment.azclipsVersion
    if ([string]::IsNullOrWhiteSpace($targetVersion)) {
        throw 'agent tool env show did not report azclipsVersion; the evaluated target build cannot be identified.'
    }
    $target = [ordered]@{ version = $targetVersion.Trim() }
    $packageFingerprint = Get-PackageFingerprint (Split-Path -Parent $AzclipsExe)
    if ($ExpectedPackageSha256 -and $packageFingerprint.packageSha256 -ne $ExpectedPackageSha256.ToLowerInvariant()) {
        throw "Target package fingerprint $($packageFingerprint.packageSha256) does not match -ExpectedPackageSha256 $($ExpectedPackageSha256.ToLowerInvariant()); no Agent evaluation was started."
    }
    $target.packageSha256 = $packageFingerprint.packageSha256
    $target.packageFiles = $packageFingerprint.packageFiles

    # Product lookups answer from the on-disk ARM cache and refresh it in the background.
    # Refresh it synchronously first so every case sees the same near-real-time cache.
    $fixtureGroup = if ([string]::IsNullOrWhiteSpace($env:AZCLIPS_EVALUATION_RESOURCE_GROUP)) {
        'azclips-test-rg'
    }
    else {
        $env:AZCLIPS_EVALUATION_RESOURCE_GROUP.Trim()
    }
    $resourceCacheState = [ordered]@{ mode = $ResourceCache }
    if ($ResourceCache -eq 'refresh') {
        $refreshStarted = [DateTimeOffset]::UtcNow
        $refreshWatch = [Diagnostics.Stopwatch]::StartNew()
        $refreshStatus = Get-LastJsonLine (Invoke-Azclips agent tool resource refresh) 'status'
        $refreshWatch.Stop()
        if ($null -eq $refreshStatus -or $refreshStatus.status -ne 'ok') {
            throw 'The ARM resource cache refresh did not report status ok; no Agent evaluation was started.'
        }
        $lookup = Get-LastJsonLine (Invoke-Azclips agent tool resource find --name $fixtureGroup --kind resourceGroup --subscription-id $subscriptionId.ToString()) 'matchCount'
        $fixtureFound = $null -ne $lookup -and @($lookup.matches | Where-Object {
                $_.name -ieq $fixtureGroup -and $_.subscriptionId -ieq $subscriptionId.ToString()
            }).Count -gt 0
        if (-not $fixtureFound -or $lookup.cache.resources -le 0) {
            throw "The refreshed ARM cache does not contain fixture group '$fixtureGroup' in subscription $subscriptionId. Create or select the fixture group before evaluating."
        }
        $resourceCacheState.refreshStartedAtUtc = $refreshStarted.ToString('o')
        $resourceCacheState.refreshMs = $refreshWatch.ElapsedMilliseconds
        $resourceCacheState.resources = [long]$lookup.cache.resources
        $resourceCacheState.fixtureGroup = $fixtureGroup
        $resourceCacheState.fixtureGroupFound = $true
    }

    foreach ($entry in $configuration.GetEnumerator()) {
        Invoke-Azclips config set --key $entry.Key --value $entry.Value | Out-Null
    }

    # Record the HTTP-retry and telemetry settings in effect. Agent settings come from the
    # agent_settings note in each trace instead.
    $settingKeys = @()
    $keyListing = Invoke-AzclipsCommand config info --keys-only 2>$null | Out-String
    if ($LASTEXITCODE -eq 0 -and $keyListing -match '(?s)\[[^\[\]]*\]') {
        try {
            $settingKeys = @(@($Matches[0] | ConvertFrom-Json) | Where-Object {
                    $_ -is [string] -and $_ -notmatch '(?i)secret|token|password|apikey' -and (
                        $_ -like 'core.http.*' -or $_ -eq 'telemetry.enabled')
                } | Sort-Object -Unique)
        }
        catch {
            Write-Verbose 'The target did not list its configuration keys as JSON; no settings are recorded.'
        }
    }
    $settings = [ordered]@{}
    foreach ($key in $settingKeys) {
        $settingText = Invoke-AzclipsCommand config get --key $key 2>$null | Out-String
        if ($LASTEXITCODE -ne 0) { continue }
        $setting = ConvertFrom-ConfigGetOutput $settingText $key
        if ($setting) {
            $settings[$key] = ConvertTo-RecordedSetting $setting.Value $setting.Source
        }
    }

    New-Item -ItemType Directory -Path (Split-Path $OutputPath) -Force | Out-Null
    New-Item -ItemType Directory -Path $TracePath -Force | Out-Null
    $env:AZCLIPS_EXE = $AzclipsExe
    $env:PROMPTFOO_DISABLE_TELEMETRY = 'true'
    $env:PROMPTFOO_DISABLE_UPDATE = 'true'
    $env:AZCLIPS_EVALUATION_SIGNED_IN = 'true'
    $env:AZCLIPS_EVALUATION_SUBSCRIPTION_ID = $subscriptionId.ToString()
    $env:AZCLIPS_EVALUATION_RECOVERY_MODE = $RecoveryMode
    $env:AZCLIPS_EVALUATION_TARGET = $target | ConvertTo-Json -Compress
    $env:AZCLIPS_EVALUATION_RESOURCE_CACHE = $resourceCacheState | ConvertTo-Json -Compress
    $env:AZCLIPS_EVALUATION_SETTINGS = $settings | ConvertTo-Json -Compress -Depth 4
    $env:AZCLIPS_EVALUATION_WARMUP_OBSERVATIONS = [string]$WarmupObservations

    Push-Location $PSScriptRoot
    try {
        if ($WarmupObservations -gt 0) {
            # The first Agent process of a run is consistently slower; absorb it outside the measurement.
            & npx --no-install promptfoo eval `
                --config promptfooconfig.js `
                --no-cache `
                --repeat 1 `
                --filter-first-n $WarmupObservations `
                --output $warmupOutputPath
            $warmupExitCode = $LASTEXITCODE
            if (($warmupExitCode -notin @(0, 100)) -or -not (Test-Path -LiteralPath $warmupOutputPath -PathType Leaf)) {
                throw "The discarded warm-up evaluation failed with exit code $warmupExitCode; no measured evaluation was started."
            }
            $warmupTracePath = Join-Path $TracePath ('warmup-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $warmupTracePath -Force | Out-Null
            Get-ChildItem -LiteralPath $TracePath -Filter '*.jsonl' -File | Move-Item -Destination $warmupTracePath
            Write-Host "Discarded warm-up artifact: $warmupOutputPath"
        }
        & npx --no-install promptfoo eval `
            --config promptfooconfig.js `
            --no-cache `
            --repeat $Repeat `
            --output $OutputPath
        $evaluationExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
catch {
    $runFailure = $_
}
finally {
    try {
        if ($configurationExisted) {
            Invoke-WithFileRetry { [IO.File]::Move($configurationBackupPath, $configurationPath, $true) }
        }
        else {
            Invoke-WithFileRetry { [IO.File]::Delete($configurationPath) }
        }
    }
    catch {
        $restoreFailure = "Failed to restore Agent configuration '$configurationPath': $($_.Exception.Message)"
        if ($configurationExisted) {
            $restoreFailure += " Backup path: '$configurationBackupPath'. Restore it before running evaluation again."
        }
        else {
            $restoreFailure += " The file did not exist before this run; remove '$configurationPath' to restore the original state."
        }
    }
    $env:AZCLIPS_EXE = $previousAzclipsExe
    $env:PROMPTFOO_DISABLE_TELEMETRY = $previousPromptfooTelemetry
    $env:PROMPTFOO_DISABLE_UPDATE = $previousPromptfooUpdate
    $env:AZCLIPS_EVALUATION_SIGNED_IN = $previousSignedIn
    $env:AZCLIPS_EVALUATION_SUBSCRIPTION_ID = $previousSubscriptionId
    $env:AZCLIPS_EVALUATION_RECOVERY_MODE = $previousRecoveryMode
    $env:AZCLIPS_EVALUATION_TARGET = $previousTarget
    $env:AZCLIPS_EVALUATION_RESOURCE_CACHE = $previousResourceCache
    $env:AZCLIPS_EVALUATION_SETTINGS = $previousSettings
    $env:AZCLIPS_EVALUATION_WARMUP_OBSERVATIONS = $previousWarmupObservations
}

if ($runFailure -or $restoreFailure) {
    $messages = @()
    if ($runFailure) { $messages += $runFailure.Exception.Message }
    if ($restoreFailure) { $messages += $restoreFailure }
    throw ($messages -join "`n")
}

if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
    throw "Promptfoo did not produce an evaluation artifact (exit code $evaluationExitCode): $OutputPath"
}
try {
    $artifact = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json -Depth 100
}
catch {
    throw "Promptfoo produced invalid JSON: $($_.Exception.Message)"
}
if ($null -eq $artifact.results -or $artifact.results.results -isnot [array] -or $artifact.results.results.Count -eq 0) {
    throw 'Promptfoo artifact contains no evaluation results.'
}

function Test-GraderError {
    param($Result)

    if ($null -eq $Result) { return $false }
    if ($null -ne $Result.metadata -and $Result.metadata.graderError -eq $true) { return $true }
    foreach ($child in @($Result.componentResults)) {
        if (Test-GraderError $child) { return $true }
    }
    return $false
}

# Promptfoo scores a failed Judge call as an ordinary failed check, which would read as a quality failure.
$graderErrors = @($artifact.results.results | Where-Object { Test-GraderError $_.gradingResult })
if ($graderErrors.Count -gt 0) {
    $graderErrorIds = ($graderErrors | ForEach-Object {
            $caseId = $_.testCase.vars.case_id
            if ($caseId) { "$caseId#$($_.testIdx)" } else { "#$($_.testIdx)" }
        }) -join ', '
    throw "Judge infrastructure errors in $($graderErrors.Count) result(s): $graderErrorIds. They would appear as quality failures, so the artifact is kept at $OutputPath but not uploaded. Rerun the evaluation."
}

Write-Host "Evaluation artifact: $OutputPath"
if ($Upload) {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Push-Location $repositoryRoot
    try {
        $uploadOutput = & node (Join-Path $PSScriptRoot 'upload-result.js') `
            --artifact $OutputPath `
            --service-url $ServiceUrl
        if ($LASTEXITCODE -ne 0) {
            throw "Uploading the evaluation artifact failed with exit code $LASTEXITCODE."
        }
        $uploadResult = $uploadOutput | ConvertFrom-Json
        Write-Host "Evaluation result: $($uploadResult.dashboardUrl)"
    }
    finally {
        Pop-Location
    }
}

exit $evaluationExitCode