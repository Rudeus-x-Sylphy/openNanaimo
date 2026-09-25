using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class HealthRecoveryChecks
{
    internal static async Task RunAsync()
    {
        CheckPolicyAndSchedule();
        await CheckPersistenceSceneDeathAndReconnectAsync();
        Console.WriteLine("HEALTH_RECOVERY_CHECKS_PASS five-second-tick town apartment scene-sync persistence cap reconnect death-return battle-suspend");
    }

    private static void CheckPolicyAndSchedule()
    {
        var character = new CharacterRecord { MaxHp = 5400, MaxMp = 520, CurrentHp = 900, CurrentMp = 375 };
        Check(HealthRecoveryPolicy.TickInterval == TimeSpan.FromSeconds(5),
            "recovery interval is five seconds");
        Check(HealthRecoveryPolicy.Town == new HealthRecoveryParameters(100, 10)
              && HealthRecoveryPolicy.Apartment == new HealthRecoveryParameters(100, 10),
            "town and apartment use 100 HP / 10 MP ticks");

        var town = HealthRecoveryPolicy.Resolve(character, HealthRecoveryScene.Town, true, false);
        Check(town.Eligible && town.Changed && town.CurrentHp == 1000 && town.CurrentMp == 385,
            "one eligible town tick resolves to the observed quantum");
        var battle = HealthRecoveryPolicy.Resolve(character, HealthRecoveryScene.Town, true, true);
        Check(!battle.Eligible && !battle.Changed,
            "battle epoch blocks non-combat recovery");

        character.CurrentHp = 5360;
        character.CurrentMp = 515;
        var capped = HealthRecoveryPolicy.Resolve(character, HealthRecoveryScene.Apartment, true, false);
        Check(capped.CurrentHp == 5400 && capped.CurrentMp == 520
              && capped.HpRestored == 40 && capped.MpRestored == 5,
            "tick caps HP and MP independently");

        character.CurrentHp = 0;
        character.CurrentMp = 375;
        var deathReturn = HealthRecoveryPolicy.ResolveDungeonDeathReturn(character, 375);
        Check(deathReturn.CurrentHp == 900 && deathReturn.CurrentMp == 375,
            "death return starts at one-sixth max HP and retains battle MP");

        var schedule = new HealthRecoverySchedule();
        var start = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        Check(schedule.Activate(HealthRecoveryScene.Town, start),
            "town activation starts a schedule");
        Check(!schedule.TryTakeDueTick(start.AddMilliseconds(4999), out _),
            "scene entry does not apply an immediate recovery step");
        Check(schedule.TryTakeDueTick(start.AddSeconds(5), out var firstScene)
              && firstScene == HealthRecoveryScene.Town,
            "first town tick becomes due at five seconds");
        Check(!schedule.Activate(HealthRecoveryScene.Apartment, start.AddSeconds(6)),
            "apartment transition preserves the running schedule");
        Check(!schedule.TryTakeDueTick(start.AddSeconds(9.9), out _)
              && schedule.TryTakeDueTick(start.AddSeconds(10), out var apartmentScene)
              && apartmentScene == HealthRecoveryScene.Apartment,
            "apartment continues the same five-second cadence");
        schedule.Suspend();
        Check(!schedule.TryTakeDueTick(start.AddMinutes(1), out _),
            "combat suspension prevents catch-up ticks");
    }

    private static async Task CheckPersistenceSceneDeathAndReconnectAsync()
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
            var healingPayloadBuilder = serviceType.GetMethod(
                "BuildUserHpMpAutoHealingPayload",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var applyDeathReturn = serviceType.GetMethod(
                "ApplyDungeonDeathReturnResourcesAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            object session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            string sessionId = (string)Get(session, "SessionId")!;
            Check(await database.BeginWorldSessionAsync(accountId, characterId, sessionId, 1, "127.0.0.1"),
                "fixture world session opened");
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

            byte[] c365 = new byte[10];
            c365[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(c365.AsSpan(2, 2), 0);
            c365[4] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(c365.AsSpan(6, 2), 400);
            BinaryPrimitives.WriteUInt16LittleEndian(c365.AsSpan(8, 2), 96);
            Check((await Dispatch(0xC365, c365)) is { } c366 && ReadOpcode(c366) == 0xC366,
                "C365 town transport remains request-driven");
            CheckResources(await database.GetCharacterAsync(accountId), 900, 375,
                "C365 does not apply a recovery quantum");

            byte[] c367 = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(c367, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(c367.AsSpan(4, 2), 400);
            BinaryPrimitives.WriteUInt16LittleEndian(c367.AsSpan(6, 2), 96);
            byte[] c368 = (await Dispatch(0xC367, c367))!;
            Check(ReadOpcode(c368) == 0xC368
                  && BinaryPrimitives.ReadUInt16LittleEndian(c368.AsSpan(0x38, 2)) == 900
                  && BinaryPrimitives.ReadUInt16LittleEndian(c368.AsSpan(0x3A, 2)) == 375,
                "C368 publishes the unchanged scene-entry snapshot");
            CheckResources(await database.GetCharacterAsync(accountId), 900, 375,
                "C367 does not apply a recovery quantum");

            Check(await Dispatch(0xC36C, []) is null,
                "C36C remains a one-way town completion event");
            var schedule = (HealthRecoverySchedule)Get(session, "HealthRecovery")!;
            Check(schedule.TryGetActiveScene(out var activeTown) && activeTown == HealthRecoveryScene.Town,
                "C36C activates timed town recovery");

            var firstTick = await database.ApplyHealthRecoveryStepAsync(
                accountId, characterId, sessionId, HealthRecoveryScene.Town);
            Check(firstTick.Applied && firstTick.CurrentHp == 1000 && firstTick.CurrentMp == 385,
                "timed town tick persists atomically");
            Set(session, "Character", (await database.GetCharacterAsync(accountId))!);
            var d8ffPayload = (byte[])healingPayloadBuilder.Invoke(null, [Get(session, "Character")!])!;
            Check(BinaryPrimitives.ReadUInt16LittleEndian(d8ffPayload.AsSpan(8, 2)) == 5400
                  && BinaryPrimitives.ReadUInt16LittleEndian(d8ffPayload.AsSpan(10, 2)) == 520
                  && BinaryPrimitives.ReadUInt16LittleEndian(d8ffPayload.AsSpan(12, 2)) == 1000
                  && BinaryPrimitives.ReadUInt16LittleEndian(d8ffPayload.AsSpan(14, 2)) == 385,
                "D8FF payload carries max and current resources after a tick");

            byte[] c38d = new byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(c38d, 1);
            Check((await Dispatch(0xC38D, c38d)) is { } c38e && ReadOpcode(c38e) == 0xC38E,
                "C38D apartment entry returns C38E");
            CheckResources(await database.GetCharacterAsync(accountId), 1000, 385,
                "C38D only synchronizes and does not heal immediately");
            Check(schedule.TryGetActiveScene(out var activeApartment)
                  && activeApartment == HealthRecoveryScene.Apartment,
                "apartment entry switches the active recovery scene without resetting resources");

            byte[] c38f = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(c38f, 320);
            BinaryPrimitives.WriteUInt16LittleEndian(c38f.AsSpan(2, 2), 240);
            Check((await Dispatch(0xC38F, c38f)) is { } c390 && ReadOpcode(c390) == 0xC390,
                "C38F apartment refresh returns C390");
            CheckResources(await database.GetCharacterAsync(accountId), 1000, 385,
                "C38F only synchronizes and does not heal immediately");

            await WriteResourcesAsync(database.DatabasePath, characterId, 5400, 520, 5390, 515);
            var capped = await database.ApplyHealthRecoveryStepAsync(
                accountId, characterId, sessionId, HealthRecoveryScene.Apartment);
            Check(capped.Applied && capped.CurrentHp == 5400 && capped.CurrentMp == 520,
                "database tick caps at maxima");
            Check(!(await database.ApplyHealthRecoveryStepAsync(
                    accountId, characterId, sessionId, HealthRecoveryScene.Town)).Applied,
                "full resources do not create another write");

            await WriteResourcesAsync(database.DatabasePath, characterId, 5400, 520, 0, 375);
            Set(session, "Character", (await database.GetCharacterAsync(accountId))!);
            await (Task)applyDeathReturn.Invoke(service, [session, 375, CancellationToken.None])!;
            CheckResources(await database.GetCharacterAsync(accountId), 900, 375,
                "death-return initializer persists partial HP and retained MP");

            await WriteResourcesAsync(database.DatabasePath, characterId, 5400, 520, 1000, 385);
            var reconnectState = new CharacterRuntimeState(1000, 385, 1, 0, 400, 96, 1);
            Check(await database.EndWorldSessionAsync(accountId, characterId, sessionId, reconnectState),
                "first world session closed with partial resources");
            string reconnectSession = Guid.NewGuid().ToString("N");
            Check(await database.BeginWorldSessionAsync(accountId, characterId, reconnectSession, 1, "127.0.0.1"),
                "reconnect world session opened");
            CheckResources(await database.GetCharacterAsync(accountId), 1000, 385,
                "reconnect preserves partial resources");
            var reconnectTick = await database.ApplyHealthRecoveryStepAsync(
                accountId, characterId, reconnectSession, HealthRecoveryScene.Town);
            Check(reconnectTick.Applied && reconnectTick.CurrentHp == 1100 && reconnectTick.CurrentMp == 395,
                "reconnected session owns subsequent recovery ticks");

            var dead = (await database.GetCharacterAsync(accountId))!;
            dead.MaxHp = 5400;
            dead.MaxMp = 520;
            var death = HealthRecoveryPolicy.ResolveDungeonDeathReturn(dead, 375);
            Check(death.CurrentHp == 900 && death.CurrentMp == 375,
                "death return remains partial before town recovery");
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
