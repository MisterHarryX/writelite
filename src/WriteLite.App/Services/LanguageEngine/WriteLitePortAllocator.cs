using System.Net;
using System.Net.Sockets;

namespace WriteLite.Services.LanguageEngine;

public sealed class WriteLitePortAllocator : IWriteLitePortAllocator
{
    public IEnumerable<int> AllocateCandidates(int preferredPort, int searchRange)
    {
        var start = Math.Clamp(preferredPort, 1024, 65000);
        var end = Math.Min(start + Math.Max(searchRange, 1), 65535);

        for (var port = start; port <= end; port++)
        {
            if (IsPortFree(port))
            {
                yield return port;
            }
        }
    }

    public static int FindFreePort(int preferred, int range)
    {
        foreach (var port in new WriteLitePortAllocator().AllocateCandidates(preferred, range))
        {
            return port;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var ephemeral = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return ephemeral;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
