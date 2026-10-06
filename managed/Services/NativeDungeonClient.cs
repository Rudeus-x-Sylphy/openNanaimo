using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace OpenNanaimo.Adapter.Services;

public sealed class NativeDungeonClient : IAsyncDisposable
{
    private readonly TcpClient _client = new() { NoDelay = true };
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<byte[], Task> _receive;
    private readonly int _port;
    private Task? _reader;
    private TaskCompletionSource<NativeDungeonState>? _pending;
    private List<byte[]>? _capturedFrames;
    private IOException? _readFailure;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    public NativeDungeonClient(Func<byte[], Task> receive, int port = 52050) { _receive = receive; _port = port; }
    public async Task ConnectAsync(CancellationToken token)
    {
        await _client.ConnectAsync(IPAddress.Loopback, _port, token);
        _reader = ReadLoopAsync(_stop.Token);
    }
    public static byte[] Frame(ushort opcode, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 8]; frame[0] = 0x0E; frame[1] = 0xE0;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), opcode); payload.CopyTo(frame.AsSpan(8));
        uint sum = 0; for (int i = 4; i < frame.Length; i++) sum += frame[i];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)(sum ^ 0x0E0E));
        return frame;
    }
    public async Task<NativeDungeonState> ExchangeAsync(byte[]? request, NativeDungeonState? import, CancellationToken token)
        => (await ExchangeCoreAsync(request, import, captureFrames: false, token)).State;

    public Task<NativeDungeonExchangeResult> ExchangeCapturedAsync(
        byte[]? request,
        NativeDungeonState? import,
        CancellationToken token)
        => ExchangeCoreAsync(request, import, captureFrames: true, token);

    private async Task<NativeDungeonExchangeResult> ExchangeCoreAsync(
        byte[]? request,
        NativeDungeonState? import,
        bool captureFrames,
        CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfUnavailable();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var pending = new TaskCompletionSource<NativeDungeonState>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = pending;
            _capturedFrames = captureFrames ? [] : null;
            try
            {
                var stream = _client.GetStream();
                if (request is not null) await stream.WriteAsync(request, timeout.Token);
                await stream.WriteAsync(Frame(import is null ? (ushort)0xF101 : (ushort)0xF100, import?.Bytes ?? []), timeout.Token);
                var state = await pending.Task.WaitAsync(timeout.Token);
                return new NativeDungeonExchangeResult(state, _capturedFrames?.ToArray() ?? []);
            }
            catch (Exception) when (Volatile.Read(ref _readFailure) is not null)
            {
                ThrowIfUnavailable();
                throw;
            }
            catch (OperationCanceledException)
            {
                // Exchanges have no transaction identifier. Retire the transport
                // after an abandoned reply so it cannot satisfy a later request.
                FailTransport(new IOException($"Native worker state exchange abandoned on port {_port}.",
                    token.IsCancellationRequested ? new OperationCanceledException(token)
                        : new TimeoutException("Native worker state exchange exceeded ten seconds.")));
                if (token.IsCancellationRequested) throw;
                ThrowIfUnavailable();
                throw;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                FailTransport(new IOException($"Native worker state exchange failed on port {_port}: {ex.Message}", ex));
                ThrowIfUnavailable();
                throw;
            }
        }
        finally
        {
            _capturedFrames = null;
            _pending = null;
            _gate.Release();
        }
    }
    public async Task SendAsync(byte[] frame, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { ThrowIfUnavailable(); await _client.GetStream().WriteAsync(frame, token); }
        finally { _gate.Release(); }
    }
    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _readFailure) is { } failure)
            throw new IOException(failure.Message, failure);
        if (_stop.IsCancellationRequested)
            throw new ObjectDisposedException(nameof(NativeDungeonClient));
    }
    private void FailTransport(IOException failure)
    {
        Interlocked.CompareExchange(ref _readFailure, failure, null);
        _pending?.TrySetException(Volatile.Read(ref _readFailure)!);
        _client.Close();
        _stop.Cancel();
    }
    private async Task ReadLoopAsync(CancellationToken token)
    {
        ushort lastOpcode = 0;
        try
        {
            var stream = _client.GetStream();
            while (!token.IsCancellationRequested)
            {
                var header = new byte[8]; await stream.ReadExactlyAsync(header, token);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                if (length < 8 || length > 8192) throw new InvalidDataException("Invalid native worker frame.");
                var frame = new byte[length]; header.CopyTo(frame, 0); await stream.ReadExactlyAsync(frame.AsMemory(8), token);
                var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6));
                lastOpcode = opcode;
                if (opcode == 0xF102)
                    _pending?.TrySetResult(new NativeDungeonState(frame[8..]));
                else if (_capturedFrames is { } captured)
                    captured.Add(frame);
                else
                    await _receive(frame);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                FailTransport(new IOException(
                    $"Native worker receive failed on port {_port}; last opcode=0x{lastOpcode:X4}: {ex.Message}", ex));
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync(); _client.Dispose();
        if (_reader is not null) await _reader;
        _stop.Dispose(); _gate.Dispose();
    }
}

public readonly record struct NativeDungeonExchangeResult(
    NativeDungeonState State,
    IReadOnlyList<byte[]> Frames);
