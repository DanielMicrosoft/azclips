// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Core.Abstractions;
using Core.Abstractions.Recovery;

namespace Core.Runtime.Recovery;

internal sealed class FailureHistoryFiles
{
    internal const string HistoryFileName = "history.json";
    internal const string LockFileName = "history.lock";

    private readonly TimeSpan _lockTimeout;
    private readonly Action<string, string> _replace;
    private readonly Action<string> _delete;
    private readonly TimeProvider _timeProvider;

    internal FailureHistoryFiles(
        string? directory = null,
        TimeSpan? lockTimeout = null,
        Action<string, string>? replace = null,
        Action<string>? delete = null,
        TimeProvider? timeProvider = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? HomePaths.GetDefaultFailureHistoryDirectory());
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_lockTimeout, TimeSpan.Zero);
        _replace = replace ?? Replace;
        _delete = delete ?? File.Delete;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal string DirectoryPath { get; }
    internal string HistoryPath => Path.Combine(DirectoryPath, HistoryFileName);

    internal async Task<FileStream> AcquireWriterLockAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await FileLock.TryAcquireAsync(Path.Combine(DirectoryPath, LockFileName), _lockTimeout, cancellationToken, ownerOnly: true, _timeProvider)
                .ConfigureAwait(false)
            ?? throw new FailureHistoryException("failure_history_locked", "Failure history is busy. Try again shortly.");
    }

    internal async Task<byte[]?> ReadHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            // Share delete so a reader never blocks a writer replacing the file.
            stream = new FileStream(HistoryPath, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }

        await using (stream.ConfigureAwait(false))
        {
            using var content = new MemoryStream();
            await stream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
            return content.ToArray();
        }
    }

    /// <summary>Writes a current-user-only temporary file and atomically replaces the history with it.</summary>
    internal async Task WriteHistoryAsync(byte[] content, CancellationToken cancellationToken)
    {
        var temp = $"{HistoryPath}.{Environment.ProcessId}.tmp";
        try
        {
            await using (var stream = OperatingSystem.IsWindows() ? CreateOwnerOnlyWindowsFile(temp) : CreateOwnerOnlyUnixFile(temp))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _replace(temp, HistoryPath);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception exception) when (IsStorageException(exception))
            {
                // Best effort; leftover temporary files are removed by DeleteControlledData.
            }
        }
    }

    /// <summary>Deletes the history, its temporary files, and finally the Windows key file.</summary>
    internal void DeleteControlledData(CancellationToken cancellationToken)
    {
        string[] entries;
        try { entries = Directory.GetFiles(DirectoryPath); }
        catch (DirectoryNotFoundException) { return; }

        foreach (var path in entries.Where(path => IsHistoryFile(Path.GetFileName(path))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _delete(path);
        }

        var key = Path.Combine(DirectoryPath, FailureHistoryKeyNames.FileName);
        if (File.Exists(key))
            _delete(key);
    }

    private static bool IsHistoryFile(string name) =>
        name == HistoryFileName ||
        (name.Length > HistoryFileName.Length + 5 &&
         name.StartsWith(HistoryFileName + ".", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal) &&
         uint.TryParse(name.AsSpan(HistoryFileName.Length + 1, name.Length - HistoryFileName.Length - 5), out _));

    // File.Replace succeeds while readers hold the target open with FileShare.Delete;
    // File.Move(overwrite) is refused on Windows in that case.
    private static void Replace(string source, string target)
    {
        if (File.Exists(target))
            File.Replace(source, target, destinationBackupFileName: null);
        else
            File.Move(source, target);
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyUnixFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Create,
        Access = FileAccess.Write,
        Share = FileShare.None,
        Options = FileOptions.Asynchronous,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
    });

    // Explicit, non-inherited DACL granting only the current user. File.Move keeps it; File.Replace keeps the target's ACL.
    [SupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyWindowsFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.Create, FileSystemRights.Read | FileSystemRights.Write,
            FileShare.None, bufferSize: 4096, FileOptions.Asynchronous, security);
    }

    internal static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;
}
