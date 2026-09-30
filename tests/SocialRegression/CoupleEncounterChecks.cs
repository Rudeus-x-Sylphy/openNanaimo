using System.Buffers.Binary;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckCoupleEncountersAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Database.GrantInventoryItemToAccountAsync(Character(fixture.First).AccountId, 43000002, 1);
        var proposal = CoupleRequest(fixture, fixture.First, fixture.Second, 43000002);
        await DispatchCouple(fixture, fixture.First, 0xC583, proposal);
        await DispatchCouple(fixture, fixture.Second, 0xC584, CoupleAnswer(fixture.First, proposal, 10));
        var creation = new byte[44];
        BinaryPrimitives.WriteUInt16LittleEndian(creation.AsSpan(24), 100);
        Check(await Dispatch(fixture, fixture.First, 0xCF6C, creation) is not null, "encounter preparation creates a room");
        var room = Invoke<object>(fixture.Service, "GetDungeonRoom", fixture.First);
        var enter = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(enter.AsSpan(2), checked((ushort)(int)Get(room, "Id")!));
        foreach (var member in new[] { fixture.Second, fixture.Third })
            Check(await Dispatch(fixture, member, 0xCF75, enter) is not null, "encounter member joins before preparation");
        foreach (var member in new[] { fixture.First, fixture.Second, fixture.Third })
        {
            var data = await Dispatch(fixture, member, 0xCFEB, new byte[4]);
            Check(data is { Length: 808 } && data[0x325] == 1,
                "couple encounters are shared by all three members before combat starts");
        }
        var battle = Get(room, "Battle")!;
        Check(((HashSet<long>)Get(battle, "ParticipantCharacterIds")!).Count == 0,
            "map preparation leaves the combat participant snapshot to the start phase");
        await Dispatch(fixture, fixture.Second, 0xCF1D, []);
        var cleared = await Dispatch(fixture, fixture.Third, 0xCFEB, new byte[4]);
        Check(cleared is { Length: 808 } && cleared[0x325] == 0,
            "a partner leaving before the start clears shared encounter eligibility");
        await Dispatch(fixture, fixture.Third, 0xCF1D, []);
        await Dispatch(fixture, fixture.First, 0xCF1D, []);

        await using var pool = new NativeDungeonPool("unused", fixture.Root);
        await using var first = new NativeDungeonClient(_ => Task.CompletedTask);
        await using var second = new NativeDungeonClient(_ => Task.CompletedTask);
        await using var third = new NativeDungeonClient(_ => Task.CompletedTask);
        foreach (var (session, native) in new[] { (fixture.First, first), (fixture.Second, second), (fixture.Third, third) })
        {
            Set(session, "NativeLease", new NativeDungeonPool.Lease(pool, "encounter", 61150));
            Set(session, "NativeDungeon", native);
            Set(session, "NativeDungeonSelectionValid", true);
            Set(session, "NativeBattleEpoch", 1L);
        }
        foreach (var member in new[] { fixture.First, fixture.Second, fixture.Third })
        {
            var data = NativeDungeonClient.Frame(0xCFEC, new byte[800]);
            await Invoke<Task>(fixture.Service, "PatchNativeCoupleFrameAsync", member, data, Token);
            Check(data[0x325] == 1, "native shared encounter includes an unpaired third member");
        }
        foreach (var property in new[] { "NativeDungeonHdIndex", "NativeDungeonEpisode", "NativeDungeonDungeon", "NativeDungeonStage", "NativeDungeonLogicalDifficulty" })
        {
            Set(fixture.Third, property, (byte)1);
            var data = NativeDungeonClient.Frame(0xCFEC, new byte[800]);
            await Invoke<Task>(fixture.Service, "PatchNativeCoupleFrameAsync", fixture.Third, data, Token);
            Check(data[0x325] == 0, "encounters require the same complete stage: " + property);
            Set(fixture.Third, property, (byte)0);
        }
        Set(fixture.Second, "NativeLease", new NativeDungeonPool.Lease(pool, "different", 61160));
        var absent = NativeDungeonClient.Frame(0xCFEC, new byte[800]);
        await Invoke<Task>(fixture.Service, "PatchNativeCoupleFrameAsync", fixture.Third, absent, Token);
        Check(absent[0x325] == 0, "encounters require both partners in the viewer's native room");
        foreach (var session in new[] { fixture.First, fixture.Second, fixture.Third })
        {
            Set(session, "NativeDungeon", null);
            Set(session, "NativeLease", null);
        }
    }
}
