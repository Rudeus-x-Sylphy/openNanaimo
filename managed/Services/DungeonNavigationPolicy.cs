using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct DungeonQuickEntrySelection(
    ushort Mode,
    byte HdIndex,
    byte Episode,
    byte Dungeon,
    byte WireDifficulty)
{
    public const byte InitialStage = 0;
}

/// <summary>
/// Owns the retail selector-domain conversions shared by progression data,
/// CF77 quick entry, CF6C room creation, and CF78 ready-room projection.
/// Resource availability remains a separate DungeonCombatCatalog concern.
/// </summary>
internal static class DungeonNavigationPolicy
{
    internal const byte DifficultyCount = 3;
    private const byte FinalDungeonIndex = 2;

    internal static bool TryParseQuickEntry(
        ReadOnlySpan<byte> requestPayload,
        out DungeonQuickEntrySelection selection)
    {
        selection = default;
        if (requestPayload.Length != DungeonProtocol.QuickEnterRequestLength)
            return false;

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(requestPayload.Slice(0, 2));
        if (mode is not (10 or 100))
            return false;

        // Retail sub_6E9BA0/sub_6E9C20 serialize the base selector tuple as
        // hd, episode, dungeon, difficulty. CF77 has no RealStage field.
        var wireDifficulty = requestPayload[5];
        if (wireDifficulty >= DifficultyCount)
            return false;

        selection = new DungeonQuickEntrySelection(
            mode,
            requestPayload[2],
            requestPayload[3],
            requestPayload[4],
            wireDifficulty);
        return true;
    }

    internal static void WriteCreateRequestSelection(
        Span<byte> createRequestPayload,
        in DungeonQuickEntrySelection selection)
    {
        if (createRequestPayload.Length != DungeonProtocol.CreateRequestLength)
            throw new ArgumentException(
                $"CF6C create payload must be {DungeonProtocol.CreateRequestLength} bytes.",
                nameof(createRequestPayload));
        if (selection.WireDifficulty >= DifficultyCount)
            throw new ArgumentOutOfRangeException(nameof(selection));

        createRequestPayload[26] = selection.HdIndex;
        createRequestPayload[27] = selection.Episode;
        createRequestPayload[28] = selection.Dungeon;
        createRequestPayload[29] = DungeonQuickEntrySelection.InitialStage;
        BinaryPrimitives.WriteUInt16LittleEndian(
            createRequestPayload.Slice(30, 2),
            selection.WireDifficulty);
    }

    internal static void WriteQuickEnterResponseSelection(
        Span<byte> quickEnterResponsePayload,
        byte episode,
        byte dungeon,
        uint roomId)
    {
        if (quickEnterResponsePayload.Length < 8)
            throw new ArgumentException("CF78 response payload is too short.", nameof(quickEnterResponsePayload));

        // Both successful CF78 branches consume payload +2/+3 as
        // episode/dungeon and payload +4 as the DWORD room id.
        quickEnterResponsePayload[2] = episode;
        quickEnterResponsePayload[3] = dungeon;
        BinaryPrimitives.WriteUInt32LittleEndian(quickEnterResponsePayload.Slice(4, 4), roomId);
    }

    internal static byte DecodeLogicalDifficulty(
        byte dungeon,
        byte realStage,
        ushort wireDifficulty)
    {
        if (wireDifficulty >= DifficultyCount)
            return byte.MaxValue;
        if (IsSuperBoss(dungeon, realStage))
            return checked((byte)wireDifficulty);

        return wireDifficulty switch
        {
            2 => 0,
            0 => 1,
            1 => 2,
            _ => byte.MaxValue
        };
    }

    internal static byte EncodeDifficultySelector(byte logicalDifficulty, bool superBoss)
    {
        if (logicalDifficulty >= DifficultyCount)
            throw new ArgumentOutOfRangeException(nameof(logicalDifficulty));
        if (superBoss)
            return logicalDifficulty;

        return logicalDifficulty switch
        {
            0 => 2,
            1 => 0,
            2 => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(logicalDifficulty))
        };
    }

    internal static byte[] BuildClientDifficultyTable(
        ReadOnlySpan<byte> persistedTable,
        int episodeCount)
    {
        if (episodeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(episodeCount));

        var clientTable = new byte[checked(episodeCount * DifficultyCount)];
        for (var episode = 0; episode < episodeCount; episode++)
        {
            for (byte logicalDifficulty = 0; logicalDifficulty < DifficultyCount; logicalDifficulty++)
            {
                var persistedIndex = episode * DifficultyCount + logicalDifficulty;
                if (persistedIndex >= persistedTable.Length)
                    continue;
                var clientSelector = EncodeDifficultySelector(logicalDifficulty, superBoss: false);
                clientTable[episode * DifficultyCount + clientSelector] = persistedTable[persistedIndex];
            }
        }
        return clientTable;
    }

    private static bool IsSuperBoss(byte dungeon, byte realStage)
        => dungeon == FinalDungeonIndex && realStage == 1;
}
