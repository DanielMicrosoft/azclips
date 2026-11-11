using System.Text.Json.Nodes;
using Core.Abstractions;
using Tests.Common.Utilities;
using Xunit;

namespace Core.Agent.Tests;

public sealed class FileLockTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    private readonly string _root = Directory.CreateTempSubdirectory("azclips-filelock-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Concurrent_updates_keep_every_writer_key()
    {
        var path = Path.Combine(_root, "state.json");

        Parallel.For(0, 40, i => Assert.True(FileLock.UpdateJson(path, document => document[$"k{i}"] = i, wait: TimeSpan.FromMinutes(1))));

        var document = FileLock.ReadJson(path);
        Assert.Equal(40, document.Count);
    }

    [Fact]
    public void A_held_lock_is_not_acquired_and_zero_wait_returns_at_once()
    {
        var lockPath = Path.Combine(_root, "x.lock");
        using var held = FileLock.TryAcquire(lockPath, TimeSpan.Zero);
        Assert.NotNull(held);

        Assert.Null(FileLock.TryAcquire(lockPath, TimeSpan.Zero));
    }

    [Fact]
    public void Update_waits_for_a_held_lock_then_writes()
    {
        var path = Path.Combine(_root, "state.json");
        File.WriteAllText(path, """{"a":1}""");
        using var held = FileLock.TryAcquire($"{path}.lock", TimeSpan.Zero);

        var updated = Task.Run(() => FileLock.UpdateJson(path, document => document["b"] = 2));

        Assert.False(updated.Wait(TimeSpan.FromSeconds(1)));
        held!.Dispose();
        Assert.True(updated.Wait(TestTimeout));
        Assert.True(updated.Result);
        Assert.Equal(2, FileLock.ReadJson(path)["b"]!.GetValue<int>());
    }

    [Fact]
    public void Concurrent_readers_do_not_make_updates_fail()
    {
        var path = Path.Combine(_root, "state.json");
        FileLock.UpdateJson(path, document => document["n"] = 0);
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                FileLock.ReadJson(path);
        });

        for (var i = 1; i <= 200; i++)
            Assert.True(FileLock.UpdateJson(path, document => document["n"] = i));

        stop.Cancel();
        reader.Wait();
        Assert.Equal(200, FileLock.ReadJson(path)["n"]!.GetValue<int>());
    }

    [Fact]
    public async Task Update_gives_up_after_the_requested_wait()
    {
        var path = Path.Combine(_root, "state.json");
        using var held = FileLock.TryAcquire($"{path}.lock", TimeSpan.Zero);
        var clock = new SignalingFakeTimeProvider();
        var wait = TimeSpan.FromMilliseconds(200);

        var updated = Task.Run(() => FileLock.UpdateJson(path, document => document["a"] = 1, wait, clock));
        await clock.WaitForTimerAsync(dueTime => dueTime > TimeSpan.Zero, TestTimeout);
        clock.Advance(wait);

        Assert.False(await updated.WaitAsync(TestTimeout));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Owner_only_lock_restricts_the_new_directory_and_lock_file_on_unix()
    {
        var directory = Path.Combine(_root, "private");
        var lockPath = Path.Combine(directory, "x.lock");

        using var held = FileLock.TryAcquire(lockPath, TimeSpan.Zero, ownerOnly: true);

        Assert.NotNull(held);
        Assert.Null(FileLock.TryAcquire(lockPath, TimeSpan.Zero));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(lockPath));
        }
    }

    [Fact]
    public void A_failed_write_leaves_no_temp_file()
    {
        // A directory at the target path makes the replace step throw.
        var path = Path.Combine(_root, "state.json");
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<Exception>(() => FileLock.UpdateJson(path, document => document["a"] = 1));

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Read_of_a_missing_or_corrupt_file_is_an_empty_object()
    {
        var path = Path.Combine(_root, "state.json");
        Assert.Empty(FileLock.ReadJson(path));

        File.WriteAllText(path, "{not json");
        Assert.Empty(FileLock.ReadJson(path));
    }
}
