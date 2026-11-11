using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Core.Abstractions;
using Core.Agent.Tools.Resource.Cache;
using Tests.Common.Stubs;
using Xunit;

namespace Core.Agent.Tests;

/// <summary>
/// Tests for <see cref="ArmCacheRefresher"/>. Two concerns:
///
/// 1. Not-signed-in fast path — must NOT call auth or touch disk.
/// 2. ARG pagination — the refresher pages via <c>$skipToken</c>. If a future
///    ARG api-version renames or drops that field, refreshers would silently
///    truncate at page 1. This test pins the wire contract so any drift
///    fails CI.
/// </summary>
public class ArmCacheRefresherTests
{
    // ── not-signed-in fast path ──

    [Fact]
    public async Task NotSignedIn_RefreshOnceReturnsWithoutTouchingAuth()
    {
        using var tempDir = new TempDirectory();
        var refresher = new ArmCacheRefresher(
            new NoAuth(),
            new StubProfileService(seedDefault: false),
            http: null,
            cacheRoot: tempDir.Path);

        await refresher.RefreshOnceAsync();
        Assert.Null(refresher.LastError);
        Assert.False(File.Exists(Path.Combine(tempDir.Path, "tenants.json")));
    }

    [Fact]
    public async Task NotSignedIn_KickAsyncSwallowsAndDoesNotThrow()
    {
        using var tempDir = new TempDirectory();
        var refresher = new ArmCacheRefresher(
            new NoAuth(),
            new StubProfileService(seedDefault: false),
            http: null,
            cacheRoot: tempDir.Path);

        refresher.KickAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (refresher.ActiveTaskCount > 0)
            await Task.Delay(10, timeout.Token);
        Assert.Null(refresher.LastError);
    }

    // ── ARG pagination ──

    [Fact]
    public async Task ArgQuery_FollowsSkipToken_AndConcatenatesAllPages()
    {
        // Two-page ARG response: page 1 carries $skipToken=PAGE2 and 2 rows;
        // page 2 has no $skipToken and 1 row. Refresher must issue exactly two
        // POSTs (second one carrying skipToken in the body) and write all 3
        // rows into resources.json.
        using var tempDir = new TempDirectory();
        const string tenantId = "11111111-1111-1111-1111-111111111111";

        var recorded = new List<string>();
        var handler = new SequenceHandler(
            recordBody: recorded,
            responses: new Queue<string>(new[]
            {
                // Any URL that isn't ARG (tenants list, subs, rgs) is returned
                // empty so the flow reaches the resources query which is what
                // we care about pinning here. subs returns a single stub id
                // because the refresher now short-circuits on empty sub lists
                // to avoid an unscoped ARG runaway.
                """{"value":[]}""",                                                    // GET /tenants
                """{"data":[{"subscriptionId":"sub-stub"}],"$skipToken":null}""",      // subs
                """{"data":[],"$skipToken":null}""",                                   // rgs
                """{"data":[{"id":"r1","name":"one"},{"id":"r2","name":"two"}],"$skipToken":"PAGE2"}""",
                """{"data":[{"id":"r3","name":"three"}]}""",
            }));
        using var http = new HttpClient(handler);

        var refresher = new ArmCacheRefresher(
            new StubAuthService(),
            new SignedInProfile(tenantId),
            http,
            cacheRoot: tempDir.Path);

        await refresher.RefreshOnceAsync();
        Assert.Null(refresher.LastError);

        // Wire assertion: the second ARG POST must carry skipToken=PAGE2 in
        // its JSON body. That's the actual bug we're guarding against.
        var argBodies = recorded.Where(b => b.Contains("\"query\"", StringComparison.Ordinal)).ToList();
        Assert.True(argBodies.Count >= 4,
            $"expected at least 4 ARG POSTs (subs + rgs + 2 pages of resources), saw {argBodies.Count}");
        var resourceBodies = argBodies.Where(b => b.Contains("\"Resources ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, resourceBodies.Count);
        Assert.DoesNotContain("$skipToken", resourceBodies[0]);
        Assert.Contains("\"$skipToken\":\"PAGE2\"", resourceBodies[1]);

        // Payload assertion: all 3 rows made it to disk.
        var resPath = Path.Combine(tempDir.Path, tenantId, "resources.json");
        Assert.True(File.Exists(resPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(resPath));
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("r1", items[0].GetProperty("id").GetString());
        Assert.Equal("r2", items[1].GetProperty("id").GetString());
        Assert.Equal("r3", items[2].GetProperty("id").GetString());
    }

    // ── stubs ──

    private sealed class NoAuth : StubAuthService
    {
        protected override Task<AuthResult> AcquireTokenAsync(string[]? scopes, string? tenantId, CancellationToken cancellationToken) =>
            throw new NotImplementedException("no token expected — refresher must return early");
        public override Task<bool> CanAcquireTokenSilentlyAsync(string? tenantId, Profile profile, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("no probe expected — refresher must return early");
    }

    private sealed class SignedInProfile(string tenantId) : StubProfileService(seedDefault: false)
    {
        public override Task<Profile?> GetActiveProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Profile?>(new Profile { Name = "default", TenantId = tenantId, SubscriptionId = "sub-1", CloudName = "AzureCloud" });
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly List<string> _recordBody;
        private readonly Queue<string> _responses;

        public SequenceHandler(List<string> recordBody, Queue<string> responses)
        {
            _recordBody = recordBody;
            _responses = responses;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _recordBody.Add(body);
            var payload = _responses.Count > 0 ? _responses.Dequeue() : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"azclips-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
