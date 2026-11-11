// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Core.Abstractions;

/// <summary>
/// Cross-process lock over a lock file (opened with <see cref="FileShare.None"/>), and locked
/// read-modify-write of shared JSON files such as <see cref="HomePaths.GetDefaultAgentStatePath"/>.
/// Writers serialize on the lock; readers do not take it.
/// </summary>
public static class FileLock
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the lock. Returns <c>null</c> when it is still
    /// held by another process; a zero wait tries once. Dispose the stream to release the lock.
    /// With <paramref name="ownerOnly"/>, on Unix the directory is created 0700 and the lock file 0600.
    /// <paramref name="timeProvider"/> measures the wait (default <see cref="TimeProvider.System"/>).
    /// </summary>
    public static async Task<FileStream?> TryAcquireAsync(
        string lockPath, TimeSpan wait, CancellationToken ct = default, bool ownerOnly = false, TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (ownerOnly && !OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else
                Directory.CreateDirectory(directory);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (ownerOnly && !OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var started = timeProvider.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, options);
            }
            catch (IOException) when (timeProvider.GetElapsedTime(started) < wait)
            {
                await Task.Delay(RetryDelay, timeProvider, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public static FileStream? TryAcquire(string lockPath, TimeSpan wait, bool ownerOnly = false, TimeProvider? timeProvider = null)
        => TryAcquireAsync(lockPath, wait, ownerOnly: ownerOnly, timeProvider: timeProvider).GetAwaiter().GetResult();

    /// <summary>Reads a JSON object without locking; a missing or unreadable file yields an empty object.</summary>
    public static JsonObject ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            // Share delete so a reader never blocks a writer replacing the file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream) is JsonObject document ? document : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Under <c>&lt;path&gt;.lock</c>, reads the current document, applies <paramref name="update"/>
    /// and replaces the file. Keys the update does not touch are kept. Returns <c>false</c> when
    /// the lock is not obtained within <paramref name="wait"/> (default <see cref="DefaultWait"/>).
    /// </summary>
    public static bool UpdateJson(string path, Action<JsonObject> update, TimeSpan? wait = null, TimeProvider? timeProvider = null)
    {
        using var gate = TryAcquire($"{path}.lock", wait ?? DefaultWait, timeProvider: timeProvider);
        if (gate is null)
            return false;

        var document = ReadJson(path);
        update(document);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temp, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // File.Replace succeeds while readers hold the target open with FileShare.Delete;
            // File.Move(overwrite) is refused on Windows in that case.
            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return true;
    }
}
