using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class NativeDungeonRevivalWorkerChecks
{
    internal static async Task RunAsync(string executable, string directory)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.CreateDirectory(directory);
        var busy = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(p => p.Port).ToHashSet();
        int port = Enumerable.Range(0, 400).Select(i => 54000 + i * 10)
            .First(p => Enumerable.Range(p, 9).All(x => !busy.Contains(x)));
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            WorkingDirectory = Path.GetFullPath(directory), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { (port + 8).ToString(), "0", "0", "0", "profile.ini", port.ToString() })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start isolated worker.");
        var output = Drain(process.StandardOutput, Path.Combine(directory, "native.log"));
        var error = Drain(process.StandardError, Path.Combine(directory, "native-error.log"));
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var client = new TcpClient { NoDelay = true };
            for (;;)
            {
                if (process.HasExited) throw new IOException("Isolated worker exited before connection.");
                try { await client.ConnectAsync(IPAddress.Loopback, port, stop.Token); break; }
                catch (SocketException) { await Task.Delay(100, stop.Token); }
            }
            var stream = client.GetStream();
            var character = new CharacterRecord
            {
                Id = 1, Name = "Revival", Level = 1, MaxHp = 2000, CurrentHp = 2000,
                MaxMp = 800, CurrentMp = 800, Hans = 1000, RevivalUseCount = 3
            };
            var initial = NativeDungeonState.Create(character, [], []);
            await stream.WriteAsync(NativeDungeonClient.Frame(0xF100, initial.Bytes), stop.Token);
            var state = await ReadState(stream, stop.Token);
            Require(state.Get(20) == 2000 && state.Get(60) == 3, "initial state import");
            // These frames deliberately share a recv buffer. Losing the parser's
            // cursor repeats F104 forever instead of reaching the state request.
            for (int i = 0; i < 4; ++i)
            {
                ushort hp = (ushort)(1000 + i), mp = (ushort)(400 + i);
                var sync = NativeDungeonClient.Frame(0xF104,
                    NetworkAdapterService.BuildNativePaidContinueRuntimeSyncPayload(hp, mp));
                var query = NativeDungeonClient.Frame(0xF101, []);
                await stream.WriteAsync(sync.Concat(query).ToArray(), stop.Token);
                var ack = await ReadFrame(stream, stop.Token);
                Require(NetworkAdapterService.TryParseNativePaidContinueRuntimeSyncAck(ack, hp, mp), "one valid F105");
                state = await ReadState(stream, stop.Token);
                Require(state.Get(20) == hp && state.Get(28) == mp && state.GetBalance(32) == 1000,
                    "F104 advances parser to F101 with synchronized HP/MP and unchanged wallet");
            }
            var invalid = NativeDungeonClient.Frame(0xF104,
                NetworkAdapterService.BuildNativePaidContinueRuntimeSyncPayload(65000, 400));
            await stream.WriteAsync(invalid.Concat(NativeDungeonClient.Frame(0xF101, [])).ToArray(), stop.Token);
            var rejected = await ReadFrame(stream, stop.Token);
            Require(Op(rejected) == 0xF105 && BinaryPrimitives.ReadUInt16LittleEndian(rejected.AsSpan(8)) == 0,
                "invalid resource sync is rejected once");
            state = await ReadState(stream, stop.Token);
            Require(state.Get(20) == 1003, "rejected sync preserves HP and parser position");
            // An F104 split across writes must be buffered rather than replayed.
            var split = NativeDungeonClient.Frame(0xF104,
                NetworkAdapterService.BuildNativePaidContinueRuntimeSyncPayload(900, 300));
            await stream.WriteAsync(split.AsMemory(0, 9), stop.Token);
            await Task.Delay(30, stop.Token);
            await stream.WriteAsync(split.AsMemory(9), stop.Token);
            await stream.WriteAsync(NativeDungeonClient.Frame(0xF101, []), stop.Token);
            Require(NetworkAdapterService.TryParseNativePaidContinueRuntimeSyncAck(await ReadFrame(stream, stop.Token), 900, 300),
                "fragmented F104 returns one acknowledgement");
            state = await ReadState(stream, stop.Token);
            Require(state.Get(20) == 900 && state.Get(28) == 300, "fragmented sync preserves checkpoint boundary");
            await Task.Delay(100, stop.Token);
            Require(client.Available == 0, "no unsolicited repeated acknowledgements after checkpoint");
            Console.WriteLine("NATIVE_DUNGEON_REVIVAL_WORKER_PASS coalesced fragmented rejected bounded no-repeat");
        }
        finally
        {
            // Only the worker started by this check belongs to this test.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
        }
    }

    private static void Require(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
    private static ushort Op(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6));
    private static async Task<NativeDungeonState> ReadState(NetworkStream stream, CancellationToken token)
    {
        var frame = await ReadFrame(stream, token);
        Require(Op(frame) == 0xF102, $"checkpoint follows acknowledgement, got {Op(frame):X4}");
        return new NativeDungeonState(frame[8..]);
    }
    private static async Task<byte[]> ReadFrame(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[8]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (length < 8 || length > 8192) throw new InvalidDataException("Invalid worker frame size.");
        var frame = new byte[length]; header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(8), token);
        return frame;
    }
    private static async Task Drain(StreamReader reader, string path)
    {
        await using var writer = new StreamWriter(path);
        int retained = 0;
        while (await reader.ReadLineAsync() is { } line)
            if (retained < 256000) { await writer.WriteLineAsync(line); retained += line.Length; }
    }
}
