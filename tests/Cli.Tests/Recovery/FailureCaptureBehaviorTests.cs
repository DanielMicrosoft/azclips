// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Abstractions;
using Core.Abstractions.Recovery;
using Core.Cli;
using Core.Cli.Parser;
using Core.Cli.Recovery;
using Core.Runtime.Recovery;
using Moq;
using Tests.Common.Utilities;
using Xunit;

namespace Cli.Tests.Recovery;

[Collection("Azclips Service Provider Tests")]
public class FailureCaptureBehaviorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    // Mirrors Program.Main: parsed positionals, or the raw arguments when global parsing fails.
    private static string[] Positional(string[] args)
    {
        try { return GlobalArgParser.Parse(args).Positional.ToArray(); }
        catch (AzclipsErrorException) { return args; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(true, "help", "no-such-group")]
    [InlineData(true, "version", "--query", "[")]
    [InlineData(false, "troubleshoot", "--id", "missing")]
    [InlineData(false, "help", "troubleshoot")]
    [InlineData(false, "--print-probe")]
    [InlineData(true, "vm", "show")]
    [InlineData(true, "vm", "create", "--name", "help")]
    [InlineData(true, "vm", "create", "--query", "--help")]
    [InlineData(true, "--query", "version", "vm", "show")]
    [InlineData(false, "--output=json", "help", "troubleshoot")]
    [InlineData(true, "vm", "show", "--name", "troubleshoot")]
    [InlineData(true, "--query", "troubleshoot", "vm", "show")]
    public void Eligibility_ExcludesTroubleshootAndInternalCommands(bool expected, params string[] args)
    {
        Assert.Equal(expected, FailureCapture.ShouldCapture(args, Positional(args), new IOException("original failure"), false));
    }

    [Fact]
    public async Task Save_PreservesInputAndOriginalTerminalBeforeWorkerLaunch()
    {
        var store = CreateStore();
        var command = "vm create --name \"two words\" --value \u4e2d\u6587";
        var error = new InvalidOperationException(" \r\n" + new string('x', 80_000) + "\t");
        var arguments = new[] { "vm", "create", "--name", "two words" };
        FailureRecord? sent = null;
        var entry = new FailureHistoryEntry("321-0a1b2c3d", DateTimeOffset.UtcNow);

        var result = await FailureCapture.SaveAsync(arguments, Positional(arguments), command, error, 1, "vm create", null,
            () => Manual(), () => store.Object, false,
            persist: (record, size, _) =>
            {
                Assert.Equal(10, size);
                sent = record;
                return Task.FromResult(entry);
            }, getParentProcessId: () => 321);

        Assert.NotNull(sent);
        Assert.Equal(command, sent.FailedCommand);
        Assert.Equal(error.Message, sent.ErrorText);
        Assert.Equal(arguments, sent.Arguments);
        Assert.Equal(321u, sent.TerminalSessionId);
        Assert.Empty(sent.Id);
        Assert.Same(entry, result.Entry);
        Assert.True(result.ShowHint);
        store.Verify(s => s.SaveAsync(It.IsAny<FailureRecord>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Save_WarnsOnlyWhenTheFailureWasStoredUnencrypted(bool unencrypted)
    {
        var store = CreateStore();
        var entry = new FailureHistoryEntry("321-0a1b2c3d", DateTimeOffset.UtcNow, unencrypted);

        var result = await FailureCapture.SaveAsync(["vm", "show"], Positional(["vm", "show"]), "vm show", new IOException("original failure"), 1, "vm show", null,
            () => Manual(), () => store.Object, false,
            persist: (_, _, _) => Task.FromResult(entry), getParentProcessId: () => 321);

        Assert.Same(entry, result.Entry);
        Assert.True(result.ShowHint);
        Assert.Equal(unencrypted ? FailureCaptureResult.UnencryptedWarning : null, result.Warning);
    }

    [Fact]
    public async Task Save_WarnsOnceWhenADamagedKeyWasReplaced()
    {
        var store = CreateStore();
        var entry = new FailureHistoryEntry("321-0a1b2c3d", DateTimeOffset.UtcNow, KeyReplaced: true);

        var result = await FailureCapture.SaveAsync(["vm", "show"], Positional(["vm", "show"]), "vm show", new IOException("original failure"), 1, "vm show", null,
            () => Manual(), () => store.Object, false,
            persist: (_, _, _) => Task.FromResult(entry), getParentProcessId: () => 321);

        Assert.Same(entry, result.Entry);
        Assert.Equal("The failure-history key was damaged and has been replaced. Earlier failures can no longer be read.", result.Warning);
    }

    [Fact]
    public async Task Save_ReportsAReplacedKeyAndAnUnencryptedSaveTogether()
    {
        var store = CreateStore();
        var entry = new FailureHistoryEntry("321-0a1b2c3d", DateTimeOffset.UtcNow, Unencrypted: true, KeyReplaced: true);

        var result = await FailureCapture.SaveAsync(["vm", "show"], Positional(["vm", "show"]), "vm show", new IOException("original failure"), 1, "vm show", null,
            () => Manual(), () => store.Object, false,
            persist: (_, _, _) => Task.FromResult(entry), getParentProcessId: () => 321);

        Assert.StartsWith("The failure-history key was damaged and has been replaced. Earlier failures can no longer be read. ", result.Warning);
        Assert.EndsWith(FailureCaptureResult.UnencryptedWarning, result.Warning);
    }

    [Fact]
    public async Task Save_TimeoutDoesNotClaimACommit()
    {
        var store = CreateStore();
        var clock = new SignalingFakeTimeProvider();
        var save = FailureCapture.SaveAsync(["vm", "show"], Positional(["vm", "show"]), "vm show", new IOException("original"), 1,
            "vm show", null, () => Manual(), () => store.Object, false,
            persist: async (_, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new Xunit.Sdk.XunitException("The timed-out writer must not return an entry.");
            }, getParentProcessId: () => 321, timeProvider: clock);
        await clock.WaitForTimerAsync(FailureCapture.CaptureBudget, TestTimeout);
        Assert.False(save.IsCompleted);
        clock.Advance(FailureCapture.CaptureBudget);
        var result = await save.WaitAsync(TestTimeout);

        Assert.Equal(TimeSpan.FromSeconds(2), FailureCapture.CaptureBudget);
        Assert.Null(result.Entry);
        Assert.Null(result.Recovery);
        Assert.False(result.ShowHint);
        Assert.Contains("not confirmed", result.Warning);
    }

    [Fact]
    public async Task Save_UserCancellationDoesNotBecomeAStorageWarning()
    {
        var store = CreateStore();
        using var user = new CancellationTokenSource();
        var result = await FailureCapture.SaveAsync(["vm", "show"], Positional(["vm", "show"]), "vm show", new IOException("original"), 1,
            "vm show", null, () => Manual(), () => store.Object, false, user.Token,
            persist: (_, _, ct) =>
            {
                user.Cancel();
                return Task.FromCanceled<FailureHistoryEntry>(ct);
            }, getParentProcessId: () => 321);

        Assert.True(result.Canceled);
        Assert.Null(result.Entry);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task Save_HttpTimeoutIsAnOriginalFailureRatherThanUserCancellation()
    {
        var store = CreateStore();
        var result = await FailureCapture.SaveAsync(["vm", "show"], Positional(["vm", "show"]), "vm show",
            new TaskCanceledException("HTTP request timeout", new TimeoutException()), 1, "vm show", null,
            () => Manual(), () => store.Object, false,
            persist: (_, _, _) => Task.FromResult(new FailureHistoryEntry("321-0a1b2c3d", DateTimeOffset.UtcNow)),
            getParentProcessId: () => 321);

        Assert.NotNull(result.Entry);
        Assert.False(result.Canceled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    public async Task Save_OffNeverTouchesHistoryStoreOrDirectory(string? trigger)
    {
        using var scope = new FailureHistoryTestScope();
        var optionReads = 0;
        var result = await FailureCapture.SaveAsync(["vm", "show"], Positional(["vm", "show"]), "vm show", new IOException("original failure"),
            1, "vm show", null,
            () => { optionReads++; return trigger is null ? new AgentOptions() : new() { Recovery = new() { Trigger = trigger } }; },
            () => throw new InvalidOperationException("Off must not resolve the history store."), false,
            persist: (_, _, _) => throw new InvalidOperationException("Off must not launch a writer."),
            getParentProcessId: () => throw new InvalidOperationException("Off must not resolve a terminal."));
        Assert.Null(result.Entry);
        Assert.Null(result.Warning);
        Assert.False(result.ShowHint);
        Assert.Equal(1, optionReads);
        Assert.False(Directory.Exists(scope.DirectoryPath));
    }

    [Fact]
    public void RecoveryDiagnostic_WritesBothHintsAndNeverTheOriginalError()
    {
        using var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            var entry = new FailureHistoryEntry("321-0a1b2c3d", DateTimeOffset.UtcNow);
            Program.EmitRecoveryDiagnostic(new FailureCaptureResult(entry, ShowHint: true));

            var text = stderr.ToString();
            Assert.Contains($"az troubleshoot recover --id {entry.Id}", text);
            Assert.Contains(FailureCaptureResult.DefaultHint, text);
            Assert.DoesNotContain("ERROR", text);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public void RecoveryWarning_IsWrittenAsTextToStderr()
    {
        const string warning = "Use 'agent.recovery.trigger': 中文 <detail>.";
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        var previousError = Console.Error;
        var previousOutput = Console.Out;
        Console.SetError(stderr);
        Console.SetOut(stdout);
        try
        {
            Program.EmitRecoveryDiagnostic(new FailureCaptureResult(Warning: warning));

            Assert.Empty(stdout.ToString());
            Assert.Contains(warning, stderr.ToString());
        }
        finally
        {
            Console.SetError(previousError);
            Console.SetOut(previousOutput);
        }
    }

    [Fact]
    public void RecoveryDiagnostic_WritesOnlyTheWarningWhenCaptureIsUnconfirmed()
    {
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        var previousError = Console.Error;
        var previousOutput = Console.Out;
        Console.SetError(stderr);
        Console.SetOut(stdout);
        try
        {
            Program.EmitRecoveryDiagnostic(new FailureCaptureResult(Warning: "capture was not confirmed"));

            var output = stderr.ToString();
            Assert.Contains("capture was not confirmed", output);
            Assert.DoesNotContain("To troubleshoot", output);
            Assert.DoesNotContain("ERROR", output);
            Assert.Empty(stdout.ToString());
        }
        finally
        {
            Console.SetError(previousError);
            Console.SetOut(previousOutput);
        }
    }

    [Fact]
    public async Task Save_StorageFailureKeepsTheCauseForDebugOnly()
    {
        using var scope = new FailureHistoryTestScope(replace: (_, _) =>
            throw new IOException("The process cannot access the file.", unchecked((int)0x80070020)));
        var result = await FailureCapture.SaveAsync(["vm", "show"], Positional(["vm", "show"]), "vm show", new IOException("original"), 1,
            "vm show", null, () => Manual(), () => scope.CreateStore(), false, getParentProcessId: () => 321);

        var cause = Assert.IsType<FailureHistoryException>(result.Cause);
        Assert.Equal("failure_history_io", cause.Code);
        Assert.Equal(
            "The failure could not be saved securely (failure_history_io). Failure history could not be accessed or saved. " +
            "Check its directory permissions and available disk space.", result.Warning);

        var logger = new Mock<ILoggingService>();
        string? debug = null;
        logger.Setup(log => log.Debug(It.IsAny<string>())).Callback<string>(message => debug = message);
        FailureHistoryDiagnostics.EmitCauseChain(cause, logger.Object);

        Assert.NotNull(debug);
        Assert.StartsWith("error cause: Core.Abstractions.Recovery.FailureHistoryException: Failure history could not be accessed or saved.", debug);
        Assert.Contains("---> System.IO.IOException: The process cannot access the file.", debug);
        Assert.Contains(" at ", debug);
        Assert.DoesNotContain("vm show", debug);
        Assert.DoesNotContain("private-resource-name", debug);
    }

    [Fact]
    public void CauseChain_IsNotWrittenForUnrelatedErrors()
    {
        var logger = new Mock<ILoggingService>();
        FailureHistoryDiagnostics.EmitCauseChain(new AzclipsErrorException("Other", 1, "outer", innerException: new IOException("inner")), logger.Object);
        FailureHistoryDiagnostics.EmitCauseChain(new FailureHistoryException("failure_history_io", "no cause"), logger.Object);
        logger.Verify(log => log.Debug(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RecoveryWarning_DefaultLevelOutputIsUnchangedByTheCause()
    {
        const string warning = "The failure could not be saved securely (failure_history_io). Failure history could not be accessed or saved.";
        var cause = new FailureHistoryException("failure_history_io", "Failure history could not be accessed or saved.", new IOException("private"));
        using var stderr = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(stderr);
        var previousLogger = SetProgramLogger(new Core.Runtime.ScopedLoggingService());
        try
        {
            Program.EmitRecoveryDiagnostic(new FailureCaptureResult(Warning: warning, Cause: cause));
            Assert.Equal($"WARNING: {warning}{Environment.NewLine}", stderr.ToString());
        }
        finally
        {
            SetProgramLogger(previousLogger);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public void RecoveryWarning_DebugLevelWritesTheFullCause()
    {
        var cause = new FailureHistoryException("failure_history_io", "Failure history could not be accessed or saved.",
            Thrown(new IOException("The process cannot access the file.")));
        var (stderr, _) = RunWithProgramLogger("debug",
            () => Program.EmitRecoveryDiagnostic(new FailureCaptureResult(Warning: "not saved", Cause: cause)));

        Assert.StartsWith($"WARNING: not saved{Environment.NewLine}DEBUG: error cause: Core.Abstractions.Recovery.FailureHistoryException: " +
            "Failure history could not be accessed or saved.", stderr);
        Assert.Contains("---> System.IO.IOException: The process cannot access the file.", stderr);
        Assert.Contains($"{Environment.NewLine}   at ", stderr);
    }

    private static Exception Thrown(Exception ex)
    {
        try { throw ex; }
        catch (Exception caught) { return caught; }
    }

    // Runs the action with Program's logger configured at the given core.logging.level and returns stderr.
    private static (string Stderr, string Stdout) RunWithProgramLogger(string? level, Action action)
    {
        var configuration = new Mock<IConfigurationService>();
        configuration.Setup(config => config.GetConfiguration<CoreOptions>())
            .Returns(new CoreOptions { Logging = { Level = level ?? "warn" } });
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        var previousError = Console.Error;
        var previousOutput = Console.Out;
        Console.SetError(stderr);
        Console.SetOut(stdout);
        var previousLogger = SetProgramLogger(new Core.Runtime.ScopedLoggingService(configuration.Object));
        try
        {
            action();
        }
        finally
        {
            SetProgramLogger(previousLogger);
            Console.SetError(previousError);
            Console.SetOut(previousOutput);
        }
        return (stderr.ToString(), stdout.ToString());
    }

    private static ILoggingService? SetProgramLogger(ILoggingService? logger)
    {
        var field = typeof(Program).GetField("_logger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var previous = (ILoggingService?)field.GetValue(null);
        field.SetValue(null, logger);
        return previous;
    }

    [Fact]
    public void TerminalSession_IdentifiesALiveParentRatherThanTheCurrentProcess()
    {
        var parentId = TerminalSession.GetParentProcessId();
        Assert.NotEqual((uint)Environment.ProcessId, parentId);
        using var parent = Process.GetProcessById(checked((int)parentId));
        Assert.False(parent.HasExited);
    }

    [Fact]
    public async Task Worker_StorageFailureCarriesTheChildRootCause()
    {
        var home = Directory.CreateTempSubdirectory("azclips-worker-cause-").FullName;
        var previousHome = Environment.GetEnvironmentVariable(HomePaths.HomeOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(HomePaths.HomeOverrideVariable, home);
            File.WriteAllText(Path.Combine(home, "config.json"), """{"telemetry":{"enabled":false}}""");
            // A file where the history directory belongs makes the child's lock acquisition fail with IOException.
            var historyDirectory = HomePaths.GetDefaultFailureHistoryDirectory();
            Directory.CreateDirectory(Path.GetDirectoryName(historyDirectory)!);
            File.WriteAllText(historyDirectory, "");

            var error = await Assert.ThrowsAsync<FailureHistoryException>(() =>
                FailureHistoryWorker.SaveAsync(FailureHistoryTestScope.CreateRecord(), 10, CancellationToken.None));

            Assert.Equal("failure_history_io", error.Code);
            Assert.NotNull(error.InnerException);
            var cause = error.InnerException!.ToString();
            Assert.Contains("FailureHistoryException", cause);
            Assert.Contains("System.IO.IOException", cause);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HomePaths.HomeOverrideVariable, previousHome);
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_DebugParentShowsTheChildStoreDebugLine()
    {
        var home = Directory.CreateTempSubdirectory("azclips-worker-debug-").FullName;
        var previousHome = Environment.GetEnvironmentVariable(HomePaths.HomeOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(HomePaths.HomeOverrideVariable, home);
            File.WriteAllText(Path.Combine(home, "config.json"), """{"telemetry":{"enabled":false}}""");
            // A directory where the key file belongs makes key storage unavailable, so the child saves unencrypted.
            Directory.CreateDirectory(Path.Combine(HomePaths.GetDefaultFailureHistoryDirectory(), FailureHistoryKeyNames.FileName));
            var debug = new List<string>();

            var entry = await FailureHistoryWorker.SaveAsync(
                FailureHistoryTestScope.CreateRecord(), 10, CancellationToken.None, DebugLogger(debug));

            Assert.True(entry.Unencrypted);
            var line = Assert.Single(debug);
            Assert.StartsWith("history worker stderr: ", line);
            Assert.Contains("Failure-history key unavailable; saving unencrypted", line);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HomePaths.HomeOverrideVariable, previousHome);
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void WorkerStartup_PassesDebugLevelOnlyWhenTheParentIsAtDebug()
    {
        var inherited = Environment.GetEnvironmentVariable("AZCLIPS_LOG_LEVEL");
        Environment.SetEnvironmentVariable("AZCLIPS_LOG_LEVEL", null);
        try
        {
            Assert.False(FailureHistoryWorker.BuildStartInfo("123").Environment.ContainsKey("AZCLIPS_LOG_LEVEL"));
            Assert.Equal("debug", FailureHistoryWorker.BuildStartInfo("123", debug: true).Environment["AZCLIPS_LOG_LEVEL"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZCLIPS_LOG_LEVEL", inherited);
        }
    }

    [Fact]
    public async Task Worker_LargeStderrIsDrainedButKeptBounded()
    {
        var debug = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var error = await Assert.ThrowsAsync<FailureHistoryException>(() => FailureHistoryWorker.SaveAsync(
            FailureHistoryTestScope.CreateRecord(), 10, deadline.Token, DebugLogger(debug), TestWorker("stderr-flood")));

        Assert.Equal("flood_done", error.Code);
        Assert.Equal("history worker stderr: ".Length + 64 * 1024, Assert.Single(debug).Length);
    }

    [Fact]
    public async Task Worker_ChildThatHangsKeepsItsPartialStderrAtTheDeadline()
    {
        var debug = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FailureHistoryWorker.SaveAsync(
            FailureHistoryTestScope.CreateRecord(), 10, deadline.Token, DebugLogger(debug), TestWorker("stderr-hang")));

        Assert.Equal("history worker stderr: partial-diagnostic-before-hang", Assert.Single(debug));
    }

    [Fact]
    public async Task Worker_ChildThatHangsKeepsPartialMultiByteStderrAtTheDeadline()
    {
        var debug = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FailureHistoryWorker.SaveAsync(
            FailureHistoryTestScope.CreateRecord(), 10, deadline.Token, DebugLogger(debug), TestWorker("stderr-utf8-hang")));

        Assert.Equal("history worker stderr: " + new string('é', 2048), Assert.Single(debug));
    }

    [Fact]
    public async Task Worker_ChildThatExitsBeforeJsonStillShowsItsStderr()
    {
        var debug = new List<string>();

        await Assert.ThrowsAnyAsync<Exception>(() => FailureHistoryWorker.SaveAsync(
            FailureHistoryTestScope.CreateRecord(), 10, CancellationToken.None, DebugLogger(debug), TestWorker("stderr-early-exit")));

        Assert.Equal("history worker stderr: diagnostic-before-exit", Assert.Single(debug));
    }

    [Fact]
    public async Task WorkerStartFailure_ReachesDebugOutput()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        var result = await FailureCapture.SaveAsync(["vm", "show"], Positional(["vm", "show"]), "vm show", new IOException("original"), 1,
            "vm show", null, () => Manual(), () => CreateStore().Object, false,
            persist: (record, max, ct) => FailureHistoryWorker.SaveAsync(record, max, ct, null,
                _ => new ProcessStartInfo(missing) { UseShellExecute = false }),
            getParentProcessId: () => 321);

        Assert.Equal("The failure could not be saved securely. Check the local history directory and Agent configuration.", result.Warning);
        var (stderr, _) = RunWithProgramLogger("debug", () => Program.EmitRecoveryDiagnostic(result));
        Assert.Contains("DEBUG: error cause: System.ComponentModel.Win32Exception", stderr);
    }

    private static ILoggingService DebugLogger(List<string> debug)
    {
        var logger = new Mock<ILoggingService>(MockBehavior.Strict);
        logger.Setup(log => log.Debug(It.IsAny<string>())).Callback<string>(debug.Add);
        return logger.Object;
    }

    // Starts the offline test worker in one of its stderr modes in place of the CLI history worker.
    private static Func<string, ProcessStartInfo> TestWorker(string mode) => handle =>
    {
        var root = Path.GetDirectoryName(typeof(FailureCaptureBehaviorTests).Assembly.Location);
        while (root is not null && !File.Exists(Path.Combine(root, "azclips.slnx")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var configuration = typeof(FailureCaptureBehaviorTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(root, "tests", "Fixtures", "FailureHistoryWorker", "bin", configuration, "net10.0", "FailureHistory.TestWorker.dll"));
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(handle);
        return start;
    };

    private static AgentOptions Manual() => new() { Recovery = new() { Trigger = "manual" } };

    private static Mock<IFailureHistoryStore> CreateStore()
    {
        return new Mock<IFailureHistoryStore>(MockBehavior.Strict);
    }
}
