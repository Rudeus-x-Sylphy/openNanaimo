using System.Buffers.Binary;
using System.Reflection;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class Program
{
    private const BindingFlags NonPublicStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;

    public static void Main()
    {
        var character = NewCharacter(39);
        var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
        SessionType.GetProperty("Character")!.SetValue(session, character);

        var login = Invoke<byte[]>("BuildPostLoginPayload", session);
        Check(login[5] == 39, "271A frame+0x0D carries the persisted title grade");

        var village = NetworkAdapterService.BuildLoadNecessityPayload(character, new byte[60], new byte[60], new byte[4], null);
        Check(village[0x24 - 8] == 39, "C355 frame+0x24 carries the persisted title grade");

        var room = DungeonProtocol.BuildRoomMember(character, 1, 0, 0, 0,
            CharacterProgression.ExperienceRequiredForLevel(character.Level),
            CharacterProgression.ExperienceRequiredForLevel(character.Level + 1), 0, 0, 0, 0);
        Check(room[0x49 - 8] == 39, "CF71 frame+0x49 carries the persisted title grade");

        var native = NativeDungeonState.Create(character, [], []);
        Check(native.Get(NativeDungeonState.DungeonGradeOffset) == 39, "native profile state carries the persisted title grade");

        var lobby = Invoke<byte[]>("BuildArenaLobbyInfoPayload", character, (byte)1);
        Check(lobby[3] == 39, "CF0E frame+0x0B carries the persisted title grade");

        var settlement = Invoke<byte[]>("BuildDungeonEndGamePayload", character, character,
            100u, CharacterProgression.ExperienceRequiredForLevel(character.Level),
            CharacterProgression.ExperienceRequiredForLevel(character.Level + 1),
            12, (byte)3, 0, 0, 0, 0);
        Check(settlement[0x0B] == 39, "CF88 record+0x07 carries the persisted title grade");
        Check(settlement[0x0E] == character.Level, "CF88 record+0x0A remains the independent character level");

        var arenaResult = Invoke<ArenaPvpResultRecord>("BuildArenaPvpResultRecord", character, (ushort)1);
        var cf8a = ArenaProtocol.BuildPvpResults([arenaResult]);
        Check(cf8a[4 + 6] == 39, "CF8A record+0x06 carries the persisted title grade");

        var frame = NativeDungeonClient.Frame(0xCF88, new byte[104 + 4]);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(64), 1);
        frame[19] = 7; frame[73] = 10; frame[74] = 39; frame[75] = 5;
        Check(NetworkAdapterService.PatchNativeDungeonTitleFrame(frame, 1, 39)
            && frame[71] == 39 && frame[19] == 7 && frame[73] == 10 && frame[74] == 39 && frame[75] == 5,
            "native settlement updates only the owning title and preserves pet, level, rating and other members");
        var shortFrame = frame[..^1];
        Check(!NetworkAdapterService.PatchNativeDungeonTitleFrame(shortFrame, 1, 39), "truncated settlement is rejected");
        var fixedCharacter = NewCharacter(42);
        Check(CharacterTitleState.GetGrade(fixedCharacter) == 42, "maximum title grade remains 42");
        Console.WriteLine("CHARACTER_TITLE_CONSISTENCY_PASS grade=39 carriers=271A,C355,CF71,CF0E,CF88,CF8A,native grade=42=PASS");
    }

    private static T Invoke<T>(string method, params object?[] args)
    {
        var info = typeof(NetworkAdapterService).GetMethod(method, NonPublicStatic)
            ?? throw new MissingMethodException(typeof(NetworkAdapterService).FullName, method);
        return (T)info.Invoke(null, args)!;
    }

    private static CharacterRecord NewCharacter(byte grade) => new()
    {
        Id = 1,
        AccountId = 1,
        Name = "TitleTest",
        Level = 39,
        Experience = CharacterProgression.ExperienceRequiredForLevel(39),
        DungeonGrade = grade,
        Appearance = new byte[36],
        MaxHp = 160,
        CurrentHp = 160,
        MaxMp = 100,
        CurrentMp = 100,
        TutorialCompleted = true
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
