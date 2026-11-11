// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Core.Abstractions;
using Core.Agent.Tools.Env;
using Core.Agent.Tools.Help;
using Core.Cli.Help;
using Core.Runtime;
using Core.Runtime.Persistence;
using Microsoft.Azure.Azclips.Extensions.Agent.Processors;
using Tests.Common.Stubs;
using Tests.Common.Utilities;
using Xunit;

namespace Core.Agent.Tests;

public sealed class DirectReadCommandTests
{
    [Fact]
    public async Task GenerateLeafRoutesThroughRealDiscovery_AndHelpCommandsMatchDirectReads()
    {
        using var fixture = new HelpCatalogFixture();
        var home = await CreateChildHomeAsync(fixture);
        var cli = BuiltCliPath();
        using (var cold = JsonDocument.Parse(await RunCliAsync(cli, home, ["agent", "tool", "docs", "show", "--name", "agent tool docs show"], fixture.Root)))
            Assert.Equal("agent tool docs show", cold.RootElement.GetProperty("cliName").GetString());
        var generated = await RunCliAsync(cli, home, ["agent", "tool", "docs", "generate"], fixture.Root);
        using (var document = JsonDocument.Parse(generated))
            Assert.True(document.RootElement.GetProperty("generated").GetBoolean());
        Assert.False(File.Exists(Path.Combine(home, "extensions", "registry.json")));

        var provider = new HelpCatalogProvider(cli, fixture.Configuration, Path.Combine(home, HomePaths.AgentDirectoryName, HomePaths.AgentHelpDirectoryName),
            _ => throw new InvalidOperationException("A warm read must not invoke az."));
        HelpCatalogProviderTests.AssertJsonEqual(
            await RunCliAsync(cli, home, ["agent", "tool", "docs", "list"], fixture.Root), await provider.ListJsonAsync());
        HelpCatalogProviderTests.AssertJsonEqual(
            await RunCliAsync(cli, home, ["agent", "tool", "docs", "show", "--name", "agent tool docs show"], fixture.Root),
            await provider.GetJsonAsync("agent tool docs show"));
        var path = Path.Combine(home, HomePaths.AgentDirectoryName, HomePaths.AgentHelpDirectoryName, HelpCatalogProvider.IndexFileName);
        await File.WriteAllTextAsync(path, "{broken");
        HelpCatalogProviderTests.AssertJsonEqual(
            await RunCliAsync(cli, home, ["agent", "tool", "docs", "list"], fixture.Root), await provider.ListJsonAsync());

        var index = new Microsoft.Azure.Azclips.Extensions.Agent.ExtensionIndex();
        Assert.Equal(typeof(DocsGenerateProcessor), index.GetCommandMetadata("agent tool docs generate", ClientType.CLI).ProcessorType);
        Assert.Empty(index.GetCommandMetadata("agent tool docs show", ClientType.CLI).ParameterAliases);
        Assert.Equal(new[] { "name" }, index.GetCommandMetadata("agent tool docs show", ClientType.CLI).Parameters.Keys);
        Assert.Empty(index.GetCommandMetadata("agent tool docs generate", ClientType.CLI).Parameters);
    }

