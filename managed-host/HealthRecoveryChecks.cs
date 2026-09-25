using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class HealthRecoveryChecks
{
    internal static async Task RunAsync()
    {
        CheckPolicy();
        await CheckPersistenceAndSceneHooksAsync();
        Console.WriteLine("HEALTH_RECOVERY_CHECKS_PASS town apartment battle-gate cap persistence c365 c367 c38d c38f c368-carrier");
    }

    private static void CheckPolicy()
    {
        var character = new CharacterRecord { MaxHp = 5400, MaxMp = 520, CurrentHp = 900, CurrentMp = 375 };
        Check(HealthRecoveryPolicy.Town == new HealthRecoveryParameters(100, 10)
              && HealthRecoveryPolicy.Apartment == new HealthRecoveryParameters(100, 10),
            "town and apartment retain independent 100/10 defaults");

        var town = HealthRecoveryPolicy.Resolve(character, HealthRecoveryScene.Town, true, false);
        Check(town.Eligible && town.Changed && town.CurrentHp == 1000 && town.CurrentMp == 385
              && town.HpRestored == 100 && town.MpRestored == 10,
            "town restores HP and MP by one scene quantum");

        var apartment = HealthRecoveryPolicy.Resolve(character, HealthRecoveryScene.Apartment, true, false);
        Check(apartment.Eligible && apartment.CurrentHp == 1000 && apartment.CurrentMp == 385,
            "apartment restores HP and MP by its own scene quantum");

        var battle = HealthRecoveryPolicy.Resolve(character, HealthRecoveryScene.Town, true, true);
        Check(!battle.Eligible && !battle.Changed && battle.CurrentHp == 900 && battle.CurrentMp == 375,
            "active dungeon epoch blocks noncombat recovery");

        character.CurrentHp = 5360;
        character.CurrentMp = 515;
        var capped = HealthRecoveryPolicy.Resolve(character, HealthRecoveryScene.Apartment, true, false);
        Check(capped.CurrentHp == 5400 && capped.CurrentMp == 520
              && capped.HpRestored == 40 && capped.MpRestored == 5,
            "recovery caps independently at MaxHP and MaxMP");
    }

    private static async Task CheckPersistenceAndSceneHooksAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "open-nanaimo-health-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            long accountId = await database.OpenLocalAccountAsync("health-recovery-check");
            long characterId = await database.CreateLocalCharacterAsync(accountId, "Recovery", 1);

            await using var service = new NetworkAdapterService(database, _ => { }, root);
            var serviceType = typeof(NetworkAdapterService);
            var sessionType = serviceType.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var dispatch = serviceType.GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            string sessionId = (string)Get(session, "SessionId")!;
            Check(await database.BeginWorldSessionAsync(accountId, characterId, sessionId, 1, "127.0.0.1"),
                "recovery fixture world session opened");
            await WriteResourcesAsync(database.DatabasePath, characterId, 5400, 520, 900, 375);
            Set(session, "AccountId", accountId);
            Set(session, "Username", "health-recovery-check");
            Set(session, "Character", (await database.GetCharacterAsync(accountId))!);
            Set(session, "ChannelId", 1);
            Set(session, "ListenerPort", 12050);
            Set(session, "OnlineTracked", true);
            Set(session, "TownId", (byte)0);
            Set(session, "TownPage", (byte)0);

            async Task<byte[]?> Dispatch(ushort opcode, byte[] payload)
            {
                byte[] frame = NativeDungeonClient.Frame(opcode, payload);
                return await (Task<byte[]?>)dispatch.Invoke(service,
                    [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
            }

            var town = await database.ApplyHealthRecoveryStepAsync(
                accountId, characterId, sessionId, HealthRecoveryScene.Town);
            Check(town.Applied && town.CurrentHp == 1000 && town.CurrentMp == 385,
                "town quantum persists atomically");
            var apartment = await database.ApplyHealthRecoveryStepAsync(
                accountId, characterId, sessionId, HealthRecoveryScene.Apartment);
            Check(apartment.Applied && apartment.CurrentHp == 1100 && apartment.CurrentMp == 395,
                "apartment quantum persists independently");

            await WriteResourcesAsync(database.DatabasePath, characterId, 5400, 520, 5390, 519);
            var capped = await database.ApplyHealthRecoveryStepAsync(
                accountId, characterId, sessionId, HealthRecoveryScene.Apartment);
            Check(capped.Applied && capped.CurrentHp == 5400 && capped.CurrentMp == 520,
                "database recovery caps at maxima");
            var full = await database.ApplyHealthRecoveryStepAsync(
                accountId, characterId, sessionId, HealthRecoveryScene.Town);
            Check(!full.Applied, "full resources do not create a recovery write");

            await WriteResourcesAsync(database.DatabasePath, characterId, 5400, 520, 900, 375);
            Set(session, "Character", (await database.GetCharacterAsync(accountId))!);

            byte[] c365 = new byte[10];
            c365[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(c365.AsSpan(2, 2), 0);
            c365[4] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(c365.AsSpan(6, 2), 400);
            BinaryPrimitives.WriteUInt16LittleEndian(c365.AsSpan(8, 2), 96);
            Check((await Dispatch(0xC365, c365)) is { } c366 && ReadOpcode(c366) == 0xC366,
                "C365 town entry returns C366");
            CheckResources(await database.GetCharacterAsync(accountId), 1000, 385, "C365 persists one town step");

            byte[] c367 = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(c367, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(c367.AsSpan(4, 2), 400);
            BinaryPrimitives.WriteUInt16LittleEndian(c367.AsSpan(6, 2), 96);
            byte[] c368 = (await Dispatch(0xC367, c367))!;
            Check(ReadOpcode(c368) == 0xC368
                  && BinaryPrimitives.ReadUInt16LittleEndian(c368.AsSpan(0x38, 2)) == 1100
                  && BinaryPrimitives.ReadUInt16LittleEndian(c368.AsSpan(0x3A, 2)) == 395,
                "C367 persists town refresh and publishes it in C368");
            CheckResources(await database.GetCharacterAsync(accountId), 1100, 395, "C367 persists second town step");

            byte[] c38d = new byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(c38d, 1);
            Check((await Dispatch(0xC38D, c38d)) is { } c38e && ReadOpcode(c38e) == 0xC38E,
                "C38D apartment entry returns C38E");
            CheckResources(await database.GetCharacterAsync(accountId), 1200, 405, "C38D persists one apartment step");

            byte[] c38f = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(c38f, 320);
            BinaryPrimitives.WriteUInt16LittleEndian(c38f.AsSpan(2, 2), 240);
            Check((await Dispatch(0xC38F, c38f)) is { } c390 && ReadOpcode(c390) == 0xC390,
                "C38F apartment refresh returns C390");
            CheckResources(await database.GetCharacterAsync(accountId), 1300, 415, "C38F persists second apartment step");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                for (int retry = 0; retry < 5; retry++)
                {
                    try { Directory.Delete(root, true); break; }
                    catch (IOException) when (retry < 4) { await Task.Delay(50); }
                }
            }
        }
    }

    private static async Task WriteResourcesAsync(
        string databasePath, long characterId, int maxHp, int maxMp, int currentHp, int currentMp)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Characters SET MaxHp=$maxHp,MaxMp=$maxMp,CurrentHp=$hp,CurrentMp=$mp WHERE Id=$id";
        command.Parameters.AddWithValue("$maxHp", maxHp);
        command.Parameters.AddWithValue("$maxMp", maxMp);
        command.Parameters.AddWithValue("$hp", currentHp);
        command.Parameters.AddWithValue("$mp", currentMp);
        command.Parameters.AddWithValue("$id", characterId);
        Check(await command.ExecuteNonQueryAsync() == 1, "recovery fixture resources written");
    }

    private static ushort ReadOpcode(byte[] frame)
        => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2));

    private static void CheckResources(CharacterRecord? character, int hp, int mp, string name)
        => Check(character is not null && character.CurrentHp == hp && character.CurrentMp == mp, name);

    private static object? Get(object instance, string name)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance);

    private static void Set(object instance, string name, object? value)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("HEALTH_RECOVERY_CHECK_FAILED " + name);
        Console.WriteLine("HEALTH_RECOVERY_CHECK_PASS " + name);
    }
}
