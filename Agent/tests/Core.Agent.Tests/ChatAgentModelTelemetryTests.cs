// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Reflection;
using Core.Abstractions;
using Core.Agent.AgentMode;
using Core.Agent.Hosting;
using Core.Agent.Providers;
using Core.Agent.Telemetry;
using Core.Telemetry;
using GitHub.Copilot;
using Tests.Common.Stubs;
using Tests.Common.Utilities;

namespace Core.Agent.Tests;

[Collection("AgentTelemetry")]
public class ChatAgentModelTelemetryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ModelQueryBudget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("user")]
    public async Task Github_RecordsSdkModelBeforeSendStops_WithoutSelectingAModel(string? initiator)
    {
        using var capture = new SendCapture();
        await using var runtime = Server([[Usage("github-model", initiator: initiator), Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            using var parent = new Activity("test-parent").Start();

            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            var stopped = Assert.Single(capture.Stopped);
            Assert.Equal("github-model", stopped.Model);
            Assert.Equal(default, stopped.Span.ParentSpanId);
            Assert.NotEqual(parent.TraceId, stopped.Span.TraceId);
            Assert.Null(parent.GetTagItem(TelemetryTags.AgentModel));
            Assert.False(runtime.CreateOptions.TryGetProperty("model", out _));
        });
    }

    [Fact]
    public async Task Builtin_PreservesConfiguredModel()
    {
        using var capture = new SendCapture();
        var queries = ModelQueries.Selected("unused-model");
        await using var runtime = Server([[Usage("wire-model"), Idle()]], queries);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal("custom-builtin-deployment", Assert.Single(capture.Stopped).Model);
            Assert.Equal("custom-builtin-deployment", runtime.CreateOptions.GetProperty("model").GetString());
            Assert.Equal(0, queries.Count);
        }, "custom-builtin-deployment");
    }

    [Fact]
    public async Task Builtin_RecordsServedModelWithResponseSource_ForSendAndSession()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([[Usage("wire-model"), Idle()], [Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            var servedModel = Assert.IsType<ServedModel>(rig.Metadata.ServedModel);
            using var served = new HttpResponseMessage(HttpStatusCode.OK);
            served.Headers.TryAddWithoutValidation(ServedModel.HeaderName, "gpt-5.4-mini-2026-03-17");
            // Stands in for the request handler, which records each model response during the turn.
            using var onUsage = rig.Session.On<AssistantUsageEvent>(_ => servedModel.Record(served));

            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);
            await rig.Agent.SendAsync("next prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal(("gpt-5.4-mini-2026-03-17", AgentModelSource.Response.Wire()),
                (capture.Stopped[0].Model, capture.Stopped[0].Source));
            // A turn without a model response keeps the configured model.
            Assert.Equal(("custom-builtin-deployment", AgentModelSource.Configuration.Wire()),
                (capture.Stopped[1].Model, capture.Stopped[1].Source));
            Assert.Empty(rig.Warnings);

            using var session = new Activity("Agent.Session").Start();
            rig.Agent.RecordModel(session);
            Assert.Equal("gpt-5.4-mini-2026-03-17", session.GetTagItem(TelemetryTags.AgentModel));
            Assert.Equal(AgentModelSource.Response.Wire(), session.GetTagItem(TelemetryTags.AgentModelSource));
        }, "custom-builtin-deployment");
    }

    [Fact]
    public async Task Builtin_WithoutServedModel_SessionKeepsConfiguredModel()
    {
        await using var runtime = Server([[Usage("wire-model"), Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            using var session = new Activity("Agent.Session").Start();
            rig.Agent.RecordModel(session);
            Assert.Equal("custom-builtin-deployment", session.GetTagItem(TelemetryTags.AgentModel));
            Assert.Equal(AgentModelSource.Configuration.Wire(), session.GetTagItem(TelemetryTags.AgentModelSource));
        }, "custom-builtin-deployment");
    }

    [Fact]
    public async Task Builtin_TraceUsesUsageModel_WhenTheNextResponseHasNoServedModel()
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var scope = harness.CreateScope();
            await using var runtime = Server([[Usage("usage-model"), Idle()], [Usage("usage-model"), Idle()]]);
            var renderer = new TurnRenderer();
            using var served = new HttpResponseMessage(HttpStatusCode.OK);
            served.Headers.TryAddWithoutValidation(ServedModel.HeaderName, "gpt-5.4-mini-2026-03-17");
            using var missing = new HttpResponseMessage(HttpStatusCode.OK);
            // Stands in for the request handler: the first response has the header, the second has none.
            var responses = new Queue<HttpResponseMessage>([served, missing]);

            var code = await AgentModeEntry.RunSessionAsync(() => scope, async (_, lifetime) =>
            {
                await using var agent = await ChatAgent.CreateAsync("offline", new StubAuthService(),
                    new StubProfileService(seedDefault: false), new AgentTestConfiguration(),
                    new DataBoundaryResolver(new StubPersistenceStore(harness.DirectoryPath), new StubProfileService(seedDefault: false)),
                    "must-not-run",
                    new ConsoleApprovalPrompt(renderer: renderer), renderer,
                    new AgentOptions { Provider = "builtin", Builtin = new BuiltinProviderOptions { ModelId = "configured-model" } },
                    "oneshot", lifetime, preAcquiredAuth: OfflineToken(), dataBoundary: "Global",
                    clientFactory: () => new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(runtime.Url) }))
                    .WaitAsync(TestTimeout);
                var telemetry = (AgentModelTelemetry)typeof(ChatAgent)
                    .GetField("_modelTelemetry", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(agent)!;
                var servedModel = telemetry.ServedModel!;
                runtime.BeforeTurn = () =>
                {
                    servedModel.Record(responses.Dequeue());
                    return Task.CompletedTask;
                };

                await agent.SendAsync("first prompt", CancellationToken.None).WaitAsync(TestTimeout);
                await agent.SendAsync("next prompt", CancellationToken.None).WaitAsync(TestTimeout);
            }, () => { });

            Assert.Equal(0, code);
            Assert.Equal(["gpt-5.4-mini-2026-03-17", "usage-model"], renderer.Models);
            runtime.ThrowIfFaulted();
        });
    }

    [Theory]
    [InlineData("child-agent", null)]
    [InlineData("child-agent", "user")]
    [InlineData(null, "sub-agent")]
    [InlineData(null, "mcp-sampling")]
    public async Task Github_IgnoresSubagentAndAuxiliaryModels(string? agentId, string? initiator)
    {
        using var capture = new SendCapture();
        await using var runtime = Server([
            [Usage("main-model"), Usage("auxiliary-model", agentId, initiator), Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal("main-model", Assert.Single(capture.Stopped).Model);
        });
    }

    [Fact]
    public async Task Github_RecordsLastMainAgentModelWithinSend()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([
            [Usage("first-model"), Usage("fallback-model"), Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal("fallback-model", Assert.Single(capture.Stopped).Model);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // The bound sits at the ingestion site in ChatAgent, not inside RecordModel, so these last two
    // are the only cases that can fail on that line. Reverting it to a null-or-whitespace check
    // leaves the empty cases green -- they are rejected either way -- and lets an over-long or
    // space-bearing name straight through to the exporter.
    [InlineData("model with spaces")]
    [InlineData("overlong")]
    public async Task Github_UnusableUsageDoesNotEraseObservedModel(string unusableModel)
    {
        using var capture = new SendCapture();
        var model = unusableModel == "overlong" ? new string('m', 129) : unusableModel;
        await using var runtime = Server([
            [Usage("main-model"), Usage(model), Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal("main-model", Assert.Single(capture.Stopped).Model);
        });
    }

    [Fact]
    public async Task Github_ModelObservationDoesNotLeakAcrossSends()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([
            [Usage("first-model"), Idle()],
            [Usage(""), Usage("child-model", agentId: "child"), Idle()],
            [Usage("next-model"), Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            for (var i = 0; i < 3; i++)
                await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal(new string?[] { "first-model", null, "next-model" },
                capture.Stopped.Select(send => send.Model));
        });
    }

    [Fact]
    public async Task Github_NoUsageLeavesModelAbsent()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([[Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Null(Assert.Single(capture.Stopped).Model);
        });
    }

    [Fact]
    public async Task SessionErrorAfterSendCompletes_DoesNotBecomeLastError()
    {
        await using var runtime = Server([[Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            rig.Agent.OnSessionErrored(new SessionErrored("rate_limit", "late", ErrorCode: null, StatusCode: 429,
                ProviderCallId: null, At: DateTimeOffset.UtcNow));

            Assert.Null(rig.Agent.LastError);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Github_CancellationPreservesOnlyObservedModel_AndIgnoresLateUsage(bool reportUsage)
    {
        using var capture = new SendCapture();
        var message = new AssistantMessageEvent
        {
            Data = new AssistantMessageData { MessageId = "cancel-boundary", Content = "cancel now" },
        };
        SessionEvent[] cancelledTurn = reportUsage ? [Usage("observed-model"), message] : [message];
        await using var runtime = Server([cancelledTurn, [Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            using var cts = new CancellationTokenSource();
            using var cancelOnMessage = rig.Session.On<AssistantMessageEvent>(_ => cts.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.Agent.SendAsync("test prompt", cts.Token).WaitAsync(TestTimeout));

            var stopped = Assert.Single(capture.Stopped);
            Assert.Equal(reportUsage ? "observed-model" : null, stopped.Model);
            var lateUsageProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var lateUsage = rig.Session.On<AssistantUsageEvent>(_ => lateUsageProcessed.TrySetResult());
            await runtime.EmitAsync(Usage("late-model"));
            await lateUsageProcessed.Task.WaitAsync(TestTimeout);
            Assert.Equal(stopped.Model, stopped.Span.GetTagItem(TelemetryTags.AgentModel));

            await rig.Agent.SendAsync("next prompt", CancellationToken.None).WaitAsync(TestTimeout);
            Assert.Null(capture.Stopped[1].Model);
        });
    }

    [Fact]
    public async Task Github_ErrorPreservesObservedModel_AndDoesNotChangeFailure()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([
            [Usage("observed-model"), new SessionErrorEvent
            {
                Data = new SessionErrorData { ErrorType = "test-error", Message = "test failure" },
            }]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout));

            Assert.Contains("test failure", error.Message);
            Assert.Equal("observed-model", Assert.Single(capture.Stopped).Model);
        });
    }

    [Fact]
    public async Task TelemetryDisabled_SendStillCompletesWithoutActivity()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([[Usage("github-model"), Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Empty(capture.Stopped);
        }, telemetryEnabled: false);
    }

    [Fact]
    public async Task Github_ResolvesSelectedModel_BeforeAnySend()
    {
        using var capture = new SendCapture();
        var queries = ModelQueries.Selected("sdk-default");
        await using var runtime = Server(queries: queries);
        await OfflineChatAgent.RunAsync(runtime, rig =>
        {
            Assert.Equal("sdk-default", rig.Agent.Model);
            Assert.Equal(AgentModelSource.Sdk, rig.Metadata.Current.Source);
            Assert.Equal(1, queries.Count);
            Assert.False(runtime.CreateOptions.TryGetProperty("model", out _));
            Assert.Empty(capture.Stopped);
            Assert.Empty(rig.Warnings);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Github_UsageOverridesSelectedModel_ButIsNotCachedForTheNextSend()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([[Usage("actual-model"), Idle()], [Idle()]], ModelQueries.Selected("sdk-default"));
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("first prompt", CancellationToken.None).WaitAsync(TestTimeout);
            await rig.Agent.SendAsync("second prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal(new[] { "actual-model", "sdk-default" }, capture.Stopped.Select(span => span.Model));
            Assert.Equal(new[] { "usage", "sdk" }, capture.Stopped.Select(span => span.Source));
            Assert.Equal("sdk-default", rig.Agent.Model);
        });
    }

    [Fact]
    public async Task Github_CancellationBeforeUsage_PreservesTheSelectedModel()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([[new AssistantMessageEvent
        {
            Data = new AssistantMessageData { MessageId = "cancel-boundary", Content = "cancel" },
        }]], ModelQueries.Selected("sdk-default"));
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            using var cts = new CancellationTokenSource();
            using var cancelOnMessage = rig.Session.On<AssistantMessageEvent>(_ => cts.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.Agent.SendAsync("test prompt", cts.Token).WaitAsync(TestTimeout));

            var stopped = Assert.Single(capture.Stopped);
            Assert.Equal("sdk-default", stopped.Model);
            Assert.Equal("sdk", stopped.Source);
        });
    }

    [Theory]
    [InlineData(null, "changed-model")]
    [InlineData("child", "sdk-default")]
    public async Task Github_TracksOnlyMainSessionModelChanges(string? agentId, string expectedModel)
    {
        using var capture = new SendCapture();
        await using var runtime = Server([[Idle()]], ModelQueries.Selected("sdk-default"));
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var onChange = rig.Session.On<SessionModelChangeEvent>(_ => processed.TrySetResult());

            await runtime.EmitAsync(new SessionModelChangeEvent
            {
                AgentId = agentId,
                Data = new SessionModelChangeData { NewModel = "changed-model" },
            });
            await processed.Task.WaitAsync(TestTimeout);
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal(expectedModel, rig.Agent.Model);
            Assert.Equal(expectedModel, Assert.Single(capture.Stopped).Model);
        });
    }

    [Fact]
    public async Task Github_UsesStartupEvent_WithoutAnExtraQuery()
    {
        var queries = ModelQueries.Failing(-32601);
        await using var runtime = Server(queries: queries);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var onStart = rig.Session.On<SessionStartEvent>(_ => processed.TrySetResult());
            await runtime.EmitAsync(new SessionStartEvent
            {
                Data = new SessionStartData
                {
                    SessionId = OfflineChatAgent.SessionId, Producer = "test", CopilotVersion = "test",
                    StartTime = DateTimeOffset.UtcNow, Version = 1, SelectedModel = "startup-model",
                },
            });
            await processed.Task.WaitAsync(TestTimeout);

            await rig.Metadata.InitializeAsync(rig.Session, CancellationToken.None);

            Assert.Equal("startup-model", rig.Agent.Model);
            Assert.Equal(0, queries.Count);
            Assert.Empty(rig.Warnings);
        }, initializeModel: false);
    }

    [Fact]
    public async Task Github_ModelChangeDuringQuery_WinsOverTheOlderRpcReply()
    {
        // Ordering invariant 2 of 2: a stale RPC reply cannot overwrite an explicit model change.
        // Guarded by the `IsSelectionChange` precedence in AgentModelTelemetry.InitializeAsync.
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new ModelQueries(async query =>
        {
            await query.Server.EmitAsync(new SessionModelChangeEvent
            {
                Data = new SessionModelChangeData { NewModel = "new-model" },
            });
            await processed.Task.WaitAsync(TestTimeout);
            await query.ReplyAsync(new { modelId = "stale-model" });
        });
        await using var runtime = Server(queries: queries);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            using var onChange = rig.Session.On<SessionModelChangeEvent>(_ => processed.TrySetResult());

            await rig.Metadata.InitializeAsync(rig.Session, CancellationToken.None);

            Assert.Equal("new-model", rig.Agent.Model);
            Assert.Equal(1, queries.Count);
            Assert.Empty(rig.Warnings);
        }, initializeModel: false);
    }

    [Fact]
    public async Task Github_LateStartupEvent_DoesNotOverwriteQueriedSelection()
    {
        // Ordering invariant 1 of 2: a startup event delivered after the query resolved is older
        // information, so it must not replace the queried selection. Guarded by the
        // `current.Model is null` precedence in AgentModelTelemetry.Observe.
        await using var runtime = Server(queries: ModelQueries.Selected("queried-model"));
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var onStart = rig.Session.On<SessionStartEvent>(_ => processed.TrySetResult());

            await runtime.EmitAsync(Started("old-startup-model"));
            await processed.Task.WaitAsync(TestTimeout);

            Assert.Equal("queried-model", rig.Agent.Model);
        });
    }

    [Fact]
    public async Task Github_StartupEventDuringQuery_DoesNotOverrideTheCurrentRpcSelection()
    {
        // Same ordering seen from the other side: the startup event lands while the query is in
        // flight, so the reply that arrives after it is the newer information and wins.
        //
        // What this test can actually fail on is the ABSENCE of Observe's `current.Model is null`
        // precedence here — add that guard to InitializeAsync and the reply stops replacing the
        // startup observation. It does NOT pin `!current.IsSelectionChange`: make the CAS loop
        // unconditional and this still passes, because the reply overwrites either way. That line
        // is pinned by Github_ModelChangeDuringQuery_WinsOverTheOlderRpcReply instead.
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Server(queries: new ModelQueries(async query =>
        {
            await query.Server.EmitAsync(Started("old-startup-model"));
            await processed.Task.WaitAsync(TestTimeout);
            await query.ReplyAsync(new { modelId = "queried-model" });
        }));
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            using var onStart = rig.Session.On<SessionStartEvent>(_ => processed.TrySetResult());

            await rig.Metadata.InitializeAsync(rig.Session, CancellationToken.None);

            Assert.Equal("queried-model", rig.Agent.Model);
        }, initializeModel: false);
    }

    [Theory]
    [InlineData("rpc-error", "the startup model query failed")]
    [InlineData("malformed", "the startup model query failed")]
    [InlineData("null", "the SDK did not return a selected model")]
    [InlineData("missing", "the SDK did not return a selected model")]
    public async Task Github_MetadataFailure_IsReported_AndUsageCanStillRecoverTheModel(string failure, string reason)
    {
        using var capture = new SendCapture();
        var queries = failure switch
        {
            "rpc-error" => ModelQueries.Failing(-32601),
            "malformed" => new ModelQueries(query => query.ReplyAsync(new { modelId = 42 })),
            "null" => new ModelQueries(query => query.ReplyAsync(null)),
            "missing" => new ModelQueries(query => query.ReplyAsync(new { })),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        await using var runtime = Server([[Usage("actual-model"), Idle()]], queries);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            Assert.Equal(string.Empty, rig.Agent.Model);
            Assert.Equal(AgentModelSource.Unavailable, rig.Metadata.Current.Source);
            var warning = Assert.Single(rig.Warnings);
            Assert.Contains("model metadata is unavailable", warning);
            Assert.Contains(reason, warning);
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);
            Assert.Equal("actual-model", Assert.Single(capture.Stopped).Model);
        });
    }

    [Fact]
    public async Task ModelQueryHang_DoesNotExceedItsBudget()
    {
        using var capture = new SendCapture();
        var clock = new SignalingFakeTimeProvider();
        var queries = ModelQueries.Holding();
        await using var runtime = Server([[Usage("actual-model"), Idle()]], queries);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            var initialization = rig.Metadata.InitializeAsync(rig.Session, CancellationToken.None);
            await queries.Started.WaitAsync(TestTimeout);
            await clock.WaitForTimerAsync(ModelQueryBudget, TestTimeout);

            // Without the clock advancing, the query stays pending.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(initialization.IsCompleted);
            Assert.Empty(rig.Warnings);

            clock.Advance(ModelQueryBudget);
            await initialization.WaitAsync(TestTimeout);
            Assert.Contains("timed out", Assert.Single(rig.Warnings));
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);
            Assert.Equal("actual-model", Assert.Single(capture.Stopped).Model);
        }, initializeModel: false, timeProvider: clock);
    }

    [Fact]
    public async Task Github_CallerCancellationDuringModelQuery_IsNotSwallowed()
    {
        // The clock never advances, so only the caller can end the query.
        var clock = new SignalingFakeTimeProvider();
        var queries = ModelQueries.Holding();
        await using var runtime = Server(queries: queries);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            using var cts = new CancellationTokenSource();
            var initialization = rig.Metadata.InitializeAsync(rig.Session, cts.Token);
            await queries.Started.WaitAsync(TestTimeout);

            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.WaitAsync(TestTimeout));
            Assert.Empty(rig.Warnings);
        }, initializeModel: false, timeProvider: clock);
    }

    private static AssistantUsageEvent Usage(string model, string? agentId = null, string? initiator = null) =>
        new()
        {
            AgentId = agentId,
            Data = new AssistantUsageData { Model = model, Initiator = initiator },
        };

    [Fact]
    public async Task ModelQueryThrows_CallerCancellationStillPropagates()
    {
        // The seam the old ModelQuery_CancelledMidFlight_PropagatesCancellation could not build:
        // the query fails on a torn transport with the caller already cancelled, so the failure
        // reaches the broad catch rather than the cancellation filter above it. Which of the two
        // the transport actually produces is not pinned here — what is pinned is that either one
        // ends as cancellation and never as a reported model failure.
        using var cts = new CancellationTokenSource();
        var queries = new ModelQueries(query =>
        {
            cts.Cancel();
            query.DropConnection();
            return Task.CompletedTask;
        });
        await using var runtime = Server(queries: queries);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => rig.Metadata.InitializeAsync(rig.Session, cts.Token).WaitAsync(TestTimeout));

                Assert.True(rig.Warnings.Count == 0,
                    "A cancelled caller is not a failed model lookup, so nothing is reported to the user. " +
                    $"Reported: {string.Join(" | ", rig.Warnings)}.");

                // The connection is gone, so a second query fails without reaching the server.
