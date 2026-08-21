using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WriteLite.Tests.Ai;

/// <summary>
/// A loopback server that answers with the shapes real local models actually produce when
/// they misbehave: a code fence, a preamble, wrapping quotes, an empty answer.
/// </summary>
/// <remarks>
/// The product rule is that malformed model output must never reach the user's document.
/// <c>ResponseCleaner</c> has unit tests for each of these shapes in isolation; this exists
/// so the same shapes are proved harmless when they arrive over a socket through the whole
/// Smart Action path, which is where it actually matters.
/// </remarks>
internal sealed class MalformedResponseServer : IDisposable
{
    private static readonly string[] BadAnswers =
    [
        "```\\nВот исправленный вариант:\\nМы, вероятно, не успеем это сделать вовремя.\\n```",
        "Вот исправленный вариант: Мы, вероятно, не успеем это сделать вовремя.",
        "\\\"Мы, вероятно, не успеем это сделать вовремя.\\\"",
        "",
    ];

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private int _answerIndex;

    public MalformedResponseServer(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Endpoint => $"http://127.0.0.1:{Port}";

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
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer, _stopping.Token);
                if (read <= 0) return;

                var request = Encoding.UTF8.GetString(buffer, 0, read);
                var body = request.Contains("/health", StringComparison.Ordinal)
                    ? """{"status":"ok"}"""
                    : NextBadAnswer();

                var bytes = Encoding.UTF8.GetBytes(body);
                var header = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                    + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");

                await stream.WriteAsync(header, _stopping.Token);
                await stream.WriteAsync(bytes, _stopping.Token);
                await stream.FlushAsync(_stopping.Token);
            }
        }
        catch
        {
            // Client vanished or the server is shutting down; nothing to do.
        }
    }

    private string NextBadAnswer()
    {
        var answer = BadAnswers[Interlocked.Increment(ref _answerIndex) % BadAnswers.Length];

        // Plain concatenation: the payload is dense with braces and quotes, and an
        // interpolated raw string here is harder to read than the thing it replaces.
        return "{\"choices\":[{\"message\":{\"content\":\"" + answer + "\"}}]}";
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stopping.Dispose();
    }
}
