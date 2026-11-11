// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
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
public sealed class AgentNativeGraphTests
{
    [Fact]
    public void ContextDisposedMidSend_DoesNotStopAnActivityOwnedByAnotherOperation()
    {
        using var trace = new AgentTraceCapture();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        var context = new AgentInvocationContext(session, trace.Telemetry);

        var send = context.StartOperation("Agent.Send");
        var activity = send.Activity!;
        Assert.False(activity.IsStopped);

        context.Dispose();

        Assert.False(activity.IsStopped);
        Assert.Empty(trace.Stopped);

        // The layer owns deadline records; only the actual send owns its Stop.
        send.Dispose();
        send.Dispose();
        Assert.True(activity.IsStopped);
        Assert.Single(trace.Stopped);
    }

    [Fact]
    public void SessionIsAnIndependentNativeRoot_NotTheRecoveryRootChild()
    {
        using var trace = new AgentTraceCapture();
        using var recoveryScope = trace.Telemetry.StartIndependentRootActivity("CLI.Activity.AiRecovery");
        var recovery = recoveryScope?.Activity;
        Assert.NotNull(recovery);
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;

        Assert.NotNull(session);
        Assert.NotEqual(recovery.TraceId, session.TraceId);
        AgentTraceCapture.AssertParent(session, null);
        Assert.False(session.HasRemoteParent);
        Assert.Equal("Agent", session.Source.Name);
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        using var create = context.StartOperation("Agent.Create");
        AgentTraceCapture.AssertParent(create.Activity!, session);
    }

