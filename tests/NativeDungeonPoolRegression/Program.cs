using System.Net;
using System.Net.Sockets;
using OpenNanaimo.Adapter.Services;

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
using var client = new TcpClient();
var connect = client.ConnectAsync(IPAddress.Loopback, port);
using var accepted = await listener.AcceptTcpClientAsync();
await connect;

Check(!NativeDungeonPool.IsPortConnectionQuiet(port),
    "an established connection keeps a worker port quarantined");
Check(!await NativeDungeonPool.WaitForPortQuietAsync(
        port, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10), CancellationToken.None),
    "quiet wait times out while the old room connection is alive");

accepted.Dispose();
client.Dispose();
listener.Stop();
Check(await NativeDungeonPool.WaitForPortQuietAsync(
        port, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(10), CancellationToken.None),
    "quiet wait releases the port after both endpoints close");

Console.WriteLine("NATIVE_DUNGEON_POOL_REGRESSION_PASS");
