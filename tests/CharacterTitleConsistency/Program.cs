using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class Program
{
    private const BindingFlags NonPublicStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;

    public static async Task Main()
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
        await CheckDungeonTitleProgressionAsync();
        Console.WriteLine("CHARACTER_TITLE_CONSISTENCY_PASS grade=39 carriers=271A,C355,CF71,CF0E,CF88,CF8A,native map=23 migration=history-bound");
    }

    private static async Task CheckDungeonTitleProgressionAsync()
    {
        for (byte episode = 0; episode <= 15; episode++)
        {
            Check(DungeonTitleProgression.TryGetGrade(0, episode, 2, 1, out var grade)
                && grade == episode + 1,
                $"village1-4 episode {episode} Super-BOSS maps to grade {episode + 1}");
            Check(!DungeonTitleProgression.TryGetGrade(0, episode, 2, 0, out _),
                $"village1-4 episode {episode} ordinary stage0 never awards a title");
        }
        for (byte dungeon = 0; dungeon <= 6; dungeon++)
        {
            Check(DungeonTitleProgression.TryGetGrade(0, 100, dungeon, 1, out var grade)
                && grade == 17 + dungeon,
                $"Lumineos ep100 dungeon {dungeon} Super-BOSS maps to grade {17 + dungeon}");
            Check(!DungeonTitleProgression.TryGetGrade(0, 100, dungeon, 0, out _),
                $"Lumineos ep100 dungeon {dungeon} ordinary stage0 never awards a title");
        }

        var tempBase = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(Path.Combine(tempBase,
            "nanaimo-title-progress-" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("unsafe title test path");
        Directory.CreateDirectory(root);
        using (File.Create(Path.Combine(root, "game.db"))) { }
        try
        {
            var db = new DatabaseService(root);
            await db.InitializeAsync();

            var migrated = await CreateCharacterAsync(db, "TitleHistory");
            await WriteTitleStateAsync(db, migrated, grade: 23, frontierValid: true,
                episode: 15, dungeon: 2, difficulty: 2, stage: 0);
            await InsertProgressEvidenceAsync(db, migrated, episode: 3, useAggregate: true);
            await InsertProgressEvidenceAsync(db, migrated, episode: 15, useAggregate: false);
            var migratedCharacter = (await db.GetCharacterByIdAsync(migrated))!;
            Check(migratedCharacter.DungeonGrade == 16,
                "legacy ep15 stage0 R7 state recomputes from persisted P4+episode15 Super-BOSS history to grade16");
            Check(await ReadStoredGradeAsync(db, migrated) == 16,
                "history-bound grade16 migration is written back to NativeDungeonProfiles");

            var bounded = await CreateCharacterAsync(db, "TitleBounded");
            await WriteTitleStateAsync(db, bounded, grade: 23, frontierValid: true,
                episode: 15, dungeon: 2, difficulty: 2, stage: 0);
            var boundedCharacter = (await db.GetCharacterByIdAsync(bounded))!;
            Check(boundedCharacter.DungeonGrade == 0,
                "legacy ordinary-stage R7 state without history falls back to bounded grade0");

            var preserved = await CreateCharacterAsync(db, "TitleHigher");
            await WriteTitleStateAsync(db, preserved, grade: 20, frontierValid: false,
                episode: 0, dungeon: 0, difficulty: 0, stage: 0);
            await InsertProgressEvidenceAsync(db, preserved, episode: 3, useAggregate: true);
            var preservedCharacter = (await db.GetCharacterByIdAsync(preserved))!;
            Check(preservedCharacter.DungeonGrade == 20,
                "non-legacy higher title grade is never downgraded by lower history");

            var lumineos = await CreateCharacterAsync(db, "TitleLumineos");
            await WriteTitleStateAsync(db, lumineos, grade: 0, frontierValid: false,
                episode: 0, dungeon: 0, difficulty: 0, stage: 0);
            await InsertLumineosMilestoneAsync(db, lumineos, grade: 23, dungeon: 6);
            var lumineosCharacter = (await db.GetCharacterByIdAsync(lumineos))!;
            Check(lumineosCharacter.DungeonGrade == 23,
                "persisted ep100/dungeon6 Super-BOSS milestone restores grade23");


            var settled16 = await ApplySettlementAsync(db, "TitleSettle16",
                new NativeDungeonSettlementRecord(
                    0, 15, 2, 1, 2, DungeonRewardPolicy.ClearRatingS, 1600, 1600));
            Check(settled16 == 16,
                "managed ep15/dungeon2/stage1 settlement transaction awards and persists grade16");

            var settled23 = await ApplySettlementAsync(db, "TitleSettle23",
                new NativeDungeonSettlementRecord(
                    0, 100, 6, 1, 0, DungeonRewardPolicy.ClearRatingS, 2300));
            Check(settled23 == 23,
                "managed ep100/dungeon6/stage1 settlement transaction awards and persists grade23");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<long> CreateCharacterAsync(DatabaseService db, string name)
    {
        var account = await db.OpenLocalAccountAsync(name);
        await db.CreateLocalCharacterAsync(account, name, 1);
        return (await db.GetCharacterAsync(account))!.Id;
    }

    private static async Task WriteTitleStateAsync(
        DatabaseService db, long characterId, byte grade, bool frontierValid,
        byte episode, byte dungeon, byte difficulty, byte stage)
    {
        var character = (await db.GetCharacterByIdAsync(characterId))!;
        var state = NativeDungeonState.Create(character, [], []);
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(NativeDungeonState.DungeonGradeOffset), grade);
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(NativeDungeonState.DungeonGradeOffset + 4), frontierValid ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(NativeDungeonState.DungeonGradeOffset + 8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(NativeDungeonState.DungeonGradeOffset + 12), episode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(NativeDungeonState.DungeonGradeOffset + 16), dungeon);
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(NativeDungeonState.DungeonGradeOffset + 20), difficulty);
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(NativeDungeonState.DungeonGradeOffset + 24), stage);
        await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
        await connection.OpenAsync();
        await using (var ensure = connection.CreateCommand())
        {
            ensure.CommandText = "CREATE TABLE IF NOT EXISTS NativeDungeonProfiles(CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id), State BLOB NOT NULL)";
            await ensure.ExecuteNonQueryAsync();
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO NativeDungeonProfiles(CharacterId,State) VALUES($id,$state) ON CONFLICT(CharacterId) DO UPDATE SET State=$state";
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$state", state.Bytes);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertProgressEvidenceAsync(
        DatabaseService db, long characterId, byte episode, bool useAggregate)
    {
        await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = useAggregate
            ? "INSERT INTO DungeonProgress(CharacterId,Episode,Difficulty,ClearMask,BestRatings,BestScore,ClearedAt,UpdatedAt) VALUES($id,$episode,0,8,0,1,$now,$now)"
            : "INSERT INTO DungeonStagePerformance(CharacterId,Episode,Difficulty,ArchiveSlot,BestScore,BestElapsedMinutes,ClearedAt,UpdatedAt) VALUES($id,$episode,0,3,1,NULL,$now,$now)";
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$episode", episode);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertLumineosMilestoneAsync(
        DatabaseService db, long characterId, byte grade, byte dungeon)
    {
        await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO DungeonTitleMilestones(CharacterId,Grade,HdIndex,Episode,Dungeon,Difficulty,Stage,ClearedAt,UpdatedAt) VALUES($id,$grade,0,100,$dungeon,0,1,$now,$now)";
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$grade", grade);
        command.Parameters.AddWithValue("$dungeon", dungeon);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<byte> ApplySettlementAsync(
        DatabaseService db, string name, NativeDungeonSettlementRecord settlement)
    {
        var account = await db.OpenLocalAccountAsync(name);
        await db.CreateLocalCharacterAsync(account, name, 1);
        var character = (await db.GetCharacterAsync(account))!;
        var session = Guid.NewGuid().ToString("N");
        Check(await db.BeginWorldSessionAsync(account, character.Id, session, 1, "127.0.0.1"),
            $"{name} settlement fixture owns its world session");
        var before = NativeDungeonState.Create(character, [], []);
        var after = new NativeDungeonState(before.Bytes.ToArray());
        var applied = await db.ApplyNativeDungeonDeltaAsync(
            account, character.Id, session, before, after, default,
            settlement: settlement);
        Check(applied.Applied, $"{name} native settlement applies atomically");
        return (await db.GetCharacterByIdAsync(character.Id))!.DungeonGrade;
    }

    private static async Task<byte> ReadStoredGradeAsync(DatabaseService db, long characterId)
    {
        await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM NativeDungeonProfiles WHERE CharacterId=$id";
        command.Parameters.AddWithValue("$id", characterId);
        var state = (byte[])(await command.ExecuteScalarAsync())!;
        return checked((byte)BinaryPrimitives.ReadUInt32LittleEndian(
            state.AsSpan(NativeDungeonState.DungeonGradeOffset, 4)));
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
