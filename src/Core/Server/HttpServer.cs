using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClaudeApprovals.Core.Models;
using ClaudeApprovals.Core.State;

namespace ClaudeApprovals.Core.Server;

/// <summary>
/// Loopback-only TCP HTTP server + router (port of the Mac HTTPServer/Router).
/// TcpListener avoids HttpListener's non-admin URL-ACL requirements, and a raw
/// socket gives natural client-drop detection while a permission long-polls.
/// Endpoints: POST /v1/permission (long-poll) · POST /v1/notify · GET /v1/health.
/// </summary>
public sealed class ApprovalServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly RequestStore _store;
    private readonly string? _token;
    private readonly CancellationTokenSource _cts = new();

    public event Action<HookPayload>? OnNotify;
    public int Port { get; }

    public ApprovalServer(RequestStore store, int port, string? token)
    {
        _store = store;
        _token = token;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port; // resolves port 0 → actual
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = HandleClient(client);
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var parser = new HttpRequestParser();
        var buf = new byte[64 * 1024];

        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buf, _cts.Token);
                if (n == 0) return; // peer closed before a full request
                bool complete;
                try { complete = parser.Feed(buf.AsSpan(0, n)); }
                catch
                {
                    await Write(stream, HttpResponseWriter.Text(400, "bad request"));
                    return;
                }
                if (complete) break;
            }

            var request = parser.Request!;
            await Route(request, client, stream);
        }
        catch { /* connection errors are normal (drops, cancels) */ }
    }

    private async Task Route(HttpRequestMessage2 request, TcpClient client, NetworkStream stream)
    {
        if (request.Method == "GET" && request.Path.StartsWith("/v1/health"))
        {
            var health = new JsonObject { ["ok"] = true, ["pending"] = _store.PendingCount };
            await Write(stream, HttpResponseWriter.Json(200, health.ToJsonString()));
            return;
        }

        if (_token is not null && request.Header("x-notch-token") != _token)
        {
            await Write(stream, HttpResponseWriter.Text(403, "forbidden"));
            return;
        }

        switch (request.Method, request.Path)
        {
            case ("POST", "/v1/permission"):
                await Permission(request, client, stream);
                break;
            case ("POST", "/v1/notify"):
                Notify(request);
                await Write(stream, HttpResponseWriter.Empty(200));
                break;
            default:
                await Write(stream, HttpResponseWriter.Text(404, "not found"));
                break;
        }
    }

    private async Task Permission(HttpRequestMessage2 request, TcpClient client, NetworkStream stream)
    {
        var payload = HookPayload.Parse(System.Text.Encoding.UTF8.GetString(request.Body));
        if (payload is null)
        {
            await Write(stream, HttpResponseWriter.Text(400, "bad payload"));
            return;
        }

        var settled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = _store.Enqueue(payload, body => settled.TrySetResult(body));

        if (id is Guid gid)
        {
            // Watch for the peer dropping while parked (Ctrl-C'd session).
            _ = Task.Run(async () =>
            {
                var probe = new byte[1];
                try
                {
                    var n = await client.Client.ReceiveAsync(probe, SocketFlags.None, _cts.Token);
                    if (n == 0 && !settled.Task.IsCompleted) _store.ClientDropped(gid);
                }
                catch { /* settled or cancelled */ }
            });
        }

        var responseBody = await settled.Task;
        var bytes = responseBody is { Length: > 0 }
            ? HttpResponseWriter.Json(200, responseBody)
            : HttpResponseWriter.Empty(200);
        await Write(stream, bytes);
    }

    private void Notify(HttpRequestMessage2 request)
    {
        var payload = HookPayload.Parse(System.Text.Encoding.UTF8.GetString(request.Body));
        if (payload is null) return;
        if (payload.HookEventName == "Stop" && payload.SessionId is not null)
            _store.SessionStopped(payload.SessionId);
        OnNotify?.Invoke(payload);
    }

    private static async Task Write(NetworkStream stream, byte[] data)
    {
        try { await stream.WriteAsync(data); } catch { /* peer gone */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
