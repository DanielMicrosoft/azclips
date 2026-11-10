// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Core.Abstractions;

namespace Cli.Tests;

/// <summary>
/// End-to-end integration tests that spawn the built <c>az</c> binary and assert
/// on the split between stdout and stderr. The in-process <c>ExecuteCli</c> fixture
/// bypasses <c>Program.Main</c> and <c>BuildStreams</c>, so it can't cover stream
/// routing — see PR <see href="https://github.com/Azure/azclips/pull/575">Azure/azclips#575</see>
/// (decision D9) for the rationale. These tests close that gap.
/// </summary>
public class StreamRoutingIntegrationTests
{
    // Resolve artifacts against the configuration these tests were built with,
    // so the suite works whether the solution is built Debug (local/GitHub) or
    // Release (distribution pipeline).
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    [Fact]
    public async Task WriteError_GoesToStderr_NotStdout()
    {
        var (exit, stdout, stderr) = await RunCliAsync("nonexistent-command-xyz");

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("ERROR:", stdout);
        Assert.Contains("ERROR:", stderr);
    }

    [Fact]
    public async Task UnstructuredFailure_StaysTextOnStderr_WhenJsonOutputIsRequested()
    {
        var (exit, stdout, stderr) = await RunCliAsync("nonexistent-command-xyz", "--output", "json");

        Assert.NotEqual(0, exit);
        Assert.Empty(stdout);
        Assert.Contains("ERROR:", stderr);
        Assert.DoesNotContain("\"error\":", stderr);
    }

