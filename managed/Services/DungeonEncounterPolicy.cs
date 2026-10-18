namespace OpenNanaimo.Adapter.Services;

internal sealed class DungeonEncounterPolicy
{
    private readonly bool _team;
    private readonly bool _rare;

    internal DungeonEncounterPolicy(int members, Func<int>? roll = null)
    {
        roll ??= () => Random.Shared.Next(10);
        _team = members >= 2 && roll() == 0;
        _rare = roll() == 0;
    }

    internal void Write(Span<byte> payload)
    {
        if (payload.Length != 800)
            throw new ArgumentException("Dungeon CFEC requires an 800-byte payload.", nameof(payload));
        payload[0x324 - 8] = _team ? (byte)1 : (byte)0;
        payload[0x326 - 8] = _rare ? (byte)1 : (byte)0;
        payload[0x327 - 8] = 1;
    }
}
