// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Abstractions;
using Core.Abstractions.Recovery;
using Core.Authentication.Recovery;
using Moq;

namespace Cli.Tests.Recovery;

public sealed class MacOsFailureHistoryKeyStorageTests
{
    private const string Service = "test-failure-history-purpose";
    private const string Account = "test-home:7";

    [Fact]
    public void MissingItem_IsTheOnlyEmptyRead_AndDoesNotWriteKey()
    {
        var native = new FakeNative { ReadStatus = -25300 };

        Assert.Empty(Create(native).ReadData());

        Assert.Equal(0, native.AddCalls);
        Assert.True(native.UserInteractionAllowed);
        Assert.Empty(native.LiveObjects);
    }

    [Theory]
    [InlineData(-25308)] // Interaction not allowed.
    [InlineData(-25315)] // Interaction required by the file-based Keychain.
    [InlineData(-25293)] // Authentication/ACL failure.
    [InlineData(-25291)] // Keychain unavailable.
    [InlineData(-128)] // Native cancellation, not caller-token cancellation.
    [InlineData(-50)]
    public void FailedRead_NeverBecomesMissingOrLeaksIdentity(int status)
    {
        var native = new FakeNative { ReadStatus = status, ReturnResultOnFailure = true };

        var error = Assert.Throws<FailureHistoryException>(() => Create(native).ReadData());

        Assert.Equal("failure_history_key_unavailable", error.Code);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message);
        Assert.DoesNotContain(Service, error.Message);
        Assert.DoesNotContain(Account, error.Message);
        Assert.Equal(0, native.AddCalls);
        Assert.True(native.UserInteractionAllowed);
        Assert.Empty(native.LiveObjects);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void InvalidDataLength_IsNotAnEmptyKey(long length)
    {
        var native = new FakeNative { DataLength = (nint)length };

        var error = Assert.Throws<FailureHistoryException>(() => Create(native).ReadData());

        Assert.Equal("failure_history_key_invalid", error.Code);
        Assert.Equal(0, native.AddCalls);
        Assert.Null(native.LastDestination);
        Assert.Empty(native.LiveObjects);
        Assert.True(native.UserInteractionAllowed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullOrNonDataResult_IsRejectedAndReleased(bool nullResult)
    {
        var native = new FakeNative { NullResult = nullResult, ResultIsData = false };

        Assert.Equal("failure_history_key_invalid",
            Assert.Throws<FailureHistoryException>(() => Create(native).ReadData()).Code);

        Assert.Null(native.LastDestination);
        Assert.Empty(native.LiveObjects);
        Assert.True(native.UserInteractionAllowed);
    }

    [Fact]
    public void PolicyReadFailure_DoesNotChangePolicyOrConstructQuery()
    {
        var native = new FakeNative { PolicyReadStatus = -50 };

        Assert.Equal("failure_history_key_unavailable",
            Assert.Throws<FailureHistoryException>(() => Create(native).ReadData()).Code);

        Assert.Empty(native.PolicyWrites);
        Assert.Equal(0, native.Creations);
        Assert.Empty(native.Queries);
    }

    [Fact]
    public void PolicyDisableFailure_RestoresPolicyWithoutTouchingKeychain()
    {
        var native = new FakeNative();
        native.PolicyStatuses.Enqueue(-50);
        native.PolicyStatuses.Enqueue(0);

        Assert.Equal("failure_history_key_unavailable",
            Assert.Throws<FailureHistoryException>(() => Create(native).ReadData()).Code);

        Assert.Equal(new[] { false, true }, native.PolicyWrites);
        Assert.Equal(0, native.Creations);
        Assert.Empty(native.Queries);
        Assert.True(native.UserInteractionAllowed);
    }

    [Fact]
    public void PolicyRestoreFailure_FailsReadAndErasesItsManagedKeyCopy()
    {
        var native = new FakeNative();
        native.PolicyStatuses.Enqueue(0);
        native.PolicyStatuses.Enqueue(-50);

        Assert.Equal("failure_history_key_unavailable",
            Assert.Throws<FailureHistoryException>(() => Create(native).ReadData()).Code);

        Assert.NotNull(native.LastDestination);
        Assert.All(native.LastDestination, value => Assert.Equal((byte)0, value));
        Assert.Empty(native.LiveObjects);
        Assert.False(native.UserInteractionAllowed);
    }

    [Fact]
    public void PartialCopyFailure_ErasesBufferAndReleasesAllNativeObjects()
    {
        var native = new FakeNative { ThrowDuringCopy = true };

        Assert.Throws<InvalidOperationException>(() => Create(native).ReadData());

        Assert.NotNull(native.LastDestination);
        Assert.All(native.LastDestination, value => Assert.Equal((byte)0, value));
        Assert.Empty(native.LiveObjects);
        Assert.True(native.UserInteractionAllowed);
    }

    [Fact]
    public void CopyMatchingException_ReleasesEvenAnAlreadyAssignedResult()
    {
        var native = new FakeNative { ThrowAfterResult = true };

        Assert.Throws<InvalidOperationException>(() => Create(native).ReadData());

        Assert.Equal(4, native.ExplicitReleases.Count);
        Assert.Empty(native.LiveObjects);
        Assert.True(native.UserInteractionAllowed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void QueryAllocationFailure_ReleasesEarlierObjectsAndRestoresPolicy(int allocation)
    {
        var native = new FakeNative { FailCreation = allocation };

        Assert.Equal("failure_history_key_unavailable",
            Assert.Throws<FailureHistoryException>(() => Create(native).ReadData()).Code);

        Assert.Empty(native.Queries);
        Assert.Empty(native.LiveObjects);
        Assert.True(native.UserInteractionAllowed);
    }

    [Fact]
    public void DictionaryPopulationFailure_ReleasesItsRetainedChildren()
    {
        var native = new FakeNative { ThrowWhenSetting = MacOsKeychainConstant.UseAuthenticationUi };

        Assert.Throws<InvalidOperationException>(() => Create(native).ReadData());

        Assert.Empty(native.Queries);
        Assert.Empty(native.LiveObjects);
        Assert.True(native.UserInteractionAllowed);
    }

    [Fact]
    public void MissingNoUiConstant_FailsClosedBeforeAnyItemOperation()
    {
        var native = new FakeNative { MissingConstant = MacOsKeychainConstant.AuthenticationUiFail };

        Assert.Equal("failure_history_key_unavailable",
            Assert.Throws<FailureHistoryException>(() => Create(native).ReadData()).Code);

        Assert.Empty(native.Queries);
        Assert.Empty(native.LiveObjects);
        Assert.True(native.UserInteractionAllowed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Add_UsesBinaryDataAndNoUiPolicy_WithoutPerformingReadback(bool allowed)
    {
        var native = new FakeNative { UserInteractionAllowed = allowed };
        var key = Enumerable.Repeat((byte)0x71, 32).ToArray();
        var original = key.ToArray();

        Create(native).WriteData(key);

        Assert.Equal(original, key);
        Assert.Equal(original, native.AddedBytes);
        var query = Assert.Single(native.Queries);
        AssertIdentityAndPolicy(query);
        Assert.DoesNotContain(MacOsKeychainConstant.ReturnData, query.Keys);
        Assert.DoesNotContain(MacOsKeychainConstant.MatchLimit, query.Keys);
        Assert.Equal(original, Assert.IsType<byte[]>(query[MacOsKeychainConstant.ValueData]));
        Assert.Equal(1, native.AddCalls);
        Assert.Null(native.LastDestination);
        Assert.Equal(allowed, native.UserInteractionAllowed);
        Assert.Equal(new[] { false, allowed }, native.PolicyWrites);
        Assert.Equal(4, native.ExplicitReleases.Count);
        Assert.Empty(native.LiveObjects);
    }

    [Theory]
    [InlineData(-25299)] // A duplicate must not be overwritten.
    [InlineData(-25308)]
    [InlineData(-25315)]
    [InlineData(-25293)]
    [InlineData(-25291)]
    [InlineData(-50)]
    public void AddFailure_IsNotRetriedOrReportedAsSuccess(int status)
    {
        var native = new FakeNative { AddStatus = status };

        Assert.Equal("failure_history_key_unavailable",
            Assert.Throws<FailureHistoryException>(() => Create(native).WriteData(new byte[32])).Code);

        Assert.Equal(1, native.AddCalls);
        Assert.Empty(native.LiveObjects);
        Assert.True(native.UserInteractionAllowed);
    }

    [Fact]
    public void AddDataAllocationFailure_DoesNotCallAdd()
    {
        var native = new FakeNative { FailCreation = 4 };

        Assert.Equal("failure_history_key_unavailable",
            Assert.Throws<FailureHistoryException>(() => Create(native).WriteData(new byte[32])).Code);

        Assert.Equal(0, native.AddCalls);
        Assert.Empty(native.LiveObjects);
        Assert.True(native.UserInteractionAllowed);
    }

    [Fact]
    public void AddRestoreFailure_ReportsFailureEvenAfterNativeAdd()
    {
        var native = new FakeNative();
        native.PolicyStatuses.Enqueue(0);
        native.PolicyStatuses.Enqueue(-50);

        Assert.Equal("failure_history_key_unavailable",
            Assert.Throws<FailureHistoryException>(() => Create(native).WriteData(new byte[32])).Code);

        Assert.Equal(1, native.AddCalls);
        Assert.Empty(native.LiveObjects);
    }

    [Fact]
    public void AddAndRestoreFailure_ReportsTheAddErrorAndLogsTheRestoreError()
    {
        var native = new FakeNative { AddStatus = -25291 };
        native.PolicyStatuses.Enqueue(0);
        native.PolicyStatuses.Enqueue(-50);
        var debug = new List<string>();
        var logger = new Mock<ILoggingService>();
        logger.Setup(log => log.Debug(It.IsAny<string>())).Callback<string>(debug.Add);

        var error = Assert.Throws<FailureHistoryException>(
            () => new MacOsFailureHistoryKeyStorage(Service, Account, native, logger.Object).WriteData(new byte[32]));

        Assert.Contains("OSStatus -25291", error.Message);
        Assert.DoesNotContain("OSStatus -50", error.ToString());
        Assert.Contains("OSStatus -50", Assert.Single(debug));
        Assert.Equal(new[] { false, true }, native.PolicyWrites);
    }

    [Fact]
    public void CopyAndRestoreFailure_RethrowsTheCopyErrorAndLogsTheRestoreError()
    {
        var native = new FakeNative { ThrowDuringCopy = true };
        native.PolicyStatuses.Enqueue(0);
        native.PolicyStatuses.Enqueue(-50);
        var debug = new List<string>();
        var logger = new Mock<ILoggingService>();
        logger.Setup(log => log.Debug(It.IsAny<string>())).Callback<string>(debug.Add);

        Assert.Throws<InvalidOperationException>(
            () => new MacOsFailureHistoryKeyStorage(Service, Account, native, logger.Object).ReadData());

        Assert.Contains("OSStatus -50", Assert.Single(debug));
    }

    [Fact]
    public void Delete_RemovesOnlyTheFirstMatchWithoutUi()
    {
        var native = new FakeNative();

        Create(native).DeleteData();

        var query = Assert.Single(native.Queries);
        AssertIdentityAndPolicy(query);
        Assert.Equal(MacOsKeychainConstant.MatchLimitOne, query[MacOsKeychainConstant.MatchLimit]);
        Assert.Equal(MacOsKeychainConstant.BooleanTrue, query[MacOsKeychainConstant.ReturnRef]);
        Assert.Equal(1, native.DeleteCalls);
        Assert.Equal(new[] { false, true }, native.PolicyWrites);
        Assert.Empty(native.LiveObjects);
    }

    [Fact]
    public void Delete_OfAnAbsentItem_DeletesNothing()
    {
        var native = new FakeNative { ReadStatus = -25300 };

        Create(native).DeleteData();

        Assert.Equal(0, native.DeleteCalls);
        Assert.Empty(native.LiveObjects);
    }

    [Fact]
    public void DeleteThenRestoreFailure_IsNotReportedAsAFailedDelete()
    {
        var native = new FakeNative();
        native.PolicyStatuses.Enqueue(0);
        native.PolicyStatuses.Enqueue(-50);
        var debug = new List<string>();
        var logger = new Mock<ILoggingService>();
        logger.Setup(log => log.Debug(It.IsAny<string>())).Callback<string>(debug.Add);

        new MacOsFailureHistoryKeyStorage(Service, Account, native, logger.Object).DeleteData();

        Assert.Equal(1, native.DeleteCalls);
        Assert.Contains("OSStatus -50", Assert.Single(debug));
    }

    [Fact]
    public void ReadThenRestoreFailure_IsStillAnError()
    {
        var native = new FakeNative();
        native.PolicyStatuses.Enqueue(0);
        native.PolicyStatuses.Enqueue(-50);

        Assert.Equal("failure_history_key_unavailable",
            Assert.Throws<FailureHistoryException>(() => Create(native).ReadData()).Code);
    }

    [Theory]
    [InlineData(-25308)]
    [InlineData(-25293)]
    [InlineData(-25291)]
    public void DeleteFailure_IsUnavailable(int status)
    {
        var native = new FakeNative { DeleteStatus = status };

        var error = Assert.Throws<FailureHistoryException>(() => Create(native).DeleteData());

        Assert.Equal("failure_history_key_unavailable", error.Code);
        Assert.True(native.UserInteractionAllowed);
        Assert.Empty(native.LiveObjects);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void InvalidWriteLength_DoesNotEnterNativeCode(int length)
    {
        var native = new FakeNative();

        Assert.Throws<ArgumentException>(() => Create(native).WriteData(new byte[length]));

        Assert.Equal(0, native.PolicyReads);
        Assert.Equal(0, native.Creations);
    }

    [Fact]
    public void ServiceAndHomeGenerationAccount_ArePassedWithoutTransformation()
    {
        var native = new FakeNative();
        var storage = new MacOsFailureHistoryKeyStorage("用途:service", "home:8:🗝", native);

        storage.ReadData();

        var query = Assert.Single(native.Queries);
        Assert.Equal("用途:service", query[MacOsKeychainConstant.Service]);
        Assert.Equal("home:8:🗝", query[MacOsKeychainConstant.Account]);
        Assert.Empty(native.LiveObjects);
    }

    [Fact]
    public async Task DifferentInstances_SerializeTheProcessWidePolicyScope()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        var firstNative = new FakeNative
        {
            BeforeMatching = () =>
            {
                firstEntered.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(60)));
            },
        };
        var secondNative = new FakeNative { BeforePolicyRead = secondEntered.Set };
        var first = Task.Run(() => Create(firstNative).ReadData());
        Task<byte[]>? second = null;
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(60)));
            second = Task.Run(() =>
            {
                secondStarted.Set();
                return Create(secondNative).ReadData();
            });
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(60)));
            Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            releaseFirst.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(60));
            if (second is not null)
                await second.WaitAsync(TimeSpan.FromSeconds(60));
        }

        Assert.True(secondEntered.IsSet);
        Assert.True(firstNative.UserInteractionAllowed);
        Assert.True(secondNative.UserInteractionAllowed);
        Assert.Empty(firstNative.LiveObjects);
        Assert.Empty(secondNative.LiveObjects);
    }

    private static MacOsFailureHistoryKeyStorage Create(FakeNative native)
        => new(Service, Account, native);

    private static void AssertIdentityAndPolicy(Dictionary<MacOsKeychainConstant, object> query)
    {
        Assert.Equal(Service, query[MacOsKeychainConstant.Service]);
        Assert.Equal(Account, query[MacOsKeychainConstant.Account]);
        Assert.Equal(MacOsKeychainConstant.GenericPassword, query[MacOsKeychainConstant.Class]);
        Assert.Equal(MacOsKeychainConstant.BooleanFalse, query[MacOsKeychainConstant.UseDataProtectionKeychain]);
        Assert.Equal(MacOsKeychainConstant.AuthenticationUiFail, query[MacOsKeychainConstant.UseAuthenticationUi]);
    }

    private sealed class FakeNative : IMacOsKeychainNative
    {
        private readonly Dictionary<nint, int> _references = [];
        private nint _nextHandle = 100;

        public Dictionary<nint, object> LiveObjects { get; } = [];
        public List<nint> ExplicitReleases { get; } = [];
        public List<Dictionary<MacOsKeychainConstant, object>> Queries { get; } = [];
        public List<bool> PolicyWrites { get; } = [];
        public Queue<int> PolicyStatuses { get; } = [];
        public bool UserInteractionAllowed { get; set; } = true;
        public int PolicyReadStatus { get; init; }
        public int PolicyReads { get; private set; }
        public int ReadStatus { get; init; }
        public int AddStatus { get; init; }
        public int DeleteStatus { get; init; }
        public int DeleteCalls { get; private set; }
        public int AddCalls { get; private set; }
        public int Creations { get; private set; }
        public int FailCreation { get; init; }
        public bool ReturnResultOnFailure { get; init; }
        public bool NullResult { get; init; }
        public bool ResultIsData { get; init; } = true;
        public bool ThrowAfterResult { get; init; }
        public bool ThrowDuringCopy { get; init; }
        public nint DataLength { get; init; } = 32;
        public MacOsKeychainConstant? ThrowWhenSetting { get; init; }
        public MacOsKeychainConstant? MissingConstant { get; init; }
        public byte[] ReadBytes { get; } = Enumerable.Repeat((byte)0x41, 32).ToArray();
        public byte[]? LastDestination { get; private set; }
        public byte[]? AddedBytes { get; private set; }
        public Action? BeforeMatching { get; init; }
        public Action? BeforePolicyRead { get; init; }

        public int GetUserInteractionAllowed(out bool allowed)
        {
            BeforePolicyRead?.Invoke();
            PolicyReads++;
            allowed = UserInteractionAllowed;
            return PolicyReadStatus;
        }

        public int SetUserInteractionAllowed(bool allowed)
        {
            PolicyWrites.Add(allowed);
            var status = PolicyStatuses.TryDequeue(out var next) ? next : 0;
            if (status == 0)
                UserInteractionAllowed = allowed;
            return status;
        }

        public nint GetConstant(MacOsKeychainConstant constant)
            => constant == MissingConstant ? 0 : -(nint)constant - 1;

        public nint CreateDictionary() => Allocate(new Dictionary<nint, nint>());
        public nint CreateString(string value) => Allocate(value);
        public nint CreateData(byte[] value) => Allocate(value.ToArray());

        public void SetDictionaryValue(nint dictionary, nint key, nint value)
        {
            if (Decode(key) == ThrowWhenSetting)
                throw new InvalidOperationException("Injected dictionary failure.");
            var values = (Dictionary<nint, nint>)LiveObjects[dictionary];
            if (values.TryGetValue(key, out var old))
                ReleaseReference(old);
            if (value > 0)
                _references[value]++;
            values[key] = value;
        }

        public int CopyMatching(nint query, out nint result)
        {
            BeforeMatching?.Invoke();
            Snapshot(query);

            result = !NullResult && (ReadStatus == 0 || ReturnResultOnFailure)
                ? Allocate(ResultIsData ? ReadBytes.ToArray() : "not-data") : 0;
            if (ThrowAfterResult)
                throw new InvalidOperationException("Injected copy-matching failure.");
            return ReadStatus;
        }

        public int Add(nint attributes)
        {
            AddCalls++;
            var query = Snapshot(attributes);
            AddedBytes = Assert.IsType<byte[]>(query[MacOsKeychainConstant.ValueData]);
            return AddStatus;
        }

        public int DeleteItem(nint item)
        {
            Assert.False(UserInteractionAllowed, "Deletion must run with the legacy UI policy disabled.");
            Assert.True(LiveObjects.ContainsKey(item));
            DeleteCalls++;
            return DeleteStatus;
        }

        public bool IsData(nint value) => LiveObjects[value] is byte[];
        public nint GetDataLength(nint value) => DataLength;

        public void CopyDataBytes(nint value, byte[] destination)
        {
            LastDestination = destination;
            ((byte[])LiveObjects[value]).CopyTo(destination, 0);
            if (ThrowDuringCopy)
                throw new InvalidOperationException("Injected data-copy failure.");
        }

        public void Release(nint value)
        {
            Assert.True(value > 0, "Borrowed CF constants must not be released.");
            Assert.DoesNotContain(value, ExplicitReleases);
            ExplicitReleases.Add(value);
            ReleaseReference(value);
        }

        private nint Allocate(object value)
        {
            if (++Creations == FailCreation)
                return 0;
            var handle = ++_nextHandle;
            LiveObjects.Add(handle, value);
            _references.Add(handle, 1);
            return handle;
        }

        private Dictionary<MacOsKeychainConstant, object> Snapshot(nint dictionary)
        {
            Assert.False(UserInteractionAllowed, "SecItem calls must run with the legacy UI policy disabled.");
            var query = ((Dictionary<nint, nint>)LiveObjects[dictionary])
                .ToDictionary(pair => Decode(pair.Key), pair => Resolve(pair.Value));
            Assert.Equal(MacOsKeychainConstant.AuthenticationUiFail, query[MacOsKeychainConstant.UseAuthenticationUi]);
            Queries.Add(query);
            return query;
        }

        private object Resolve(nint value) => value < 0 ? Decode(value)
            : LiveObjects[value] is byte[] bytes ? bytes.ToArray() : LiveObjects[value];

        private static MacOsKeychainConstant Decode(nint value) => (MacOsKeychainConstant)(-value - 1);

        private void ReleaseReference(nint value)
        {
            if (value < 0 || --_references[value] != 0)
                return;
            if (LiveObjects[value] is Dictionary<nint, nint> dictionary)
            {
                foreach (var child in dictionary.Values)
                    ReleaseReference(child);
            }
            _references.Remove(value);
            LiveObjects.Remove(value);
        }
    }
}
