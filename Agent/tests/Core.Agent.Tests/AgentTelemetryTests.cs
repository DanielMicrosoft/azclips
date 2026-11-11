// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using Core.Abstractions;
using Core.Agent.AgentMode;
using Core.Agent.Telemetry;
using Core.Telemetry;
using GitHub.Copilot;
using Tests.Common.Stubs;

namespace Core.Agent.Tests;

[CollectionDefinition("AgentTelemetry", DisableParallelization = true)]
public class AgentTelemetryCollection { }

public sealed class AgentLaunchContextTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ValidLaunch_PreservesIdentityAndConsent(bool localEnabled, bool parentEnabled)
    {
        var context = new TelemetryInvocationContext(Guid.NewGuid().ToString("N"), parentEnabled,
            new string('1', 32), new string('2', 16), "profile show", "tenant", "subscription", UploadRegion.EU);
        var fileId = Guid.NewGuid().ToString("N");
        var environment = new Dictionary<string, string?>();
        TelemetryLayerEnvironment.SetAgentLaunch(environment, context, fileId);

        var launch = await AgentTelemetryHost.ResolveLaunchAsync(localEnabled, name => environment.GetValueOrDefault(name),
            _ => throw new InvalidOperationException("A valid launch must not read standalone routing."));

        Assert.Equal(fileId, launch.FileId);
        Assert.Null(launch.Diagnostic);
        Assert.Equal(context with { Enabled = localEnabled && parentEnabled }, launch.Context);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"InvocationId\":\"bad\"}")]
    public async Task MalformedLaunch_IsDiagnosed_AndCannotReenableTheParent(string json)
    {
        var environment = new Dictionary<string, string?>
        {
            [TelemetryLayerEnvironment.Layer] = "agent",
            [TelemetryLayerEnvironment.FileId] = Guid.NewGuid().ToString("N"),
            [TelemetryLayerEnvironment.Context] = json,
            [TelemetryLayerEnvironment.Enabled] = "false",
        };
        var reads = 0;
        var launch = await AgentTelemetryHost.ResolveLaunchAsync(true, name => environment.GetValueOrDefault(name), enabled =>
        {
            reads++;
            Assert.False(enabled);
            return Task.FromResult(new TelemetryInvocationContext(Guid.NewGuid().ToString("N"), enabled, UploadRegion: UploadRegion.EU));
        });

        Assert.NotNull(launch.Diagnostic);
        Assert.Null(launch.FileId);
        Assert.False(launch.Context.Enabled);
        Assert.Equal(UploadRegion.EU, launch.Context.UploadRegion);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    public async Task InvalidFileIdentity_FallsBackWithoutUsingThePath(string fileId)
    {
        var context = new TelemetryInvocationContext(Guid.NewGuid().ToString("N"), true);
        var environment = new Dictionary<string, string?>();
        TelemetryLayerEnvironment.SetAgentLaunch(environment, context, Guid.NewGuid().ToString("N"));
        environment[TelemetryLayerEnvironment.FileId] = fileId;
        var launch = await AgentTelemetryHost.ResolveLaunchAsync(true, name => environment.GetValueOrDefault(name),
            enabled => Task.FromResult(new TelemetryInvocationContext(Guid.NewGuid().ToString("N"), enabled)));
        Assert.Null(launch.FileId);
        Assert.NotNull(launch.Diagnostic);
        Assert.NotEqual(context.InvocationId, launch.Context.InvocationId);
    }

    [Fact]
    public async Task Standalone_UsesTheCacheSnapshotWithoutASecondAuthenticationPath()
    {
        var reads = 0;
        var snapshot = new TelemetryInvocationContext(Guid.NewGuid().ToString("N"), true, TenantId: "cached-tenant");
        var launch = await AgentTelemetryHost.ResolveLaunchAsync(true, _ => null, enabled =>
        {
            reads++;
            Assert.True(enabled);
            return Task.FromResult(snapshot);
        });
        Assert.Equal(snapshot, launch.Context);
        Assert.Equal(UploadRegion.EU, launch.Context.UploadRegion);
        Assert.Null(launch.Diagnostic);
        Assert.Equal(1, reads);
    }
}

