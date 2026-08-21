using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WriteLite.Tests.Ai;

/// <summary>
/// A loopback stub that reproduces the failure mode observed from the real llama.cpp
/// server on 2026-08-12: one inference slot, and a slot that is never returned when the
/// client abandons the connection mid-generation.
/// </summary>
/// <remarks>
/// Why a stub rather than the real server: the defect is a contract violation between
/// WriteLite and *any* single-slot inference backend, and the real server takes seconds
/// per request, needs a 2.3 GB model on disk, and cannot be made to fail on a schedule.
/// This models the two properties that actually matter and nothing else:
///
/// 1. <c>--parallel 1</c> — one request is served at a time.
/// 2. A request whose client disconnected before the response was written leaks its slot.
///    That is what produced the accumulating CLOSE_WAIT sockets in production.
///
/// <c>/health</c> keeps answering 200 throughout, which is the whole reason the wedge went
/// unnoticed: the backend looks alive from every angle except the one that matters.
///
/// Written on a raw <see cref="TcpListener"/> rather than <c>HttpListener</c> on purpose.
/// <c>HttpListener</c> buffers small responses, so a write to a vanished client succeeds
/// and the disconnect is invisible — an earlier version of this stub sat on top of it and
/// consequently passed against the very defect it was written to catch. With a socket we
/// can ask directly whether the peer is still there.
/// </remarks>
internal sealed class SingleSlotInferenceServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly TimeSpan _workDuration;
    private readonly Task _loop;

    private int _requestsStarted;
    private int _requestsCompleted;
    private int _slotsLeaked;

    public SingleSlotInferenceServer(int port, TimeSpan? workDuration = null)
    {
        _workDuration = workDuration ?? TimeSpan.FromMilliseconds(400);
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Endpoint => $"http://127.0.0.1:{Port}";

    /// <summary>Requests that reached the inference stage.</summary>
    public int RequestsStarted => Volatile.Read(ref _requestsStarted);

    /// <summary>Requests whose response was written to a client that was still waiting.</summary>
    public int RequestsCompleted => Volatile.Read(ref _requestsCompleted);

    /// <summary>Slots never returned because the client vanished mid-generation.</summary>
    public int SlotsLeaked => Volatile.Read(ref _slotsLeaked);

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
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
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request is null)
                {
                    return;
                }

                // Health never stops answering, however wedged inference is.
                if (request.Contains("/health", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, """{"status":"ok"}""");
                    return;
                }

                if (!await _slot.WaitAsync(TimeSpan.FromSeconds(30), _stopping.Token))
                {
                    return;
                }

                Interlocked.Increment(ref _requestsStarted);
                await Task.Delay(_workDuration, _stopping.Token);

                // Generation is done. Is anyone still listening? A client that aborted
                // has sent a FIN, which shows up as readable-with-nothing-to-read.
                if (PeerHasGoneAway(client.Client))
                {
                    // The real server leaks the slot here. So do we — deliberately not
                    // releasing, which is the entire defect being modelled.
                    Interlocked.Increment(ref _slotsLeaked);
                    return;
                }

                const string payload =
                    """{"choices":[{"message":{"content":"{\"language\":\"ru\",\"text\":\"Привет, как дела?\"}"}}]}""";

                await WriteResponseAsync(stream, payload);
                Interlocked.Increment(ref _requestsCompleted);
                _slot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        catch
        {
            // Any other transport failure behaves like a vanished client.
            Interlocked.Increment(ref _slotsLeaked);
        }
    }

    /// <summary>
    /// True when the peer has closed its side. A socket that is readable but has no bytes
    /// available has received a FIN, which is what an aborted HTTP request leaves behind.
    /// </summary>
    private static bool PeerHasGoneAway(Socket socket)
    {
        try
        {
            return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
        }
        catch
        {
            return true;
        }
    }

    private static async Task<string?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var text = new StringBuilder();

        // Enough of the request to route on; the body is irrelevant to this stub.
        for (var i = 0; i < 64; i++)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                return text.Length > 0 ? text.ToString() : null;
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            var value = text.ToString();
            if (!value.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                continue;
            }

            // Headers complete. Drain the declared body so the socket is not left with
            // unread bytes, which would make the FIN probe ambiguous later.
            var headerEnd = value.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
            var contentLength = ParseContentLength(value);
            var bodySoFar = Encoding.UTF8.GetByteCount(value[headerEnd..]);
            while (bodySoFar < contentLength)
            {
                var more = await stream.ReadAsync(buffer);
                if (more <= 0) break;
                bodySoFar += more;
            }

            return value;
        }

        return text.ToString();
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line["Content-Length:".Length..].Trim(), out var length)) return length;
        }

        return 0;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: application/json\r\n"
            + $"Content-Length: {bytes.Length}\r\n"
            + "Connection: close\r\n\r\n");

        await stream.WriteAsync(header);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stopping.Dispose();
        _slot.Dispose();
    }
}
