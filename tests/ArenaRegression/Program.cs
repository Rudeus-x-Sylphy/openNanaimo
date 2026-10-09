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
        Check(request.TargetDamage == 0, "object collisions do not select an arbitrary opponent");

        var response = ArenaProtocol.BuildGameEventResult(0x00B2, 90, 10, request);
        Check(response.Length == ArenaProtocol.GameEventResponseLength
            && response.Length == 36, "arena D010 response uses the complete fixed length");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(0, 2)) == 0x00B2
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8, 2)) == 90
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(26, 2)) == 0,
            "arena D010 carries actor HP independently of PvP damage");
        Check(response.AsSpan(28).ToArray().All(value => value == 0), "arena D010 optional tail is initialized");

        Check(ArenaProtocol.CalculatePvpDamage(100, 20) == 90
            && ArenaProtocol.CalculatePvpDamage(100, 20) == ArenaProtocol.CalculatePvpDamage(100, 20)
            && ArenaProtocol.CalculatePvpDamage(int.MaxValue, 0) == ushort.MaxValue,
            "PvP damage is stable and bounded independently of object metadata");
        foreach (var kind in new ushort[] { 10, 20, 30, 60 })
        foreach (var hp in new ushort[] { 0, 900 })
        {
            var hit = ArenaProtocol.BuildPlayerHitResult(kind, 17, 23, 0, 1, hp, 100);
            Check(hit.Length == 16 && hit[6] == 0 && hit[7] == 1
                && BinaryPrimitives.ReadUInt16LittleEndian(hit.AsSpan(8)) == hp
                && hit[10] == (hp == 0 ? 200 : 100)
                && BinaryPrimitives.ReadUInt16LittleEndian(hit.AsSpan(12)) == 100
                && BinaryPrimitives.ReadUInt16LittleEndian(hit.AsSpan(14)) == 100,
                "player hit publishes the same applied damage in both native display branches");
        }
        var scoreData = ArenaProtocol.BuildGameData(0, 0);
        Check(scoreData.AsSpan(0x26E, 50).ToArray().All(value =>
                value >= ArenaProtocol.SkyArenaItemTypeMinimum
                && value <= ArenaProtocol.SkyArenaItemTypeMaximum),
            "sky arena game data uses the dedicated item-type table");
        Check(scoreData.AsSpan(0x2A0, 50).ToArray().All(value => value <= 4),
            "sky arena score table contains only active positive score classes");
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
