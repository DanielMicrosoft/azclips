// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Runtime.Recovery;
using System.Diagnostics;
using System.Text.Json;
using Core.Abstractions;
using Core.Abstractions.Recovery;
using Core.Cli.Help;
using Core.Cli.Parser;

namespace Core.Cli.Recovery;

internal sealed record FailureCaptureResult(
    FailureHistoryEntry? Entry = null, bool ShowHint = false, string? Warning = null, bool Canceled = false,
    Exception? Cause = null)
{
    internal const string UnencryptedWarning =
        "Secure OS key storage is unavailable, so this failure was saved without encryption in a file only your user account can read. " +
        "Only the latest failure is kept this way; it is deleted on the next save once secure storage is available.";

    internal const string KeyReplacedWarning = EncryptedFailureHistoryStore.KeyReplacedMessage;

    internal const string DefaultHint =
        "Or run az troubleshoot recover without --id to troubleshoot the latest error in this terminal.";

    internal object? Recovery => ShowHint && Entry is not null
        ? new
        {
            id = Entry.Id,
            command = $"az troubleshoot recover --id {Entry.Id}",
            defaultCommand = "az troubleshoot recover",
            defaultDescription = DefaultHint,
        }
        : null;
}

internal static class FailureCapture
{
    internal static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(2);

    internal static bool ShouldCapture(string[] args, IReadOnlyList<string> positional, Exception error, bool inAgentMode) =>
        !inAgentMode &&
        !IsTroubleshootInvocation(positional) &&
        (args.Length == 0 || args[0] is not ("--upload-telemetry" or "--completions" or "--completion-candidates" or
                                           "--print-probe" or FailureHistoryWorker.Argument));

    // Compares only the first positional token after an optional leading "help";
    // it does not validate or resolve the command.
    private static bool IsTroubleshootInvocation(IReadOnlyList<string> positional)
    {
        var start = positional.Count > 0 && positional[0].Equals("help", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return positional.Count > start && positional[start].Equals("troubleshoot", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<FailureCaptureResult> SaveAsync(
        string[] args,
        IReadOnlyList<string> positional,
        string failedCommand,
        Exception error,
        int exitCode,
        string? commandName,
        Activity? activity,
        Func<AgentOptions> getOptions,
        Func<IFailureHistoryStore> getStore,
        bool inAgentMode,
        CancellationToken cancellationToken = default,
        Func<FailureRecord, int, CancellationToken, Task<FailureHistoryEntry>>? persist = null,
        Func<uint>? getParentProcessId = null,
        TimeProvider? timeProvider = null)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(Canceled: true);
        if (exitCode == 0 || !ShouldCapture(args, positional, error, inAgentMode))
            return new();

        using var userCancellation = new ConsoleCancellationScope(cancellationToken);
        using var budget = new CancellationTokenSource(CaptureBudget, timeProvider ?? TimeProvider.System);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(userCancellation.Token, budget.Token);
        try
        {
            // Off is decided from configuration alone: no store, history directory, or control file is touched.
            // Unreadable configuration counts as off.
            AgentOptions options;
            try { options = getOptions(); }
            catch (Exception) { return new(); }
            if (!options.Recovery.IsEnabled)
                return new();

            var parentId = (getParentProcessId ?? TerminalSession.GetParentProcessId)();
            var record = CreateRecord(args, failedCommand, error, exitCode, commandName, activity, parentId);
            var save = persist ?? getStore().SaveAsync;
            var entry = await save(record, options.Recovery.HistorySize, deadline.Token).ConfigureAwait(false);
            string?[] warnings =
            [
                entry.KeyReplaced ? FailureCaptureResult.KeyReplacedWarning : null,
                entry.Unencrypted ? FailureCaptureResult.UnencryptedWarning : null,
            ];
            var warning = string.Join(" ", warnings.OfType<string>());
            return new(entry, !options.Recovery.IsAutomatic, warning.Length > 0 ? warning : null);
        }
        catch (OperationCanceledException) when (userCancellation.Token.IsCancellationRequested)
        {
            return new(Canceled: true);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new(Warning: "Secure failure history exceeded its time limit. Saving was not confirmed.");
        }
        catch (FailureHistoryException ex)
        {
            return new(Warning: $"The failure could not be saved securely ({ex.Code}). {ex.Message}", Cause: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
                                      InvalidOperationException or NotSupportedException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            // Do not echo configuration values or the raw diagnostic a second time.
            return new(Warning: "The failure could not be saved securely. Check the local history directory and Agent configuration.", Cause: ex);
        }
    }

    private static FailureRecord CreateRecord(
        string[] args, string failedCommand, Exception error, int exitCode, string? commandName,
        Activity? activity, uint parentId)
    {
        var structured = error as AzclipsErrorException;
        return new FailureRecord
        {
            Id = string.Empty,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            TerminalSessionId = parentId,
            Arguments = args.ToArray(),
            FailedCommand = failedCommand,
            ErrorText = error.Message,
            ExitCode = exitCode,
            CommandName = commandName,
            ErrorCode = structured?.Code,
            ExceptionType = error.GetType().FullName,
            ErrorDetails = structured is null ? null : JsonSerializer.SerializeToElement(structured.Details),
            HttpStatusCode = error is HttpRequestException { StatusCode: { } status } ? (int)status : null,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            ProfileName = Program.TryGetProfileOverride(args),
            ClientVersion = typeof(Program).Assembly.GetName().Version?.ToString(),
            CommandTraceId = activity?.TraceId.ToHexString(),
            CommandSpanId = activity?.SpanId.ToHexString(),
        };
    }
}
