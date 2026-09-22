#if NET6_0
internal static class CompatibilityChecks
{
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
    }

    internal static async Task RunAsync()
    {
        byte[] expected = [1, 2, 3, 4, 5];
        using var stream = new FragmentedStream(expected);
        var actual = new byte[5];
        await stream.ReadExactlyAsync(actual);
        if (!actual.SequenceEqual(expected)) throw new Exception("Fragmented stream read failed.");
        try { await stream.ReadExactlyAsync(new byte[1]); throw new Exception("Truncated stream accepted."); }
        catch (EndOfStreamException) { }
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        using var pending = new FragmentedStream(expected);
        try { await pending.ReadExactlyAsync(new byte[1], canceled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        int[] shuffled = Enumerable.Range(0, 100).ToArray();
        Random.Shared.Shuffle(shuffled);
        if (!shuffled.OrderBy(value => value).SequenceEqual(Enumerable.Range(0, 100))) throw new Exception("Shuffle lost elements.");
        byte[] values = [0, 0, 2, 0];
        if (values.AsSpan().IndexOfAnyExcept(0) != 2 || values.AsSpan(0, 2).IndexOfAnyExcept(0) != -1)
            throw new Exception("Span scan mismatch.");
        Console.WriteLine("NET6_COMPATIBILITY_CHECKS_PASS fragmented-read eof cancellation shuffle span");
    }
}
#endif
