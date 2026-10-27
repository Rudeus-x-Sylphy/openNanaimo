using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

// Host construction/SQLite lifecycle only. Does not launch or patch a client.
internal static class TutorialAppearanceChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly MethodInfo Login = typeof(NetworkAdapterService).GetMethod("BuildPostLoginPayload", PrivateStatic)!;
    private static readonly MethodInfo Channel = typeof(NetworkAdapterService).GetMethod("BuildChannelConnectionPayload", PrivateStatic)!;
    private static readonly MethodInfo Dispatch = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", PrivateInstance)!;

    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string temp = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(temp, "open-nanaimo-tutorial-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            await db.EnsureLocalInitialGrantSettingsAsync();
            await using var service = new NetworkAdapterService(db, _ => { }, root);
            var noCharacter = NewSession(null);
            var noCharacterLogin = LoginPayload(noCharacter);
            Check(noCharacterLogin.Length == 60 && noCharacterLogin[4] == 0 && noCharacterLogin[5] == 0
                && noCharacterLogin[6] == 0 && noCharacterLogin.AsSpan(8, 52).ToArray().All(b => b == 0),
                "no-character creation gate uses an empty identity and appearance projection");

            foreach (int gender in new[] { 0, 1 })
            foreach (uint reportedEffect in new uint[] { 0, 10160017, 10160160, 10160024 })
            {
                uint effect = reportedEffect == 0 ? 0 : reportedEffect - (gender == 0 ? 100000u : 0u);
                string name = $"Guide{gender}{reportedEffect}";
                string nameHex = Convert.ToHexString(Encoding.ASCII.GetBytes(name));
                string profile = $"version=2\nname_hex={nameHex}\ngender={gender}\nlevel=25\npet=15009205\n"
                    + "pet_age_a=3\npet_age_b=3\nhp_max=1500\nmp_max=500\ncoin=12345\nnana_point=67890\n"
                    + "equip_effect=0\n"; // sidecar must win without being cleared to fix login
                string sidecar = "version=1\ncoin=0:12345\nnana=0:67890\n"
                    + $"equip0={10130337 - (gender == 0 ? 100000 : 0)}\nequip1=0\nequip2=0\nequip3=0\nequip4=0\n"
                    + $"effect={effect}\nselected_pet=15009205\nowned_pet=15009205\n"
                    + (effect == 0 ? "" : $"owned_equipment={effect}\n");
                string profilePath = Path.Combine(root, "nanaimo_launcher_profile.ini");
                string sidecarPath = Path.Combine(root, "nanaimo_inventory_state_v1.dat");
                await File.WriteAllTextAsync(profilePath, profile);
                if (!File.Exists(sidecarPath)) await File.WriteAllTextAsync(sidecarPath, sidecar);
                var preservedLegacy = await File.ReadAllTextAsync(sidecarPath);
                new PersistentStateStore(db.DatabasePath).Put(sidecarPath, Encoding.UTF8.GetBytes(sidecar));
                var character = await db.ImportLocalProfileAsync(profile, root);
                byte[] stored = character.Appearance.ToArray();
                var inventory = Owned(character);
                Check(!character.TutorialCompleted && Read(stored, 24) == effect,
                    $"fresh g={gender} effect={effect} retains selected database loadout");
                var session = NewSession(character);
                AssertGuideLogin(session, gender);
                var reimported = await db.ImportLocalProfileAsync(profile, root);
                Check(!reimported.TutorialCompleted && reimported.Appearance.SequenceEqual(stored)
                    && Owned(reimported).SequenceEqual(inventory), "unfinished relogin/reimport preserves stored state");
                AssertGuideLogin(NewSession(reimported), gender);
                Set(session, "OnlineTracked", true);
                string sessionId = (string)Get(session, "SessionId")!;
                Check(await db.BeginWorldSessionAsync(character.AccountId, character.Id, sessionId, 1, "127.0.0.1"),
                    "world session accepted");
                var channel = (byte[])Channel.Invoke(null, [character, true])!;
                Check(channel[0] == 100 && channel[1] == 1, "C352 keeps native guide entry");
                var preGuide = (await Send(service, session, 0xC354, []))!;
                var preGuideReady = Frame(preGuide, 0xC594);
                var preGuideProfile = Frame(preGuide, 0xC355);
                Check(Opcodes(preGuide).SequenceEqual(new ushort[] { 0xC594, 0xC355 })
                    && Read(preGuideReady, 8) == 0
                    && preGuideProfile.Length == 728 && preGuideProfile[13] == 0,
                    "pre-guide C354 restores zero story progress without inventory or equipped-pet state");
                Check(await Send(service, session, 0xC353, []) is null, "short guide completion rejected");
                var guide = new byte[20];
                Encoding.ASCII.GetBytes(name).CopyTo(guide, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(guide.AsSpan(16), 1);
                byte[] wrongIdentity = guide.ToArray(); wrongIdentity[0] = (byte)'X';
                Check(await Send(service, session, 0xC353, wrongIdentity) is null, "wrong guide identity rejected");
                Check(!(await db.GetCharacterAsync(character.AccountId))!.TutorialCompleted,
                    "invalid guide messages never bypass tutorial");
                Check((await db.GetCharacterAsync(character.AccountId))!.CardSummonCount == character.CardSummonCount,
                    "invalid guide messages never grant tutorial keys");
                var ready = (await Send(service, session, 0xC353, guide))!;
                Check(ready.Length == 12 && Opcodes(ready).SequenceEqual(new ushort[] { 0xC594 })
                    && Read(ready, 8) == 0,
                    "valid completion keeps unfinished village story guidance available");
                var completed = (await db.GetCharacterAsync(character.AccountId))!;
                Check(completed.TutorialCompleted && completed.Appearance.SequenceEqual(stored)
                    && completed.EquippedPetItemCode == 15009205 && inventory.All(Owned(completed).Contains),
                    "completion preserves deferred loadout, pet and all owned equipment");
                Check(completed.CardSummonCount == character.CardSummonCount + 19,
                    "C353 completion adds exactly 19 normal keys to the persisted counter");
                var summon = Frame((await Send(service, session, 0xC3E9, []))!, 0xC3EA);
                var cards = Frame((await Send(service, session, 0xC3E7, [10, 0, 1, 0]))!, 0xC3E8);
                Check(summon.Length == 20 && summon[9] == completed.CardSummonCount
                    && cards[64] == completed.CardSummonCount,
                    "request-driven C3EA and C3E8 expose the tutorial key balance");
                byte[] expected = DatabaseService.NormalizeAppearanceForGender(stored, gender, 15009205);
                var village = (await Send(service, session, 0xC354, []))!;
                var villageReady = Frame(village, 0xC594);
                var villageProfile = Frame(village, 0xC355);
                Check(Opcodes(village).SequenceEqual(new ushort[] { 0xC594, 0xC355, 0xC476, 0xC44C })
                    && Read(villageReady, 8) == 0 && villageProfile[13] == 1,
                    "post-guide C354 restores unfinished story guidance, inventory and configured pet");
                var move = new byte[8];
                BinaryPrimitives.WriteUInt16LittleEndian(move.AsSpan(4), ushort.MaxValue);
                BinaryPrimitives.WriteUInt16LittleEndian(move.AsSpan(6), ushort.MaxValue);
                var actor = (await Send(service, session, 0xC367, move))!;
                Check(Opcodes(actor).SequenceEqual(new ushort[] { 0xC368, 0xC379 })
                    && Frame(actor, 0xC368).AsSpan(12, 36).SequenceEqual(expected),
                    "first village actor restores every saved slot, including D6, without relaunch");
                var initialBox = Frame(actor, 0xC379);
                var queriedBox = Frame((await Send(service, session, 0xC378, []))!, 0xC379);
                Check(initialBox.AsSpan(10).SequenceEqual(queriedBox.AsSpan(10))
                    && initialBox.AsSpan(132, 36).SequenceEqual(expected),
                    "login initializes the complete equipped snapshot before opening inventory or changing equipment");
                var resources = NetworkAdapterService.ResolveInventoryVitals(completed);
                var directActor = NetworkAdapterService.BuildRoomEnterPayload(completed, 0, 160, 304);
                Check(BinaryPrimitives.ReadUInt16LittleEndian(directActor.AsSpan(44)) == resources.MaximumHp
                    && BinaryPrimitives.ReadUInt16LittleEndian(directActor.AsSpan(46)) == resources.MaximumMp
                    && BinaryPrimitives.ReadUInt16LittleEndian(directActor.AsSpan(48)) == resources.CurrentHp,
                    "actor constructor itself uses equipment-effective resources without an implicit heal");
                Check(Opcodes((await Send(service, session, 0xC367, move))!).SequenceEqual(new ushort[] { 0xC368 }),
                    "later town pages do not repeat initialization");
                await Send(service, session, 0xC354, []);
                var repeated = (await Send(service, session, 0xC367, move))!;
                Check(Frame(repeated, 0xC379).AsSpan(10).SequenceEqual(initialBox.AsSpan(10)),
                    "repeated initialization replaces the same slots without a synthetic equip acknowledgment");
                var beforeRepeat = (await db.GetCharacterAsync(character.AccountId))!;
                Check(Opcodes((await Send(service, session, 0xC353, guide))!).SequenceEqual(new ushort[] { 0xC594 }),
                    "duplicate completion stays request-driven");
                var afterRepeat = (await db.GetCharacterAsync(character.AccountId))!;
                Check(Owned(afterRepeat).SequenceEqual(Owned(beforeRepeat))
                    && afterRepeat.Appearance.SequenceEqual(stored), "duplicate completion cannot duplicate or clear equipment");
                Check(afterRepeat.CardSummonCount == completed.CardSummonCount,
                    "duplicate C353 never repeats the normal-key reward");
                var disconnect = typeof(NetworkAdapterService).GetMethod("TrackDisconnectedAsync", PrivateInstance)!;
                await (Task)disconnect.Invoke(service, [session])!;
                var relog = await db.ImportLocalProfileAsync(profile, root);
                var context = LoginPayload(NewSession(relog));
                Check(relog.TutorialCompleted && context[5] == 0 && context[6] == 2
                    && context.AsSpan(24, 36).SequenceEqual(expected), "completed relogin immediately restores configured appearance");
                Check(relog.CardSummonCount == completed.CardSummonCount,
                    "relogin and profile reimport preserve the tutorial key balance");
                Check(await File.ReadAllTextAsync(profilePath) == profile && await File.ReadAllTextAsync(sidecarPath) == preservedLegacy
                    && Encoding.UTF8.GetString(new PersistentStateStore(db.DatabasePath).Read(sidecarPath)!) == sidecar,
                    "INI and sidecar are unchanged throughout tutorial lifecycle");
            }
            await CheckTutorialKeyTransactionAsync(db, root);
            EquipmentInitializationChecks.Run(service);
            Console.WriteLine("TUTORIAL_APPEARANCE_CHECKS_PASS cases=8 plus no-character; host construction only");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (root.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("open-nanaimo-tutorial-", StringComparison.Ordinal)
                && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckTutorialKeyTransactionAsync(DatabaseService db, string root)
    {
        async Task Execute(string sql, long id)
        {
            await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync();
        }
        async Task<(CharacterRecord Character, string Session)> Create(string name, int keys)
        {
            var profile = $"version=2\nname_hex={Convert.ToHexString(Encoding.ASCII.GetBytes(name))}\ngender=1\nlevel=1\n";
            var character = await db.ImportLocalProfileAsync(profile, root);
            var session = Guid.NewGuid().ToString("N");
            Check(await db.BeginWorldSessionAsync(character.AccountId, character.Id, session, 1, "127.0.0.1"),
                "tutorial reward fixture owns an active world session");
            await Execute("UPDATE Characters SET CardSummonCount=" + keys
                + ",CardGoldenKeyCount=3,CardMysteryKeyCount=4 WHERE Id=$id", character.Id);
            return (character, session);
        }

        foreach (var keys in new[] { 0, 7, 236 })
        {
            var (character, session) = await Create("KeyReward" + keys, keys);
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
                () => db.MarkTutorialCompletedAsync(character.AccountId, character.Id, session, 1))));
            var saved = (await db.GetCharacterAsync(character.AccountId))!;
            Check(results.All(result => result) && saved.TutorialCompleted && saved.CardSummonCount == keys + 19
                && saved.CardGoldenKeyCount == 3 && saved.CardMysteryKeyCount == 4
                && !saved.Items.Any(item => item.ItemCode / 1_000_000u == 47),
                $"concurrent completion at {keys} keys grants exactly 19 without inventory key items or other-key changes");
            await Execute("UPDATE Characters SET CardSummonCount=CardSummonCount-1 WHERE Id=$id", character.Id);
            Check(await db.MarkTutorialCompletedAsync(character.AccountId, character.Id, session, 3)
                && (await db.GetCharacterAsync(character.AccountId))!.CardSummonCount == keys + 18,
                "database replay after spending a key neither refills nor repeats the reward");
            var reopened = new DatabaseService(root);
            await reopened.InitializeAsync();
            Check((await reopened.GetCharacterAsync(character.AccountId))!.CardSummonCount == keys + 18,
                "database reinitialization does not migrate tutorial keys into mystery keys or grant again");
        }

        var (guarded, activeSession) = await Create("KeyGuard", 0);
        foreach (var request in new[]
        {
            (guarded.AccountId, guarded.Id, "stale-session"),
            (guarded.AccountId + 1, guarded.Id, activeSession),
            (guarded.AccountId, guarded.Id + 1, activeSession)
        })
            Check(!await db.MarkTutorialCompletedAsync(request.Item1, request.Item2, request.Item3, 1),
                "wrong identity or stale session cannot claim tutorial keys");
        await Execute("UPDATE Accounts SET IsOnline=0 WHERE Id=$id", guarded.AccountId);
        Check(!await db.MarkTutorialCompletedAsync(guarded.AccountId, guarded.Id, activeSession, 1),
            "offline account cannot claim tutorial keys");
        await Execute("UPDATE Accounts SET IsOnline=1 WHERE Id=$id", guarded.AccountId);
        await Execute("UPDATE Characters SET IsOnline=0 WHERE Id=$id", guarded.Id);
        Check(!await db.MarkTutorialCompletedAsync(guarded.AccountId, guarded.Id, activeSession, 1),
            "offline character cannot claim tutorial keys");
        await Execute("UPDATE Characters SET IsOnline=1 WHERE Id=$id", guarded.Id);
        Check(!(await db.GetCharacterAsync(guarded.AccountId))!.TutorialCompleted
            && (await db.GetCharacterAsync(guarded.AccountId))!.CardSummonCount == 0,
            "rejected authorization attempts leave completion and balance unchanged");

        await Execute("CREATE TRIGGER RejectTutorialPet BEFORE INSERT ON CharacterItems WHEN NEW.CharacterId="
            + guarded.Id + " BEGIN SELECT RAISE(ABORT,'tutorial rollback probe'); END", guarded.Id);
        try
        {
            await db.MarkTutorialCompletedAsync(guarded.AccountId, guarded.Id, activeSession, 1);
            throw new InvalidDataException("Expected tutorial pet write failure");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            var rolledBack = (await db.GetCharacterAsync(guarded.AccountId))!;
            Check(!rolledBack.TutorialCompleted && rolledBack.CardSummonCount == 0
                && rolledBack.PetVariant == guarded.PetVariant && rolledBack.CurrentMapId == guarded.CurrentMapId
                && rolledBack.Items.Count == guarded.Items.Count,
                "later pet write failure rolls back tutorial status, keys, pet and position together");
        }
        await Execute("DROP TRIGGER RejectTutorialPet", guarded.Id);
        Check(await db.MarkTutorialCompletedAsync(guarded.AccountId, guarded.Id, activeSession, 1)
            && (await db.GetCharacterAsync(guarded.AccountId))!.CardSummonCount == 19,
            "retry after rollback grants the full reward once");

        var (full, fullSession) = await Create("KeyFull", 237);
        Check(!await db.MarkTutorialCompletedAsync(full.AccountId, full.Id, fullSession, 1),
            "insufficient key capacity rejects the entire completion without clamping");
        var unchanged = (await db.GetCharacterAsync(full.AccountId))!;
        Check(!unchanged.TutorialCompleted && unchanged.CardSummonCount == 237,
            "capacity failure leaves tutorial retryable and balance unchanged");
        await Execute("UPDATE Characters SET CardSummonCount=236 WHERE Id=$id", full.Id);
        Check(await db.MarkTutorialCompletedAsync(full.AccountId, full.Id, fullSession, 1)
            && (await db.GetCharacterAsync(full.AccountId))!.CardSummonCount == 255,
            "exact-capacity retry grants all 19 keys");

        var (legacy, legacySession) = await Create("KeyLegacy", 0);
        await Execute("UPDATE Characters SET TutorialCompleted=1 WHERE Id=$id", legacy.Id);
        Check(await db.MarkTutorialCompletedAsync(legacy.AccountId, legacy.Id, legacySession, 1)
            && (await db.GetCharacterAsync(legacy.AccountId))!.CardSummonCount == 0,
            "completed legacy characters are not assumed to be owed a reward");
        var (story, storySession) = await Create("KeyStory", 0);
        Check(await db.MarkTutorialCompletedAsync(story.AccountId, story.Id, storySession, 1),
            "main tutorial completes before village guide reward");
        var intro = await db.CompleteStoryGuideAsync(story.AccountId, story.Id, storySession, 0, 0);
        var village = await db.CompleteStoryGuideAsync(story.AccountId, story.Id, storySession, 2, 0);
        var repeat = await db.CompleteStoryGuideAsync(story.AccountId, story.Id, storySession, 2, 0);
        Check(intro.Success && village.Success && repeat.Success
            && (await db.GetCharacterAsync(story.AccountId))!.CardSummonCount == 29,
            "village card guide adds its independent 10 keys once after the tutorial's 19");
        Console.WriteLine("TUTORIAL_KEY_TRANSACTION_CHECKS_PASS host construction and SQLite only");
    }

    private static void AssertGuideLogin(object session, int gender)
    {
        var character = (CharacterRecord)Get(session, "Character")!;
        byte[] before = character.Appearance.ToArray();
        byte[] payload = LoginPayload(session);
        byte[] expected = DatabaseService.NormalizeAppearanceForGender(before, gender, 0);
        expected.AsSpan(24, 8).Clear();
        Check(payload.Length == 60 && payload[5] == character.DungeonGrade && payload[6] == 0
            && payload.AsSpan(24, 36).SequenceEqual(expected),
            "271A main-guide uses the persisted avatar, withholding only tutorial-unsafe effect/pet slots");
        Check(character.Appearance.SequenceEqual(before), "login projection does not mutate session character");
    }

    private static object NewSession(CharacterRecord? character)
    {
        object session = Activator.CreateInstance(SessionType, nonPublic: true)!;
        Set(session, "AccountId", character?.AccountId ?? 1L);
        Set(session, "Username", character?.Name ?? "NoCharacter");
        Set(session, "Character", character);
        Set(session, "ChannelId", 1);
        Set(session, "ListenerPort", 12050);
        Set(session, "RemoteIp", "127.0.0.1");
        return session;
    }
    private static byte[] LoginPayload(object session) => (byte[])Login.Invoke(null, [session])!;
    private static Task<byte[]?> Send(NetworkAdapterService service, object session, ushort opcode, byte[] payload)
        => (Task<byte[]?>)Dispatch.Invoke(service,
            [NativeDungeonClient.Frame(opcode, payload), opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
    private static uint Read(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static byte[] Frame(byte[] response, ushort opcode)
    {
        for (int offset = 0; offset < response.Length;)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 4, 2));
            if (BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 6, 2)) == opcode)
                return response.AsSpan(offset, length).ToArray();
            offset += length;
        }
        throw new InvalidDataException($"Missing response frame {opcode:X4}");
    }
    private static string[] Owned(CharacterRecord character) => character.Items.Select(i => $"{i.ItemCode}:{i.Quantity}").OrderBy(x => x).ToArray();
    private static ushort[] Opcodes(byte[] response)
    {
        var result = new List<ushort>();
        for (int offset = 0; offset < response.Length;)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 4, 2));
            Check(length >= 8 && length <= response.Length - offset, "response frame length covers its payload");
            result.Add(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 6, 2)));
            offset += length;
        }
        return result.ToArray();
    }
    private static object? Get(object instance, string name) => SessionType.GetProperty(name)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => SessionType.GetProperty(name)!.SetValue(instance, value);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("TUTORIAL_APPEARANCE_CHECK_FAILED " + message);
        Console.WriteLine("TUTORIAL_APPEARANCE_CHECK_PASS " + message);
    }
}
