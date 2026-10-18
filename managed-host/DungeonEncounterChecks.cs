using System.Reflection;
using System.Collections;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class DungeonEncounterChecks
{
    public static void Run()
    {
        var wins = 0;
        for (var roll = 0; roll < 10; roll++)
        {
            var calls = 0;
            var plan = new DungeonEncounterPolicy(2, () => { calls++; return roll; });
            var payload = new byte[800];
            payload[0x31D] = 1;
            plan.Write(payload);
            Check(payload[0x31C] == (roll == 0 ? 1 : 0), "team threshold");
            Check(payload[0x31E] == payload[0x31C], "rare threshold");
            Check(payload[0x31D] == 1 && payload[0x31F] == 1, "couple preserved, mystery guaranteed");
            wins += payload[0x31C];
            var second = new byte[800];
            second[0x31D] = 1;
            plan.Write(second);
            Check(payload.SequenceEqual(second) && calls == 2, "shared plan never rerolls on write");
        }
        Check(wins == 1, "exact ten percent domain");
        var solo = new byte[800];
        new DungeonEncounterPolicy(1, () => 0).Write(solo);
        Check(solo[0x31C] == 0 && solo[0x31E] == 1 && solo[0x31F] == 1, "solo eligibility");
        var draws = new Queue<int>([0, 9]);
        new DungeonEncounterPolicy(3, draws.Dequeue).Write(solo);
        Check(solo[0x31C] == 1 && solo[0x31E] == 0, "independent team and rare draws");
        try { new DungeonEncounterPolicy(1).Write(new byte[799]); throw new Exception("short CFEC accepted"); }
        catch (ArgumentException) { }

        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var owner = typeof(NetworkAdapterService);
        var roomType = owner.GetNestedType("DungeonRoom", BindingFlags.NonPublic)!;
        var room = Activator.CreateInstance(roomType, nonPublic: true)!;
        var sessionType = owner.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, nonPublic: true)!;
        sessionType.GetProperty("Character", flags)!.SetValue(session, new CharacterRecord { Id = 1 });
        sessionType.GetProperty("OnlineTracked", flags)!.SetValue(session, true);
        var memberType = owner.GetNestedType("DungeonMember", BindingFlags.NonPublic)!;
        var member = Activator.CreateInstance(memberType, [session, (byte)0])!;
        var members = (IDictionary)roomType.GetProperty("Members", flags)!.GetValue(room)!;
        members.Add(sessionType.GetProperty("SessionId", flags)!.GetValue(session)!, member);
        var battleProperty = roomType.GetProperty("Battle", flags)!;
        var battle = battleProperty.GetValue(room)!;
        var encounters = battle.GetType().GetProperty("SpecialEncounters", flags)!;
        var write = owner.GetMethod("WriteDungeonSpecialEncountersLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
        var reset = owner.GetMethod("ResetDungeonBattleLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
        encounters.SetValue(battle, new DungeonEncounterPolicy(2, () => 0));
        write.Invoke(null, [room, solo]);
        Check(solo[0x31C] == 1, "managed room uses frozen battle plan");
        reset.Invoke(null, [room, false]);
        Check(ReferenceEquals(battleProperty.GetValue(room), battle)
            && encounters.GetValue(battle) is not null, "CF7F preserves prepared encounter plan");
        reset.Invoke(null, [room, true]);
        var next = battleProperty.GetValue(room)!;
        Check(!ReferenceEquals(next, battle) && encounters.GetValue(next) is null,
            "same-map retry/continuation owns a fresh plan");
        write.Invoke(null, [room, solo]);
        Check(solo[0x31C] == 0 && solo[0x31F] == 1, "next battle recomputes eligibility");
        Console.WriteLine("DUNGEON_ENCOUNTER_CHECKS_PASS threshold=10% mystery=100% shared=true epoch-reset=true");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
