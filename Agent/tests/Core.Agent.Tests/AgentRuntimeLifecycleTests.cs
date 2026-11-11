// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using Core.Abstractions;
using Core.Agent.AgentMode;
using Core.Agent.Hosting;
using Core.Agent.Providers;
using Core.Agent.Telemetry;
using Core.Agent.Tools.Help;
using Core.Agent.Tools.Resource.Cache;
using Core.Telemetry;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Tests.Common.Stubs;

namespace Core.Agent.Tests;

[Collection("AgentTelemetry")]
public sealed class AgentRuntimeLifecycleTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task AgentSession_UsesRecoveryContextAsItsNativeParent()
    {
        var context = new TelemetryInvocationContext(
            Guid.NewGuid().ToString("N"),
            true,
            RecoveryTraceParent: "00-11111111111111111111111111111111-2222222222222222-01");
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(context: context), async harness =>
        {
            var scope = harness.CreateScope();
            Activity? session = null;

            var exitCode = await AgentModeEntry.RunSessionAsync(
                () => scope,
                (activity, _) =>
                {
                    session = activity;
                    return Task.CompletedTask;
                },
                () => { });

            Assert.Equal(0, exitCode);
            Assert.NotNull(session);
            Assert.Equal(ActivityTraceId.CreateFromString(
                "11111111111111111111111111111111".AsSpan()), session.TraceId);
            Assert.Equal(ActivitySpanId.CreateFromString("2222222222222222".AsSpan()), session.ParentSpanId);
            var record = Assert.Single(harness.ReadEntries());
            Assert.Equal("Agent.Session", record.GetProperty("operationName").GetString());
            Assert.Equal("11111111111111111111111111111111", record.GetProperty("traceId").GetString());
            Assert.Equal("2222222222222222", record.GetProperty("parentSpanId").GetString());
        });
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("hook")]
    [InlineData("permission")]
    [InlineData("event")]
    public async Task CallbacksBeforeAnyActivity_HonorHostStop_AndOwnTheirActualFinally(string kind)
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var scope = harness.CreateScope();
            var lifetime = new AgentRuntimeLifetime(scope);
            using var context = new AgentInvocationContext(null, scope.Telemetry, lifetime);
            var entered = Signal();
            var cancelled = Signal();
            var releaseFinally = Signal();
            var exited = false;
            var enteredWithoutActivity = false;

            async Task<string> Work(CancellationToken token)
            {
                enteredWithoutActivity = Activity.Current is null;
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "done";
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
                finally
                {
                    await releaseFinally.Task;
                    exited = true;
                }
            }

            var config = new SessionConfig
            {
                Tools = [AIFunctionFactory.Create(Work)],
                Hooks = new SessionHooks
                {
                    OnSessionStart = async (_, _) =>
                    {
                        await Work(context.CallbackCancellationToken);
                        return new SessionStartHookOutput();
                    },
                },
                OnPermissionRequest = async (_, _) =>
                {
                    await Work(context.CallbackCancellationToken);
    #pragma warning disable GHCP001 // Exercise the SDK permission callback with an offline response.
                    return PermissionDecision.ApproveOnce();
    #pragma warning restore GHCP001
                },
                OnEvent = _ => Work(context.CallbackCancellationToken).GetAwaiter().GetResult(),
            };
            context.Bind(config);
            Task callback = kind switch
            {
                "tool" => config.Tools!.Single().GetService<AIFunction>()!.InvokeAsync(new AIFunctionArguments()).AsTask(),
                "hook" => config.Hooks!.OnSessionStart!(new SessionStartHookInput(), new HookInvocation { SessionId = "offline" }),
                "permission" => config.OnPermissionRequest!(null!, null!),
                _ => Task.Run(() => config.OnEvent!(new SessionIdleEvent { Data = new SessionIdleData() })),
            };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(enteredWithoutActivity);

            using var rootScope = scope.Telemetry.StartIndependentRootActivity("Agent.Session");
            var root = rootScope?.Activity;
            var completion = lifetime.CompleteAsync(root, terminalStatus: ActivityStatusCode.Ok);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(TelemetryLayerState.Running, scope.State);
            Assert.False(completion.IsCompleted);
            Assert.False(exited);
            releaseFinally.SetResult();

            if (kind == "event")
                await callback;
            else
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callback);
            Assert.Equal(TelemetryLayerCompletion.Published, await completion);
            Assert.True(exited);
            Assert.Single(harness.ReadEntries());
        });
    }

    [Fact]
    public async Task SdkTeardown_AllowsItsTrackedHooksAndNestedTools()
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var scope = harness.CreateScope();
            using var rootScope = scope.Telemetry.StartIndependentRootActivity("Agent.Session");
            var root = rootScope?.Activity;
            var lifetime = new AgentRuntimeLifetime(scope);
            var context = new AgentInvocationContext(root, scope.Telemetry, lifetime);
            var resource = new AgentSdkLifetime(context);
            var nestedResult = "";
            var sawHostStop = false;
            var config = new SessionConfig
            {
                Tools = [new HelpTools("must-not-run", new AgentTestConfiguration(), context.StartToolActivity).GetCommand],
            };
            config.Hooks = new SessionHooks
            {
                OnSessionStart = async (_, _) =>
                {
                    using var tool = ToolInvocationLog.Start("teardown", activityCreator: context.StartToolActivity);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CallbackCancellationToken); }
                    catch (OperationCanceledException) { sawHostStop = true; }
                    nestedResult = (await config.Tools.Single().GetService<AIFunction>()!
                        .InvokeAsync(new AIFunctionArguments { ["name"] = "" }))!.ToString()!;
                    return new SessionStartHookOutput();
                },
            };
            context.Bind(config);
            resource.SetClientCleanup(async () =>
                await config.Hooks.OnSessionStart!(new SessionStartHookInput(), new HookInvocation { SessionId = "offline" }));
            lifetime.RegisterCleanup(resource.DisposeAsync);

            Assert.Equal(TelemetryLayerCompletion.Published, await lifetime.CompleteAsync(root,
                new Dictionary<string, object?> { [TelemetryTags.RecoveryOutcome] = "ok" }, ActivityStatusCode.Ok));

            Assert.True(sawHostStop);
            Assert.Contains("missing_name", nestedResult);
            Assert.Throws<ObjectDisposedException>(() => context.StartOperation("Agent.Send"));
            var entries = harness.ReadEntries();
            var outer = Assert.Single(entries, entry => entry.GetProperty("operationName").GetString() == "tool.teardown");
            var inner = Assert.Single(entries, entry => entry.GetProperty("operationName").GetString() == "tool.get_command");
            Assert.Equal(outer.GetProperty("spanId").GetString(), inner.GetProperty("parentSpanId").GetString());
        });
    }

    [Fact]
    public async Task CreateFailure_WithUncooperativeClientCleanup_UsesTheSharedDeadline()
    {
        var clock = new FakeTimeProvider();
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(timeProvider: clock), async harness =>
        {
            var scope = harness.CreateScope();
            var cleanupEntered = Signal();
            var releaseClient = Signal();
            AgentSdkLifetime? resource = null;
            AgentInvocationContext? context = null;
            var actualCleanupFinished = false;
            var running = AgentModeEntry.RunSessionAsync(() => scope, (session, lifetime) =>
            {
                context = new AgentInvocationContext(session, scope.Telemetry, lifetime);
                resource = new AgentSdkLifetime(context);
                resource.SetClientCleanup(async () =>
                {
                    cleanupEntered.SetResult();
                    await releaseClient.Task;
                    actualCleanupFinished = true;
                });
                lifetime.RegisterCleanup(resource.DisposeAsync);
                throw new InvalidOperationException("original offline create failure");
            }, () => { }, timeProvider: clock);
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(running.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(1, await running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(actualCleanupFinished);
            Assert.Null(scope.FileSession.PublishedPath);

            Assert.Null(scope.Telemetry.StartActivity("Agent.Send"));
            releaseClient.SetResult();
            await resource!.DisposeAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(actualCleanupFinished);
            Assert.Throws<ObjectDisposedException>(() => context!.StartOperation("Agent.Send"));
            Assert.Null(scope.FileSession.PublishedPath);
        });
    }

    [Fact]
    public async Task ActualCreateFailure_PreservesTheSdkError_AndPublishesItsSession()
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var scope = harness.CreateScope();
            await using var server = new OfflineCopilotServer { RejectCreate = true };
            var auth = new NoAuthService();
            var renderer = new SilentRenderer();
            Exception? original = null;
            var code = await AgentModeEntry.RunSessionAsync(() => scope, async (_, lifetime) =>
            {
                try
                {
                    await ChatAgent.CreateAsync("offline", auth, new StubProfileService(seedDefault: false),
                        new AgentTestConfiguration(),
                        new DataBoundaryResolver(new StubPersistenceStore(harness.DirectoryPath), new StubProfileService(seedDefault: false)),
                        "must-not-run", new ConsoleApprovalPrompt(renderer: renderer), renderer,
                        new AgentOptions(), "oneshot", lifetime,
                        preAcquiredAuth: OfflineToken(),
                        clientFactory: () => new CopilotClient(new CopilotClientOptions
                        {
                            Connection = RuntimeConnection.ForUri(server.Url),
                        }));
                }
                catch (Exception ex)
                {
                    original = ex;
                    throw;
                }
            }, () => { });

            Assert.Equal(1, code);
            Assert.Equal(1, server.CreateCount);
            Assert.NotNull(original);
            Assert.Contains("offline create failure", original.Message);
            Assert.Contains(nameof(ChatAgent.CreateAsync), original.StackTrace);
            Assert.Equal(0, auth.AcquireCount);
            var session = Assert.Single(harness.ReadEntries(), entry => entry.GetProperty("operationName").GetString() == "Agent.Session");
            Assert.Equal("error", AgentLayerTestHarness.Tags(session)[TelemetryTags.RecoveryOutcome].GetString());
            server.ThrowIfFaulted();
        });
    }

    [Theory]
    [InlineData("builtin", false, 1, 1, 0)]
    [InlineData("builtin", true, 1, 0, 1)]
    [InlineData("github", false, 0, 0, 0)]
    public async Task StandaloneStartup_DoesNotAuthenticateForRouting_AndPublishesAuthFailure(
        string provider, bool authFails, int acquisitions, int lookups, int expectedCode)
    {
        var cacheReads = 0;
        var launch = await AgentTelemetryHost.ResolveLaunchAsync(true, _ => null, enabled =>
        {
            cacheReads++;
            return Task.FromResult(new TelemetryInvocationContext(Guid.NewGuid().ToString("N"), enabled));
        });
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(context: launch.Context), async harness =>
        {
            var scope = harness.CreateScope();
            var auth = authFails ? new NoAuthService("offline signed-out account") : new OfflineAuthService();
            var lookupCalls = 0;
            var code = await AgentModeEntry.RunSessionAsync(() => scope, async (session, _) =>
            {
                var startup = await AgentStartupBoundary.ResolveAsync(new AgentOptions { Provider = provider },
                    auth, new OfflineProfileService(),
                    handedOverBoundary: null,
                    (_, _, _) => { lookupCalls++; return Task.FromResult<string?>("Global"); },
                    session, CancellationToken.None);
                startup.Failure?.Throw();
                if (startup.Auth is not null)
                    Assert.Equal(startup.Auth.AccessToken,
                        BuiltinProviderConfig.Build(new BuiltinProviderOptions(), BuiltinEndpointResolver.Choose(boundary: null).Url, startup.Auth.AccessToken).BearerToken);
            }, () => { });

            Assert.Equal(expectedCode, code);
            Assert.Equal(acquisitions, auth.AcquireCount);
            Assert.Equal(1, cacheReads);
            Assert.Equal(lookups, lookupCalls);
            // A lookup answer refreshes the cache only; the file keeps the route fixed at launch.
            var entry = Assert.Single(harness.ReadEntries());
            Assert.Equal(scope.Context.UploadRegion.ToDataBoundary(), entry.GetProperty("dataBoundary").GetString());
            Assert.False(entry.TryGetProperty("uploadRegion", out _));
            Assert.False(entry.TryGetProperty("dataBoundaryOutcome", out _));
        });
    }

    [Fact]
    public async Task UserCancellationCallbacks_ArePartOfTheSameDrainBudget()
    {
        var clock = new FakeTimeProvider();
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(timeProvider: clock), async harness =>
        {
            var scope = harness.CreateScope();
            using var rootScope = scope.Telemetry.StartIndependentRootActivity("Agent.Session");
            var root = rootScope?.Activity;
            var lifetime = new AgentRuntimeLifetime(scope, clock);
            using var userCancellation = new CancellationTokenSource();
            using var release = new ManualResetEventSlim();
            var entered = Signal();
            using var registration = userCancellation.Token.Register(() =>
            {
                entered.TrySetResult();
                release.Wait();
            });
            var cancelling = lifetime.CancelAsync(userCancellation);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var completion = lifetime.CompleteAsync(root,
                new Dictionary<string, object?> { [TelemetryTags.RecoveryOutcome] = "cancelled" }, ActivityStatusCode.Unset);
            try
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                Assert.Equal(TelemetryLayerCompletion.NotPublished,
                    await completion.WaitAsync(TimeSpan.FromSeconds(60)));
                Assert.False(cancelling.IsCompleted);
                Assert.Null(scope.FileSession.PublishedPath);
            }
            finally
            {
                release.Set();
                await cancelling.WaitAsync(TimeSpan.FromSeconds(60));
            }
        });
    }

    private static AuthResult OfflineToken() => new()
    {
        AccessToken = "offline-token", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        Scopes = [], TenantId = "offline-tenant", CorrelationId = Guid.NewGuid(),
    };

    private class OfflineAuthService : StubAuthService
    {
        protected override Task<AuthResult> AcquireTokenAsync(string[]? scopes, string? tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(OfflineToken());
    }

    private sealed class NoAuthService(string message = "Unexpected authentication in an offline test.") : OfflineAuthService
    {
        protected override Task<AuthResult> AcquireTokenAsync(string[]? scopes, string? tenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    private sealed class OfflineProfileService() : StubProfileService(seedDefault: false)
    {
        public override Task<Profile?> GetActiveProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Profile?>(new Profile { Name = "offline", TenantId = "offline-tenant" });
    }
}
