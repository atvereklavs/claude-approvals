using System.Text;

namespace ClaudeApprovals.Core.Server;

/// <summary>A parsed HTTP/1.1 request (method, path, lowercased headers, body).</summary>
public sealed record HttpRequestMessage2(
    string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>
/// Incremental single-request HTTP/1.1 parser (port of the Mac HTTPRequestParser).
/// Feed bytes; when Feed returns true, Request is available.
/// </summary>
public sealed class HttpRequestParser
{
    private readonly MemoryStream _buffer = new();
    private int _headerEnd = -1;
    private int _contentLength;
    private (string Method, string Path, Dictionary<string, string> Headers)? _head;

    public HttpRequestMessage2? Request { get; private set; }

    public bool Feed(ReadOnlySpan<byte> data)
    {
        _buffer.Write(data);
        var bytes = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);

        if (_headerEnd < 0)
        {
            var idx = IndexOfDoubleCrLf(bytes);
            if (idx < 0)
            {
                if (_buffer.Length > 64 * 1024) throw new InvalidDataException("headers too large");
                return false;
            }
            _headerEnd = idx + 4;
            ParseHead(Encoding.UTF8.GetString(bytes[..idx]));
        }

        if (_head is null) return false;
        var available = (int)_buffer.Length - _headerEnd;
        if (available < _contentLength) return false;

        var body = bytes.Slice(_headerEnd, _contentLength).ToArray();
        var (method, path, headers) = _head.Value;
        Request = new HttpRequestMessage2(method, path, headers, body);
        return true;
    }

    private void ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) throw new InvalidDataException("bad request line");

        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        _contentLength = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
        _head = (parts[0], parts[1], headers);
    }

    private static int IndexOfDoubleCrLf(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        return -1;
    }
}

/// <summary>Minimal HTTP/1.1 response serializer.</summary>
public static class HttpResponseWriter
{
    public static byte[] Serialize(int status, string contentType, byte[] body)
    {
        var reason = status switch
        {
            200 => "OK", 204 => "No Content", 400 => "Bad Request",
            403 => "Forbidden", 404 => "Not Found", 500 => "Internal Server Error",
            _ => "Status",
        };
        var head = $"HTTP/1.1 {status} {reason}\r\n" +
                   $"Content-Type: {contentType}\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   "Connection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        var result = new byte[headBytes.Length + body.Length];
        headBytes.CopyTo(result, 0);
        body.CopyTo(result, headBytes.Length);
        return result;
    }

    public static byte[] Json(int status, string json) =>
        Serialize(status, "application/json", Encoding.UTF8.GetBytes(json));

    public static byte[] Text(int status, string text) =>
        Serialize(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    public static byte[] Empty(int status) =>
        Serialize(status, "application/json", Array.Empty<byte>());
}