#pragma warning disable GHCP001 // The SDK marks this read-only metadata API experimental.
                var second = await Record.ExceptionAsync(() => rig.Session.Rpc.Model.GetCurrentAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(60)));
#pragma warning restore GHCP001
                Assert.NotNull(second);
                Assert.Equal(1, queries.Count);
            }
            finally
            {
                // A torn transport cannot acknowledge the SDK's graceful session detach.
                await rig.Client.ForceStopAsync().WaitAsync(TestTimeout);
            }
        }, initializeModel: false);
    }

    [Fact]
    public async Task ModelQueryThrowsUnexpectedType_RecoveryStillRuns()
    {
        // The SDK normalises wire faults before they reach us: an RPC error and a torn connection
        // both arrive as IOException wrapping the real cause, a malformed result as JsonException.
        // Reaching the failure path with a type the old catch did not name therefore takes a
        // non-wire fault, and a disposed session gives one — ObjectDisposedException, which used
        // to escape into ChatAgent.CreateAsync, dispose the client and rethrow, ending the
        // recovery session the user is waiting on. See Github_MetadataFailure_... for the claim
        // this one cannot make with a disposed session: that a later send still names the model.
        await using var runtime = Server();
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Session.DisposeAsync();

            var failure = await Record.ExceptionAsync(
                () => rig.Metadata.InitializeAsync(rig.Session, CancellationToken.None).WaitAsync(TestTimeout));

            Assert.True(failure is null,
                "A read-only metadata query must not be able to end the recovery session: whatever the " +
                "experimental RPC throws has to degrade to an unavailable model. Escaped: " +
                $"{failure?.GetType().FullName}: {failure?.Message}");
            Assert.Equal(AgentModelSource.Unavailable, rig.Metadata.Current.Source);
            Assert.Contains("model metadata is unavailable", Assert.Single(rig.Warnings));
        }, initializeModel: false);
    }

    [Theory]
    [InlineData(128, null, true)]
    [InlineData(129, null, false)]
    [InlineData(0, "model with spaces", false)]
    // $ in .NET also matches before a single trailing newline, so the anchored pattern admitted
    // both of these until it became \z. The second is 129 characters, one of them a control char.
    [InlineData(0, "gpt-5\n", false)]
    [InlineData(-128, null, false)]
    public async Task ModelName_IsBoundedBeforeExport(int length, string? literal, bool exportable)
    {
        using var capture = new SendCapture();
        // A negative length is the "128 valid characters plus a trailing newline" case.
        var model = literal ?? (length < 0 ? new string('m', -length) + "\n" : new string('m', length));
        await using var runtime = Server([[Idle()]], ModelQueries.Selected(model));
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            var exported = Assert.Single(capture.Stopped);
            Assert.True(exported.Model == (exportable ? model : null),
                "agent.model admits 1-128 characters of [A-Za-z0-9._:-] and records anything else as " +
                $"unavailable; a {model.Length}-character name exported as '{exported.Model}'.");
            Assert.Equal(exportable ? "sdk" : "unavailable", exported.Source);
            if (!exportable)
                Assert.Contains("model metadata is unavailable", Assert.Single(rig.Warnings));
        });
    }

    [Theory]
    [InlineData("session.start")]
    [InlineData("session.resume")]
    [InlineData("session.model_change")]
    public void NullStartupData_DoesNotThrowOrEraseSelection(string eventType)
    {
        var metadata = new AgentModelTelemetry(new AgentOptions { Provider = "github" }, _ => { });
        metadata.Observe(Started("selected-model"));

        metadata.Observe(SessionEvent.FromJson($"{{\"type\":\"{eventType}\",\"data\":null}}"));

        Assert.Equal("selected-model", metadata.Current.Model);
    }

    [Fact]
    public async Task NullUsageData_DoesNotLogAnSdkHandlerError()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([
            [SessionEvent.FromJson("""{"type":"assistant.usage","data":null}"""), Idle()]], ModelQueries.Selected("selected-model"));
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal("selected-model", Assert.Single(capture.Stopped).Model);
            Assert.Empty(rig.SdkErrors);
        });
    }

    [Fact]
    public async Task ResumeEvent_ProvidesSelectionBeforeInitialization()
    {
        var queries = ModelQueries.Selected("unused-model");
        await using var runtime = Server(queries: queries);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = rig.Session.On<SessionResumeEvent>(_ => processed.TrySetResult());
            await runtime.EmitAsync(new SessionResumeEvent
            {
                Data = new SessionResumeData
                {
                    EventCount = 1, ResumeTime = DateTimeOffset.UtcNow, SelectedModel = "resumed-model",
                },
            });
            await processed.Task.WaitAsync(TestTimeout);

            await rig.Metadata.InitializeAsync(rig.Session, CancellationToken.None);

            Assert.Equal("resumed-model", rig.Agent.Model);
            Assert.Equal(AgentModelSource.Sdk, rig.Metadata.Current.Source);
            Assert.Equal(0, queries.Count);
            Assert.Empty(rig.SdkErrors);
        }, initializeModel: false);
    }

    [Fact]
    public async Task CancelledSend_LateUsageBeforeTheNextTurnIsIgnored()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([
            [new AssistantMessageEvent
            {
                Data = new AssistantMessageData { MessageId = "cancel", Content = "cancel" },
            }], [Idle()]], ModelQueries.Selected("selected-model"));
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            using var cts = new CancellationTokenSource();
            using var cancel = rig.Session.On<AssistantMessageEvent>(_ => cts.Cancel());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.Agent.SendAsync("first prompt", cts.Token).WaitAsync(TestTimeout));
            var lateUsage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var onUsage = rig.Session.On<AssistantUsageEvent>(usage =>
            {
                if (usage.Data?.Model == "previous-send-model")
                    lateUsage.TrySetResult();
            });
            runtime.BeforeTurn = () => runtime.EmitAsync(Usage("previous-send-model", initiator: "user"));

            await rig.Agent.SendAsync("next prompt", CancellationToken.None).WaitAsync(TestTimeout);

            await lateUsage.Task.WaitAsync(TestTimeout);
            Assert.Equal("selected-model", capture.Stopped[1].Model);
            Assert.Equal("sdk", capture.Stopped[1].Source);
        });
    }

    [Fact]
    public async Task UsageAfterTurnEnd_IsIgnored_AndLaterTurnsAreCaptured()
    {
        using var capture = new SendCapture();
        await using var runtime = Server([
            [Usage("first-model"), new AssistantTurnEndEvent
            {
                Data = new AssistantTurnEndData { TurnId = "first" },
            }, Usage("late-model"), new AssistantTurnStartEvent
            {
                Data = new AssistantTurnStartData { TurnId = "second" },
            }, Usage("second-model"), new AssistantTurnEndEvent
            {
                Data = new AssistantTurnEndData { TurnId = "second" },
            }, Usage("late-model"), Idle()]]);
        await OfflineChatAgent.RunAsync(runtime, async rig =>
        {
            await rig.Agent.SendAsync("test prompt", CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal("second-model", Assert.Single(capture.Stopped).Model);
        });
    }

    private static SessionIdleEvent Idle() => new() { Data = new SessionIdleData() };

    private static SessionStartEvent Started(string model) => new()
    {
        Data = new SessionStartData
        {
            SessionId = OfflineChatAgent.SessionId, Producer = "test", CopilotVersion = "test",
            StartTime = DateTimeOffset.UtcNow, Version = 1, SelectedModel = model,
        },
    };

    // Without explicit queries, the server reports that no model is selected.
    private static OfflineCopilotServer Server(SessionEvent[][]? turns = null, ModelQueries? queries = null) =>
        new(turns, (queries ?? ModelQueries.Selected(null)).HandleAsync);

    /// <summary>Answers <c>session.model.getCurrent</c> for one test and counts the requests.</summary>
    private sealed class ModelQueries(Func<OfflineCopilotServer.ModelQuery, Task> answer)
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        /// <summary>Completes when the first request reaches the server.</summary>
        internal Task Started => _started.Task;

        internal static ModelQueries Selected(string? model) =>
            new(query => query.ReplyAsync(new { modelId = model }));

        internal static ModelQueries Failing(int code) =>
            new(query => query.FailAsync(code, "offline model query failure"));

        /// <summary>Never answers, so the request stays pending until the client gives up.</summary>
        internal static ModelQueries Holding() => new(_ => Task.CompletedTask);

        internal Task HandleAsync(OfflineCopilotServer.ModelQuery query)
        {
            Interlocked.Increment(ref _count);
            _started.TrySetResult();
            return answer(query);
        }
    }

    private sealed class TurnRenderer : IAgentRenderer
    {
        public List<string> Models { get; } = [];
        public void OnText(string chunk) { }
        public void OnEvent(AgentEvent ev)
        {
            if (ev is LlmTurnCompleted turn)
                Models.Add(turn.Model);
        }
        public void OnSpinnerFrame(string frame, string verb) { }
        public void OnSpinnerStop() { }
    }

    private static AuthResult OfflineToken() => new()
    {
        AccessToken = "offline-token", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        Scopes = [], TenantId = "offline-tenant", CorrelationId = Guid.NewGuid(),
    };

    private sealed class SendCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<(Activity Span, string? Model, string? Source)> Stopped { get; } = [];

        public SendCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Agent",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == "Agent.Send")
                        Stopped.Add((activity, activity.GetTagItem(TelemetryTags.AgentModel)?.ToString(),
                            activity.GetTagItem(TelemetryTags.AgentModelSource)?.ToString()));
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }
}
