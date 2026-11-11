// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Abstractions;

namespace Core.Agent.AgentMode;

/// <summary>
/// Removes Agent configuration keys that are no longer read. Runs at Agent start only.
/// </summary>
internal static class RetiredAgentKeys
{
    internal static readonly string[] Keys = ["agent.enabled", "agent.sessionStyle", "agent.builtin.endpoint"];

    // Time limit for removing retired keys, including the configuration lock wait.
    internal static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(1);

    /// <summary>Never fails the Agent start: any error leaves the keys in place.</summary>
    internal static async Task TryRemoveAsync(IConfigurationService config, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        using var deadline = new CancellationTokenSource(CleanupBudget, timeProvider ?? TimeProvider.System);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var stored = await config.ListConfigurationAsync(cancellationToken: budget.Token).ConfigureAwait(false);
            foreach (var key in Keys.Where(stored.ContainsKey))
                await config.DeleteConfigurationAsync(key, force: true, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
        }
    }
}
