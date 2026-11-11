// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Core.Abstractions;
using Core.Abstractions.Recovery;
using Core.Agent.Hosting;
using Core.Agent.Telemetry;
using Core.Agent.Tools;
using Core.Agent.Tools.Help;
using Core.Agent.Tools.Resource.Cache;
using Core.Agent.Tools.Resource.Sku;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Tests.Common.Stubs;
using Xunit;

namespace Core.Agent.Tests;

#pragma warning disable GHCP001 // Exercise the same preview permission contract used by ChatAgent.
[Collection("Console")]
public class RecoverySessionPolicyTests
{
    [Theory]
    [InlineData("oneshot")]
    [InlineData("interactive")]
    public void SessionConfig_ExposesTheSameToolsForBothTriggers(string style)
    {
        using var http = new HttpClient();
        var automatic = CreateConfig(RecoveryTrigger.Automatic, style, new(), http, CancellationToken.None);
        var manual = CreateConfig(RecoveryTrigger.Manual, style, new(), http, CancellationToken.None);

        Assert.Null(automatic.AvailableTools);
        Assert.Null(manual.AvailableTools);
        Assert.Equal(automatic.Tools!.Select(t => t.Name), manual.Tools!.Select(t => t.Name));
        Assert.Contains(manual.Tools!, t => t.Name == "get_command");
        Assert.Contains(manual.Tools!, t => t.Name == "find_resources");
        Assert.Contains(manual.Tools!, t => t.Name == "propose_plan");
        Assert.Contains(manual.Tools!, t => t.Name == "submit_response");
    }

    [Theory]
    [InlineData(RecoveryTrigger.Automatic, "oneshot")]
    [InlineData(RecoveryTrigger.Manual, "oneshot")]
    [InlineData(RecoveryTrigger.Automatic, "interactive")]
    [InlineData(RecoveryTrigger.Manual, "interactive")]
    public async Task SessionConfig_UsesExistingReadOnlyAndUserApprovalPolicy(RecoveryTrigger trigger, string style)
    {
        using var http = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var approval = new ConsoleApprovalPrompt();
        var config = CreateConfig(trigger, style, approval, http, timeout.Token);
        var context = new PermissionInvocation { SessionId = "offline-policy-test" };
        var read = await config.OnPermissionRequest!(new PermissionRequestCustomTool
        {
            ToolName = "find_resources",
            ToolDescription = "Read-only fixture",
        }, context);
        Assert.Equal(JsonSerializer.Serialize(PermissionDecision.ApproveOnce()), JsonSerializer.Serialize(read));

        var previousInput = Console.In;
        var previousOutput = Console.Out;
        Console.SetIn(new StringReader("n" + Environment.NewLine));
        Console.SetOut(TextWriter.Null);
        try
        {
            var pending = config.OnPermissionRequest(new PermissionRequestShell
            {
                CanOfferSessionApproval = false,
                Commands = [],
                FullCommandText = "echo offline",
                HasWriteFileRedirection = false,
                Intention = "Test approval without executing a command",
                PossiblePaths = [],
                PossibleUrls = [],
            }, context);
            Assert.False(pending.IsCompleted);
            var answer = await approval.PumpOneAsync(timeout.Token);
            Assert.True(answer.Handled);
            Assert.False(answer.Approved);
            Assert.Equal(JsonSerializer.Serialize(PermissionDecision.Reject(feedback: null)),
                JsonSerializer.Serialize(await pending));
        }
        finally
        {
            Console.SetIn(previousInput);
            Console.SetOut(previousOutput);
        }
    }

    private static SessionConfig CreateConfig(
        RecoveryTrigger trigger, string style, ConsoleApprovalPrompt approval, HttpClient http, CancellationToken ct)
    {
        var options = new AgentOptions();
        options.Recovery.Style = style;
        var settings = RecoverySessionSettings.Resolve(options, trigger);
        var auth = new StubAuthService();
        var profiles = new StubProfileService(seedDefault: false);
        options.Provider = "github";
        return ChatAgent.BuildSessionConfigAsync(
            options, auth, "offline test", new CachedResourceTools(new ArmDetailsClient(auth), profiles),
            new HelpTools("unused-az", new AgentTestConfiguration()), new AvailableLocationsTool(auth, profiles, http),
            new SubmitResponseTool(), new ProposePlanTool(), "unused-az", profiles, approval, new EventRenderer(),
            new SessionState { IsOneShot = !settings.IsInteractive }, null,
            settings.IsInteractive ? "inline" : "oneshot",
            new AgentInvocationContext(session: null, new StubTelemetryService(), lifetime: null, lifetimeToken: ct),
            ct).GetAwaiter().GetResult();
    }
}