    [Fact]
    public void IndependentSession_RestoresTheUnrelatedAmbientOnExit()
    {
        using var trace = new AgentTraceCapture();
        using var unrelatedScope = trace.Telemetry.StartIndependentRootActivity("unrelated");
        var unrelated = unrelatedScope?.Activity;
        using (var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session"))
        {
            var session = sessionScope?.Activity;
            Assert.NotNull(session);
            Assert.NotEqual(unrelated!.TraceId, session.TraceId);
            AgentTraceCapture.AssertParent(session, null);
            Assert.Same(session, Activity.Current);
        }
        Assert.Same(unrelated, Activity.Current);
    }

    [Fact]
    public async Task RealToolCallbacks_RebindStaleCreateContextToSendAndReprompt()
    {
        using var trace = new AgentTraceCapture();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var help = new HelpTools("must-not-run", new AgentTestConfiguration(), trace.StartToolActivity);
        var plan = new ProposePlanTool(activityCreator: trace.StartToolActivity);
        var config = new SessionConfig { Tools = [help.GetCommand, plan.ProposePlan] };
        context.Bind(config);
        var getCommand = config.Tools!.First().GetService<AIFunction>()!;
        ExecutionContext capturedCreate;
        Activity createActivity;
        using (var create = context.StartOperation("Agent.Create"))
        {
            createActivity = create.Activity!;
            capturedCreate = ExecutionContext.Capture()!;
            // Empty input takes the real tool's validation path without launching az.
            await getCommand.InvokeAsync(new AIFunctionArguments { ["name"] = "" });
        }
        Assert.True(createActivity.IsStopped);

        using var turn = trace.Telemetry.StartActivity("Agent.Turn");
        Activity sendActivity;
        using (var send = context.StartOperation("Agent.Send"))
        {
            sendActivity = send.Activity!;
            await AgentTraceCapture.InvokeCapturedAsync(
                capturedCreate, getCommand, new AIFunctionArguments { ["name"] = "" });
            await AgentTraceCapture.InvokeCapturedAsync(
                capturedCreate, getCommand, new AIFunctionArguments { ["name"] = "" });
        }

        Activity repromptSend;
        using (var reprompt = trace.Telemetry.StartActivity("Agent.Reprompt"))
        using (var send = context.StartOperation("Agent.Send"))
        {
            repromptSend = send.Activity!;
            var propose = config.Tools.ElementAt(1).GetService<AIFunction>()!;
            await AgentTraceCapture.InvokeCapturedAsync(capturedCreate, propose,
                new AIFunctionArguments
                {
                    ["issues"] = new[] { "private issue" },
                    ["steps"] = new[] { new StepMeta(1, "private label", "private command") },
                });
        }

        var tools = trace.Stopped.Where(activity => activity.OperationName == "tool.get_command").ToArray();
        Assert.Equal(3, tools.Length);
        AgentTraceCapture.AssertParent(tools[0], createActivity);
        Assert.All(tools[1..], tool =>
            AgentTraceCapture.AssertParent(tool, sendActivity));
        Assert.Equal(3, tools.Select(tool => tool.SpanId).Distinct().Count());
        var proposed = Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.propose_plan");
        AgentTraceCapture.AssertParent(proposed, repromptSend);
        Assert.DoesNotContain(proposed.TagObjects, tag => tag.Value?.ToString()?.Contains("private", StringComparison.Ordinal) == true);
        Assert.Same(turn, Activity.Current);
    }

    [Fact]
    public void Binding_PreservesSdkResolutionAndAllToolMetadata()
    {
        using var trace = new AgentTraceCapture(listen: false);
        using var context = new AgentInvocationContext(null, trace.Telemetry);
        var inner = new ProposePlanTool().ProposePlan;
        var config = new SessionConfig { Tools = [inner] };
        context.Bind(config);

        var bound = Assert.IsAssignableFrom<AIFunction>(Assert.Single(config.Tools!));
        Assert.Same(bound, bound.GetService<AIFunction>());
        Assert.NotSame(inner, bound);
        Assert.Equal(inner.Name, bound.Name);
        Assert.Equal(inner.Description, bound.Description);
        Assert.Equal(inner.JsonSchema.GetRawText(), bound.JsonSchema.GetRawText());
        Assert.Equal(inner.ReturnJsonSchema?.GetRawText(), bound.ReturnJsonSchema?.GetRawText());
        Assert.Same(inner.JsonSerializerOptions, bound.JsonSerializerOptions);
        Assert.Same(inner.UnderlyingMethod, bound.UnderlyingMethod);
        Assert.Same(inner.AdditionalProperties, bound.AdditionalProperties);
    }

    [Fact]
    public async Task HooksAndEvents_RebindCapturedContextWithoutChangingResults()
    {
        using var trace = new AgentTraceCapture();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var startResult = new SessionStartHookOutput { AdditionalContext = "private startup context" };
        var promptResult = new UserPromptSubmittedHookOutput { AdditionalContext = "private prompt context" };
        var preResult = new PreToolUseHookOutput();
        var invocation = new HookInvocation { SessionId = "offline" };
        var config = new SessionConfig
        {
            Hooks = new SessionHooks
            {
                OnSessionStart = async (_, actual) =>
                {
                    Assert.Same(invocation, actual);
                    using var tool = ToolInvocationLog.Start("hook_start", activityCreator: trace.StartToolActivity);
                    await Task.Yield();
                    return startResult;
                },
                OnUserPromptSubmitted = async (_, actual) =>
                {
                    Assert.Same(invocation, actual);
                    using var tool = ToolInvocationLog.Start("hook_prompt", activityCreator: trace.StartToolActivity);
                    await Task.Yield();
                    return promptResult;
                },
                OnPreToolUse = async (_, actual) =>
                {
                    Assert.Same(invocation, actual);
                    using var tool = ToolInvocationLog.Start("hook_pre", activityCreator: trace.StartToolActivity);
                    await Task.Yield();
                    return preResult;
                },
            },
            OnEvent = _ =>
            {
                using var tool = ToolInvocationLog.Start("hook_event", activityCreator: trace.StartToolActivity);
            },
        };
        context.Bind(config);
        ExecutionContext captured;
        Activity createActivity;
        using (var create = context.StartOperation("Agent.Create"))
        {
            createActivity = create.Activity!;
            captured = ExecutionContext.Capture()!;
            Assert.Same(startResult, await config.Hooks.OnSessionStart!(new SessionStartHookInput(), invocation));
        }
        using var send = context.StartOperation("Agent.Send");
        Task? callbacks = null;
        ExecutionContext.Run(captured.CreateCopy(), _ => callbacks = InvokeHooksAsync(), null);
        await callbacks!;

        var startup = Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.hook_start");
        Assert.Equal(createActivity.SpanId, startup.ParentSpanId);
        foreach (var name in new[]
        {
            "tool.hook_prompt", "tool.hook_pre", "tool.hook_event",
        })
        {
            var activity = Assert.Single(trace.Stopped, activity => activity.OperationName == name);
            AgentTraceCapture.AssertParent(activity, send.Activity);
            Assert.DoesNotContain(activity.TagObjects, tag => tag.Value?.ToString()?.Contains("private", StringComparison.Ordinal) == true);
        }

        async Task InvokeHooksAsync()
        {
            Assert.Same(promptResult, await config.Hooks.OnUserPromptSubmitted!(
                new UserPromptSubmittedHookInput { Prompt = "private prompt" }, invocation));
            Assert.Same(preResult, await config.Hooks.OnPreToolUse!(new PreToolUseHookInput(), invocation));
            config.OnEvent(new SessionIdleEvent { Data = new SessionIdleData() });
            Assert.Same(createActivity, Activity.Current);
        }
    }

    [Fact]
    public async Task NestedBoundTools_KeepTheImmediateToolParent()
    {
        using var trace = new AgentTraceCapture();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var inner = new HelpTools("must-not-run", new AgentTestConfiguration(), trace.StartToolActivity).GetCommand;
        AIFunction? boundInner = null;
        var outer = AIFunctionFactory.Create(async () =>
        {
            using var scope = ToolInvocationLog.Start("outer", activityCreator: trace.StartToolActivity);
            return await boundInner!.InvokeAsync(new AIFunctionArguments { ["name"] = "" });
        });
        var config = new SessionConfig { Tools = [inner, outer] };
        context.Bind(config);
        boundInner = config.Tools!.First().GetService<AIFunction>()!;
        using var send = context.StartOperation("Agent.Send");

        await config.Tools.Last().GetService<AIFunction>()!.InvokeAsync(new AIFunctionArguments());

        var outerSpan = Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.outer");
        var innerSpan = Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.get_command");
        AgentTraceCapture.AssertParent(outerSpan, send.Activity);
        AgentTraceCapture.AssertParent(innerSpan, outerSpan);
    }

    [Fact]
    public async Task CompletedCallbackContext_DoesNotLeakItsParentOrCancelledTokenToTheNextSend()
    {
        using var trace = new AgentTraceCapture();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        using var oldCancellation = new CancellationTokenSource();
        ExecutionContext? captured = null;
        var function = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var tool = ToolInvocationLog.Start("current", activityCreator: trace.StartToolActivity);
            await Task.Yield();
            return "current turn";
        });
        var config = new SessionConfig
        {
            Tools = [function],
            OnEvent = _ => captured = ExecutionContext.Capture(),
        };
        context.Bind(config);
        using (context.StartOperation("Agent.Create", oldCancellation.Token))
            config.OnEvent!(new SessionIdleEvent { Data = new SessionIdleData() });
        oldCancellation.Cancel();
        using var send = context.StartOperation("Agent.Send");