[Collection("AgentTelemetry")]
public sealed class AgentTelemetryProviderTests
{
    [Fact]
    public async Task Host_AwaitsInjectedProfileAndCacheBeforePublishingItsScope()
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var profile = new TaskCompletionSource<Profile?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var profiles = new PendingProfileService(profile.Task);
            var persistence = new StubPersistenceStore(harness.DirectoryPath);
            persistence.Files[HomePaths.DataBoundaryCacheFileName] =
                System.Text.Encoding.UTF8.GetBytes("{\"injected-tenant\":\"Global\"}");
            var host = new AgentTelemetryHost(harness.Factory, harness.Configuration,
                new DataBoundaryResolver(persistence, profiles));

            var pending = host.GetScopeAsync();
            Assert.False(pending.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => host.Scope);
            profile.SetResult(new Profile { Name = "injected", TenantId = "injected-tenant" });
            await using var scope = await pending.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Same(scope, host.Scope);
            Assert.Same(scope, await host.GetScopeAsync());
            Assert.Equal("injected-tenant", scope.Context.TenantId);
            Assert.Equal(UploadRegion.ROW, scope.Context.UploadRegion);
        });
    }

    private sealed class PendingProfileService(Task<Profile?> profile) : StubProfileService(seedDefault: false)
    {
        public override Task<Profile?> GetActiveProfileAsync(CancellationToken cancellationToken = default) => profile;
    }

    [Fact]
    public async Task BorrowedDiViews_DoNotDisposeTheLayerProvider()
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var scope = harness.CreateScope();
            var provider = new AgentTelemetryProvider(scope.TelemetryProvider);
            var borrowed = new BorrowedAgentTelemetryService(scope.Telemetry);

            borrowed.Dispose();

            Assert.Equal("Agent", provider.ActivitySource.Name);
            Assert.False((object)provider is IDisposable);
            using var rootScope = scope.Telemetry.StartIndependentRootActivity("Agent.Session");
            var root = rootScope?.Activity;
            Assert.NotNull(root);
            Assert.Equal(TelemetryLayerCompletion.Published,
                await scope.CompleteAsync(root, terminalStatus: ActivityStatusCode.Ok));
            var entry = Assert.Single(harness.ReadEntries());
            Assert.Equal("Agent", entry.GetProperty("resource").GetProperty("service.name").GetString());
            Assert.Equal("agent-v1", AgentLayerTestHarness.Tags(entry)["schema"].GetString());
        });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OptOut_CannotBeOverriddenByAnUnrelatedListener(bool localEnabled, bool parentEnabled)
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(localEnabled, context:
            new TelemetryInvocationContext(Guid.NewGuid().ToString("N"), parentEnabled)), async harness =>
        {
            var scope = harness.CreateScope();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Agent",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(listener);

            Assert.Null(scope.Telemetry.StartIndependentRootActivity("Agent.Session"));
            Assert.Null(scope.Telemetry.StartActivity("Agent.Send"));
            Assert.Equal(TelemetryLayerCompletion.Disabled, await scope.CompleteAsync());
            Assert.Null(scope.FileSession.PartialPath);
            Assert.Null(scope.FileSession.PublishedPath);
        });
    }

    [Theory]
    [InlineData("success", "ok", 0, "Ok", null)]
    [InlineData("cancelled", "cancelled", 130, "Unset", null)]
    [InlineData("host-fault", "error", 1, "Error", "host_fault")]
    [InlineData("sdk-failure", "error", 1, "Error", "rate_limit")]
    [InlineData("sdk-other", "error", 1, "Error", "sdk_other")]
    public async Task RunSessionAsync_PublishesEveryTerminalOutcome(
        string exit, string expectedOutcome, int expectedExitCode, string expectedStatus, string? expectedReason)
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(), async harness =>
        {
            var scope = harness.CreateScope();
            using var unrelated = new Activity("unrelated").Start();
            var disposed = 0;
            var cleanupRan = false;
            Activity? observedSession = null;
            Activity? observedCurrent = null;
            Activity? ambientAtServiceDisposal = null;

            var code = await AgentModeEntry.RunSessionAsync(() => scope, (session, lifetime) =>
            {
                observedSession = session;
                observedCurrent = Activity.Current;
                lifetime.RegisterCleanup(() =>
                {
                    Assert.NotNull(session);
                    Assert.False(session.IsStopped);
                    session.SetTag(TelemetryTags.RecoveryOutcome, "cleanup-cannot-change-the-outcome");
                    cleanupRan = true;
                    return Task.CompletedTask;
                });
                return exit switch
                {
                    "cancelled" => throw new OperationCanceledException(),
                    "host-fault" => throw new InvalidOperationException("test-only private failure"),
                    "sdk-failure" => throw new AgentSessionFailedException("rate_limit", "test-only private failure"),
                    "sdk-other" => throw new AgentSessionFailedException("unlisted-private-type", "test-only private failure"),
                    _ => Task.CompletedTask,
                };
            }, () => { ambientAtServiceDisposal = Activity.Current; disposed++; });

            Assert.NotNull(observedSession);
            Assert.Same(observedSession, observedCurrent);
            Assert.NotEqual(unrelated.TraceId, observedSession.TraceId);
            Assert.Equal(default, observedSession.ParentSpanId);
            Assert.Equal(expectedExitCode, code);
            Assert.Equal(1, disposed);
            Assert.True(cleanupRan);
            Assert.Same(unrelated, ambientAtServiceDisposal);
            Assert.Same(unrelated, Activity.Current);
            var root = Assert.Single(harness.ReadEntries(), entry => entry.GetProperty("operationName").GetString() == "Agent.Session");
            var tags = AgentLayerTestHarness.Tags(root);
            Assert.Equal(expectedOutcome, tags[TelemetryTags.RecoveryOutcome].GetString());
            Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
            if (expectedReason is null)
                Assert.False(tags.ContainsKey(TelemetryTags.RecoveryFailureReason));
            else
                Assert.Equal(expectedReason, tags[TelemetryTags.RecoveryFailureReason].GetString());
            Assert.DoesNotContain("private", root.GetRawText(), StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(130)]
    public async Task RunSessionAsync_PreservesBusinessResultsWhenTelemetryIsDisabled(int expected)
    {
        await AgentLayerTestHarness.RunAsync(new AgentLayerTestHarness(enabled: false), async harness =>
        {
            var scope = harness.CreateScope();
            var cleanup = false;
            var code = await AgentModeEntry.RunSessionAsync(() => scope, (session, lifetime) =>
            {
                Assert.Null(session);
                lifetime.RegisterCleanup(() => { cleanup = true; return Task.CompletedTask; });
                return expected switch
                {
                    130 => throw new OperationCanceledException(),
                    1 => throw new InvalidOperationException("expected offline failure"),
                    _ => Task.CompletedTask,
                };
            }, () => { });
            Assert.Equal(expected, code);
            Assert.True(cleanup);
            Assert.Null(scope.FileSession.PublishedPath);
        });
    }

    [Fact]
    public async Task RunSessionAsync_StillHandlesTelemetryStartupFailure()
    {
        var disposed = false;
        var code = await AgentModeEntry.RunSessionAsync(
            () => throw new InvalidOperationException("offline registration failure"),
            (_, _) => throw new InvalidOperationException("must not run"), () => disposed = true);
        Assert.Equal(1, code);
        Assert.True(disposed);
    }

    [Fact]
    public void ClassifiesTheSentinelEventRendererSubstitutes_AsUnknown()
    {
        var renderer = new EventRenderer();
        SessionErrored? rendered = null;
        renderer.OnEvent = ev => { if (ev is SessionErrored error) rendered = error; };
        renderer.Forward(new SessionErrorEvent { Data = null! });
        Assert.NotNull(rendered);
        Assert.Equal(RecoveryFailure.Unknown, AgentModeEntry.ClassifySdkFailure(rendered.ErrorType));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalError_WinsOverCancellation(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        var error = new SessionErrored("authentication", "offline error", null, 401, null, DateTimeOffset.UtcNow);
        var failure = Assert.Throws<AgentSessionFailedException>(() =>
            AgentModeEntry.ThrowIfSessionEndedInError(error, cancellation.Token));
        Assert.Equal("authentication", failure.ErrorType);
    }

    [Fact]
    public void CompletedFinalTurn_DoesNotBecomeAnError()
    {
        AgentModeEntry.ThrowIfSessionEndedInError(null, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            AgentModeEntry.ThrowIfSessionEndedInError(null, cancellation.Token));
    }
}
