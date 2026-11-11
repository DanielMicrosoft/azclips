// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Abstractions;
using Core.Agent.AgentMode;
using Tests.Common.Utilities;

namespace Core.Agent.Tests;

public sealed class RetiredAgentKeysTests
{
    [Fact]
    public async Task RetiredKeys_AreDeletedAndOtherKeysAreKept()
    {
        var config = new RecordingConfiguration(new()
        {
            ["agent.enabled"] = true,
            ["agent.sessionStyle"] = "interactive",
            ["agent.builtin.endpoint"] = "https://example.test/",
            ["agent.provider"] = "github",
        });

        await RetiredAgentKeys.TryRemoveAsync(config, CancellationToken.None);

        Assert.Equal(["agent.enabled", "agent.sessionStyle", "agent.builtin.endpoint"], config.Deleted);
        Assert.Equal("github", config.Values["agent.provider"]);
    }

    [Fact]
    public async Task NoRetiredKeys_DeletesNothing()
    {
        var config = new RecordingConfiguration(new() { ["agent.provider"] = "builtin" });
        await RetiredAgentKeys.TryRemoveAsync(config, CancellationToken.None);
        Assert.Empty(config.Deleted);
    }

    [Fact]
    public async Task ConfigurationFailure_DoesNotFailAgentStart()
    {
        var config = new RecordingConfiguration([]) { Fail = true };
        await RetiredAgentKeys.TryRemoveAsync(config, CancellationToken.None);
        Assert.Empty(config.Deleted);
    }

    [Fact]
    public async Task SlowDelete_ReturnsWhenTheCleanupBudgetExpires()
    {
        var config = new RecordingConfiguration(new() { ["agent.enabled"] = true }) { HangOnDelete = true };
        var clock = new SignalingFakeTimeProvider();
        var testTimeout = TimeSpan.FromSeconds(60);

        var cleanup = RetiredAgentKeys.TryRemoveAsync(config, CancellationToken.None, clock);
        await clock.WaitForTimerAsync(RetiredAgentKeys.CleanupBudget, testTimeout);
        Assert.False(cleanup.IsCompleted);
        clock.Advance(RetiredAgentKeys.CleanupBudget);

        await cleanup.WaitAsync(testTimeout);
        Assert.Empty(config.Deleted);
    }

    private sealed class RecordingConfiguration(Dictionary<string, object?> values) : IConfigurationService
    {
        internal Dictionary<string, object?> Values { get; } = values;
        internal List<string> Deleted { get; } = [];
        internal bool Fail { get; init; }
        internal bool HangOnDelete { get; init; }

        public Task<Dictionary<string, object?>> ListConfigurationAsync(bool includeDefaults = false, CancellationToken cancellationToken = default) =>
            Fail ? throw new IOException("locked") : Task.FromResult(new Dictionary<string, object?>(Values));

        public Task DeleteConfigurationAsync(string key, bool force = false, CancellationToken cancellationToken = default)
        {
            if (HangOnDelete)
                return Task.Delay(Timeout.Infinite, cancellationToken);
            Deleted.Add(key);
            Values.Remove(key);
            return Task.CompletedTask;
        }

        public T GetConfiguration<T>(string? key = null, T? defaultValue = null) where T : class, new() => throw new NotSupportedException();
        public Task SetConfigurationAsync(string key, object? value, bool force = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<object?> ResolveDefaultParameterAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string GetConfigFilePath() => throw new NotSupportedException();
        public IConfigurationSchema GetSchema() => throw new NotSupportedException();
    }
}