        var result = await AgentTraceCapture.InvokeCapturedAsync(
            captured!, config.Tools!.Single().GetService<AIFunction>()!, new AIFunctionArguments());

        Assert.Equal("current turn", result?.ToString());
        AgentTraceCapture.AssertParent(
            Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.current"), send.Activity);
    }

    [Fact]
    public async Task InFlightCallback_DoesNotAdoptALaterSendsParentOrCancellation()
    {
        using var trace = new AgentTraceCapture();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AIFunction? nested = null;
        var outer = AIFunctionFactory.Create(async () =>
        {
            using var scope = ToolInvocationLog.Start("outer", activityCreator: trace.StartToolActivity);
            entered.SetResult();
            await release.Task;
            return await nested!.InvokeAsync(new AIFunctionArguments { ["name"] = "" });
        });
        var config = new SessionConfig { Tools = [new HelpTools("must-not-run", new AgentTestConfiguration(), trace.StartToolActivity).GetCommand, outer] };
        context.Bind(config);
        nested = config.Tools!.First().GetService<AIFunction>()!;
        using var first = context.StartOperation("Agent.Send");
        var call = config.Tools.Last().GetService<AIFunction>()!.InvokeAsync(new AIFunctionArguments()).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        first.Dispose();
        using var laterCancellation = new CancellationTokenSource();
        laterCancellation.Cancel();
        using var later = context.StartOperation("Agent.Send", laterCancellation.Token);

        release.SetResult();
        await call.WaitAsync(TimeSpan.FromSeconds(60));

        var outerSpan = Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.outer");
        var nestedSpan = Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.get_command");
        Assert.Equal(first.Activity!.SpanId, outerSpan.ParentSpanId);
        AgentTraceCapture.AssertParent(nestedSpan, outerSpan);
        Assert.NotEqual(later.Activity!.SpanId, nestedSpan.ParentSpanId);
        Assert.Same(later.Activity, Activity.Current);
    }

