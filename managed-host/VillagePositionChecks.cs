using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class VillagePositionChecks
{
    internal static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string root = Path.Combine(Path.GetTempPath(), "open-nanaimo-village-position-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            long accountId = await database.OpenLocalAccountAsync("village-position-check");
            await database.CreateLocalCharacterAsync(accountId, "VilPos", 1);
            var created = (await database.GetCharacterAsync(accountId))!;

            await WriteDirtyPositionAsync(database.DatabasePath, created.Id, 4, 18, ushort.MaxValue, ushort.MaxValue);
            var reopened = new DatabaseService(root);
            await reopened.InitializeAsync();
            var healed = (await reopened.GetCharacterAsync(accountId))!;
            Check(healed.CurrentMapId == 4 && healed.CurrentTownPage == 18,
                "startup self-heal preserves map/page");
            Check(healed.PositionX == TownPositionPolicy.FallbackX
                  && healed.PositionY == TownPositionPolicy.FallbackY,
                "startup self-heal replaces persisted FFFF/FFFF with 400/96");

            string persistenceSession = Guid.NewGuid().ToString("N");
            Check(await reopened.BeginWorldSessionAsync(accountId, healed.Id, persistenceSession, 1, "127.0.0.1"),
                "persistence guard fixture starts");
            Check(await reopened.SaveCharacterRuntimeStateAsync(
                    accountId,
                    healed.Id,
                    persistenceSession,
                    new CharacterRuntimeState(
                        healed.CurrentHp,
                        healed.CurrentMp,
                        4,
                        18,
                        ushort.MaxValue,
                        ushort.MaxValue,
                        1)),
                "runtime save accepts state after coordinate normalization");
            var guarded = (await reopened.GetCharacterAsync(accountId))!;
            Check(guarded.PositionX == TownPositionPolicy.FallbackX
                  && guarded.PositionY == TownPositionPolicy.FallbackY,
                "runtime save never persists FFFF/FFFF");
            await reopened.EndWorldSessionAsync(
                accountId,
                healed.Id,
                persistenceSession,
                new CharacterRuntimeState(
                    healed.CurrentHp,
                    healed.CurrentMp,
                    4,
                    18,
                    ushort.MaxValue,
                    ushort.MaxValue,
                    1));
            guarded = (await reopened.GetCharacterAsync(accountId))!;
            Check(guarded.PositionX == TownPositionPolicy.FallbackX
                  && guarded.PositionY == TownPositionPolicy.FallbackY,
                "disconnect save never persists FFFF/FFFF");

            Check(TownPositionPolicy.IsPersistable(400, 96), "known village entry is legal");
            Check(!TownPositionPolicy.IsPersistable(ushort.MaxValue, ushort.MaxValue),
                "native FFFF/FFFF sentinel is not persistable");
            Check(!TownPositionPolicy.IsPersistable(0x3FF, 0x3FF),
                "legacy clamp image 03FF/03FF is not persistable");

            var townUserInfoBuilder = typeof(NetworkAdapterService).GetMethod(
                "BuildTownUserInfoPayload",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: [typeof(CharacterRecord), typeof(ushort), typeof(ushort)],
                modifiers: null)!;
            var stalePositionCharacter = new CharacterRecord
            {
                Id = 79,
                AccountId = accountId,
                Name = "StaleWirePosition",
                TutorialCompleted = true,
                PositionX = 0,
                PositionY = 0
            };
            var townUserInfo = (byte[])townUserInfoBuilder.Invoke(
                null,
                [stalePositionCharacter, TownPositionPolicy.FallbackX, TownPositionPolicy.FallbackY])!;
            Check(BinaryPrimitives.ReadUInt32LittleEndian(townUserInfo.AsSpan(80, 4))
                  == (((uint)TownPositionPolicy.FallbackX << 2)
                      | ((uint)TownPositionPolicy.FallbackY << 12)),
                "C36A uses the session bootstrap position instead of stale character coordinates");

            foreach (var (savedMap, savedPage) in new[] { (1, 33), (4, 18) })
            {
                var savedCharacter = new CharacterRecord
                {
                    Id = 70 + savedMap,
                    AccountId = accountId,
                    Name = $"Saved{savedMap}_{savedPage}",
                    TutorialCompleted = true,
                    CurrentMapId = savedMap,
                    CurrentTownPage = savedPage,
                    PositionX = TownPositionPolicy.FallbackX,
                    PositionY = TownPositionPolicy.FallbackY
                };
                var c355Payload = NetworkAdapterService.BuildLoadNecessityPayload(
                    savedCharacter,
                    new byte[60],
                    new byte[60],
                    null);
                Check(c355Payload[0x20 - 8] == TownPositionPolicy.LoginBootstrapMapId
                      && c355Payload[0x21 - 8] == TownPositionPolicy.LoginBootstrapTownPage,
                    $"C355 bootstraps saved map/page {savedMap}/{savedPage} through safe 0/0");
            }

            await using var service = new NetworkAdapterService(reopened, _ => { }, root);
            var serviceType = typeof(NetworkAdapterService);
            var sessionType = serviceType.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var dispatch = serviceType.GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            var runtimeCharacter = new CharacterRecord
            {
                Id = 77,
                AccountId = accountId,
                Name = "VilPos",
                TutorialCompleted = true,
                CurrentMapId = 4,
                CurrentTownPage = 18,
                PositionX = ushort.MaxValue,
                PositionY = ushort.MaxValue,
                MaxHp = 2000,
                CurrentHp = 2000,
                MaxMp = 500,
                CurrentMp = 500
            };
            Set(session, "AccountId", accountId);
            Set(session, "Character", runtimeCharacter);
            Set(session, "ChannelId", 1);
            Set(session, "ListenerPort", 12050);
            Set(session, "OnlineTracked", true);
            Set(session, "TownId", TownPositionPolicy.LoginBootstrapMapId);
            Set(session, "TownPage", TownPositionPolicy.LoginBootstrapTownPage);

            async Task<byte[]?> Dispatch(ushort opcode, byte[] payload)
            {
                byte[] frame = NativeDungeonClient.Frame(opcode, payload);
                return await (Task<byte[]?>)dispatch.Invoke(service,
                    [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
            }

            byte[] c367 = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(c367, TownPositionPolicy.LoginBootstrapTownPage);
            BinaryPrimitives.WriteUInt16LittleEndian(c367.AsSpan(4, 2), ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(c367.AsSpan(6, 2), ushort.MaxValue);
            byte[] c368 = (await Dispatch(0xC367, c367))!;
            Check(ReadOpcode(c368) == 0xC368 && c368.Length == 60,
                "C367 sentinel receives one C368/60 response");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(c368.AsSpan(0x30, 2)) == TownPositionPolicy.FallbackX
                  && BinaryPrimitives.ReadUInt16LittleEndian(c368.AsSpan(0x32, 2)) == TownPositionPolicy.FallbackY,
                "C368 +0x30/+0x32 carries 400/96 instead of 03FF/03FF");
            Check(c368[9] == TownPositionPolicy.LoginBootstrapTownPage,
                "C368 retains the safe bootstrap page instead of restoring page18 before first actor creation");
            Check(runtimeCharacter.CurrentMapId == TownPositionPolicy.LoginBootstrapMapId
                  && runtimeCharacter.CurrentTownPage == TownPositionPolicy.LoginBootstrapTownPage
                  && runtimeCharacter.PositionX == TownPositionPolicy.FallbackX
                  && runtimeCharacter.PositionY == TownPositionPolicy.FallbackY,
                "C367 replaces the unsafe saved page with the first-process bootstrap tuple");

            byte[] c365 = new byte[10];
            c365[0] = 4;
            BinaryPrimitives.WriteUInt16LittleEndian(c365.AsSpan(2, 2), 18);
            BinaryPrimitives.WriteUInt16LittleEndian(c365.AsSpan(6, 2), ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(c365.AsSpan(8, 2), ushort.MaxValue);
            byte[] c366 = (await Dispatch(0xC365, c365))!;
            Check(ReadOpcode(c366) == 0xC366,
                "C365 sentinel still follows the request-driven C366 tuple");
            Check(runtimeCharacter.PositionX == TownPositionPolicy.FallbackX
                  && runtimeCharacter.PositionY == TownPositionPolicy.FallbackY,
                "C365 never writes its FFFF/FFFF sentinel into Character");

            runtimeCharacter.PositionX = 444;
            runtimeCharacter.PositionY = 222;
            Set(session, "LastReportedPositionX", (ushort)444);
            Set(session, "LastReportedPositionY", (ushort)222);
            byte[] cb21Sentinel = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(cb21Sentinel.AsSpan(8, 2), ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(cb21Sentinel.AsSpan(10, 2), ushort.MaxValue);
            Check(await Dispatch(0xCB21, cb21Sentinel) is null,
                "CB21 activity sentinel remains response-free");
            Check(runtimeCharacter.PositionX == 444 && runtimeCharacter.PositionY == 222,
                "CB21 FFFF/FFFF is relayed as activity but cannot overwrite the last legal position");

            byte[] cb21Move = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(cb21Move.AsSpan(8, 2), 512);
            BinaryPrimitives.WriteUInt16LittleEndian(cb21Move.AsSpan(10, 2), 288);
            await Dispatch(0xCB21, cb21Move);
            Check(runtimeCharacter.PositionX == 512 && runtimeCharacter.PositionY == 288,
                "CB21 legal movement still updates the persistent carrier");

            Console.WriteLine("VILLAGE_POSITION_CHECKS_PASS c355-bootstrap c365 c367 c368-offsets cb21 sentinel persistence startup-self-heal");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string full = Path.GetFullPath(root);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
            {
                for (var retry = 0; retry < 5; retry++)
                {
                    try { Directory.Delete(full, true); break; }
                    catch (IOException) when (retry < 4) { await Task.Delay(50); }
                }
            }
        }
    }

    private static async Task WriteDirtyPositionAsync(
        string databasePath,
        long characterId,
        int mapId,
        int page,
        int x,
        int y)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Characters
            SET TutorialCompleted=1,
                CurrentMapId=$mapId,
                CurrentTownPage=$page,
                PositionX=$x,
                PositionY=$y,
                LastSavedAt=$now
            WHERE Id=$id
            """;
        command.Parameters.AddWithValue("$mapId", mapId);
        command.Parameters.AddWithValue("$page", page);
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", y);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", characterId);
        Check(await command.ExecuteNonQueryAsync() == 1, "dirty position fixture written");
    }

    private static ushort ReadOpcode(byte[] frame)
        => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2));

    private static void Set(object instance, string name, object? value)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private static void Check(bool passed, string name)
    {
        if (!passed)
            throw new InvalidDataException("VILLAGE_POSITION_CHECK_FAILED " + name);
        Console.WriteLine("VILLAGE_POSITION_CHECK_PASS " + name);
    }
}
