// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Abstractions;
using Core.Agent.AgentMode;
using Core.Telemetry;
using Tests.Common.Utilities;

namespace Core.Agent.Tests;

[Collection("AgentTelemetry")]
public class AgentLayerAsyncWriterTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(130, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(130, false)]
    public async Task RunSessionAsync_ChildDisposeCannotBlockAdmissionToBoundedCompletion(
        int expectedExit, bool expireBudget)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestArtifacts",
            "AgentLayerAsyncWriter", Guid.NewGuid().ToString("N"));
        using var blockedDirectory = new BlockingDirectory(directory);
        var clock = new SignalingFakeTimeProvider();
        var factory = new TelemetryLayerScopeFactory(
            blockedDirectory, new AgentTestConfiguration(),
            new Core.AssemblyLoading.AssemblyLoadingService(
                new Core.AssemblyLoading.DefaultAssemblyLoadingProvider(),
                new Core.AssemblyLoading.DefaultAssemblyVersionProvider()),
            timeProvider: clock);
        var scope = factory.CreateScope("Agent", new TelemetryInvocationContext(Guid.NewGuid().ToString("N"), true));
        Assert.True(scope.TelemetryProvider.ActivitySource.HasListeners());
        var businessReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposedServices = 0;
        var publishedAfterRelease = false;
        var host = Task.Run(() => AgentModeEntry.RunSessionAsync(() => scope, (session, lifetime) =>
        {
            using var producer = lifetime.TryEnterProducer();
            try
            {
                using var child = scope.Telemetry.StartActivityWithParent("Agent.Send", session!.Context);
                return expectedExit switch
                {
                    1 => throw new InvalidOperationException("Expected offline business failure"),
                    130 => throw new OperationCanceledException(),
                    _ => Task.CompletedTask,
                };
            }
            finally
            {
                businessReturned.TrySetResult();
            }
        }, () => Interlocked.Increment(ref disposedServices), timeProvider: clock));

        try
        {
            await blockedDirectory.Entered.Task.WaitAsync(TestTimeout);
            try
            {
                await businessReturned.Task.WaitAsync(TestTimeout);
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Child.Dispose prevented the actual Agent host from entering completion; state={scope.State}.");
            }
            await clock.WaitForTimerAsync(
                dueTime => dueTime > TimeSpan.Zero && dueTime <= LayerTelemetryScope.FileCompletionBudget, TestTimeout);
            await WaitForFileFenceAsync(scope).WaitAsync(TestTimeout);
            Assert.Equal(TelemetryLayerState.Fenced, scope.State);
            Assert.False(scope.FileSession.HasRecords);
            Assert.True(scope.TelemetryProvider.ActivitySource.HasListeners());
            clock.Advance(expireBudget ? LayerTelemetryScope.FileCompletionBudget : TimeSpan.FromSeconds(1));
            if (!expireBudget)
            {
                Assert.Equal(TelemetryLayerState.Fenced, scope.State);
                Assert.False(host.IsCompleted);
                blockedDirectory.Release();
            }

            Assert.Equal(expectedExit, await host.WaitAsync(TestTimeout));
            Assert.Equal(1, disposedServices);
            // Query only after the real host returned; this does not start completion from a side caller.
            Assert.Equal(expireBudget ? TelemetryLayerCompletion.NotPublished : TelemetryLayerCompletion.Published,
                await scope.CompleteAsync());
            Assert.Equal(!expireBudget, scope.FileSession.Published);
            Assert.Equal(!expireBudget, File.Exists(scope.FileSession.PublishedPath));
            Assert.Equal(expireBudget, scope.TelemetryProvider.ActivitySource.HasListeners());
            if (!expireBudget)
            {
                var entries = AgentLayerTestHarness.ReadEntries(scope);
                Assert.Equal(["Agent.Send", "Agent.Session"],
                    entries.Select(entry => entry.GetProperty("operationName").GetString()));
            }
        }
        finally
        {
            blockedDirectory.Release();
            try
            {
                await host.WaitAsync(TestTimeout);
                await WaitForProviderReleaseAsync(scope).WaitAsync(TestTimeout);
                await AgentLayerTestHarness.ReleaseAsync(scope);
                publishedAfterRelease = scope.FileSession.Published || File.Exists(scope.FileSession.PublishedPath);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }
        Assert.Equal(!expireBudget, publishedAfterRelease);
    }

    private static async Task WaitForProviderReleaseAsync(LayerTelemetryScope scope)
    {
        while (scope.TelemetryProvider.ActivitySource.HasListeners())
            await Task.Delay(1);
    }

    private static async Task WaitForFileFenceAsync(LayerTelemetryScope scope)
    {
        while (scope.State < TelemetryLayerState.Fenced)
            await Task.Delay(1);
    }

    private sealed class BlockingDirectory(string path) : ITraceDirectoryProvider, IDisposable
    {
        private readonly ManualResetEventSlim _released = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string GetTraceDirectory()
        {
            Entered.TrySetResult();
            if (!_released.Wait(TestTimeout))
                throw new TimeoutException("The controlled directory seam was not released.");
            return path;
        }

        internal void Release() => _released.Set();
        public void Dispose() => Release();
    }
}