    [Fact]
    public async Task ParallelToolsAndIndependentSessions_KeepExplicitParentsAndNestedWork()
    {
        using var trace = new AgentTraceCapture();
        using var sessionAScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var sessionA = sessionAScope?.Activity;
        using var contextA = new AgentInvocationContext(sessionA, trace.Telemetry);
        using var sessionBScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var sessionB = sessionBScope?.Activity;
        using var contextB = new AgentInvocationContext(sessionB, trace.Telemetry);
        Assert.NotEqual(sessionA!.TraceId, sessionB!.TraceId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ConcurrentDictionary<string, Activity>();
        int count = 0;
        var inner = AIFunctionFactory.Create(async (string key, CancellationToken ct) =>
        {
            using var tool = ToolInvocationLog.Start("parallel", "private args", trace.StartToolActivity);
            observed[key] = Activity.Current!;
            if (Interlocked.Increment(ref count) == 4) entered.SetResult();
            await release.Task.WaitAsync(ct);
            using var nested = trace.Telemetry.StartActivity("MsalAuthService.AcquireTokenSilentlyAsync");
            AgentTraceCapture.AssertParent(nested!, observed[key]);
            return key;
        });
        var configA = new SessionConfig { Tools = [inner] };
        var configB = new SessionConfig { Tools = [inner] };
        contextA.Bind(configA);
        contextB.Bind(configB);
        using var sendA = contextA.StartOperation("Agent.Send");
        using var sendB = contextB.StartOperation("Agent.Send");
        var functionA = configA.Tools!.Single().GetService<AIFunction>()!;
        var functionB = configB.Tools!.Single().GetService<AIFunction>()!;
        var calls = new[]
        {
            functionA.InvokeAsync(new AIFunctionArguments { ["key"] = "a1" }).AsTask(),
            functionA.InvokeAsync(new AIFunctionArguments { ["key"] = "a2" }).AsTask(),
            functionB.InvokeAsync(new AIFunctionArguments { ["key"] = "b1" }).AsTask(),
            functionB.InvokeAsync(new AIFunctionArguments { ["key"] = "b2" }).AsTask(),
        };
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.All(observed.Where(pair => pair.Key.StartsWith('a')), pair =>
                Assert.Equal(sendA.Activity!.SpanId, pair.Value.ParentSpanId));
            Assert.All(observed.Where(pair => pair.Key.StartsWith('b')), pair =>
                Assert.Equal(sendB.Activity!.SpanId, pair.Value.ParentSpanId));
            Assert.Equal(4, observed.Values.Select(activity => activity.SpanId).Distinct().Count());
            Assert.Equal(sessionA!.SpanId, sendA.Activity!.ParentSpanId);
            Assert.Equal(sessionB!.SpanId, sendB.Activity!.ParentSpanId);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionAndCancellation_ReleaseBindingsAndKeepLaterSendsCorrect(bool cancel)
    {
        using var trace = new AgentTraceCapture();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = AIFunctionFactory.Create((Func<CancellationToken, Task<string>>)(async ct =>
        {
            using var tool = ToolInvocationLog.Start("failure", activityCreator: trace.StartToolActivity);
            entered.TrySetResult();
            await Task.Yield();
            if (cancel)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("expected test failure");
        }));
        var config = new SessionConfig { Tools = [inner] };
        context.Bind(config);
        var bound = config.Tools!.Single().GetService<AIFunction>()!;
        using (context.StartOperation("Agent.Send", cancellation.Token))
        {
            var invocation = bound.InvokeAsync(new AIFunctionArguments()).AsTask();
            await entered.Task;
            cancellation.Cancel();
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => invocation);
        }
        Assert.Same(session, Activity.Current);
        using (var later = context.StartOperation("Agent.Send"))
        {
            Assert.Equal(session!.SpanId, later.Activity!.ParentSpanId);
            Assert.Throws<InvalidOperationException>(() => context.StartOperation("Agent.Send"));
        }
        context.Dispose();
        context.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bound.InvokeAsync(new AIFunctionArguments()).AsTask());
        Assert.Throws<ObjectDisposedException>(() => context.StartOperation("Agent.Send"));
        Assert.Same(session, Activity.Current);
        Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.failure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolCancellation_HonorsBothOperationAndCallbackTokens(bool cancelCallback)
    {
        using var trace = new AgentTraceCapture();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        using var operationCancellation = new CancellationTokenSource();
        using var callbackCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            using var tool = ToolInvocationLog.Start("wait", activityCreator: trace.StartToolActivity);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "";
        });
        var config = new SessionConfig { Tools = [function] };
        context.Bind(config);
        using var send = context.StartOperation("Agent.Send", operationCancellation.Token);
        var call = config.Tools!.Single().GetService<AIFunction>()!
            .InvokeAsync(new AIFunctionArguments(), callbackCancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));

        (cancelCallback ? callbackCancellation : operationCancellation).Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Same(send.Activity, Activity.Current);
    }

    [Fact]
    public async Task IdleCallbacks_UseSessionInsteadOfCompletedCreate()
    {
        using var trace = new AgentTraceCapture();
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var config = new SessionConfig { Tools = [new HelpTools("must-not-run", new AgentTestConfiguration(), trace.StartToolActivity).GetCommand] };
        context.Bind(config);
        ExecutionContext captured;
        using (context.StartOperation("Agent.Create"))
            captured = ExecutionContext.Capture()!;

        await AgentTraceCapture.InvokeCapturedAsync(
            captured, config.Tools!.Single().GetService<AIFunction>()!, new AIFunctionArguments { ["name"] = "" });

        var tool = Assert.Single(trace.Stopped, activity => activity.OperationName == "tool.get_command");
        AgentTraceCapture.AssertParent(tool, session);
    }

    [Fact]
    public async Task NoTelemetryListener_StillExecutesToolsWithoutSpansOrLeakedAmbientState()
    {
        using var trace = new AgentTraceCapture(listen: false);
        Assert.False(trace.Source.HasListeners());
        using var context = new AgentInvocationContext(null, trace.Telemetry);
        var config = new SessionConfig { Tools = [new HelpTools("must-not-run", new AgentTestConfiguration(), trace.StartToolActivity).GetCommand] };
        context.Bind(config);
        using var operation = context.StartOperation("Agent.Send");

        var result = await config.Tools!.Single().GetService<AIFunction>()!
            .InvokeAsync(new AIFunctionArguments { ["name"] = "" });

        Assert.Null(operation.Activity);
        Assert.Contains("missing_name", result!.ToString());
        Assert.Null(Activity.Current);
    }

    [Fact]
    public void ToolScope_DisposesOnlyOnce()
    {
        using var trace = new AgentTraceCapture();
        var name = "dispose_" + Guid.NewGuid().ToString("N");
        var scope = ToolInvocationLog.Start(name, activityCreator: trace.StartToolActivity);
        scope.Dispose();
        scope.Dispose();
        Assert.Single(trace.Stopped);
        Assert.Single(ToolInvocationLog.Snapshot(), invocation => invocation.Name == name);
    }

    [Fact]
    public async Task RejectedSend_LeavesTheSlotsCancellationHandleUnchanged()
    {
        // A send that loses the race for the slot must not write anything shared on its way out.
        // The handle an abort reaches is the one carried by whatever is in the slot, so a rejected
        // caller that lands there hands abort the handle of a turn that never started.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // Deliberately unrelated to `timeout`: if the live send shared the test's own budget, a
        // reverted fix would still end it -- on that budget -- and the red would be a timeout
        // rather than an assertion.
        using var liveBudget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer { Calls = [new("wait", new { })] };
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var agent = await ConnectOfflineAsync(
            server, context, BlockingTool(entered, release.Task, trace.StartToolActivity), timeout.Token);

        var live = agent.SendAsync("hold the slot", liveBudget.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var reserved = context.CurrentTurnCts;
        Assert.NotNull(reserved);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SendAsync("overlap", timeout.Token));
        Assert.Contains("one in-flight operation", rejected.Message);

        Assert.Same(reserved, context.CurrentTurnCts);

        release.SetResult();
        await live;
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task DisposeWhileSendInFlight_ClosesTheSendItself()
    {
        // Cancellation, not a transport fault or a cross-thread context Stop, ends the send.
        // Actual layer tests separately cover uncooperative cleanup and its shared deadline.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var liveBudget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer { Calls = [new("wait", new { })] };
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = await ConnectOfflineAsync(
            server, context, BlockingTool(entered, never.Task, trace.StartToolActivity), timeout.Token);

        var sending = agent.SendAsync("hold the slot", liveBudget.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var sendActivity = context.CurrentOperation!.Activity!;

        await agent.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sending.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(sendActivity.IsStopped);
        Assert.Null(context.CurrentOperation);
        Assert.DoesNotContain("Abandoned", sendActivity.StatusDescription ?? "");
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task SessionError_CancelsTheLiveSend()
    {
        // The SessionErrored branch is the third reader of the turn's cancellation source and the
        // only one the host cannot reach directly, so it needs its own coverage. The tell is the
        // exception type, not the timing: without the cancel the turn still ends promptly, because
        // the SDK faults SendAndWaitAsync itself with InvalidOperationException("Session error:
        // ..."). With the cancel, Cancel() completes the send's task synchronously from inside the
        // OnEvent callback, before the SDK gets to fault it, so the turn unwinds as a cancellation.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var liveBudget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer
        {
            // A tool that never returns, so session.idle cannot fire and nothing but the error can
            // end this turn.
            Calls = [new("wait", new { })],
            SessionErrorAfterDispatch = "offline session error",
        };
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ChatAgent? self = null;
        await using var agent = await ConnectOfflineAsync(
            server, context, BlockingTool(entered, never.Task, trace.StartToolActivity), timeout.Token,
            // The production handler itself, not a hand-rolled equivalent. Installing a copy here
            // would prove only that the copy cancels: ChatAgent.CreateAsync needs a live daemon, so
            // the real call site would keep its zero coverage and a rebase could drop the branch
            // with every test still green.
            onAgentEvent: ev => ChatAgent.HandleAgentEvent(
                ev, self, _ => { }, spinner: null, renderer: new SilentRenderer()));
        self = agent;

        var live = agent.SendAsync("this turn fails", liveBudget.Token);
        var sw = Stopwatch.StartNew();
        var settled = await Task.WhenAny(live, Task.Delay(TimeSpan.FromSeconds(60), timeout.Token));
        sw.Stop();

        Assert.True(ReferenceEquals(settled, live),
            $"the turn never ended at all: still running after {sw.Elapsed}. This guard only "
            + "converts a hang into an assertion; the cancellation check below is the real one.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live);
        Assert.Equal("offline session error", agent.LastError?.Message);
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task OperationLeavesTheSlotBeforeItsCtsIsDisposed()
    {
        // CancelCurrentOperation reads the slot under the lock and cancels outside it, which is
        // only safe because the source outlives the reservation that carries it. Dispose the source
        // first and the window inverts: the slot is still occupied while its handle is already
        // dead, so an abort lands on a disposed source and silently does nothing.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var trace = new AgentTraceCapture();
        await using var server = new OfflineCopilotServer { Calls = [new("wait", new { })] };
        using var sessionScope = trace.Telemetry.StartIndependentRootActivity("Agent.Session");
        var session = sessionScope?.Activity;
        using var context = new AgentInvocationContext(session, trace.Telemetry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var agent = await ConnectOfflineAsync(
            server, context, BlockingTool(entered, release.Task, trace.StartToolActivity), timeout.Token);

        var live = agent.SendAsync("one turn", timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var turnCts = context.CurrentTurnCts;
        Assert.NotNull(turnCts);

        bool? usableOnceTheSlotWasClear = null;
        context.CurrentOperation!.Disposed += () =>
        {
            // Cancel is the probe, not CanBeCanceled: that stays true after Dispose, so it passes
            // under the reverted order too. Cancel is also what CancelCurrentOperation actually
            // calls, and cancelling a turn that is already finishing is harmless.
            try { turnCts.Cancel(); usableOnceTheSlotWasClear = true; }
            catch (ObjectDisposedException) { usableOnceTheSlotWasClear = false; }
        };

        release.SetResult();
        await live;

        Assert.True(usableOnceTheSlotWasClear,
            "At the moment the operation left the slot its cancellation source was already "
            + "disposed, so an abort that read the slot a moment earlier would have cancelled "
            + "nothing. SendAsync must dispose the operation before the source.");
        server.ThrowIfFaulted();
    }

    private static AIFunction BlockingTool(
        TaskCompletionSource entered, Task release, Func<string, IDisposable?> activityCreator) =>
        AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            using var tool = ToolInvocationLog.Start("wait", activityCreator: activityCreator);
            entered.TrySetResult();
            await release.WaitAsync(ct);
            return "";
        }, new AIFunctionFactoryOptions { Name = "wait" });

    private static async Task<ChatAgent> ConnectOfflineAsync(
        OfflineCopilotServer server, AgentInvocationContext context, AIFunction tool,
        CancellationToken ct, Action<AgentEvent>? onAgentEvent = null)
    {
        var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForUri(server.Url),
        });
        var config = new SessionConfig
        {
            SessionId = Guid.NewGuid().ToString(),
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = [tool],
        };
        // Same translation the host installs: the SDK's SessionErrorEvent only becomes a
        // SessionErrored through EventRenderer, and a hand-rolled stand-in would not exercise the
        // required-field deserialisation the fake's payload has to satisfy.
        if (onAgentEvent is not null)
            config.OnEvent = new EventRenderer { OnEvent = onAgentEvent }.Forward;
        using (context.StartOperation("Agent.Create"))
            return await ChatAgent.ConnectAsync(client, config, "offline-model", null, context, ct);
    }
}
