// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Runtime.Recovery;
using System.Diagnostics;
using Core.Abstractions.Recovery;
using Core.Cli.Recovery;
using Xunit;

namespace Cli.Tests.Recovery;

public class PipeProcessRunnerTests
{
    [Fact]
    public async Task Cancellation_StopsTheOwnedChildEvenWhenItNeverReads()
    {
        var processId = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipeProcessRunner.RunAsync(
            _ => CreateChild(block: true), new string('x', 4_000_000), timeout.Token,
            (child, _) =>
            {
                processId = child.Id;
                return Task.CompletedTask;
            }));

        AssertStopped(processId);
    }

    [Fact]
    public async Task EarlyExit_StopsAnOutstandingLargeWrite()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var error = await Record.ExceptionAsync(() => PipeProcessRunner.RunAsync(
            _ => CreateChild(block: false), new string('x', 4_000_000), timeout.Token));

        Assert.True(error is IOException or InvalidOperationException, error?.ToString());
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task SynchronousReceiverFailure_StillStopsTheChild()
    {
        var processId = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var expected = new InvalidOperationException("synthetic receiver failure");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PipeProcessRunner.RunAsync(
            _ => CreateChild(block: true), new string('x', 4_000_000), timeout.Token,
            (child, _) =>
            {
                processId = child.Id;
                throw expected;
            }));

        Assert.Same(expected, error);
        AssertStopped(processId);
    }

    [Fact]
    public void WorkerStartup_UsesOnlyAPipeHandleAndDoesNotLaunchAnAgent()
    {
        var start = FailureHistoryWorker.BuildStartInfo("123");

        Assert.Equal(new[] { FailureHistoryWorker.Argument, "123" }, start.ArgumentList.TakeLast(2));
        Assert.DoesNotContain("--failed-command", start.ArgumentList);
        Assert.DoesNotContain(RecoveryRequest.InputPipeArgument, start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal("1", start.Environment["AZCLIPS_IN_AGENT_MODE"]);
    }

    private static ProcessStartInfo CreateChild(bool block)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(block ? "Start-Sleep -Seconds 30" : "exit 23");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(block ? "sleep 30" : "exit 23");
        }
        return start;
    }

    private static void AssertStopped(int processId)
    {
        Assert.NotEqual(0, processId);
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited, $"Owned child {processId} is still running.");
        }
        catch (ArgumentException)
        {
            // Exited processes need not remain in the operating system's process table.
        }
    }
}
