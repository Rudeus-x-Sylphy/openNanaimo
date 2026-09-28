using System.Buffers.Binary;
using OpenNanaimo.Adapter.Services;

static class Program
{
    private static readonly ushort[] QuickModes = [10, 100];

    public static void Main()
    {
        CheckDifficultyDomain();
        CheckProgressionProjection();
        CheckAllVillageEntryTuples();
        CheckIncidentTuple();
        CheckRejections();
        Console.WriteLine("DUNGEON_NAVIGATION_REGRESSION_PASS progression/cf77/cf6c/cf78 tuple closure");
    }

    private static void CheckDifficultyDomain()
    {
        for (byte logical = 0; logical < DungeonNavigationPolicy.DifficultyCount; logical++)
        {
            var wire = DungeonNavigationPolicy.EncodeDifficultySelector(logical, superBoss: false);
            Check(DungeonNavigationPolicy.DecodeLogicalDifficulty(0, 0, wire) == logical,
                $"normal difficulty round-trip failed: logical={logical} wire={wire}");
            Check(DungeonNavigationPolicy.EncodeDifficultySelector(logical, superBoss: true) == logical,
                $"super-boss difficulty must remain identity: logical={logical}");
            Check(DungeonNavigationPolicy.DecodeLogicalDifficulty(2, 1, logical) == logical,
                $"super-boss difficulty decode must remain identity: wire={logical}");
        }
    }

    private static void CheckProgressionProjection()
    {
        byte[] persisted = [11, 12, 13, 21, 22, 23];
        var projected = DungeonNavigationPolicy.BuildClientDifficultyTable(persisted, episodeCount: 2);
        Check(projected.SequenceEqual(new byte[] { 12, 13, 11, 22, 23, 21 }),
            "C355 progression table did not project logical low/middle/high into wire selectors 2/0/1");
    }

    private static void CheckAllVillageEntryTuples()
    {
        // Villages 1..4 ship episode 0..15, each with three ordinary stage-0
        // dungeons. Every low/middle/high door uses the same CF77 tuple domain.
        for (byte episode = 0; episode < 16; episode++)
        for (byte dungeon = 0; dungeon < 3; dungeon++)
        {

            for (byte logicalDifficulty = 0;
                 logicalDifficulty < DungeonNavigationPolicy.DifficultyCount;
                 logicalDifficulty++)
            {
                var wireDifficulty = DungeonNavigationPolicy.EncodeDifficultySelector(
                    logicalDifficulty,
                    superBoss: false);
                foreach (var mode in QuickModes)
                {
                    var request = BuildQuickEntry(mode, 0, episode, dungeon, wireDifficulty);
                    Check(DungeonNavigationPolicy.TryParseQuickEntry(request, out var selection),
                        $"CF77 tuple rejected: mode={mode} ep={episode} dg={dungeon} diff={wireDifficulty}");
                    Check(selection == new DungeonQuickEntrySelection(
                            mode, 0, episode, dungeon, wireDifficulty),
                        $"CF77 tuple remapped: mode={mode} ep={episode} dg={dungeon} diff={wireDifficulty}");

                    var create = new byte[DungeonProtocol.CreateRequestLength];
                    DungeonNavigationPolicy.WriteCreateRequestSelection(create, selection);
                    Check(create[26] == 0
                          && create[27] == episode
                          && create[28] == dungeon
                          && create[29] == DungeonQuickEntrySelection.InitialStage
                          && BinaryPrimitives.ReadUInt16LittleEndian(create.AsSpan(30, 2)) == wireDifficulty,
                        $"CF6C projection diverged: mode={mode} ep={episode} dg={dungeon} diff={wireDifficulty}");

                    var response = new byte[28];
                    DungeonNavigationPolicy.WriteQuickEnterResponseSelection(
                        response, episode, dungeon, 0x12345678);
                    Check(response[2] == episode
                          && response[3] == dungeon
                          && BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4, 4)) == 0x12345678,
                        $"CF78 projection diverged: ep={episode} dg={dungeon}");
                }
            }
        }
    }

    private static void CheckIncidentTuple()
    {
        // Reproduced report: Village 4 / stage 16, turn left, low difficulty,
        // dungeon 2. The retail request is hd0/ep15/dg1/diff2; episode 15 is
        // never a RealStage selector and must not overwrite CF6C stage.
        byte[] request = [100, 0, 0, 15, 1, 2, 0xFF, 0xFF];
        Check(DungeonNavigationPolicy.TryParseQuickEntry(request, out var selection),
            "incident CF77 tuple was rejected");
        Check(selection.HdIndex == 0 && selection.Episode == 15
              && selection.Dungeon == 1 && selection.WireDifficulty == 2,
            "incident CF77 tuple was not preserved as hd0/ep15/dg1/diff2");

        var create = new byte[DungeonProtocol.CreateRequestLength];
        DungeonNavigationPolicy.WriteCreateRequestSelection(create, selection);
        Check(create[27] == 15 && create[28] == 1 && create[29] == 0,
            "incident tuple regressed to episode-as-stage or dungeon-2 substitution");
    }

    private static void CheckRejections()
    {
        Check(!DungeonNavigationPolicy.TryParseQuickEntry(
                BuildQuickEntry(20, 0, 15, 1, 2), out _),
            "explicit room-id mode20 was accepted as a base selector tuple");
        Check(!DungeonNavigationPolicy.TryParseQuickEntry(
                BuildQuickEntry(100, 0, 15, 1, 3), out _),
            "out-of-range difficulty was accepted");
        Check(!DungeonNavigationPolicy.TryParseQuickEntry(new byte[7], out _),
            "short CF77 request was accepted");
    }

    private static byte[] BuildQuickEntry(
        ushort mode,
        byte hd,
        byte episode,
        byte dungeon,
        byte difficulty)
    {
        var request = new byte[DungeonProtocol.QuickEnterRequestLength];
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(0, 2), mode);
        request[2] = hd;
        request[3] = episode;
        request[4] = dungeon;
        request[5] = difficulty;
        request[6] = request[7] = 0xFF;
        return request;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
