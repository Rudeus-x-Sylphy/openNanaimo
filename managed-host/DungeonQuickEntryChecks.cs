using OpenNanaimo.Adapter.Services;

public static class DungeonQuickEntryChecks
{
    public static void Run()
    {
        byte[] village4Stage16Low = [100, 0, 0, 15, 1, 2, 0xFF, 0xFF];
        Check(NetworkAdapterService.TryParseDungeonQuickSelection(
                village4Stage16Low,
                out var createMode,
                out var hdIndex,
                out var episode,
                out var dungeon,
                out var difficulty)
            && createMode == 100
            && hdIndex == 0
            && episode == 15
            && dungeon == 1
            && difficulty == 2,
            "Village 4 / stage 16 low-difficulty CF77 tuple was not decoded as hd0/ep15/dg1/diff2");

        byte[] joinOrAuto = [10, 0, 0, 15, 2, 2, 0xFF, 0xFF];
        Check(NetworkAdapterService.TryParseDungeonQuickSelection(
                joinOrAuto,
                out var joinMode,
                out hdIndex,
                out episode,
                out dungeon,
                out difficulty)
            && joinMode == 10
            && hdIndex == 0
            && episode == 15
            && dungeon == 2
            && difficulty == 2,
            "CF77 mode10 must use the same hd/episode/dungeon/difficulty base tuple as mode100");

        byte[] explicitRoom = [20, 0, 0, 15, 1, 2, 0x34, 0x12];
        Check(!NetworkAdapterService.TryParseDungeonQuickSelection(
                explicitRoom, out _, out _, out _, out _, out _),
            "CF77 mode20 room-id entry was incorrectly parsed as a base selection tuple");

        byte[] invalidDifficulty = [100, 0, 0, 15, 1, 3, 0xFF, 0xFF];
        Check(!NetworkAdapterService.TryParseDungeonQuickSelection(
                invalidDifficulty, out _, out _, out _, out _, out _),
            "Out-of-range CF77 difficulty was accepted");

        Console.WriteLine("DUNGEON_QUICK_ENTRY_CHECKS_PASS cf77-base-tuple hd/episode/dungeon/difficulty");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
