using System.Buffers.Binary;
using OpenNanaimo.Adapter.Services;

internal static class Program
{
    public static void Main()
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 50);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), 1950);
        payload[4] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), 29820);
        Check(ArenaProtocol.TryParseGameEvent(payload, out var request), "arena event 50 parses");
        Check(request.PrimaryUid == 1950 && request.ValueAt8 == 29820, "event metadata remains separate from damage");
        Check(request.TargetDamage == 10, "player-target damage is bounded to ten per hit");

        var response = ArenaProtocol.BuildGameEventResult(0x00B2, 90, 10, request);
        Check(response.Length == ArenaProtocol.GameEventResponseLength
            && response.Length == 36, "arena D010 response uses the complete fixed length");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(0, 2)) == 0x00B2
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8, 2)) == 90
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(26, 2)) == 10,
            "arena D010 carries actor HP and bounded target damage");
        Check(response.AsSpan(28).ToArray().All(value => value == 0), "arena D010 optional tail is initialized");

        var scoreData = ArenaProtocol.BuildGameData(0, 0);
        Check(ArenaProtocol.ResolveScoreDelta(scoreData, 0) is >= -500 and <= 500,
            "arena game data exposes a bounded score delta");
        Console.WriteLine("ARENA_REGRESSION_PASS room-protocol damage-score-settlement-carriers");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("ARENA_REGRESSION_FAIL " + name);
        Console.WriteLine("ARENA_REGRESSION_PASS " + name);
    }
}
