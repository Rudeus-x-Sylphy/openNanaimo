namespace OpenNanaimo.Adapter.Services;

internal static class DungeonTitleProgression
{
    internal const byte MaximumAutomaticGrade = 24;
    internal const byte LumineosWireEpisode = 100;

    internal static bool TryGetGrade(
        byte hdIndex,
        byte episode,
        byte dungeon,
        byte stage,
        out byte grade)
    {
        grade = 0;
        if (hdIndex != 0 || stage != 1)
            return false;

        // Villages 1..4: the fourth map of each four-map village is the
        // episode's dungeon2 -> stage1 Super-BOSS. Difficulty is deliberately
        // absent: low/middle/high all award the same title milestone.
        if (episode <= 15 && dungeon == 2)
        {
            grade = checked((byte)(episode + 1));
            return true;
        }

        // Village 5 / Lumineos keeps the wire identity ep100/dungeon0..7.
        // Resource lookup aliases ep16..23/dungeon0, but progression must use
        // the unchanged selection tuple rather than the resource alias.
        if (episode == LumineosWireEpisode && dungeon <= 7)
        {
            grade = checked((byte)(17 + dungeon));
            return true;
        }

        return false;
    }

    internal static bool IsLumineosTuple(byte hdIndex, byte episode, byte dungeon, byte stage)
        => hdIndex == 0
            && episode == LumineosWireEpisode
            && dungeon <= 7
            && stage <= 1;

    internal static bool IsLegacyEpisode15R7State(ReadOnlySpan<byte> state)
    {
        if (state.Length < NativeDungeonState.DungeonGradeOffset
                + NativeDungeonState.DungeonGradeStateLength
            || ReadUInt32(state, NativeDungeonState.DungeonGradeOffset) != 23
            || ReadUInt32(state, NativeDungeonState.DungeonGradeOffset + 4) != 1)
            return false;

        return ReadUInt32(state, NativeDungeonState.DungeonGradeOffset + 8) == 0
            && ReadUInt32(state, NativeDungeonState.DungeonGradeOffset + 12) == 15
            && ReadUInt32(state, NativeDungeonState.DungeonGradeOffset + 16) == 2
            && ReadUInt32(state, NativeDungeonState.DungeonGradeOffset + 24) <= 1;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> state, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(state.Slice(offset, 4));
}
