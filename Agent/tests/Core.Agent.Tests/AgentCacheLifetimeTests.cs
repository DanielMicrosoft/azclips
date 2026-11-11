// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using Core.Abstractions;
using Core.Agent.Telemetry;
using Core.Agent.Tools.Resource.Cache;
using Core.Telemetry;
using Microsoft.Extensions.Time.Testing;
using Tests.Common.Stubs;

namespace Core.Agent.Tests;

[Collection("AgentTelemetry")]
public sealed class AgentCacheLifetimeTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task HostShutdown_TracksTheOuterKickBeforeItsTenantIsKnown()
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var scope = harness.CreateScope();
            using var rootScope = scope.Telemetry.StartIndependentRootActivity("Agent.Session");
            var root = rootScope?.Activity;
            var entered = Signal();
            var cancelled = Signal();
            var releaseFinally = Signal();
            var exited = false;
            var profiles = new ReadingProfileService(scope.Telemetry)
            {
                Read = async token =>
                {
                    entered.SetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { cancelled.SetResult(); throw; }
                    finally { await releaseFinally.Task; exited = true; }
                    return null;
                },
            };
            using var http = new HttpClient(new AgentOfflineHttp());
            var refresher = new ArmCacheRefresher(new NoAuthService(), profiles, http, harness.DirectoryPath);
            var lifetime = new AgentRuntimeLifetime(scope);
            refresher.AttachHostLifetime(lifetime);
            lifetime.RegisterCleanup(refresher.StopAndDrainAsync);
            refresher.KickAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(refresher.ActiveTaskCount > 0);

            var completion = lifetime.CompleteAsync(root, new Dictionary<string, object?>(), ActivityStatusCode.Ok);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(completion.IsCompleted);
            Assert.False(exited);
            refresher.KickAsync();
            Assert.Equal(1, profiles.Reads);
            releaseFinally.SetResult();

            Assert.Equal(TelemetryLayerCompletion.Published, await completion);
            Assert.True(exited);
            Assert.Equal(0, refresher.ActiveTaskCount);
            Assert.Contains(harness.ReadEntries(), entry =>
                entry.GetProperty("operationName").GetString() == "ProfileService.GetActiveProfileAsync");
        });
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelTheSharedRefreshOrAnotherCaller()
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var scope = harness.CreateScope();
            using var rootScope = scope.Telemetry.StartIndependentRootActivity("Agent.Session");
            var root = rootScope?.Activity;
            var authEntered = Signal();
            var releaseAuth = Signal();
            var sharedToken = CancellationToken.None;
            var auth = new DelegatingAuthService
            {
                Acquire = async token =>
                {
                    sharedToken = token;
                    authEntered.TrySetResult();
                    await releaseAuth.Task.WaitAsync(token);
                    return OfflineToken();
                },
            };
            var profiles = new ReadingProfileService(scope.Telemetry)
            {
                Read = _ => Task.FromResult<Profile?>(new Profile
                {
                    Name = "offline", TenantId = "offline-tenant", SubscriptionId = "offline-subscription",
                    CloudName = "AzureCloud",
                }),
            };
            using var http = new HttpClient(new AgentOfflineHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[],"data":[]}"""),
            })));
            var refresher = new ArmCacheRefresher(auth, profiles, http, Path.Combine(harness.DirectoryPath, "cache"));
            var lifetime = new AgentRuntimeLifetime(scope);
            refresher.AttachHostLifetime(lifetime);
            lifetime.RegisterCleanup(refresher.StopAndDrainAsync);
            using var caller = new CancellationTokenSource();

            var first = refresher.RefreshOnceAsync(caller.Token);
            await authEntered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var second = refresher.RefreshOnceAsync();
            Assert.True(SpinWait.SpinUntil(() => refresher.WaitingCallerCount("offline-tenant") == 2, TimeSpan.FromSeconds(60)));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(sharedToken.IsCancellationRequested);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, auth.AcquireCount);
            releaseAuth.SetResult();
            await second.WaitAsync(TimeSpan.FromSeconds(60));
            // One acquisition for the tenant list, one for the tenant's ARG sweep.
            Assert.Equal(2, auth.AcquireCount);
            Assert.Equal(TelemetryLayerCompletion.Published,
                await lifetime.CompleteAsync(root, new Dictionary<string, object?>(), ActivityStatusCode.Ok));
        });
    }

    [Fact]
    public async Task CliWithoutHostAttachment_KeepsItsDetachedRefreshPolicy()
    {
        var entered = Signal();
        var release = Signal();
        var exited = Signal();
        var tokenSeen = CancellationToken.None;
        var profiles = new ReadingProfileService()
        {
            Read = async token =>
            {
                tokenSeen = token;
                entered.SetResult();
                await release.Task;
                exited.SetResult();
                return null;
            },
        };
        using var http = new HttpClient(new AgentOfflineHttp());
        var refresher = new ArmCacheRefresher(new NoAuthService(), profiles, http,
            Path.Combine(AppContext.BaseDirectory, "TestArtifacts", "CliCache", Guid.NewGuid().ToString("N")));
        refresher.KickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));

        await refresher.StopAndDrainAsync();

        Assert.False(refresher.IsHostClosing);
        Assert.False(tokenSeen.CanBeCanceled);
        Assert.False(exited.Task.IsCompleted);
        release.SetResult();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task FirstInstalledRefresher_IsTheOneTheAgentOwnsAndDrains()
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var scope = harness.CreateScope();
            using var rootScope = scope.Telemetry.StartIndependentRootActivity("Agent.Session");
            var root = rootScope?.Activity;
            using var http = new HttpClient(new AgentOfflineHttp());
            var actual = new ArmCacheRefresher(new NoAuthService(), new StubProfileService(seedDefault: false), http, harness.DirectoryPath);
            var cache = new ResourceCache(harness.DirectoryPath, refresher: actual);
            var constructedRejected = false;
            var installed = cache.GetOrCreateRefresherCore(() =>
            {
                constructedRejected = true;
                throw new InvalidOperationException("First-writer-wins must not construct a rejected owner.");
            });
            Assert.Same(actual, installed);
            Assert.False(constructedRejected);
            var lifetime = new AgentRuntimeLifetime(scope);
            installed.AttachHostLifetime(lifetime);
            lifetime.RegisterCleanup(installed.StopAndDrainAsync);
            Assert.Equal(TelemetryLayerCompletion.Published,
                await lifetime.CompleteAsync(root, new Dictionary<string, object?>(), ActivityStatusCode.Ok));
            Assert.True(actual.IsHostClosing);
        });
    }

    [Fact]
    public async Task UncooperativeKick_IsAbandonedAtDeadline_AndCannotWriteLate()
    {
        var clock = new FakeTimeProvider();
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(timeProvider: clock), async harness =>
        {
            var scope = harness.CreateScope();
            using var rootScope = scope.Telemetry.StartIndependentRootActivity("Agent.Session");
            var root = rootScope?.Activity;
            var entered = Signal();
            var release = Signal();
            var lateActivityWasRejected = false;
            var profiles = new ReadingProfileService(scope.Telemetry)
            {
                Read = async _ =>
                {
                    entered.SetResult();
                    await release.Task;
                    using var late = scope.Telemetry.StartActivity("AuthBeforeTokenAcquisition");
                    lateActivityWasRejected = late is null;
                    return null;
                },
            };
            using var http = new HttpClient(new AgentOfflineHttp());
            var refresher = new ArmCacheRefresher(new NoAuthService(), profiles, http, harness.DirectoryPath);
            var lifetime = new AgentRuntimeLifetime(scope, clock);
            refresher.AttachHostLifetime(lifetime);
            lifetime.RegisterCleanup(refresher.StopAndDrainAsync);
            refresher.KickAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));

            var completion = lifetime.CompleteAsync(root,
                new Dictionary<string, object?> { [TelemetryTags.RecoveryOutcome] = "ok" }, ActivityStatusCode.Ok);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(TelemetryLayerCompletion.NotPublished,
                await completion.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.Null(scope.FileSession.PublishedPath);

            release.SetResult();
            await refresher.StopAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(lateActivityWasRejected);
            Assert.Null(scope.FileSession.PublishedPath);
        });
    }

    private static AuthResult OfflineToken() => new()
    {
        AccessToken = "offline-token", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        Scopes = [], TenantId = "offline-tenant", CorrelationId = Guid.NewGuid(),
    };

    private sealed class NoAuthService : StubAuthService
    {
        protected override Task<AuthResult> AcquireTokenAsync(string[]? scopes, string? tenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected authentication in an offline test.");
    }

    private sealed class DelegatingAuthService : StubAuthService
    {
        internal required Func<CancellationToken, Task<AuthResult>> Acquire { get; init; }

        protected override Task<AuthResult> AcquireTokenAsync(string[]? scopes, string? tenantId, CancellationToken cancellationToken) =>
            Acquire(cancellationToken);
    }

    /// <summary>Runs <see cref="Read"/> for the active profile inside a profile-service activity.</summary>
    private sealed class ReadingProfileService(ITelemetryService? telemetry = null) : StubProfileService(seedDefault: false)
    {
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        internal required Func<CancellationToken, Task<Profile?>> Read { get; init; }

        public override async Task<Profile?> GetActiveProfileAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            using var activity = telemetry?.StartActivity("ProfileService.GetActiveProfileAsync");
            return await Read(cancellationToken).ConfigureAwait(false);
        }
    }
}
