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
                await File.WriteAllTextAsync(sidecarPath, sidecar);
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
                var ready = (await Send(service, session, 0xC353, guide))!;
                Check(ready.Length == 12 && Opcodes(ready).SequenceEqual(new ushort[] { 0xC594 })
                    && Read(ready, 8) == 0,
                    "valid completion keeps unfinished village story guidance available");
                var completed = (await db.GetCharacterAsync(character.AccountId))!;
                Check(completed.TutorialCompleted && completed.Appearance.SequenceEqual(stored)
                    && completed.EquippedPetItemCode == 15009205 && inventory.All(Owned(completed).Contains),
                    "completion preserves deferred loadout, pet and all owned equipment");
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
                Check(actor.Length == 60 && Opcodes(actor).SequenceEqual(new ushort[] { 0xC368 })
                    && actor.AsSpan(12, 36).SequenceEqual(expected),
                    "first village actor restores every saved slot, including D6, without relaunch");
                var beforeRepeat = (await db.GetCharacterAsync(character.AccountId))!;
                Check(Opcodes((await Send(service, session, 0xC353, guide))!).SequenceEqual(new ushort[] { 0xC594 }),
                    "duplicate completion stays request-driven");
                var afterRepeat = (await db.GetCharacterAsync(character.AccountId))!;
                Check(Owned(afterRepeat).SequenceEqual(Owned(beforeRepeat))
                    && afterRepeat.Appearance.SequenceEqual(stored), "duplicate completion cannot duplicate or clear equipment");
                var disconnect = typeof(NetworkAdapterService).GetMethod("TrackDisconnectedAsync", PrivateInstance)!;
                await (Task)disconnect.Invoke(service, [session])!;
                var relog = await db.ImportLocalProfileAsync(profile, root);
                var context = LoginPayload(NewSession(relog));
                Check(relog.TutorialCompleted && context[5] == 0 && context[6] == 2
                    && context.AsSpan(24, 36).SequenceEqual(expected), "completed relogin immediately restores configured appearance");
                Check(await File.ReadAllTextAsync(profilePath) == profile && await File.ReadAllTextAsync(sidecarPath) == sidecar,
                    "INI and sidecar are unchanged throughout tutorial lifecycle");
            }
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
