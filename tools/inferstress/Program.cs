using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WriteLite.AI.Local;

namespace InferStress;

/// <summary>
/// Cancellation stress harness for the local inference backend.
/// </summary>
/// <remarks>
/// Simulates what WriteLite does while someone types: ask the model, change your mind a
/// few tens of milliseconds later, ask again — over and over — and then check that the
/// backend still answers. That sequence is what silenced local AI in Phase 2, because
/// aborting an in-flight request leaves a single-slot llama.cpp server holding a
/// connection it never reaps.
///
///   inferstress                       run against the built-in single-slot stub
///   inferstress --endpoint http://127.0.0.1:8742   run against the real llama-server
///   inferstress --bursts 40 --cancel-after 40
///
/// The stub is the deterministic version and needs no model on disk. The `--endpoint`
/// form is the honest one: it exercises the same contract against the real server.
/// </remarks>
internal static class Program
{
    private const string Sentence = "Я сегодня небыл дома и незнаю что делать дальше.";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var bursts = IntArg(args, "--bursts", 20);
        var cancelAfterMs = IntArg(args, "--cancel-after", 40);
        var endpoint = StringArg(args, "--endpoint");
        var workMs = IntArg(args, "--work", 250);

        if (args.Contains("--verbose"))
        {
            LocalAiDiagnostics.Log = (name, detail) => Console.WriteLine($"    [{name}] {detail}");
        }

        StubServer? stub = null;
        if (endpoint is null)
        {
            stub = new StubServer(FreePort(), TimeSpan.FromMilliseconds(workMs));
            endpoint = stub.Endpoint;
            Console.WriteLine($"stub single-slot server on {endpoint} (work {workMs} ms/request)");
        }
        else
        {
            Console.WriteLine($"real backend at {endpoint}");
        }

        using var _ = stub;

        await using var backend = new QwenModelBackend(
            modelDirectory: Path.Combine(Path.GetTempPath(), "inferstress-" + Guid.NewGuid()),
            endpoint: endpoint,
            allowUnverifiedLoopback: true);

        Console.WriteLine($"bursts={bursts} cancelAfter={cancelAfterMs} ms");
        Console.WriteLine(new string('-', 70));

        var cancelled = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < bursts; i++)
        {
            using var typed = new CancellationTokenSource(TimeSpan.FromMilliseconds(cancelAfterMs));
            try
            {
                if (await backend.InferAsync(Sentence, "ru", typed.Token) is null)
                {
                    cancelled++;
                }
            }
            catch (OperationCanceledException)
            {
                cancelled++;
            }

            if ((i + 1) % 5 == 0)
            {
                Console.WriteLine($"  {i + 1,3}/{bursts} bursts  state={backend.State}  {Elapsed(sw)}");
            }
        }

        Console.WriteLine(new string('-', 70));
        Console.WriteLine($"cancelled/superseded: {cancelled}/{bursts}");

        // The question that matters.
        Console.Write("final request after the storm ... ");
        var finalSw = Stopwatch.StartNew();
        using var final = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await backend.InferAsync(Sentence, "ru", final.Token);
        finalSw.Stop();

        var health = backend.Health();
        Console.WriteLine(result is not null
            ? $"OK in {finalSw.ElapsedMilliseconds} ms"
            : $"FAILED after {finalSw.ElapsedMilliseconds} ms");
        Console.WriteLine($"health: {health}");

        if (stub is not null)
        {
            Console.WriteLine(
                $"stub: started={stub.RequestsStarted} completed={stub.RequestsCompleted} "
                + $"slotsLeaked={stub.SlotsLeaked}");
        }

        var ok = result is not null && (stub?.SlotsLeaked ?? 0) == 0;
        Console.WriteLine();
        Console.WriteLine(ok
            ? "PASS — the backend survived sustained cancellation"
            : "FAIL — the backend was silenced by cancellation (the Phase 2 defect)");
        return ok ? 0 : 1;
    }

    private static string Elapsed(Stopwatch sw) => $"{sw.Elapsed.TotalSeconds:F1}s";

    private static int IntArg(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
            ? value
            : fallback;
    }

    private static string? StringArg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>
/// One inference slot, and a slot that is never returned when the client vanishes
/// mid-generation — the llama.cpp behaviour observed in Phase 2. <c>/health</c> keeps
/// answering 200 throughout, which is precisely why the wedge was invisible.
/// </summary>
internal sealed class StubServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly TimeSpan _work;
    private int _started, _completed, _leaked;

    public StubServer(int port, TimeSpan work)
    {
        _work = work;
        Endpoint = $"http://127.0.0.1:{port}";
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public string Endpoint { get; }
    public int RequestsStarted => Volatile.Read(ref _started);
    public int RequestsCompleted => Volatile.Read(ref _completed);
    public int SlotsLeaked => Volatile.Read(ref _leaked);

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stopping.Token); }
            catch { return; }
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
                if (request is null) return;

                if (request.Contains("/health", StringComparison.Ordinal))
                {
                    await WriteAsync(stream, """{"status":"ok"}""");
                    return;
                }

                if (!await _slot.WaitAsync(TimeSpan.FromSeconds(30), _stopping.Token)) return;

                Interlocked.Increment(ref _started);
                await Task.Delay(_work, _stopping.Token);

                // A client that aborted has sent a FIN: readable with nothing to read.
                if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                {
                    Interlocked.Increment(ref _leaked);   // slot deliberately not returned
                    return;
                }

                await WriteAsync(stream,
                    """{"choices":[{"message":{"content":"{\"language\":\"ru\",\"text\":\"Привет.\"}"}}]}""");
                Interlocked.Increment(ref _completed);
                _slot.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch { Interlocked.Increment(ref _leaked); }
    }

    /// <summary>
    /// Reads headers and drains the declared body. Replying before the request body has
    /// been read makes the peer reset the connection, which looks exactly like the defect
    /// under test — so the stub has to be a well-behaved HTTP server to prove anything.
    /// </summary>
    private static async Task<string?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var text = new StringBuilder();

        for (var i = 0; i < 64; i++)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0) return text.Length > 0 ? text.ToString() : null;

            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            var value = text.ToString();
            var headerEnd = value.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) continue;

            var contentLength = 0;
            foreach (var line in value[..headerEnd].Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                }
            }

            var bodySoFar = Encoding.UTF8.GetByteCount(value[(headerEnd + 4)..]);
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

    private static async Task WriteAsync(NetworkStream stream, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
            + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try { _listener.Stop(); } catch { }
        _stopping.Dispose();
        _slot.Dispose();
    }
}
