using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenNanaimo.Adapter.Services;

int checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    checks++; Console.WriteLine("PASS " + label);
}
var repeatedDisposal = new NativeDungeonClient(_ => Task.CompletedTask);
await Task.WhenAll(repeatedDisposal.DisposeAsync().AsTask(), repeatedDisposal.DisposeAsync().AsTask());
await repeatedDisposal.DisposeAsync();
Check(true, "overlapping and repeated transport teardown completes once");

async Task<byte[]> ReadFrame(NetworkStream stream)
{
    var header = new byte[8]; await stream.ReadExactlyAsync(header);
    var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4))];
    header.CopyTo(frame, 0); await stream.ReadExactlyAsync(frame.AsMemory(8)); return frame;
}
NativeDungeonState State(uint marker)
{
    var bytes = new byte[NativeDungeonState.Size];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), marker);
    return new NativeDungeonState(bytes);
}
async Task ExpectFailure(Func<Task> request, string expected)
{
    var timer = Stopwatch.StartNew();
    try { await request(); throw new InvalidOperationException("Transport failure was accepted."); }
    catch (IOException ex)
    {
        Check(ex.ToString().Contains(expected, StringComparison.Ordinal), "failure retains " + expected);
        Check(timer.Elapsed < TimeSpan.FromSeconds(2), "failed transport rejects requests immediately");
    }
}
async Task WithServer(Func<NativeDungeonClient, TcpClient, Task> run)
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    try
    {
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var client = new NativeDungeonClient(_ => Task.CompletedTask, port);
        await client.ConnectAsync(CancellationToken.None);
        using var peer = await listener.AcceptTcpClientAsync();
        await run(client, peer);
    }
    finally { listener.Stop(); }
}
await WithServer(async (client, peer) =>
{
    var request = client.ExchangeCapturedAsync(null, null, CancellationToken.None);
    var frame = await ReadFrame(peer.GetStream());
    Check(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == 0xF101, "snapshot remains request driven");
    await peer.GetStream().WriteAsync(NativeDungeonClient.Frame(0xCF7E, new byte[4]));
    await peer.GetStream().WriteAsync(NativeDungeonClient.Frame(0xF102, State(17).Bytes));
    var response = await request;
    Check(response.State.Get(4) == 17 && response.Frames.Count == 1, "snapshot and captured frames preserve ordering");
});
await WithServer(async (client, peer) =>
{
    peer.Close(); await Task.Delay(100);
    await ExpectFailure(() => client.ExchangeAsync(null, null, CancellationToken.None), "receive failed");
    await ExpectFailure(() => client.SendAsync(NativeDungeonClient.Frame(0xCF70, []), CancellationToken.None), "receive failed");
});
await WithServer(async (client, peer) =>
{
    var request = client.ExchangeAsync(null, null, CancellationToken.None);
    await ReadFrame(peer.GetStream()); peer.Close();
    await ExpectFailure(async () => await request, "receive failed");
});
await WithServer(async (client, peer) =>
{
    var request = client.ExchangeAsync(null, null, CancellationToken.None);
    await ReadFrame(peer.GetStream());
    await peer.GetStream().WriteAsync(NativeDungeonClient.Frame(0xF102, new byte[4]));
    await ExpectFailure(async () => await request, "Invalid native state length");
    await ExpectFailure(() => client.ExchangeAsync(null, null, CancellationToken.None), "Invalid native state length");
});
await WithServer(async (client, peer) =>
{
    using var cancel = new CancellationTokenSource();
    var request = client.ExchangeAsync(null, null, cancel.Token);
    await ReadFrame(peer.GetStream()); cancel.Cancel();
    try { await request; throw new InvalidOperationException("Canceled exchange completed."); }
    catch (OperationCanceledException) { Check(true, "caller cancellation remains distinguishable"); }
    await ExpectFailure(() => client.ExchangeAsync(null, null, CancellationToken.None), "exchange abandoned");
    var buffer = new byte[1];
    Check(await peer.GetStream().ReadAsync(buffer) == 0, "abandoned exchange closes the sequence-less transport");
});
Console.WriteLine($"NATIVE_DUNGEON_TRANSPORT_PASS checks={checks}");
