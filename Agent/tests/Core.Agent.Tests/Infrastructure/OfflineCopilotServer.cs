// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;

namespace Core.Agent.Tests;

/// <summary>Loopback JSON-RPC peer for the real SDK; no daemon, credentials or model calls.</summary>
/// <remarks>
/// Records receive-loop failures for <see cref="ThrowIfFaulted"/> to report in the test body.
/// A request for a method this server does not implement gets JSON-RPC error -32601 and is
/// recorded as a fault; the connection stays open.
/// </remarks>
internal sealed class OfflineCopilotServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TaskCompletionSource<Stream> _connection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<SessionEvent[]> _turns;
    private readonly bool _scripted;
    private readonly Func<ModelQuery, Task>? _modelQuery;
    private readonly Task _run;
    private readonly TaskCompletionSource _firstToolHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Completed only on a fault path. Nothing else may complete it, or ThrowIfFaulted throws on
    // every test.
    private readonly TaskCompletionSource<Exception> _fault = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string _sessionId = "";
    private int _pending;

    /// <summary>Creates a server for the real SDK.</summary>
    /// <param name="turns">
    /// Events emitted after an <c>assistant.turn_start</c>, one array per <c>session.send</c>. With no
    /// turns, a send dispatches <see cref="Calls"/> instead. A send after the last scripted turn is
    /// recorded as a fault and stops the server from answering further requests.
    /// </param>
    /// <param name="modelQuery">
    /// Answers <c>session.model.getCurrent</c>. Without it, that request is an unknown method.
    /// </param>
    internal OfflineCopilotServer(SessionEvent[][]? turns = null, Func<ModelQuery, Task>? modelQuery = null)
    {
        _turns = new Queue<SessionEvent[]>(turns ?? []);
        _scripted = _turns.Count > 0;
        _modelQuery = modelQuery;
        _listener.Start();
        Url = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _run = RunAsync();
    }

    internal string Url { get; }
    internal ToolCall[] Calls { get; init; } = [];
    internal List<JsonElement> ToolResults { get; } = [];
    internal bool RejectNextSend { get; set; }
    internal bool RejectCreate { get; init; }
    internal int CreateCount { get; private set; }
    internal int SendCount { get; private set; }
    internal Task FirstToolHandled => _firstToolHandled.Task;

    /// <summary>The <c>params</c> of the last <c>session.create</c> request.</summary>
    internal JsonElement CreateOptions { get; private set; }

    /// <summary>Runs on the receive loop after a scripted send is acknowledged, before its turn events.</summary>
    internal Func<Task>? BeforeTurn { get; set; }

    /// <summary>
    /// When set, a <c>session.error</c> event is emitted straight after a turn's tool calls are
    /// dispatched, while the send is still waiting for idle. The payload carries the two fields the
    /// SDK marks required (<c>errorType</c>, <c>message</c>); any other shape fails deserialisation
    /// and the event never reaches the host at all.
    /// </summary>
    internal string? SessionErrorAfterDispatch { get; init; }

    /// <summary>Reports receive-loop failures from the test body.</summary>
    /// <remarks>Do not throw them from disposal, which could mask the original assertion failure.</remarks>
    internal void ThrowIfFaulted()
    {
        if (_fault.Task.IsCompleted)
            throw new InvalidOperationException(
                "Offline SDK server faulted — see inner exception for the message it could not handle.",
                _fault.Task.Result);
    }

    private void Fault(string reason, Exception? cause = null) =>
        _fault.TrySetResult(new InvalidOperationException($"Offline SDK server: {reason}", cause));

    /// <summary>Sends <paramref name="sessionEvent"/> to the connected client.</summary>
    /// <remarks>
    /// The event carries the session id from <c>session.create</c>, so emit only after the session
    /// exists.
    /// </remarks>
    internal async Task EmitAsync(SessionEvent sessionEvent)
    {
        var stream = await _connection.Task.WaitAsync(_stop.Token);
        await WriteAsync(stream, new
        {
            jsonrpc = "2.0",
            method = "session.event",
            @params = new
            {
                sessionId = _sessionId,
                @event = JsonSerializer.Deserialize<JsonElement>(sessionEvent.ToJson()),
            },
        });
    }

    private async Task RunAsync()
    {
        string? handling = null;
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            _connection.TrySetResult(stream);
            while (!_stop.IsCancellationRequested)
            {
                // Cleared before the read, not just set after it. Left stale, a failure that is not
                // a dispatch failure -- a malformed next frame, a write that fails, a socket that
                // drops while _stop is not cancelled -- would be reported against the previous
                // message, which had already been answered correctly.
                handling = null;
                using var message = await ReadAsync(stream, _stop.Token);
                if (message is null)
                    return;
                var request = message.RootElement;
                handling = request.GetRawText();
                if (!request.TryGetProperty("method", out var methodElement))
                {
                    Fault($"request carried no 'method': {handling}");
                    continue;
                }
                var method = methodElement.GetString();
                if (!request.TryGetProperty("id", out var idElement))
                {
                    // A JSON-RPC notification: no id, no reply expected. The SDK sends these on the
                    // abort path, and this fake has nothing to do with any of them. Reading id
                    // unconditionally made one crash the receive loop, and that crash only surfaced
                    // at server shutdown -- the same trap session.detach fell into.
                    continue;
                }
                var id = idElement.Clone();
                object result;
                switch (method)
                {
                    case "connect":
                        result = new { ok = true, protocolVersion = 3, version = "offline-test" };
                        break;
                    case "session.create":
                        CreateCount++;
                        if (RejectCreate)
                        {
                            await WriteAsync(stream, new
                            {
                                jsonrpc = "2.0", id,
                                error = new { code = -32603, message = "offline create failure" },
                            });
                            continue;
                        }
                        if (request.TryGetProperty("params", out var createParams))
                            CreateOptions = createParams.Clone();
                        if (createParams.ValueKind == JsonValueKind.Object
                            && createParams.TryGetProperty("sessionId", out var created)
                            && created.GetString() is { Length: > 0 } sessionId)
                            _sessionId = sessionId;
                        else
                            Fault($"session.create carried no params.sessionId: {handling}");
                        result = new { sessionId = _sessionId };
                        break;
                    case "session.send":
                        SendCount++;
                        if (RejectNextSend)
                        {
                            RejectNextSend = false;
                            await WriteAsync(stream, new
                            {
                                jsonrpc = "2.0", id,
                                error = new { code = -32603, message = "offline send failure" },
                            });
                            continue;
                        }
                        await ReplyAsync(stream, id, new { messageId = Guid.NewGuid().ToString() });
                        if (_scripted)
                        {
                            var turn = _turns.Dequeue();
                            // A throwing callback is the test's failure, not the connection's: record
                            // it and keep answering, so teardown requests such as session.detach still
                            // get a reply and the test reports the fault instead of hanging.
                            if (BeforeTurn is not null)
                            {
                                try { await BeforeTurn(); }
                                catch (Exception ex) { Fault($"BeforeTurn threw while handling {handling}", ex); }
                            }
                            await EmitAsync(new AssistantTurnStartEvent
                            {
                                Data = new AssistantTurnStartData { TurnId = Guid.NewGuid().ToString() },
                            });
                            foreach (var sessionEvent in turn)
                                await EmitAsync(sessionEvent);
                            continue;
                        }
                        _pending = Calls.Length;
                        foreach (var call in Calls)
                        {
                            await EventAsync(stream, "external_tool.requested", new
                            {
                                sessionId = _sessionId,
                                requestId = Guid.NewGuid().ToString(),
                                toolCallId = Guid.NewGuid().ToString(),
                                toolName = call.Name,
                                arguments = call.Arguments,
                            });
                        }
                        if (SessionErrorAfterDispatch is { } errorMessage)
                        {
                            await EventAsync(stream, "session.error", new
                            {
                                errorType = "offline_test_error",
                                message = errorMessage,
                            });
                        }
                        if (_pending == 0)
                            await EventAsync(stream, "session.idle", new { });
                        continue;
                    case "session.tools.handlePendingToolCall":
                        if (request.TryGetProperty("params", out var toolParams))
                            ToolResults.Add(toolParams.Clone());
                        else
                            Fault($"session.tools.handlePendingToolCall carried no params: {handling}");
                        _firstToolHandled.TrySetResult();
                        await ReplyAsync(stream, id, new { });
                        // Guarded, because a _pending that goes negative never returns to zero:
                        // session.idle would then never fire again and the next send would hang to
                        // the test's budget, with nothing to say why.
                        if (_pending > 0 && --_pending == 0)
                            await EventAsync(stream, "session.idle", new { });
                        continue;
                    case "session.model.getCurrent" when _modelQuery is not null:
                        var query = new ModelQuery(this, stream, id);
                        await _modelQuery(query);
                        // Returning ends the loop and disposes the socket the client is waiting on.
                        if (query.ConnectionDropped)
                            return;
                        continue;
                    case "session.destroy":
                    case "session.abort":
                    // Teardown is not this test's subject, and the SDK's teardown sequence is not
                    // frozen: session.detach appeared here only once ChatAgent.DisposeAsync started
                    // cancelling the in-flight turn, which let disposal run to completion instead of
                    // timing out part-way through. Answering the teardown verbs generically keeps a
                    // span-parentage test from failing over the shape of a goodbye.
                    case "session.detach":
                    case "session.shutdown":
                        result = new { };
                        break;
                    default:
                        Fault($"unexpected request {method} :: {handling}");
                        await WriteAsync(stream, new
                        {
                            jsonrpc = "2.0", id,
                            error = new { code = -32601, message = $"Method not found: {method}" },
                        });
                        continue;
                }
                await ReplyAsync(stream, id, result);
            }
        }
        catch (Exception ex) when (!IsExpectedShutdown(ex))
        {
            Fault($"could not handle {handling ?? "<no message read>"}", ex);
        }
    }

    // Cancelling _stop tears the socket down under whatever read or write is in flight, so these
    // are the ordinary way this loop ends -- not something a test should be told about.
    private bool IsExpectedShutdown(Exception ex) =>
        _stop.IsCancellationRequested
        && ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException;

    private Task ReplyAsync(Stream stream, JsonElement id, object result) =>
        WriteAsync(stream, new { jsonrpc = "2.0", id, result });

    private Task EventAsync(Stream stream, string type, object data) =>
        WriteAsync(stream, new
        {
            jsonrpc = "2.0",
            method = "session.event",
            @params = new
            {
                sessionId = _sessionId,
                @event = new
                {
                    id = Guid.NewGuid().ToString(),
                    timestamp = DateTimeOffset.UtcNow,
                    parentId = (string?)null,
                    type,
                    data,
                },
            },
        });

    // Serialized because EmitAsync writes from the test while the receive loop also writes.
    private async Task WriteAsync(Stream stream, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await _writeGate.WaitAsync(_stop.Token);
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), _stop.Token);
            await stream.WriteAsync(bytes, _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<JsonDocument?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new List<byte>();
        var next = new byte[1];
        while (header.Count < 8192)
        {
            if (await stream.ReadAsync(next, ct) == 0)
            {
                if (header.Count == 0)
                    return null;
                throw new EndOfStreamException("Incomplete offline RPC header.");
            }
            header.Add(next[0]);
            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
                break;
        }
        var headerText = Encoding.ASCII.GetString(header.ToArray());
        // First, not Single: a peer is allowed to send other headers, and a second one would have
        // turned a framing detail into a receive-loop crash attributed to an unrelated test.
        // Content-Length is the one header this framing cannot recover from, so a missing or
        // unparseable value is the one case that still has to end the loop.
        var lengthHeader = headerText.Split("\r\n")
            .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Offline RPC header carried no Content-Length: '{headerText}'.");
        if (!int.TryParse(lengthHeader["Content-Length:".Length..], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var length) || length < 0)
            throw new InvalidOperationException($"Offline RPC header carried an unusable Content-Length: '{lengthHeader}'.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return JsonDocument.Parse(body);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(60));
        }
        // One predicate for both filters. They used to list different sets, so
        // ObjectDisposedException was "expected shutdown" in the loop -- deliberately not caught
        // there -- and then escaped from here, out of the `await using` finally, replacing whatever
        // the test body had actually failed on. TimeoutException is WaitAsync's own failure and
        // belongs to teardown alone.
        catch (Exception ex) when (IsExpectedShutdown(ex) || (_stop.IsCancellationRequested && ex is TimeoutException))
        {
        }
        finally
        {
            // Only once the loop has actually finished. _stop.Token is read throughout RunAsync,
            // so disposing the source while a read is still parked on it replaces a hung fake with
            // an ObjectDisposedException charged to whichever test runs next.
            if (_run.IsCompleted)
            {
                _stop.Dispose();
                _writeGate.Dispose();
            }
        }
    }

    internal sealed record ToolCall(string Name, object Arguments);

    /// <summary>One <c>session.model.getCurrent</c> request, answered by the test's handler.</summary>
    /// <remarks>A handler that returns without answering leaves the request pending.</remarks>
    internal sealed class ModelQuery(OfflineCopilotServer server, Stream stream, JsonElement id)
    {
        internal bool ConnectionDropped { get; private set; }

        /// <summary>The server that received the request, for emitting events before the answer.</summary>
        internal OfflineCopilotServer Server => server;

        internal Task ReplyAsync(object? result) =>
            server.WriteAsync(stream, new { jsonrpc = "2.0", id, result });

        internal Task FailAsync(int code, string message) =>
            server.WriteAsync(stream, new { jsonrpc = "2.0", id, error = new { code, message } });

        /// <summary>Closes the connection instead of answering, once the handler returns.</summary>
        internal void DropConnection() => ConnectionDropped = true;
    }
}