    [Fact]
    public async Task EnvProcessorMatchesDirectProvider_ForSignedInAndSignedOutProfiles()
    {
        using var fixture = new HelpCatalogFixture();
        var home = await CreateChildHomeAsync(fixture);
        var cli = BuiltCliPath();
        var profile = new ProfileService(new NullLoggingService(), new FilePersistenceStore(home));
        foreach (var signedIn in new[] { false, true })
        {
            if (signedIn)
                await profile.SaveAndActivateProfileAsync(new Profile
                {
                    Name = "offline",
                    TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    SubscriptionId = "11111111-1111-1111-1111-111111111111",
                });

            using var command = JsonDocument.Parse(await RunCliAsync(cli, home, ["agent", "tool", "env", "show"], fixture.Root));
            using var direct = JsonDocument.Parse(await EnvSnapshotProvider.GetJsonAsync(profile, cli));
            foreach (var field in new[] { "os", "shell", "sshSession", "browserAvailable", "signedIn", "subscriptionId", "tenantId", "flavor", "azclipsVersion" })
                Assert.True(JsonElement.DeepEquals(command.RootElement.GetProperty(field), direct.RootElement.GetProperty(field)), field);
            Assert.Equal(fixture.Root, command.RootElement.GetProperty("cwd").GetString());
            Assert.Equal(signedIn, command.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.Equal(direct.RootElement.EnumerateObject().Select(p => p.Name), command.RootElement.EnumerateObject().Select(p => p.Name));
        }
    }

    [Fact]
    public async Task CliHelpReturnsStructuredErrorForAnExtensionWithoutAnIndex()
    {
        using var fixture = new HelpCatalogFixture();
        fixture.Install("NoIndex", "1.0.0", typeof(AgentOptions).Assembly);
        var home = await CreateChildHomeAsync(fixture);

        var output = await RunCliAsync(BuiltCliPath(), home, ["agent", "tool", "docs", "list"], fixture.Root);

        HelpCatalogProviderTests.AssertError(output, "help_catalog_regeneration_failed");
        Assert.False(File.Exists(Path.Combine(home, HomePaths.AgentDirectoryName, HomePaths.AgentHelpDirectoryName, HelpCatalogProvider.IndexFileName)));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("show")]
    public async Task DocsLeafShowsCatalogRegenerationGuidance(string action)
    {
        using var fixture = new HelpCatalogFixture();
        var home = await CreateChildHomeAsync(fixture);

        var output = await RunCliAsync(BuiltCliPath(), home, ["agent", "tool", "docs", action, "--help"], fixture.Root);

        Assert.Contains("local help catalog", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("az agent tool docs generate", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentGenerationProcesses_PublishReadableCatalogs()
    {
        using var fixture = new HelpCatalogFixture();
        var home = await CreateChildHomeAsync(fixture);
        var cli = BuiltCliPath();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            RunCliAsync(cli, home, ["agent", "tool", "docs", "generate"], fixture.Root)));
        foreach (var result in results)
        {
            using var generated = JsonDocument.Parse(result);
            Assert.True(generated.RootElement.GetProperty("generated").GetBoolean());
        }

        var provider = new HelpCatalogProvider(cli, fixture.Configuration, Path.Combine(home, HomePaths.AgentDirectoryName, HomePaths.AgentHelpDirectoryName),
            _ => throw new InvalidOperationException("Concurrent publishers must leave a fresh catalog."));
        using var command = JsonDocument.Parse(await provider.GetJsonAsync("agent tool docs generate"));
        Assert.Equal("agent tool docs generate", command.RootElement.GetProperty("cliName").GetString());
    }

    [Fact]
    public void DocsRegistrationsAndManifest_ExposeDistinctActionsWithRequiredLookupName()
    {
        var index = new Microsoft.Azure.Azclips.Extensions.Agent.ExtensionIndex();
        var list = index.GetCommandMetadata("agent tool docs list", ClientType.CLI);
        var show = index.GetCommandMetadata("agent tool docs show", ClientType.CLI);
        Assert.Equal(typeof(DocsListProcessor), list.ProcessorType);
        Assert.Equal(typeof(DocsShowProcessor), show.ProcessorType);
        Assert.Empty(list.Parameters);
        Assert.Empty(list.ParameterAliases);
        Assert.True(Assert.Single(show.Parameters).Value.IsRequired);
        Assert.True(Assert.Single(Assert.Single(show.ParameterSets!).Value.Parameters!).Value.IsRequired);
        Assert.Empty(index.GetCommandMetadata("agent tool env show", ClientType.CLI).Parameters);

        var manifest = ExtensionHelpManifestBuilder.BuildManifest(index, "1.0.0", "agent");
        var docs = manifest.Commands.Where(command => command.CliName.StartsWith("agent tool docs ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "agent tool docs generate", "agent tool docs list", "agent tool docs show" },
            docs.Select(command => command.CliName));
        Assert.Empty(docs.Single(command => command.CliName == "agent tool docs list").Parameters);
        Assert.True(Assert.Single(docs.Single(command => command.CliName == "agent tool docs show").Parameters).Required);
    }

    [Theory]
    [InlineData("docs", "list", "show", "generate")]
    [InlineData("env", "show")]
    public async Task ToolGroups_OnlyExposeActionsThroughHelp(string group, params string[] actions)
    {
        using var fixture = new HelpCatalogFixture();
        var home = await CreateChildHomeAsync(fixture);
        var index = new Microsoft.Azure.Azclips.Extensions.Agent.ExtensionIndex();
        Assert.Throws<KeyNotFoundException>(() => index.GetCommandMetadata($"agent tool {group}", ClientType.CLI));
        var help = await RunCliAsync(BuiltCliPath(), home, ["agent", "tool", group, "--help"], fixture.Root);
        foreach (var action in actions)
            Assert.Contains(action, help, StringComparison.Ordinal);
        var error = await RunCliAsync(BuiltCliPath(), home, ["agent", "tool", group], fixture.Root, expectedExitCode: 4);
        Assert.Contains("CommandNotFound", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("help", "generate")]
    public async Task RetiredHelpPaths_AreNotExecutable(params string[] path)
    {
        using var fixture = new HelpCatalogFixture();
        var home = await CreateChildHomeAsync(fixture);
        var error = await RunCliAsync(BuiltCliPath(), home, ["agent", "tool", .. path], fixture.Root, expectedExitCode: 4);
        Assert.Contains("CommandNotFound", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsShowWithoutName_ReportsMissingRequiredFlag()
    {
        using var fixture = new HelpCatalogFixture();
        var home = await CreateChildHomeAsync(fixture);
        var error = await RunCliAsync(BuiltCliPath(), home, ["agent", "tool", "docs", "show"], fixture.Root, expectedExitCode: 2);
        Assert.Contains("UsageError", error, StringComparison.Ordinal);
        Assert.Contains("candidate: [name]", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(home, HomePaths.AgentDirectoryName, HomePaths.AgentHelpDirectoryName, HelpCatalogProvider.IndexFileName)));
    }

    [Fact]
    public void AgentProcessorCancellation_IsAvailableToReadAndGeneratePaths()
    {
        foreach (var processor in new AgentProcessorBase[] { new DocsGenerateProcessor(), new DocsListProcessor(), new DocsShowProcessor(), new EnvProcessor() })
        {
            Assert.False(processor.Token.IsCancellationRequested);
            processor.Cancel();
            Assert.True(processor.Token.IsCancellationRequested);
        }
    }

    private static async Task<string> CreateChildHomeAsync(HelpCatalogFixture fixture)
    {
        var home = Path.Combine(fixture.Root, "cli-home");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "config.json"), JsonSerializer.Serialize(new
        {
            agent = new { enabled = false },
            telemetry = new { enabled = false },
            assemblyLoading = new { installedExtensionsRoot = fixture.InstalledRoot, bypassExtensionCache = true },
        }));
        return home;
    }

    private static string BuiltCliPath()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        return Path.Combine(RepoLocator.GetRepoRoot(), "artifacts", configuration, "Core.Cli",
            OperatingSystem.IsWindows() ? "az.exe" : "az");
    }

    private static async Task<string> RunCliAsync(string cli, string home, string[] arguments, string callerDirectory, int expectedExitCode = 0)
    {
        var start = new ProcessStartInfo(cli)
        {
            WorkingDirectory = Path.GetDirectoryName(cli)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[HomePaths.HomeOverrideVariable] = home;
        start.Environment["AZCLIPS_IN_AGENT_MODE"] = "1";
        start.Environment["AZCLIPS_CALLER_CWD"] = callerDirectory;
        start.Environment["AZCLIPS_LOG_LEVEL"] = "error";
        start.Environment["AZCLIPS_BENCH_RESOLVE_ONLY"] = "false";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        var stderr = await errors;
        Assert.True(process.ExitCode == expectedExitCode, $"Expected exit {expectedExitCode}, got {process.ExitCode}.\n{stderr}");
        return expectedExitCode == 0 ? await output : await output + stderr;
    }
}
