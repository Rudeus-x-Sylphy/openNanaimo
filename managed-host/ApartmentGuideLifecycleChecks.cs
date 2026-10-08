using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentGuideLifecycleChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Service = typeof(NetworkAdapterService);
    private static readonly Type Session = Service.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException("APARTMENT_GUIDE_LIFECYCLE_FAILED " + label);
            ++checks; Console.WriteLine("CHECK_PASS apartment-guide " + label);
        }
        // Original-client dump: C59A changed only maxima to 1684/215, leaving
        // current HP/MP at 9484/1205. The retail HUD then computed width 500.
        var character = new CharacterRecord { MaxHp = 1684, MaxMp = 215, CurrentHp = 9484, CurrentMp = 1205 };
        var resources = new BattleResourceSnapshot(9484, 1205, 2) { MaximumHp = 9484, MaximumMp = 1215 };
        var payload = NetworkAdapterService.BuildApartmentGuideCompletionPayload(true, character, true, 0, resources);
        var heal = NetworkAdapterService.BuildUserHpMpAutoHealingPayloadWithResources(character, resources);
        Check(payload.Length == 28 && payload[0] == 0 && U32(payload, 4) == 5 && U16(payload, 8) == 0,
            "C59A preserves complete guide identity, kind and extent");
        Check(U16(payload, 12) == 9484 && U16(payload, 14) == 1215
            && U16(payload, 12) == U16(heal, 8) && U16(payload, 14) == U16(heal, 10),
            "completion and recovery use the same effective maxima");
        Check(resources.CurrentHp / U16(payload, 12) * 100 <= 100
            && resources.CurrentMp / U16(payload, 14) * 100 <= 100, "exact dump values no longer overrun either HUD texture");
        Check(character.MaxHp == 1684 && character.MaxMp == 215 && character.CurrentHp == 9484
            && character.CurrentMp == 1205 && resources.CurrentHp == 9484,
            "completion does not mutate base profile or absolute current resources");
        payload = NetworkAdapterService.BuildApartmentGuideCompletionPayload(true, character, false, 0, null);
        Check(U16(payload, 12) == 1684 && U16(payload, 14) == 215, "no-pet/no-snapshot completion retains profile maxima");
        payload = NetworkAdapterService.BuildApartmentGuideCompletionPayload(false, character, false, 7, resources);
        Check(payload[0] == 3 && payload.Skip(1).All(x => x == 0), "rejection preserves recoverable result and zero fields");
        character.MaxHp = 0; character.MaxMp = -1;
        payload = NetworkAdapterService.BuildApartmentGuideCompletionPayload(true, character, false, 0, null);
        Check(U16(payload, 12) == 1 && U16(payload, 14) == 1, "legacy zero maxima use the same nonzero bound as recovery");

        // All seven story guides use effective resource maxima for HUD sizing.
        // Cover current HP=64500 with base HP=21500 and pet-adjusted resources.
        var shopCharacter = new CharacterRecord { Level = 200, Experience = CharacterProgression.ExperienceRequiredForLevel(200), MaxHp = 21500, MaxMp = 2100,
            CurrentHp = 64500, CurrentMp = 7510 };
        var shopResources = new BattleResourceSnapshot(64500, 7510, 2)
            { MaximumHp = 64500, MaximumMp = 8520 };
        for (uint guide = 0; guide <= 6; ++guide)
        {
            var kind = guide == 6 ? (ushort)7 : (ushort)0;
            payload = NetworkAdapterService.BuildStoryGuideCompletionPayload(
                true, guide, shopCharacter, true, kind, shopResources);
            Check(payload.Length == 28 && payload[0] == 0 && payload[1] == 1
                && U32(payload, 4) == guide && U16(payload, 8) == kind && payload[11] == 200,
                $"guide {guide} preserves result, reward flag, ID, kind, level and extent");
            Check(U16(payload, 12) == 64500 && U16(payload, 14) == 8520
                && shopResources.CurrentHp * 100.0 / U16(payload, 12) <= 100
                && shopResources.CurrentMp * 100.0 / U16(payload, 14) <= 100,
                $"guide {guide} keeps effective resources inside HUD bounds");
            payload = NetworkAdapterService.BuildStoryGuideCompletionPayload(
                false, guide, shopCharacter, true, kind, shopResources);
            Check(payload[0] == (guide == 5 ? 3 : 1) && payload.Skip(1).All(x => x == 0),
                $"guide {guide} preserves its own rejection branch without resources");
            payload = NetworkAdapterService.BuildStoryGuideCompletionPayload(
                true, guide, shopCharacter, false, kind, null);
            Check(U16(payload, 12) == 21500 && U16(payload, 14) == 2100 && payload[1] == 0,
                $"guide {guide} no-snapshot fallback and repeated reward flag unchanged");
        }
        Check(shopCharacter.MaxHp == 21500 && shopCharacter.CurrentHp == 64500
            && shopResources.CurrentHp == 64500, "shared guide construction never mutates saved or live resources");

        var root = Path.Combine(Path.GetTempPath(), "open-nanaimo-apartment-guide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root); await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("apartment-guide-scope");
            await db.CreateLocalCharacterAsync(account, "GuideScope", 0);
            character = (await db.GetCharacterAsync(account))!;
            await using (var sql = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False"))
            {
                await sql.OpenAsync(); await using var command = sql.CreateCommand();
                command.CommandText = $"UPDATE Characters SET TutorialCompleted=1,Level=4,Experience={CharacterProgression.ExperienceRequiredForLevel(4)},Hans=1000 WHERE Id=$id";
                command.Parameters.AddWithValue("$id", character.Id); await command.ExecuteNonQueryAsync();
            }
            character = (await db.GetCharacterAsync(account))!;
            await using var service = new NetworkAdapterService(db, _ => { }, root);
            var session = Activator.CreateInstance(Session, nonPublic: true)!;
            Set(session, "AccountId", account); Set(session, "Character", character);
            Set(session, "Username", "apartment-guide-scope"); Set(session, "OnlineTracked", true);
            Set(session, "ChannelId", 1); Set(session, "ApartmentOwnerCharacterId", character.Id);
            Set(session, "NonCombatResourceSnapshot", resources);
            var id = (string)Get(session, "SessionId")!;
            Check(await db.BeginWorldSessionAsync(account, character.Id, id, 1, "127.0.0.1"), "authorized fixture");
            var before = (await db.GetCharacterAsync(account))!;
            var move = new byte[20]; BinaryPrimitives.WriteUInt16LittleEndian(move, 1);
            var entry = await Send(service, session, 0xC38D, move);
            Check(entry is { Length: 112 } && U16(entry, 6) == 0xC38E && entry[8] == 10,
                "entry remains one C38E without extra resource or actor responses");
            var failed = await Send(service, session, 0xC38D, new byte[20]);
            Check(failed is { Length: 112 } && failed[8] == 30, "failed entry does not change the existing room");
            var actor = await Send(service, session, 0xC38F, new byte[4]);
            Check(actor is { Length: 124 } && U16(actor, 6) == 0xC390,
                "early actor request remains exactly one local actor response");
            var completion = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(completion.AsSpan(4), 5);
            completion[0] = 7;
            var rejected = await Send(service, session, 0xC599, completion);
            Check(rejected is { Length: 36 } && rejected[8] == 3,
                "invalid completion kind remains recoverable with no resource mutation");
            completion[0] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(completion.AsSpan(4), 0);
            var other = await Send(service, session, 0xC599, completion);
            Check(other is { Length: 36 } && U32(other, 12) == 0 && U16(other, 20) == resources.MaximumHp,
                "intro guide completion shares effective maxima and retains its own identity");
            // Exercise the production C599 dispatch, DB reward transaction, retry and reopen.
            Set(session, "NonCombatResourceSnapshot", shopResources);
            BinaryPrimitives.WriteUInt32LittleEndian(completion.AsSpan(4), 1);
            var shopBefore = (await db.GetCharacterAsync(account))!;
            var petShop = await Send(service, session, 0xC599, completion);
            Check(petShop is { Length: 36 } && petShop[8] == 0 && petShop[9] == 1
                && U32(petShop, 12) == 1 && U16(petShop, 20) == 64500 && U16(petShop, 22) == 8520,
                "pet-shop C599 returns exactly one full C59A with effective maxima");
            var shopAfter = (await db.GetCharacterAsync(account))!;
            uint[] gifts = [15005007u, 17000003u, 21000001u, 14000005u];
            foreach (var gift in gifts)
                Check(shopAfter.Items.Single(x => x.ItemCode == gift).Quantity
                    == (shopBefore.Items.FirstOrDefault(x => x.ItemCode == gift)?.Quantity ?? 0) + 1,
                    $"pet-shop gift {gift} committed once without changing its code");
            petShop = await Send(service, session, 0xC599, completion);
            Check(petShop is { Length: 36 } && petShop[8] == 0 && petShop[9] == 0
                && U16(petShop, 20) == 64500, "pet-shop retry keeps resources and does not grant again");
            var reopened = await new DatabaseService(root).GetCharacterAsync(account);
            foreach (var gift in gifts)
                Check(reopened!.Items.Single(x => x.ItemCode == gift).Quantity
                    == shopAfter.Items.Single(x => x.ItemCode == gift).Quantity,
                    $"pet-shop gift {gift} survives reopen without duplication");
            Set(session, "NonCombatResourceSnapshot", resources);
            var beforeGuideReward = (await db.GetCharacterAsync(account))!;
            var hansBefore = beforeGuideReward.Hans;
            var certificatesBefore = beforeGuideReward.Items.FirstOrDefault(
                item => item.ItemCode == 46_000_008u)?.Quantity ?? 0;
            BinaryPrimitives.WriteUInt32LittleEndian(completion.AsSpan(4), 5);
            var done = await Send(service, session, 0xC599, completion);
            Check(done is { Length: 36 } && done[8] == 0 && U16(done, 20) == resources.MaximumHp
                && U16(done, 22) == resources.MaximumMp, "dispatch uses effective maxima before reward-window handling");
            await Send(service, session, 0xC599, completion);
            var afterGuideReward = (await db.GetCharacterAsync(account))!;
            Check(afterGuideReward.Hans == hansBefore
                && afterGuideReward.Items.Single(item => item.ItemCode == 46_000_008u).Quantity
                    == certificatesBefore + 1,
                "repeated completion grants exactly one certificate");
            var schedule = (HealthRecoverySchedule)Get(session, "HealthRecovery")!;
            var clock = DateTimeOffset.UtcNow;
            schedule.Activate(HealthRecoveryScene.Apartment, clock);
            foreach (var elapsed in new[] { 5, 60, 3600, 86400 })
            {
                Check(schedule.TryTakeDueTick(clock.AddSeconds(elapsed), out _), $"long guide recovery remains scheduled at {elapsed}s");
                Check((bool)Service.GetMethod("IsNonCombatRecoverySceneActive", Private)!
                    .Invoke(service, [session, HealthRecoveryScene.Apartment])!, "guide does not suppress apartment healing");
                var changed = resources with { MaximumHp = 9600, CurrentHp = 9500, MaximumMp = 1300, CurrentMp = 1250 };
                Set(session, "NonCombatResourceSnapshot", changed);
                done = await Send(service, session, 0xC599, completion);
                Check(done is { Length: 36 } && U16(done, 20) == 9600 && U16(done, 22) == 1300,
                    "delayed/repeated completion uses latest authoritative resource maxima");
            }
            var after = (await db.GetCharacterAsync(account))!;
            Check(after.CurrentHp == before.CurrentHp && after.CurrentMp == before.CurrentMp,
                "entry and completion do not shrink persisted HP/MP");
            actor = await Send(service, session, 0xC38F, new byte[4]);
            Check(actor is { Length: 124 } && U16(actor, 6) == 0xC390,
                "post-guide actor request remains exactly one local actor response");
            entry = await Send(service, session, 0xC38D, move);
            Check(entry is { Length: 112 }, "completed-guide reentry keeps ordinary room response");
            Service.GetMethod("LeaveApartmentScene", Private)!.Invoke(service, [session, "guide test exit"]);
            Check((long)Get(session, "ApartmentOwnerCharacterId")! == 0, "ordinary room exit resets ownership");
            Set(session, "ApartmentOwnerCharacterId", character.Id + 1);
            Check((bool)Service.GetMethod("IsNonCombatRecoverySceneActive", Private)!
                .Invoke(service, [session, HealthRecoveryScene.Apartment])!, "visitor recovery remains available");
            Set(session, "NonCombatResourceSnapshot", null);
            done = await Send(service, session, 0xC599, completion);
            Check(done is { Length: 36 } && U16(done, 20) == character.MaxHp,
                "no-pet completion uses the same base carrier as no-pet entry");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("open-nanaimo-apartment-guide-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected guide fixture path.");
            Directory.Delete(target, recursive: true);
        }
        Console.WriteLine($"APARTMENT_GUIDE_LIFECYCLE_CHECKS_PASS checks={checks}");
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static object? Get(object session, string name) => Session.GetProperty(name)!.GetValue(session);
    private static void Set(object session, string name, object? value) => Session.GetProperty(name)!.SetValue(session, value);
    private static async Task<byte[]?> Send(NetworkAdapterService service, object session, ushort op, byte[] payload)
    {
        var frame = new byte[payload.Length + 8];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), op); payload.CopyTo(frame, 8);
        return await (Task<byte[]?>)Service.GetMethod("HandleNativeFrameAsync", Private)!
            .Invoke(service, [frame, op, "WorldAdapter", "guide-check", "127.0.0.1", session, CancellationToken.None])!;
    }
}
