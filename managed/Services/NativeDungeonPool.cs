using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpenNanaimo.Adapter.Services;

public sealed class NativeDungeonPool(string executable, string dataDirectory) : IAsyncDisposable
{
    private sealed class Room
    {
        public required int Port { get; init; }
        public int Users { get; set; }
        public Process? Process { get; init; }
        public Task? Output { get; init; }
        public Task? Error { get; init; }
    }
    public sealed class Lease(NativeDungeonPool owner, string key, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;
        private bool _released;
        public async ValueTask DisposeAsync()
        { if (!_released) { _released = true; await owner.ReleaseAsync(key); } }
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly NativeProcessJob _job = new();
    private readonly Dictionary<string, Room> _rooms = [];
    private bool _disposed;

    public sealed record RoomSnapshot(string Key, int Port, int Users, int? ProcessId);
    public async Task<IReadOnlyList<RoomSnapshot>> GetSnapshotAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return _rooms.Select(r => new RoomSnapshot(r.Key,r.Value.Port,r.Value.Users,r.Value.Process?.Id)).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<Lease> AcquireAsync(string key, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeDungeonPool));
            if (!_rooms.TryGetValue(key, out var room))
            {
                if (!_rooms.Values.Any(r => r.Port == 52050)) room = new Room { Port = 52050 };
                else room = await StartRoomAsync(token);
                _rooms.Add(key, room);
            }
            if (room.Users >= 6) throw new InvalidOperationException("Native dungeon party is full.");
            if (room.Process?.HasExited == true) throw new IOException("Native dungeon room exited.");
            room.Users++;
            return new Lease(this, key, room.Port);
        }
        finally { _gate.Release(); }
    }

    private async Task<Room> StartRoomAsync(CancellationToken token)
    {
        var busy = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        int port = Enumerable.Range(0, 100).Select(i => 53000 + i * 10)
            .FirstOrDefault(candidate => Enumerable.Range(candidate, 9).All(p => !busy.Contains(p)));
        if (port == 0) throw new IOException("No free native dungeon port block.");
        string directory = Path.Combine(dataDirectory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { (port + 8).ToString(), "0", "0", "0", "room-profile.ini", port.ToString() }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new IOException("Cannot start native dungeon room.");
        _job.Add(process);
        var output = PumpAsync(process.StandardOutput, Path.Combine(directory, "native.log"));
        var error = PumpAsync(process.StandardError, Path.Combine(directory, "native-error.log"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                if (process.HasExited) throw new IOException($"Native room exited. See {directory}");
                try { using var probe = new TcpClient(); await probe.ConnectAsync(IPAddress.Loopback, port + 7, timeout.Token); break; }
                catch (SocketException) { await Task.Delay(100, timeout.Token); }
            }
            return new Room { Port = port, Process = process, Output = output, Error = error };
        }
        catch
        {
            if (!process.HasExited) process.Kill(); await process.WaitForExitAsync();
            await Task.WhenAll(output, error); process.Dispose(); throw;
        }
    }

    private static async Task PumpAsync(StreamReader reader, string path)
    {
        await using var log = new StreamWriter(path) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line) await log.WriteLineAsync(line);
    }
    private async Task ReleaseAsync(string key)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_rooms.TryGetValue(key, out var room) || --room.Users > 0) return;
            _rooms.Remove(key); await StopRoomAsync(room);
            if (room.Port == 52050) await Task.Delay(150);
        }
        finally { _gate.Release(); }
    }
    private static async Task StopRoomAsync(Room room)
    {
        if (room.Process is not { } process) return;
        if (!process.HasExited) process.Kill(); await process.WaitForExitAsync();
        await Task.WhenAll(room.Output!, room.Error!); process.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            foreach (var room in _rooms.Values) await StopRoomAsync(room);
            _rooms.Clear();
            _job.Dispose();
        }
        finally { _gate.Release(); }
    }
}
