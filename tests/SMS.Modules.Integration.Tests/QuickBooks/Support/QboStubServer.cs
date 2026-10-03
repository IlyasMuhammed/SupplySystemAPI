using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SMS.Modules.Integration.Tests.QuickBooks.Support;

/// <summary>One request the SDK sent to the stub.</summary>
internal sealed record StubRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>A scripted reply. <see cref="Drop"/> closes the connection without answering.</summary>
internal sealed record StubResponse(int Status, string Body, string? IntuitTid = "stub-tid", bool Drop = false);

/// <summary>
/// A minimal HTTP/1.1 server on 127.0.0.1 standing in for QuickBooks, so tests run Intuit's real
/// DataService / QueryService / FaultHandler end to end with no network beyond loopback. Replies are
/// scripted in order; each connection is answered once and closed.
/// </summary>
internal sealed class QboStubServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<StubResponse> _script = new();
    private readonly List<StubRequest> _requests = new();

    public QboStubServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public IReadOnlyList<StubRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public QboStubServer Reply(int status, string body, string? intuitTid = "stub-tid")
    {
        _script.Enqueue(new StubResponse(status, body, intuitTid));
        return this;
    }

    public QboStubServer DropConnection()
    {
        _script.Enqueue(new StubResponse(0, string.Empty, null, Drop: true));
        return this;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new MemoryStream();
                var chunk  = new byte[8192];
                int headerEnd;
                while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8)) < 0)
                {
                    var read = await stream.ReadAsync(chunk);
                    if (read == 0) return;
                    buffer.Write(chunk, 0, read);
                }

                var all        = buffer.ToArray();
                var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
                var lines      = headerText.Split("\r\n");
                var requestLine = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                if (headers.TryGetValue("Expect", out var expect) && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                    await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray());

                var body = new MemoryStream();
                body.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
                if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length))
                {
                    while (body.Length < length)
                    {
                        var read = await stream.ReadAsync(chunk);
                        if (read == 0) break;
                        body.Write(chunk, 0, read);
                    }
                }
                else if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    while (IndexOf(body, "0\r\n\r\n"u8) < 0)
                    {
                        var read = await stream.ReadAsync(chunk);
                        if (read == 0) break;
                        body.Write(chunk, 0, read);
                    }
                    body = Dechunk(body.ToArray());
                }

                var request = new StubRequest(requestLine[0], requestLine.Length > 1 ? requestLine[1] : "/", headers,
                    Encoding.UTF8.GetString(body.ToArray()));
                lock (_requests) _requests.Add(request);

                var response = _script.TryDequeue(out var scripted)
                    ? scripted
                    : new StubResponse(500, "{\"unscripted\":true}");

                if (response.Drop)
                {
                    client.Client.LingerState = new LingerOption(true, 0);   // reset, no response
                    client.Client.Close();
                    return;
                }

                var payload = Encoding.UTF8.GetBytes(response.Body);
                var head = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n")
                    .Append("Content-Type: application/json;charset=UTF-8\r\n")
                    .Append("Content-Length: ").Append(payload.Length).Append("\r\n");
                if (response.IntuitTid is not null) head.Append("intuit_tid: ").Append(response.IntuitTid).Append("\r\n");
                head.Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            }
            catch
            {
                // A test that aborts mid-request must not take the server down.
            }
        }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _   => "Status"
    };

    private static int IndexOf(MemoryStream stream, ReadOnlySpan<byte> pattern) =>
        stream.GetBuffer().AsSpan(0, (int)stream.Length).IndexOf(pattern);

    private static MemoryStream Dechunk(byte[] raw)
    {
        var result = new MemoryStream();
        var position = 0;
        while (position < raw.Length)
        {
            var lineEnd = raw.AsSpan(position).IndexOf("\r\n"u8);
            if (lineEnd < 0) break;
            var size = Convert.ToInt32(Encoding.ASCII.GetString(raw, position, lineEnd).Split(';')[0].Trim(), 16);
            position += lineEnd + 2;
            if (size == 0) break;
            result.Write(raw, position, size);
            position += size + 2;
        }
        return result;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
