// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using Core.Abstractions;
using Core.Agent.Hosting;
using Core.Agent.Telemetry;
using Core.Agent.Tools.Help;
using Core.Agent.Tools.Resource.Cache;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Xunit;

namespace Core.Agent.Tests;

[Collection("AgentTelemetry")]
public sealed class SdkAgentNativeGraphTests
{
    [Fact]
    public async Task StartupError_RemainsSeparateFromTheSuccessfulFinalTurn()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });
        await using var agent = await ChatAgent.ConnectAsync(client, new SessionConfig
        {
            SessionId = Guid.NewGuid().ToString(),
            OnPermissionRequest = PermissionHandler.ApproveAll,
        }, "offline-model", null, context, timeout.Token);
        var startup = new SessionErrored("authentication", "offline startup", null, 401, null, DateTimeOffset.UtcNow);

        agent.OnSessionErrored(startup);
        Assert.Null(agent.LastError);
        Assert.Same(startup, agent.StartupError);
        await agent.SendAsync("successful offline turn", timeout.Token);

        Assert.Null(agent.LastError);
        Assert.Same(startup, agent.StartupError);
        Core.Agent.AgentMode.AgentModeEntry.ThrowIfSessionEndedInError(agent.LastError, timeout.Token);
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task RealSdkParallelCallbacks_PreserveTheirNestedToolParents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer
        {
            Calls = [new("parallel", new { }), new("parallel", new { })],
        };
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        AIFunction? nested = null;
        var outer = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            using var timing = ToolInvocationLog.Start("parallel", activityCreator: trace.StartToolActivity);
            if (Interlocked.Increment(ref count) == 2) entered.SetResult();
            await release.Task.WaitAsync(ct);
            return await nested!.InvokeAsync(new AIFunctionArguments { ["name"] = "" }, ct);
        }, new AIFunctionFactoryOptions { Name = "parallel" });
        var config = new SessionConfig
        {
            SessionId = Guid.NewGuid().ToString(),
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = [new HelpTools("must-not-run", new AgentTestConfiguration(), trace.StartToolActivity).GetCommand, outer],
        };
        var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });
        ChatAgent agent;
        using (context.StartOperation("Agent.Create"))
            agent = await ChatAgent.ConnectAsync(client, config, "offline-model", null, context, timeout.Token);
        await using var agentScope = agent;
        nested = config.Tools!.First().GetService<AIFunction>()!;
        var sending = agent.SendAsync("offline parallel tools", timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            release.TrySetResult();
        }
        await sending;
        var send = Assert.Single(trace.Stopped, activity => activity.OperationName == "Agent.Send");
        var outers = trace.Stopped.Where(activity => activity.OperationName == "tool.parallel").ToArray();
        var inners = trace.Stopped.Where(activity => activity.OperationName == "tool.get_command").ToArray();
        Assert.Equal(2, outers.Length);
        Assert.Equal(2, inners.Length);
        Assert.All(outers, activity => AgentTraceCapture.AssertParent(activity, send));
        Assert.All(inners, activity => AgentTraceCapture.AssertParent(activity,
            Assert.Single(outers, outerActivity => outerActivity.SpanId == activity.ParentSpanId)));
        Assert.Equal(2, inners.Select(activity => activity.ParentSpanId).Distinct().Count());
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task RealSdkDispatch_ParentsRegisteredToolsToChatAgentSendNotCreate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer
        {
            Calls =
            [
                new("get_command", new { name = "" }),
                new("get_command", new { name = "" }),
                new("propose_plan", new
                {
                    issues = new[] { "private issue" },
                    steps = new[] { new { id = 1, label = "private label", cmd = "private command" } },
                }),
            ],
        };
        using var recoveryScope = trace.Telemetry.StartIndependentRootActivity("CLI.Activity.AiRecovery");
        var recovery = recoveryScope?.Activity;
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });
        var config = new SessionConfig
        {
            SessionId = Guid.NewGuid().ToString(),
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = [new HelpTools("must-not-run", new AgentTestConfiguration(), trace.StartToolActivity).GetCommand,
                new ProposePlanTool(activityCreator: trace.StartToolActivity).ProposePlan],
        };
        ChatAgent agent;
        Activity createActivity;
        using (var create = context.StartOperation("Agent.Create"))
        {
            createActivity = create.Activity!;
            agent = await ChatAgent.ConnectAsync(client, config, "offline-model", null, context, timeout.Token);
        }
        await using var agentScope = agent;
        Assert.True(createActivity.IsStopped);

        using var turn = trace.Telemetry.StartActivity("Agent.Turn");
        await agent.SendAsync("private prompt", timeout.Token);
        await agent.SendAsync("second private prompt", timeout.Token);

        Assert.Equal(6, server.ToolResults.Count);
        var sends = trace.Stopped.Where(activity => activity.OperationName == "Agent.Send").ToArray();
        Assert.Equal(2, sends.Length);
        Assert.All(sends, send => AgentTraceCapture.AssertParent(send, turn));
        var tools = trace.Stopped.Where(activity => activity.OperationName.StartsWith("tool.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(6, tools.Length);
        Assert.All(tools, tool =>
        {
            Assert.Equal(session!.TraceId, tool.TraceId);
            Assert.NotEqual(recovery!.TraceId, tool.TraceId);
            Assert.NotEqual(createActivity.SpanId, tool.ParentSpanId);
        });
        var lookups = tools.Where(tool => tool.OperationName == "tool.get_command").ToArray();
        Assert.Equal(4, lookups.Length);
        Assert.Equal(2, tools.Count(tool => tool.OperationName == "tool.propose_plan"));
        Assert.All(tools[..3], tool => AgentTraceCapture.AssertParent(tool, sends[0]));
        Assert.All(tools[3..], tool => AgentTraceCapture.AssertParent(tool, sends[1]));
        Assert.Equal(6, tools.Select(activity => activity.SpanId).Distinct().Count());
        Assert.All(sends, send =>
        {
            Assert.Equal("offline-model", send.GetTagItem(TelemetryTags.AgentModel));
            Assert.Equal("Agent", send.Source.Name);
        });
        Assert.Same(turn, Activity.Current);
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task RealSdkSendFailureAndDispose_ClearOperationState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer { RejectNextSend = true };
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });
        ChatAgent agent;
        using (context.StartOperation("Agent.Create"))
        {
            agent = await ChatAgent.ConnectAsync(client, new SessionConfig
            {
                SessionId = Guid.NewGuid().ToString(),
                OnPermissionRequest = PermissionHandler.ApproveAll,
            }, "", null, context, timeout.Token);
        }
        await using var agentScope = agent;

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => agent.SendAsync("first", timeout.Token));
        Assert.Contains("offline send failure", failure.Message);
        Assert.Same(session, Activity.Current);
        await agent.SendAsync("second", timeout.Token);
        var sends = trace.Stopped.Where(activity => activity.OperationName == "Agent.Send").ToArray();
        Assert.Equal(2, sends.Length);
        Assert.All(sends, send => AgentTraceCapture.AssertParent(send, session));
        Assert.NotEqual(sends[0].SpanId, sends[1].SpanId);

        await agent.DisposeAsync();
        await agent.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.SendAsync("after disposal", timeout.Token));
        Assert.Equal(2, server.SendCount);
        Assert.Same(session, Activity.Current);
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task RejectedOverlappingSend_LeavesTheLiveSendCancellable()
    {
        // A second SendAsync loses the race for the single operation slot and is rejected. It must
        // leave nothing of itself behind: if it can replace what the live send published, Esc and
        // disposal both go nowhere and the turn runs to completion uncancelled.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // Deliberately not `timeout`. When the live send and the test's own waits shared one token,
        // a reverted fix still ended the live send -- via that shared token, once the shared budget
        // expired. The test went red on a timeout rather than on an assertion, and was recorded as
        // validated. These two must stay distinct or this test stops testing anything.
        using var liveBudget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Assert.NotEqual(liveBudget.Token, timeout.Token);
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer { Calls = [new("wait", new { })] };
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            using var tool = ToolInvocationLog.Start("wait", activityCreator: trace.StartToolActivity);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "";
        }, new AIFunctionFactoryOptions { Name = "wait" });
        var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });
        ChatAgent agent;
        using (context.StartOperation("Agent.Create"))
        {
            agent = await ChatAgent.ConnectAsync(client, new SessionConfig
            {
                SessionId = Guid.NewGuid().ToString(),
                OnPermissionRequest = PermissionHandler.ApproveAll,
                Tools = [wait],
            }, "", null, context, timeout.Token);
        }
        await using var agentScope = agent;

        var live = agent.SendAsync("hold the slot", liveBudget.Token);
        await entered.Task.WaitAsync(timeout.Token);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SendAsync("overlap", timeout.Token));
        Assert.Contains("one in-flight operation", rejected.Message);

        // The live send must still answer to abort, and it must do so on abort's own budget -- not
        // on its own, which is why the two tokens above are unrelated.
        var sw = Stopwatch.StartNew();
        await agent.AbortAsync(timeout.Token);
        var settled = await Task.WhenAny(live, Task.Delay(TimeSpan.FromSeconds(60), timeout.Token));
        sw.Stop();
        Assert.True(ReferenceEquals(settled, live),
            $"The live send was still running {sw.Elapsed} after AbortAsync returned: the rejected "
            + "overlapping send disarmed the turn that owns the slot.");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live);
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task Abort_WhenIdleCompletesTheWaitFirst_StillCancelsTheSend()
    {
        // Esc cancels the running tool; the server can then answer session.idle before the SDK's
        // own cancel callback completes the wait. The turn must still end as cancelled.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer { Calls = [new("wait", new { })] };
        using var context = new AgentInvocationContext(null, trace.Telemetry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = AIFunctionFactory.Create(async () =>
        {
            entered.SetResult();
            await release.Task;
            return "";
        }, new AIFunctionFactoryOptions { Name = "wait" });
        var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });
        ChatAgent agent;
        using (context.StartOperation("Agent.Create"))
        {
            agent = await ChatAgent.ConnectAsync(client, new SessionConfig
            {
                SessionId = Guid.NewGuid().ToString(),
                OnPermissionRequest = PermissionHandler.ApproveAll,
                Tools = [wait],
            }, "", null, context, timeout.Token);
        }
        await using var agentScope = agent;

        var live = agent.SendAsync("go", CancellationToken.None);
        await entered.Task.WaitAsync(timeout.Token);

        // Cancellation callbacks run in reverse registration order, so this one runs before the
        // SDK's: it lets the tool finish and holds the cancelling thread until idle ends the wait.
        var idleWonTheRace = false;
        context.CurrentTurnCts!.Token.Register(() =>
        {
            release.SetResult();
            idleWonTheRace = live.ContinueWith(_ => { }, TaskScheduler.Default).Wait(TimeSpan.FromSeconds(60));
        });
        await agent.AbortAsync(timeout.Token);

        Assert.True(idleWonTheRace);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live);
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task RealSdkCancellation_ReachesToolAndReleasesSendScope()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer { Calls = [new("wait", new { })] };
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            using var tool = ToolInvocationLog.Start("wait", activityCreator: trace.StartToolActivity);
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "";
            }
            finally
            {
                exited.SetResult();
            }
        }, new AIFunctionFactoryOptions { Name = "wait" });
        var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });
        ChatAgent agent;
        using (context.StartOperation("Agent.Create"))
        {
            agent = await ChatAgent.ConnectAsync(client, new SessionConfig
            {
                SessionId = Guid.NewGuid().ToString(),
                OnPermissionRequest = PermissionHandler.ApproveAll,
                Tools = [wait],
            }, "", null, context, timeout.Token);
        }
        await using var agentScope = agent;
        var sending = agent.SendAsync("cancel this turn", cancel.Token);
        await entered.Task.WaitAsync(timeout.Token);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await exited.Task.WaitAsync(timeout.Token);
        await server.FirstToolHandled.WaitAsync(timeout.Token);
        var toolSpan = Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.wait");
        var send = Assert.Single(trace.Stopped, activity => activity.OperationName == "Agent.Send");
        AgentTraceCapture.AssertParent(toolSpan, send);
        Assert.Same(session, Activity.Current);
        server.ThrowIfFaulted();
    }
}