    [Fact]
    public async Task RetiredRecoverySettings_AreKeptOnFailureAndTheRecoveryHintIsShown()
    {
        var home = Directory.CreateTempSubdirectory("azclips-retired-keys-tests-").FullName;
        var config = Path.Combine(home, "config.json");
        await File.WriteAllTextAsync(config,
            """{"agent":{"enabled":true,"sessionStyle":"interactive"},"telemetry":{"enabled":false}}""");
        try
        {
            var (exit, stdout, stderr) = await RunCliInHomeAsync(home, "vm", "create", "--output", "table");

            Assert.Equal(2, exit);
            Assert.Empty(stdout);
            Assert.Contains("--name", stderr);
            Assert.DoesNotContain("WARNING:", stderr);
            Assert.Contains("az config set --key agent.recovery.trigger --value manual", stderr);
            Assert.Contains("az config set --key agent.recovery.trigger --value auto", stderr);
            Assert.True(File.Exists(Path.Combine(home, "agent", "state.json")));
            // Retired keys are removed only when the Agent starts, never on the failure path.
            var agent = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(config))!["agent"];
            Assert.Equal(true, (bool?)agent?["enabled"]);
            Assert.False(Directory.Exists(Path.Combine(home, HomePaths.AgentDirectoryName, HomePaths.AgentFailureHistoryDirectoryName)));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task AutoRecovery_ShowsOriginalErrorBeforeAiPlan()
    {
        // Required: AI recovery must never hide the original error.
        using var layout = FakeAgentLayout.Create();

        var (exit, _, stderr) = await layout.RunAsync("config", "set", "--key", "bogus..key", "--value", "x");

        Assert.Equal(2, exit);
        var error = stderr.IndexOf("ERROR: [InvalidInput]", StringComparison.Ordinal);
        var plan = stderr.IndexOf("AI PLAN", StringComparison.Ordinal);
        Assert.True(error >= 0, $"The original error is missing:\n{stderr}");
        Assert.True(plan >= 0, $"The fake agent did not run:\n{stderr}");
        Assert.True(error < plan, $"The original error must come before the AI plan:\n{stderr}");
    }

    [Fact]
    public async Task AutoRecovery_MissingRequiredArgumentsDoesNotStartAi()
    {
        using var layout = FakeAgentLayout.Create();

        var (exit, stdout, stderr) = await layout.RunAsync("config", "set");

        Assert.Equal(2, exit);
        Assert.Empty(stdout);
        Assert.Contains("Required parameter '--key' is missing.", stderr);
        Assert.Contains("Required parameter '--value' is missing.", stderr);
        Assert.DoesNotContain("AI PLAN", stderr);
        Assert.False(File.Exists(layout.RecordPath));
    }

    [Fact]
    public async Task BrokenConfiguration_ShowsOnlyTheOriginalErrorAndKeepsTheExitCode()
    {
        var (exit, _, stderr) = await RunCliWithConfigurationAsync("{ bad json", "profile", "show", "--name", "nosuchprofile");

        Assert.True(exit == 2, $"Expected exit code 2, got {exit}.\n{stderr}");
        Assert.Contains("contains invalid JSON", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }

    [Fact]
    public async Task AutoRecovery_SendsTheFailedCommandWithItsGlobalFlags()
    {
        using var layout = FakeAgentLayout.Create();

        await layout.RunAsync("config", "set", "--key", "bogus..key", "--value", "x", "--output", "table");

        using var record = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(layout.RecordPath));
        using var request = System.Text.Json.JsonDocument.Parse(record.RootElement.GetProperty("request").GetString()!);
        Assert.Equal("config set --key bogus..key --value x --output table",
            request.RootElement.GetProperty("FailedCommand").GetString());
    }

    [Fact]
    public async Task AutoRecovery_WithInvalidStoredStyle_RunsAsOneshot()
    {
        using var layout = FakeAgentLayout.Create(style: "one-shot");

        var (exit, _, stderr) = await layout.RunAsync("config", "set", "--key", "bogus..key", "--value", "x");

        Assert.Equal(2, exit);
        Assert.DoesNotContain("Unhandled exception", stderr);
        var error = stderr.IndexOf("ERROR: [InvalidInput]", StringComparison.Ordinal);
        var plan = stderr.IndexOf("AI PLAN", StringComparison.Ordinal);
        Assert.True(error >= 0 && plan > error, stderr);
    }

    [Fact]
    public async Task AutoRecovery_PassesTheFailureOnlyThroughThePipe()
    {
        using var layout = FakeAgentLayout.Create();

        await layout.RunAsync("config", "set", "--key", "bogus..key", "--value", "x");

        using var record = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(layout.RecordPath));
        var request = record.RootElement.GetProperty("request").GetString()!;
        var argv = string.Join(" ", record.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        var environment = string.Join("\n", record.RootElement.GetProperty("environment").EnumerateObject()
            .Select(p => $"{p.Name}={p.Value.GetString()}"));
        foreach (var secret in new[] { "bogus..key", "Unknown configuration key" })
        {
            Assert.Contains(secret, request);
            Assert.DoesNotContain(secret, argv);
            Assert.DoesNotContain(secret, environment);
        }
    }

    [Fact]
    public async Task ConfigGet_WritesResultToStdout_UnderWarnDefault()
    {
        var (exit, stdout, stderr) = await RunCliAsync("config", "get", "--key", "core.logging.level");

        Assert.Equal(0, exit);
        AssertConfigGetResult(stdout, "core.logging.level", "warn");
        // Under the warn default, a plain get produces no diagnostic chatter on stderr —
        // including the ExtensionLoadTracker summary, which must stay gated behind --debug.
        Assert.DoesNotContain("DEBUG:", stderr);
        Assert.DoesNotContain("VERBOSE:", stderr);
        Assert.DoesNotContain("[ExtensionLoadTracker]", stderr);
    }

    [Fact]
    public async Task ConfigGet_AppliesQuery_ToCommandOutput()
    {
        var (exit, stdout, stderr) = await RunCliAsync(
            "config", "get", "--key", "core.logging.level", "--query", "value");

        Assert.Equal(0, exit);
        // A --query narrowing a result to a scalar now uses clients.cli.output.simpleFormat
        // (default tsv) rather than the complex-result default (json) -- plain text, no quotes.
        Assert.Equal("warn", stdout.Trim());
        Assert.DoesNotContain("ERROR:", stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupProfileLoad_AppliesDebugOverride_WhenProfileFileExists(bool debugOverride)
    {
        var homeDirectory = Directory.CreateTempSubdirectory("azclips-startup-log-tests-").FullName;
        File.WriteAllText(Path.Combine(homeDirectory, "config.json"), """{"telemetry":{"enabled":false}}""");
        File.WriteAllText(Path.Combine(homeDirectory, "profiles.json"), """
{
  "default": {
    "name": "default",
    "tenantId": "11111111-1111-1111-1111-111111111111",
    "subscriptionId": "22222222-2222-2222-2222-222222222222",
    "cloudName": "AzureCloud",
    "authMethod": "Browser"
  }
}
""");
        File.WriteAllText(Path.Combine(homeDirectory, "active-profile"), "default");

        try
        {
            var args = new List<string> { "config", "get", "--key", "core.logging.level" };
            if (debugOverride)
            {
                args.Insert(0, "--debug");
            }
            var (exit, stdout, stderr) = await RunCliInHomeAsync(homeDirectory, args.ToArray());

            Assert.Equal(0, exit);
            AssertConfigGetResult(stdout, "core.logging.level", "warn");
            if (debugOverride)
            {
                Assert.Contains("VERBOSE: [ProfileService] Loading profiles from", stderr);
            }
            else
            {
                Assert.DoesNotContain("[ProfileService] Loading profiles from", stderr);
            }
            Assert.DoesNotContain("VERBOSE:", stdout);
        }
        finally
        {
            try { Directory.Delete(homeDirectory, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task ConfigGet_AppliesQuery_HonorsExplicitOutputOverride_ForScalarResult()
    {
        var (exit, stdout, stderr) = await RunCliAsync(
            "--output", "json", "config", "get", "--key", "core.logging.level", "--query", "value");

        Assert.Equal(0, exit);
        Assert.Equal("\"warn\"" + Environment.NewLine, stdout);
        Assert.DoesNotContain("ERROR:", stderr);
    }

    [Fact]
    public async Task Logout_InvalidQuery_FailsBeforeCommandExecution()
    {
        var (exit, stdout, stderr) = await RunCliAsync("logout", "--query", "[[[");

        Assert.Equal(2, exit);
        Assert.Empty(stdout);
        Assert.Contains("--query expression", stderr);
    }

    [Fact]
    public async Task InvalidQuery_RemainsText_WhenJsonOutputIsRequested()
    {
        var (exit, stdout, stderr) = await RunCliAsync("--output", "json", "logout", "--query", "[[[");

        Assert.Equal(2, exit);
        Assert.Empty(stdout);
        Assert.Contains("ERROR: [UsageError]", stderr);
        Assert.Contains("--query expression", stderr);
        Assert.DoesNotContain("\"error\"", stderr);
    }

    [Fact]
    public async Task InvalidInput_AgentDisabled_EmitsTypedErrorAndExitsTwo()
    {
        // Agent fallback is off by default, so the agent-eligible InvalidInput falls through to the typed error.
        var (exit, stdout, stderr) = await RunCliAsync("config", "set", "--key", "bogus..key", "--value", "x");

        Assert.Equal(2, exit);
        Assert.Empty(stdout);
        Assert.Contains("ERROR: [InvalidInput]", stderr);
    }

    [Fact]
    public async Task DebugFlag_EmitsDebugToStderr_LeavesStdoutClean()
    {
        var (exit, stdout, stderr) = await RunCliAsync("--debug", "config", "get", "--key", "core.logging.level");

        Assert.Equal(0, exit);
        AssertConfigGetResult(stdout, "core.logging.level", "warn");
        Assert.Contains("DEBUG:", stderr);
        // stdout should still be pure result — no diagnostic leakage.
        Assert.DoesNotContain("DEBUG:", stdout);
        Assert.DoesNotContain("VERBOSE:", stdout);
        Assert.DoesNotContain("INFO:", stdout);
    }

    [Fact]
    public async Task DebugFlag_EmitsExtensionLoaderChainAndSummary()
    {
        var (exit, _, stderr) = await RunCliAsync("--debug", "config", "get", "--key", "core.logging.level");

        Assert.Equal(0, exit);
        // Per-load lines from the two loaders (at least one).
        Assert.Matches(@"\[(BundledExtensionLoader|InstalledExtensionLoader)\] Resolving extension", stderr);
        // Aggregate table header + total row from ExtensionLoadTracker.EmitSummary in finally.
        Assert.Contains("[ExtensionLoadTracker] Extension", stderr);
        Assert.Contains("[ExtensionLoadTracker] Total (", stderr);
    }

    [Fact]
    public async Task BareGroup_RendersGroupHelpToStdout()
    {
        var (exit, stdout, stderr) = await RunCliAsync("vm");

        Assert.Equal(0, exit);
        Assert.Contains("# GROUP", stdout);
        Assert.Contains("az vm:", stdout);
        Assert.DoesNotContain("ERROR:", stderr);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-help")]
    [InlineData("-?")]
    [InlineData("--?")]
    public async Task GroupHelpAlias_RendersGroupHelpToStdout(string helpToken)
    {
        var (exit, stdout, _) = await RunCliAsync("vm", helpToken);

        Assert.Equal(0, exit);
        Assert.Contains("az vm:", stdout);
    }

    [Fact]
    public async Task GroupWithUnknownFlag_KeepsCommandNotFound_AndRendersNoHelp()
    {
        var (exit, stdout, stderr) = await RunCliAsync("vm", "--list");

        Assert.Equal(4, exit);
        Assert.Empty(stdout);
        Assert.Contains("CommandNotFound", stderr);
    }

    [Fact]
    public async Task MissingRequiredParameters_EmitsTextError_WhenJsonOutputIsRequested()
    {
        var (exit, stdout, stderr) = await RunCliAsync("--output", "json", "vm", "create");

        Assert.Equal(2, exit);
        Assert.Empty(stdout);
        Assert.Contains("ERROR: [UsageError]", stderr);
        foreach (var flag in new[] { "name", "location", "resource-group", "size", "admin-username" })
        {
            Assert.Contains($"Required parameter '--{flag}' is missing.", stderr);
        }
        Assert.Contains("az vm create --help", stderr);
        Assert.DoesNotContain("\"error\"", stderr);
    }

    [Fact]
    public async Task MissingRequiredParameters_DoesNotRenderHelpDocument_WhenRedirected()
    {
        var (_, stdout, stderr) = await RunCliAsync("vm", "create");

        Assert.Empty(stdout);
        Assert.DoesNotContain("# COMMAND", stderr);
        Assert.DoesNotContain("# ARGUMENTS", stderr);
    }

    [Fact]
    public async Task ForceColor_KeepsJsonResultAnsiFree_OnRedirectedStdout()
    {
        var (exit, stdout, _) = await RunCliWithEnvAsync(
            new Dictionary<string, string> { ["FORCE_COLOR"] = "1" },
            "config", "get", "--key", "core.logging.level");

        Assert.Equal(0, exit);
        Assert.DoesNotContain('\u001b', stdout);
        AssertConfigGetResult(stdout, "core.logging.level", "warn");
    }

    [Fact]
    public async Task ForceColor_ColorsJsoncResult_OnRedirectedStdout()
    {
        var (exit, stdout, _) = await RunCliWithEnvAsync(
            new Dictionary<string, string> { ["FORCE_COLOR"] = "1" },
            "--output", "jsonc", "config", "get", "--key", "core.logging.level");

        Assert.Equal(0, exit);
        Assert.Contains('\u001b', stdout);
        AssertConfigGetResult(StripAnsi(stdout), "core.logging.level", "warn");
    }

    [Fact]
    public async Task NoColor_RendersJsoncAsPlainJson_EvenWhenColorIsForced()
    {
        var (exit, stdout, _) = await RunCliWithEnvAsync(
            new Dictionary<string, string> { ["NO_COLOR"] = "1", ["FORCE_COLOR"] = "1" },
            "--output", "jsonc", "config", "get", "--key", "core.logging.level");

        Assert.Equal(0, exit);
        Assert.DoesNotContain('\u001b', stdout);
        AssertConfigGetResult(stdout, "core.logging.level", "warn");
    }

    [Fact]
    public async Task ForceColor_KeepsTextErrorAnsiFree_OnRedirectedStderr()
    {
        var (exit, stdout, stderr) = await RunCliWithEnvAsync(
            new Dictionary<string, string> { ["FORCE_COLOR"] = "1" },
            "--output", "json", "vm", "create");

        Assert.Equal(2, exit);
        Assert.Empty(stdout);
        Assert.DoesNotContain('\u001b', stderr);
        Assert.Contains("ERROR: [UsageError]", stderr);
    }

    [Fact]
    public async Task ConfigSet_SimpleFormat_ChangesQueryScalarRendering_EndToEnd()
    {
        // Exercises the real path -- az config set writes JSON, ConfigurationService binds it into
        // ClientsOptions, OutputFormatPolicy resolves it -- not just the in-memory unit tests, which
        // construct ClientsOptions/CliOutputOptions directly and never touch config persistence.
        var homeDirectory = Directory.CreateTempSubdirectory("azclips-stream-routing-tests-").FullName;
        File.WriteAllText(Path.Combine(homeDirectory, "config.json"),
            """{"agent":{"recovery":{"trigger":"off"}},"telemetry":{"enabled":false}}""");
        try
        {
            var (setExit, _, setStderr) = await RunCliInHomeAsync(
                homeDirectory,
                "config", "set", "--key", "clients.cli.output.simpleFormat", "--value", "json", "--force");
            Assert.Equal(0, setExit);
            Assert.DoesNotContain("ERROR:", setStderr);

            var (exit, stdout, stderr) = await RunCliInHomeAsync(
                homeDirectory,
                "config", "get", "--key", "core.logging.level", "--query", "value");

            Assert.Equal(0, exit);
            Assert.Equal("\"warn\"" + Environment.NewLine, stdout);
            Assert.DoesNotContain("ERROR:", stderr);
        }
        finally
        {
            try { Directory.Delete(homeDirectory, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Task<(int exit, string stdout, string stderr)> RunCliAsync(params string[] args)
        => RunCliWithConfigurationAsync(DefaultConfiguration, null, args);

    private const string DefaultConfiguration =
        """{"agent":{"recovery":{"trigger":"off"}},"telemetry":{"enabled":false}}""";

    [Fact]
    public async Task CommandTiming_SeparatesLoadingPreparationAndInvocation_OnStderr()
    {
        var (exit, stdout, stderr) = await RunCliAsync("config", "get", "--key", "core.logging.level", "--debug");

        Assert.Equal(0, exit);
        AssertConfigGetResult(stdout, "core.logging.level", "warn");
        Assert.DoesNotContain("[CommandTiming]", stdout);
        AssertTimingSummary(stderr, 0);
        Assert.Contains("Init/startup", stderr);
        Assert.Contains("Command-tree construction", stderr);
        Assert.Contains("Selected-extension loading", stderr);
        Assert.Contains("Dispatch preparation", stderr);
        Assert.Contains("Command invocation", stderr);
        Assert.Contains("Output formatting/write", stderr);
        Assert.DoesNotContain("Discovery/command metadata", stderr);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public async Task CommandTiming_HostPathsReportOnlyApplicablePhases(string flag)
    {
        var (exit, stdout, stderr) = await RunCliAsync(flag, "--debug");

        Assert.Equal(0, exit);
        Assert.NotEmpty(stdout);
        AssertTimingSummary(stderr, 0);
        Assert.DoesNotContain("[CommandTiming]", stdout);
        Assert.DoesNotContain("Command invocation", stderr);
        Assert.DoesNotContain("Selected-extension loading", stderr);
        if (flag == "--help")
        {
            Assert.Contains("Discovery/command metadata", stderr);
            Assert.Contains("Help render/output", stderr);
            Assert.Contains("[ExtensionLoadTracker]", stderr);
        }
        else
        {
            Assert.DoesNotContain("Routing discovery/selection/index", stderr);
            Assert.DoesNotContain("[ExtensionLoadTracker]", stderr);
        }
    }

    [Theory]
    [InlineData("--completions")]
    [InlineData("--completion-candidates")]
    public async Task CommandTiming_CompletionIsExcludedWithoutChangingProtocol(string mode)
    {
        var (normalExit, normalOut, normalError) = await RunCliAsync(mode, "vm");
        var (debugExit, debugOut, debugError) = await RunCliAsync(mode, "vm", "--debug");

        Assert.Equal(0, normalExit);
        Assert.Equal(normalExit, debugExit);
        Assert.DoesNotContain("[CommandTiming]", normalError);
        Assert.DoesNotContain("[CommandTiming]", debugOut);
        Assert.DoesNotContain("[CommandTiming]", debugError);
        Assert.DoesNotContain("[ExtensionLoadTracker]", debugError);
        Assert.DoesNotContain("Command invocation", debugError);
        if (mode == "--completions")
        {
            using var normalJson = System.Text.Json.JsonDocument.Parse(normalOut);
            Assert.NotEmpty(normalJson.RootElement.EnumerateObject());
            // Completion retains its own grammar: the second context token is a subcommand,
            // not a newly introduced global diagnostic flag.
            Assert.Equal("{}", debugOut.Trim());
            using var json = System.Text.Json.JsonDocument.Parse(debugOut);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, json.RootElement.ValueKind);
        }
        else
        {
            Assert.Equal(normalOut, debugOut);
        }
    }

    [Theory]
    [InlineData("--completions", "{}")]
    [InlineData("--completion-candidates", "")]
    public async Task CommandTiming_DisabledCompletionDoesNotForceDiscovery(string mode, string expected)
    {
        var configuration = """{"completion":{"enabled":false},"telemetry":{"enabled":false}}""";
        var (exit, stdout, stderr) = await RunCliWithConfigurationAsync(configuration, mode, "--debug");

        Assert.Equal(0, exit);
        Assert.Equal(expected, stdout.Trim());
        Assert.DoesNotContain("[CommandTiming]", stderr);
        Assert.DoesNotContain("Routing discovery/selection/index", stderr);
        Assert.DoesNotContain("Discovery/command metadata", stderr);
        Assert.DoesNotContain("[ExtensionLoadTracker]", stderr);
    }

    [Fact]
    public async Task CommandTiming_UsageFailurePreservesExitAndDoesNotClaimInvocation()
    {
        var (exit, stdout, stderr) = await RunCliAsync("vm", "create", "--debug");

        Assert.Equal(2, exit);
        Assert.Empty(stdout);
        Assert.Contains("ERROR:", stderr);
        AssertTimingSummary(stderr, 2);
        Assert.Contains("Dispatch preparation", stderr);
        Assert.Contains("Failure handling", stderr);
        Assert.DoesNotContain("Command invocation", stderr);
    }

    [Theory]
    [InlineData("warn")]
    [InlineData("error")]
    [InlineData("info")]
    [InlineData("verbose")]
    public async Task CommandTiming_NonDebugLevelsRemainSilent(string level)
    {
        var (exit, _, stderr) = await RunCliWithEnvAsync(
            new Dictionary<string, string> { ["AZCLIPS_LOG_LEVEL"] = level },
            "config", "get", "--key", "core.logging.level");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("[CommandTiming]", stderr);
    }

    [Fact]
    public async Task CommandTiming_ConfiguredDebugAppliesWithoutFlag()
    {
        var (exit, _, stderr) = await RunCliWithEnvAsync(
            new Dictionary<string, string> { ["AZCLIPS_LOG_LEVEL"] = "debug" },
            "config", "get", "--key", "core.logging.level");

        Assert.Equal(0, exit);
        AssertTimingSummary(stderr, 0);
    }

    [Theory]
    [InlineData("--completions")]
    [InlineData("--completion-candidates")]
    public async Task CommandTiming_CompletionRemainsExcludedUnderConfiguredDebug(string mode)
    {
        var (exit, _, stderr) = await RunCliWithEnvAsync(
            new Dictionary<string, string> { ["AZCLIPS_LOG_LEVEL"] = "debug" },
            mode, "vm");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("[CommandTiming]", stderr);
        Assert.DoesNotContain("[ExtensionLoadTracker]", stderr);
    }

    private static void AssertTimingSummary(string stderr, int exit)
    {
        var text = StripAnsi(stderr);
        Assert.Single(text.Split('\n'), line => line.Contains("[CommandTiming] Total managed command time:"));
        Assert.Contains($"exit={exit}", text);
        Assert.Contains("Finalization", text);
        Assert.Contains("Uninstrumented (interval union)", text);
        Assert.Contains("Durations are inclusive", text);
        Assert.DoesNotContain("(incomplete)", text);
    }

    private static Task<(int exit, string stdout, string stderr)> RunCliWithEnvAsync(
        IReadOnlyDictionary<string, string>? env, params string[] args)
        => RunCliWithConfigurationAsync(DefaultConfiguration, env, args);

    private static Task<(int exit, string stdout, string stderr)> RunCliWithConfigurationAsync(
        string configuration, params string[] args)
        => RunCliWithConfigurationAsync(configuration, null, args);

    private static string StripAnsi(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[[0-9;]*m", string.Empty);

    private static async Task<(int exit, string stdout, string stderr)> RunCliWithConfigurationAsync(
        string configuration, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        // Isolate config/profiles/extensions from the developer's/CI's real ~/.azclips -- a
        // persisted override (e.g. clients.cli.output.simpleFormat) would otherwise change these
        // assertions. Fresh temp directory per invocation so parallel test runs never collide.
        var homeDirectory = Directory.CreateTempSubdirectory("azclips-stream-routing-tests-").FullName;
        // Telemetry off so Program.cs's detached --upload-telemetry child has nothing to read/write
        // under this home -- avoids racing our cleanup below rather than just tolerating the race.
        File.WriteAllText(Path.Combine(homeDirectory, "config.json"), configuration);
        try
        {
            return await RunCliInHomeWithEnvAsync(homeDirectory, env, args);
        }
        finally
        {
            // Best-effort: Program.cs's SpawnTelemetryUploader detaches an --upload-telemetry
            // child that inherits AZCLIPS_HOME and may still be reading/writing the trace
            // directory here, so an immediate recursive delete can race it (esp. on Windows,
            // where an open handle turns this into a flaky IOException, not a real failure).
            try { Directory.Delete(homeDirectory, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void AssertConfigGetResult(string stdout, string expectedKey, string expectedValue)
    {
        using var document = System.Text.Json.JsonDocument.Parse(stdout);
        var entry = document.RootElement;

        Assert.Equal(expectedKey, entry.GetProperty("key").GetString());
        Assert.Equal(expectedValue, entry.GetProperty("value").GetString());
    }

    /// <summary>Runs the CLI against an already-prepared, caller-owned <paramref name="homeDirectory"/> -- lets a test issue multiple invocations (e.g. <c>config set</c> then <c>config get</c>) against the same persisted config.</summary>
    private static Task<(int exit, string stdout, string stderr)> RunCliInHomeAsync(string homeDirectory, params string[] args)
        => RunCliInHomeWithEnvAsync(homeDirectory, null, args);

    private static Task<(int exit, string stdout, string stderr)> RunCliInHomeWithEnvAsync(
        string homeDirectory,
        IReadOnlyDictionary<string, string>? env,
        params string[] args)
        => RunCliInHomeWithEnvAsync(homeDirectory, env, args, ResolveCliBinary());

    private static async Task<(int exit, string stdout, string stderr)> RunCliInHomeWithEnvAsync(
        string homeDirectory,
        IReadOnlyDictionary<string, string>? env,
        string[] args,
        string cliBinary)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Pin AZCLIPS_LOG_LEVEL=warn so ambient developer/CI env doesn't shift the effective level
        // and invalidate the routing assertions below. Tests that need Debug pass `--debug` explicitly;
        // `args > env` precedence means the flag still wins over this pin.
        psi.Environment["AZCLIPS_LOG_LEVEL"] = "warn";
        psi.Environment["AZCLIPS_HOME"] = homeDirectory;
        foreach (var key in psi.Environment.Keys.Where(key =>
            key.StartsWith("AZCLIPS_AGENT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            psi.Environment.Remove(key);
        }
        psi.Environment.Remove("AZCLIPS_IN_AGENT_MODE");
        // Ambient color vars would otherwise leak into every assertion.
        psi.Environment.Remove("NO_COLOR");
        psi.Environment.Remove("FORCE_COLOR");
        foreach (var (name, value) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[name] = value;
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        // Drain stdout/stderr concurrently via ReadToEndAsync so we deterministically capture
        // trailing lines. Event-based capture (OutputDataReceived) can race the process exit
        // and drop the last few lines under CI scheduling. Same pattern as
        // tests/Benchmark/Startup/StartupBenchmarkBase.RunToExit.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>A copy of the built CLI and the Config extension, with the fake agent as azclips-agent beside az.</summary>
    private sealed class FakeAgentLayout : IDisposable
    {
        private readonly string _root;
        private readonly string _home;

        private FakeAgentLayout(string root)
        {
            _root = root;
            _home = Path.Combine(root, "home");
            RecordPath = Path.Combine(root, "agent-record.json");
        }

        internal string RecordPath { get; }

        internal static FakeAgentLayout Create(string style = "oneshot")
        {
            var artifacts = ResolveArtifactsDirectory();
            var fakeAgent = Path.Combine(ResolveRepositoryRoot(), "tests", "Fixtures", "FakeAgent", "bin", BuildConfiguration, "net10.0");
            Assert.True(Directory.Exists(fakeAgent), $"Build the fake agent fixture first: {fakeAgent}");

            var layout = new FakeAgentLayout(Directory.CreateTempSubdirectory("azclips-fake-agent-tests-").FullName);
            var cli = Path.Combine(layout._root, "Core.Cli");
            CopyDirectory(Path.Combine(artifacts, "Core.Cli"), cli);
            CopyDirectory(Path.Combine(artifacts, "Microsoft.Azure.Azclips.Extensions.Config"),
                Path.Combine(layout._root, "Microsoft.Azure.Azclips.Extensions.Config"));
            foreach (var file in Directory.GetFiles(fakeAgent, "azclips-agent*"))
                File.Copy(file, Path.Combine(cli, Path.GetFileName(file)));
            Directory.CreateDirectory(layout._home);
            File.WriteAllText(Path.Combine(layout._home, "config.json"),
                $$$"""{"agent":{"recovery":{"trigger":"auto","style":"{{{style}}}"}},"telemetry":{"enabled":false}}""");
            return layout;
        }

        internal Task<(int exit, string stdout, string stderr)> RunAsync(params string[] args)
        {
            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "az.exe" : "az";
            return RunCliInHomeWithEnvAsync(
                _home, new Dictionary<string, string> { ["AZCLIPS_TEST_FAKE_AGENT_OUT"] = RecordPath }, args,
                Path.Combine(_root, "Core.Cli", exeName));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static void CopyDirectory(string source, string target)
        {
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(directory.Replace(source, target));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, target));
        }

        private static string ResolveRepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "azclips.slnx")))
                    return dir.FullName;
            }
            throw new InvalidOperationException($"Could not locate azclips.slnx above {AppContext.BaseDirectory}.");
        }
    }

    private static string ResolveCliBinary()
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "az.exe" : "az";
        var cliBinary = Path.Combine(ResolveArtifactsDirectory(), "Core.Cli", exeName);

        if (!File.Exists(cliBinary))
        {
            throw new InvalidOperationException(
                $"CLI binary not found: {cliBinary}. Run `dotnet build azclips.slnx` first.");
        }

        return cliBinary;
    }

    private static string ResolveArtifactsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", BuildConfiguration);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate 'artifacts/{BuildConfiguration}' above {AppContext.BaseDirectory}. " +
            $"Run `dotnet build azclips.slnx` to produce it.");
    }
}
