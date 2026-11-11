// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Abstractions;
using Core.Abstractions.Recovery;
using Core.Runtime.Recovery;
using Moq;
using Tests.Common.Utilities;
using static Cli.Tests.Recovery.FailureHistoryTestScope;

namespace Cli.Tests.Recovery;

[Collection("Azclips Service Provider Tests")]
public sealed class EncryptedFailureHistoryStoreTests
{
    [Fact]
    public async Task TwelveWrites_RetainExactlyLatestTenBySequenceDespiteClockRollback()
    {
        using var scope = new FailureHistoryTestScope();
        var keys = new TestFailureHistoryKeys();
        var store = scope.CreateStore(keys);
        var written = new List<FailureRecord>();
        for (var index = 0; index < 12; index++)
        {
            var record = CreateRecord() with { OccurredAtUtc = CreateRecord().OccurredAtUtc.AddDays(-index) };
            var entry = await store.SaveAsync(record, 10);
            written.Add(Committed(record, entry, (ulong)index));
        }

        var records = await store.ReadRecordsAsync();
        var expected = written.Skip(2).Reverse().ToArray();
        Assert.Equal(expected.Select(record => record.Id), records.Select(record => record.Id));
        Assert.Equal(expected.Select(record => new FailureHistoryEntry(record.Id, record.OccurredAtUtc)), await store.ReadIndexAsync());
        AssertKeysCleared(keys);
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task LowerLimit_PrunesOnlyOnASuccessfulWrite()
    {
        using var scope = new FailureHistoryTestScope();
        var store = scope.CreateStore();
        var written = new List<FailureHistoryEntry>();
        for (var index = 0; index < 12; index++)
            written.Add(await store.SaveAsync(CreateRecord(), 20));
        var original = await File.ReadAllBytesAsync(scope.Files.HistoryPath);
        Assert.Equal(12, (await store.ReadRecordsAsync()).Count);
        Assert.Equal(original, await File.ReadAllBytesAsync(scope.Files.HistoryPath));
        var next = await scope.CreateStore().SaveAsync(CreateRecord(), 3);
        Assert.Equal(new[] { next.Id, written[^1].Id, written[^2].Id }, (await store.ReadRecordsAsync()).Select(record => record.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task InvalidLimitHint_IsRejectedBeforeIO(int limit)
    {
        using var scope = new FailureHistoryTestScope();
        var keys = new TestFailureHistoryKeys();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scope.CreateStore(keys).SaveAsync(CreateRecord(), limit));
        Assert.False(Directory.Exists(scope.DirectoryPath));
        Assert.Empty(keys.CreateRequests);
    }

    [Fact]
    public async Task EmptyReads_AreSideEffectFree()
    {
        using var scope = new FailureHistoryTestScope();
        var keys = NoKeyAccess();
        var store = scope.CreateStore(keys);
        Assert.Empty(await store.ReadIndexAsync());
        Assert.Empty(await store.ReadRecordsAsync());
        Assert.False(Directory.Exists(scope.DirectoryPath));
        Assert.Empty(keys.CreateRequests);
    }

    [Fact]
    public async Task Reads_DoNotWaitForTheWriterLock()
    {
        using var scope = new FailureHistoryTestScope();
        var entry = await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        var lockPath = Path.Combine(scope.DirectoryPath, EncryptedFailureHistoryStore.LockFileName);
        using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(entry, Assert.Single(await scope.CreateStore(NoKeyAccess()).ReadIndexAsync()));
        Assert.Equal(entry.Id, Assert.Single(await scope.CreateStore().ReadRecordsAsync()).Id);
    }

    [Fact]
    public async Task CompleteRecord_RoundTripsLongRawInputsWithoutPlaintextOnDisk()
    {
        using var scope = new FailureHistoryTestScope();
        const string marker = "failure-history-secret-marker-8b7e489d";
        var raw = marker + string.Concat(Enumerable.Repeat(" 中文 🚀 \"quoted\" \\' $variable\r\n\t", 4_000));
        var pending = CreateRecord() with
        {
            Arguments = ["vm", "create", "--raw", raw, "", "\r\n", "\"literal quotes\""],
            FailedCommand = $"az vm create --raw \"{raw}\"",
            ErrorText = raw + "\nlast diagnostic line",
            CommandName = "vm create", ErrorCode = "Conflict", ExceptionType = "Contoso.CommandException",
            ErrorDetails = JsonSerializer.SerializeToElement(new { detail = "private-error-details" }),
            HttpStatusCode = 409, WorkingDirectory = @"C:\Users\某人\quoted directory",
            ProfileName = "private-profile", ClientVersion = "1.2.3",
            CommandTraceId = "0123456789abcdef0123456789abcdef", CommandSpanId = "0123456789abcdef", ExitCode = -17,
        };
        var keys = new TestFailureHistoryKeys();
        var entry = await scope.CreateStore(keys).SaveAsync(pending, 10);
        var actual = Assert.Single(await scope.CreateStore(keys).ReadRecordsAsync());
        Assert.Equal(JsonSerializer.Serialize(Committed(pending, entry, 0)), JsonSerializer.Serialize(actual));
        foreach (var path in Directory.GetFiles(scope.DirectoryPath))
        {
            var text = await File.ReadAllTextAsync(path);
            foreach (var secret in new[] { marker, "private-error-details", "private-profile" })
                Assert.DoesNotContain(secret, text);
        }
        Assert.True(IsOwnerOnly(scope.Files.HistoryPath));
    }

    [Fact]
    public async Task HistoryFile_HasNoVersionAndOnlyIdSequenceTimeKindPayloadPerRecord()
    {
        using var scope = new FailureHistoryTestScope();
        var entry = await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(scope.Files.HistoryPath))!.AsObject();
        Assert.Equal(new[] { "nextSequence", "records" }, root.Select(property => property.Key));
        Assert.Equal(1UL, root["nextSequence"]!.GetValue<ulong>());
        var record = Assert.Single(root["records"]!.AsArray())!.AsObject();
        Assert.Equal(new[] { "id", "sequence", "occurredAt", "kind", "payload" }, record.Select(property => property.Key));
        Assert.Equal(entry.Id, record["id"]!.GetValue<string>());
        Assert.Equal(0UL, record["sequence"]!.GetValue<ulong>());
        Assert.Equal("encrypted", record["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task EveryEncryptedRecord_UsesAFreshNonce()
    {
        using var scope = new FailureHistoryTestScope();
        var store = scope.CreateStore();
        await store.SaveAsync(CreateRecord(), 10);
        await store.SaveAsync(CreateRecord(), 10);
        var payloads = JsonNode.Parse(await File.ReadAllTextAsync(scope.Files.HistoryPath))!["records"]!.AsArray()
            .Select(record => Convert.FromBase64String(record!["payload"]!.GetValue<string>())[..12]).ToArray();
        Assert.False(payloads[0].SequenceEqual(payloads[1]));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("time")]
    [InlineData("swap")]
    [InlineData("ciphertext")]
    public async Task AuthenticatedData_BindsEachPayloadToItsIdAndTime(string target)
    {
        using var scope = new FailureHistoryTestScope();
        var store = scope.CreateStore();
        await store.SaveAsync(CreateRecord(), 10);
        await store.SaveAsync(CreateRecord(), 10);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(scope.Files.HistoryPath))!;
        var records = root["records"]!.AsArray();
        var first = records[0]!.AsObject();
        switch (target)
        {
            case "id": first["id"] = FailureId.Create(48213); break;
            case "time": first["occurredAt"] = CreateRecord().OccurredAtUtc.AddMinutes(1); break;
            case "swap":
                (first["payload"], records[1]!["payload"]) = (records[1]!["payload"]!.DeepClone(), first["payload"]!.DeepClone());
                break;
            case "ciphertext":
                var bytes = Convert.FromBase64String(first["payload"]!.GetValue<string>());
                bytes[^1] ^= 0x80;
                first["payload"] = Convert.ToBase64String(bytes);
                break;
        }
        await File.WriteAllTextAsync(scope.Files.HistoryPath, root.ToJsonString());
        var opened = await store.ReadRecordsAsync();
        Assert.Equal(target == "swap" ? 0 : 1, opened.Count);
    }

    [Theory]
    [InlineData("null-id")]
    [InlineData("assigned-id")]
    [InlineData("assigned-sequence")]
    [InlineData("missing-pid")]
    [InlineData("missing-time")]
    [InlineData("non-utc-time")]
    [InlineData("null-arguments")]
    [InlineData("null-argument")]
    [InlineData("null-command")]
    [InlineData("null-error")]
    [InlineData("success-exit-code")]
    public async Task InvalidPendingFields_AreRejectedBeforeIO(string field)
    {
        using var scope = new FailureHistoryTestScope();
        var record = CreateRecord();
        record = field switch
        {
            "null-id" => record with { Id = null! },
            "assigned-id" => record with { Id = FailureId.Create(record.TerminalSessionId) },
            "assigned-sequence" => record with { Sequence = 1 },
            "missing-pid" => record with { TerminalSessionId = 0 },
            "missing-time" => record with { OccurredAtUtc = default },
            "non-utc-time" => record with { OccurredAtUtc = record.OccurredAtUtc.ToOffset(TimeSpan.FromHours(1)) },
            "null-arguments" => record with { Arguments = null! },
            "null-argument" => record with { Arguments = [null!] },
            "null-command" => record with { FailedCommand = null! },
            "null-error" => record with { ErrorText = null! },
            "success-exit-code" => record with { ExitCode = 0 },
            _ => throw new ArgumentException(nameof(field)),
        };
        var keys = new TestFailureHistoryKeys();
        Assert.Equal("failure_history_invalid_record",
            (await Assert.ThrowsAsync<FailureHistoryException>(() => scope.CreateStore(keys).SaveAsync(record, 10))).Code);
        Assert.False(Directory.Exists(scope.DirectoryPath));
        Assert.Empty(keys.CreateRequests);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"version":2,"nextSequence":5,"records":"unknown"}""")]
    public async Task UnparsableHistory_IsEmptyAndOverwrittenWithoutBackup(string content)
    {
        using var scope = new FailureHistoryTestScope();
        Directory.CreateDirectory(scope.DirectoryPath);
        await File.WriteAllTextAsync(scope.Files.HistoryPath, content);
        var store = scope.CreateStore();
        Assert.Empty(await store.ReadIndexAsync());
        Assert.Empty(await store.ReadRecordsAsync());

        var entry = await store.SaveAsync(CreateRecord(), 10);

        var saved = Assert.Single(await store.ReadRecordsAsync());
        Assert.Equal(entry.Id, saved.Id);
        Assert.Equal(0UL, saved.Sequence);
        Assert.Equal(new[] { EncryptedFailureHistoryStore.HistoryFileName, EncryptedFailureHistoryStore.LockFileName },
            Directory.GetFiles(scope.DirectoryPath).Select(Path.GetFileName).Order());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("id")]
    [InlineData("offset")]
    [InlineData("kind")]
    [InlineData("encrypted-object")]
    [InlineData("plain-string")]
    public async Task InvalidRecord_IsSkippedAndOtherRecordsStayReadable(string field)
    {
        using var scope = new FailureHistoryTestScope();
        var good = await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        await AppendInvalidRecordAsync(scope, field);

        Assert.Equal(new[] { good }, await scope.CreateStore().ReadIndexAsync());
        Assert.Equal(good.Id, Assert.Single(await scope.CreateStore().ReadRecordsAsync()).Id);
    }

    [Fact]
    public async Task UnavailableKeyStorage_SaveSucceedsAndDropsInvalidRecord()
    {
        using var scope = new FailureHistoryTestScope();
        var encrypted = await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        await AppendInvalidRecordAsync(scope, "kind");

        var plain = await scope.CreateStore(TestFailureHistoryKeys.Unavailable()).SaveAsync(CreateRecord(), 10);

        Assert.True(plain.Unencrypted);
        Assert.Equal(new[] { plain, encrypted }, await scope.CreateStore().ReadIndexAsync());
        Assert.DoesNotContain("bogus", await File.ReadAllTextAsync(scope.Files.HistoryPath));
    }

    private static async Task AppendInvalidRecordAsync(FailureHistoryTestScope scope, string field)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(scope.Files.HistoryPath))!;
        var invalid = new JsonObject
        {
            ["id"] = FailureId.Create(48213),
            ["sequence"] = 99,
            ["occurredAt"] = "2026-09-24T00:00:00+00:00",
            ["kind"] = "encrypted",
            ["payload"] = "AAAA",
        };
        switch (field)
        {
            case "id": invalid["id"] = "not-an-id"; break;
            case "offset": invalid["occurredAt"] = "2026-09-24T00:00:00+08:00"; break;
            case "kind": invalid["kind"] = "bogus"; break;
            case "encrypted-object": invalid["payload"] = new JsonObject(); break;
            case "plain-string": invalid["kind"] = "plain"; break;
        }
        root["records"]!.AsArray().Add(field == "null" ? null : invalid);
        await File.WriteAllTextAsync(scope.Files.HistoryPath, root.ToJsonString());
    }

    [Fact]
    public async Task PlainRecordWithoutPayload_IsSkipped()
    {
        using var scope = new FailureHistoryTestScope();
        Directory.CreateDirectory(scope.DirectoryPath);
        await File.WriteAllTextAsync(scope.Files.HistoryPath,
            """{"nextSequence":1,"records":[{"id":"42-00000000","sequence":0,"occurredAt":"2026-09-24T00:00:00+00:00","kind":"plain"}]}""");

        Assert.Empty(await scope.CreateStore().ReadRecordsAsync());
    }

    [Fact]
    public async Task UnavailableKeyStorage_KeepsEncryptedRecordsAndOnlyTheLatestPlainRecord()
    {
        using var scope = new FailureHistoryTestScope();
        var encrypted = await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        var unavailable = scope.CreateStore(TestFailureHistoryKeys.Unavailable());

        var firstPlain = await unavailable.SaveAsync(CreateRecord() with { ErrorText = "first plain" }, 10);
        var secondPlain = await unavailable.SaveAsync(CreateRecord() with { ErrorText = "second plain" }, 10);

        Assert.True(firstPlain.Unencrypted);
        Assert.True(secondPlain.Unencrypted);
        Assert.Equal(new[] { secondPlain, encrypted }, await unavailable.ReadIndexAsync());
        var text = await File.ReadAllTextAsync(scope.Files.HistoryPath);
        Assert.Contains("second plain", text);
        Assert.DoesNotContain("first plain", text);
        Assert.Equal(new[] { secondPlain.Id, encrypted.Id }, (await scope.CreateStore().ReadRecordsAsync()).Select(record => record.Id));
        Assert.True(IsOwnerOnly(scope.Files.HistoryPath));
    }

    [Fact]
    public async Task AvailableKeyStorage_DropsThePlainRecordOnTheNextSave()
    {
        using var scope = new FailureHistoryTestScope();
        var encrypted = await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        await scope.CreateStore(TestFailureHistoryKeys.Unavailable()).SaveAsync(CreateRecord() with { ErrorText = "plain" }, 10);

        var store = scope.CreateStore();
        var next = await store.SaveAsync(CreateRecord(), 10);

        Assert.False(next.Unencrypted);
        Assert.Equal(new[] { next.Id, encrypted.Id }, (await store.ReadRecordsAsync()).Select(record => record.Id));
        Assert.DoesNotContain("plain\"", await File.ReadAllTextAsync(scope.Files.HistoryPath));
    }

    [Fact]
    public async Task RetentionOne_TrimsEncryptedAndPlainRecordsTogetherBySequence()
    {
        using var scope = new FailureHistoryTestScope();
        await scope.CreateStore().SaveAsync(CreateRecord(), 1);
        var plain = await scope.CreateStore(TestFailureHistoryKeys.Unavailable()).SaveAsync(CreateRecord(), 1);
        Assert.Equal(new[] { plain }, await scope.CreateStore().ReadIndexAsync());
        var encrypted = await scope.CreateStore().SaveAsync(CreateRecord(), 1);
        Assert.Equal(new[] { encrypted }, await scope.CreateStore().ReadIndexAsync());
    }

    [Fact]
    public async Task UnavailableKeyStorage_ReadsFailWhenEncryptedRecordsExist()
    {
        using var scope = new FailureHistoryTestScope();
        await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        var unavailable = scope.CreateStore(TestFailureHistoryKeys.Unavailable());
        var plain = await unavailable.SaveAsync(CreateRecord(), 10);

        Assert.Equal("failure_history_key_unavailable",
            (await Assert.ThrowsAsync<FailureHistoryException>(() => unavailable.ReadRecordsAsync())).Code);
        Assert.Equal(plain.Id, (await unavailable.ReadPlainAsync(null))!.Id);
        Assert.Equal(plain.Id, (await unavailable.ReadPlainAsync(plain.Id))!.Id);
    }

    [Fact]
    public async Task UnavailableKeyStorage_ReadKeepsTheKeyStoreErrorAsInnerException()
    {
        using var scope = new FailureHistoryTestScope();
        await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        var keyError = new CryptographicException("key storage is locked");
        var store = scope.CreateStore(new TestFailureHistoryKeys { Read = (_, _) => throw keyError });

        var error = await Assert.ThrowsAsync<FailureHistoryException>(() => store.ReadRecordsAsync());

        Assert.Equal("failure_history_key_unavailable", error.Code);
        Assert.Same(keyError, error.InnerException);
    }

    [Fact]
    public async Task UnavailableKeyStorage_SaveFallsBackAndLogsTheKeyStoreErrorAtDebugOnly()
    {
        using var scope = new FailureHistoryTestScope();
        var keyError = new CryptographicException("key storage is locked");
        var logger = new Mock<ILoggingService>(MockBehavior.Strict);
        var debug = new List<string>();
        logger.Setup(log => log.Debug(It.IsAny<string>())).Callback<string>(debug.Add);
        var store = new EncryptedFailureHistoryStore(
            new TestFailureHistoryKeys { Read = (_, _) => throw keyError }, scope.Files, logger.Object);

        var entry = await store.SaveAsync(CreateRecord(), 10);

        Assert.True(entry.Unencrypted);
        Assert.Equal(entry.Id, (await store.ReadPlainAsync(entry.Id))!.Id);
        Assert.Contains(keyError.ToString(), Assert.Single(debug));
    }

    [Fact]
    public async Task PlainRecord_IsOnlyReturnedWhenTheSelectedRecordIsPlain()
    {
        using var scope = new FailureHistoryTestScope();
        var encrypted = await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        var plain = await scope.CreateStore(TestFailureHistoryKeys.Unavailable()).SaveAsync(CreateRecord(), 10);
        var store = scope.CreateStore(NoKeyAccess());

        Assert.Equal(plain.Id, (await store.ReadPlainAsync(null))!.Id);
        Assert.Equal(plain.Id, (await store.ReadPlainAsync(plain.Id))!.Id);
        Assert.Null(await store.ReadPlainAsync(encrypted.Id));
        Assert.Null(await store.ReadPlainAsync("42-00000000"));
    }

    [Fact]
    public async Task OnlyPlainRecords_AreReadWithoutKeyAccess()
    {
        using var scope = new FailureHistoryTestScope();
        var plain = await scope.CreateStore(TestFailureHistoryKeys.Unavailable()).SaveAsync(CreateRecord(), 10);
        Assert.Equal(plain.Id, Assert.Single(await scope.CreateStore(NoKeyAccess()).ReadRecordsAsync()).Id);
    }

    [Fact]
    public async Task ConfirmedMissingKey_ReadsSkipOldRecordsAndTheNextSaveDropsThem()
    {
        using var scope = new FailureHistoryTestScope();
        await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        var missing = new TestFailureHistoryKeys
        {
            Read = (_, _) => throw new FailureHistoryException("failure_history_key_missing", "missing"),
        };
        Assert.Empty(await scope.CreateStore(missing).ReadRecordsAsync());

        var newKey = new TestFailureHistoryKeys([.. Enumerable.Repeat((byte)0x77, 32)]);
        var entry = await scope.CreateStore(newKey).SaveAsync(CreateRecord(), 10);
        Assert.Equal(new[] { entry }, await scope.CreateStore(newKey).ReadIndexAsync());
        Assert.True(newKey.CreateRequests.Single());
    }

    [Fact]
    public async Task WrongKey_ReadsSkipRecordsItCannotOpen()
    {
        using var scope = new FailureHistoryTestScope();
        await scope.CreateStore().SaveAsync(CreateRecord(), 10);
        var wrong = new TestFailureHistoryKeys([.. Enumerable.Repeat((byte)0x77, 32)]);
        Assert.Empty(await scope.CreateStore(wrong).ReadRecordsAsync());
    }

    [Fact]
    public async Task ReplacedKey_IsReportedOnlyByTheSaveThatReplacedIt()
    {
        using var scope = new FailureHistoryTestScope();
        var keys = new TestFailureHistoryKeys { ReplaceOnNextCreate = true };
        var store = scope.CreateStore(keys);

        var first = await store.SaveAsync(CreateRecord(), 10);
        var second = await store.SaveAsync(CreateRecord(), 10);

        Assert.True(first.KeyReplaced);
        Assert.False(first.Unencrypted);
        Assert.False(second.KeyReplaced);
        Assert.All(await store.ReadIndexAsync(), entry => Assert.False(entry.KeyReplaced));
    }

    [Fact]
    public async Task ReplacedKey_IsStillReportedWhenTheHistoryWriteFails()
    {
        using var scope = new FailureHistoryTestScope(replace: (_, _) => throw new IOException("write failed"));
        var keys = new TestFailureHistoryKeys { ReplaceOnNextCreate = true };

        var error = await Assert.ThrowsAsync<FailureHistoryException>(() => scope.CreateStore(keys).SaveAsync(CreateRecord(), 10));

        Assert.Equal("failure_history_io", error.Code);
        Assert.EndsWith("The failure-history key was damaged and has been replaced. Earlier failures can no longer be read.", error.Message);
    }

    [Fact]
    public async Task InvalidKeyThatCannotBeReplaced_IsNeverSavedUnencrypted()
    {
        using var scope = new FailureHistoryTestScope();
        var keys = new TestFailureHistoryKeys
        {
            Read = (_, _) => throw new FailureHistoryException("failure_history_key_invalid", "Could not be replaced."),
        };

        Assert.Equal("failure_history_key_invalid",
            (await Assert.ThrowsAsync<FailureHistoryException>(() => scope.CreateStore(keys).SaveAsync(CreateRecord(), 10))).Code);
        Assert.False(File.Exists(scope.Files.HistoryPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(33)]
    public async Task InvalidKeySize_IsRejectedAndCleared(int length)
    {
        using var scope = new FailureHistoryTestScope();
        var keys = new TestFailureHistoryKeys([.. Enumerable.Repeat((byte)0x44, length)]);
        Assert.Equal("failure_history_key_invalid",
            (await Assert.ThrowsAsync<FailureHistoryException>(() => scope.CreateStore(keys).SaveAsync(CreateRecord(), 10))).Code);
        Assert.False(File.Exists(scope.Files.HistoryPath));
        AssertKeysCleared(keys);
    }

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("argument")]
    [InlineData("null-reference")]
    public async Task UnexpectedProviderDefects_AreNotMisclassified(string kind)
    {
        using var scope = new FailureHistoryTestScope();
        Exception expected = kind switch
        {
            "invalid-operation" => new InvalidOperationException("Provider defect."),
            "argument" => new ArgumentException("Provider defect."),
            _ => new NullReferenceException("Provider defect."),
        };
        var keys = new TestFailureHistoryKeys { Read = (_, _) => throw expected };
        Assert.Same(expected, await Record.ExceptionAsync(() => scope.CreateStore(keys).SaveAsync(CreateRecord(), 10)));
    }

    [Fact]
    public async Task ReadersHoldingTheFileOpen_DoNotBlockReplacement()
    {
        using var scope = new FailureHistoryTestScope();
        var store = scope.CreateStore();
        await store.SaveAsync(CreateRecord(), 10);
        using (new FileStream(scope.Files.HistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await store.SaveAsync(CreateRecord(), 10);
        Assert.Equal(2, (await store.ReadRecordsAsync()).Count);
    }

    [Fact]
    public async Task ReadersDuringSaves_DoNotMakeWritersLoseRecords()
    {
        using var scope = new FailureHistoryTestScope();
        var keys = new TestFailureHistoryKeys();
        await scope.CreateStore(keys).SaveAsync(CreateRecord(), 3);
        var writer = Task.Run(async () =>
        {
            for (var index = 0; index < 100; index++)
                await scope.CreateStore(keys).SaveAsync(CreateRecord(), 3);
        });

        while (!writer.IsCompleted)
        {
            try { await scope.CreateStore(keys).ReadRecordsAsync(); }
            catch (FailureHistoryException exception) when (exception.Code == "failure_history_io") { }
        }
        await writer;
        Assert.Equal(3, (await scope.CreateStore(keys).ReadRecordsAsync()).Count);
    }

    [Fact]
    public async Task ConcurrentInstances_AllocateUniqueSequencesWithoutLostWrites()
    {
        using var scope = new FailureHistoryTestScope();
        var keys = new TestFailureHistoryKeys();
        var entries = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => scope.CreateStore(keys).SaveAsync(CreateRecord(), 4))));
        var records = await scope.CreateStore(keys).ReadRecordsAsync();
        Assert.Equal(entries.Select(entry => entry.Id).Order(), records.Select(record => record.Id).Order());
        Assert.Equal(Enumerable.Range(0, 4).Reverse().Select(index => (ulong)index), records.Select(record => record.Sequence));
    }

    [Fact]
    public async Task FailedAtomicReplacement_PreservesHistoryAndCleansTheTemporaryFile()
    {
        var fail = false;
        using var scope = new FailureHistoryTestScope(replace: (source, target) =>
        {
            if (fail)
                throw new IOException("private filesystem diagnostic");
            File.Move(source, target, overwrite: true);
        });
        var store = scope.CreateStore();
        var first = await store.SaveAsync(CreateRecord(), 10);
        var original = await File.ReadAllBytesAsync(scope.Files.HistoryPath);
        fail = true;
        var error = await Assert.ThrowsAsync<FailureHistoryException>(() => store.SaveAsync(CreateRecord(), 10));
        Assert.Equal("failure_history_io", error.Code);
        Assert.DoesNotContain("private filesystem diagnostic", error.Message);
        Assert.Equal("private filesystem diagnostic", Assert.IsType<IOException>(error.InnerException).Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(scope.Files.HistoryPath));
        Assert.Equal(first.Id, Assert.Single(await store.ReadRecordsAsync()).Id);
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task KeyErrorThenWriteError_KeepsBothCauses()
    {
        using var scope = new FailureHistoryTestScope(replace: (_, _) => throw new IOException("write failed"));
        var keyError = new CryptographicException("key storage is locked");
        var store = scope.CreateStore(new TestFailureHistoryKeys { Read = (_, _) => throw keyError });

        var error = await Assert.ThrowsAsync<FailureHistoryException>(() => store.SaveAsync(CreateRecord(), 10));

        Assert.Equal("failure_history_io", error.Code);
        var causes = Assert.IsType<AggregateException>(error.InnerException).InnerExceptions;
        Assert.Equal("write failed", Assert.IsType<IOException>(causes[0]).Message);
        Assert.Same(keyError, causes[1]);
    }

    [Fact]
    public async Task WriterLock_HasABoundedAndCancellableWait()
    {
        using var scope = new FailureHistoryTestScope();
        Directory.CreateDirectory(scope.DirectoryPath);
        using var held = new FileStream(Path.Combine(scope.DirectoryPath, EncryptedFailureHistoryStore.LockFileName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var clock = new SignalingFakeTimeProvider();
        var lockTimeout = TimeSpan.FromSeconds(2);
        var store = new EncryptedFailureHistoryStore(NoKeyAccess(), new FailureHistoryFiles(scope.DirectoryPath, lockTimeout, timeProvider: clock));
        var save = store.SaveAsync(CreateRecord(), 10);
        await clock.WaitForTimerAsync(dueTime => dueTime > TimeSpan.Zero, TimeSpan.FromSeconds(60));
        Assert.False(save.IsCompleted);
        clock.Advance(lockTimeout);
        Assert.Equal("failure_history_locked",
            (await Assert.ThrowsAsync<FailureHistoryException>(() => save.WaitAsync(TimeSpan.FromSeconds(60)))).Code);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(CreateRecord(), 10, cancellation.Token));
        Assert.False(File.Exists(scope.Files.HistoryPath));
    }

    [Fact]
    public async Task AlreadyCancelledOperations_HaveNoSideEffects()
    {
        using var scope = new FailureHistoryTestScope();
        var store = scope.CreateStore(NoKeyAccess());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(CreateRecord(), 10, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadRecordsAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ClearAsync(cancellation.Token));
        Assert.False(Directory.Exists(scope.DirectoryPath));
    }

    [Fact]
    public async Task CancellationAfterKeyRetrieval_StillClearsOwnedBytes()
    {
        using var scope = new FailureHistoryTestScope();
        using var cancellation = new CancellationTokenSource();
        var owned = Enumerable.Repeat((byte)0x55, 32).ToArray();
        var keys = new TestFailureHistoryKeys
        {
            Read = (_, _) => { cancellation.Cancel(); return Task.FromResult(owned); },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.CreateStore(keys).SaveAsync(CreateRecord(), 10, cancellation.Token));
        Assert.All(owned, value => Assert.Equal((byte)0, value));
        Assert.False(File.Exists(scope.Files.HistoryPath));
    }

    private static TestFailureHistoryKeys NoKeyAccess() =>
        new() { Read = (_, _) => throw new InvalidOperationException("Unexpected key access.") };

    private static void AssertKeysCleared(TestFailureHistoryKeys keys) =>
        Assert.All(keys.ReturnedKeys, key => Assert.All(key, value => Assert.Equal((byte)0, value)));

    private static bool IsOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
            return File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var security = new FileInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToArray();
        return security.AreAccessRulesProtected && rules.Length == 1 &&
            rules[0].IdentityReference == System.Security.Principal.WindowsIdentity.GetCurrent().User;
    }
}
