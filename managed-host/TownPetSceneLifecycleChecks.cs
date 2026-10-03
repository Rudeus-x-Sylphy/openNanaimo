using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

// Dispatcher/packet-construction checks only; not original-client rendering acceptance.
internal static class TownPetSceneLifecycleChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ServiceType = typeof(NetworkAdapterService);
    private static readonly Type SessionType = ServiceType.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly MethodInfo Dispatch = ServiceType.GetMethod("HandleNativeFrameAsync", Private)!;
    private static int _checks;

    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "open-nanaimo-town-pet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("town-pet-check");
            await db.CreateLocalCharacterAsync(account, "PetScene", 1);
            var character = (await db.GetCharacterAsync(account))!;
            character.TutorialCompleted = true;
            character.Appearance = new byte[36];
            character.EquippedPetItemCode = 15000001;
            character.PetVariant = 1;
            character.Items.Add(new CharacterItemRecord { ItemCode = 15000002, Quantity = 1, PetCurrentStage = 2, PetMaximumStage = 3 });
            await using var service = new NetworkAdapterService(db, _ => { }, root);
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            Set(session, "AccountId", account); Set(session, "Character", character);
            Set(session, "OnlineTracked", true); Set(session, "ChannelId", 1);
            Set(session, "TownId", (byte)4); Set(session, "TownPage", (byte)18);
            var id = (string)Get(session, "SessionId")!;
            Check(await db.BeginWorldSessionAsync(account, character.Id, id, 1, "127.0.0.1"), "fixture owned online session");
            var presences = ServiceType.GetField("_activeWorldSessions", Private)!.GetValue(service)!;
            var presenceType = ServiceType.GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
            var presence = presenceType.GetConstructors().Single().Invoke([session, id, account, character.Id,
                "town-pet-check", character.Name, "127.0.0.1", 1, DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })]);
            presences.GetType().GetMethod("TryAdd")!.Invoke(presences, [id, presence]);
            try
            {
                async Task<byte[]?> Send(ushort opcode, byte[] payload)
                {
                    var frame = NativeDungeonClient.Frame(opcode, payload);
                    return await (Task<byte[]?>)Dispatch.Invoke(service,
                        [frame, opcode, "WorldAdapter", "pet-check", "127.0.0.1", session, CancellationToken.None])!;
                }
                async Task Enter(byte page = 18)
                {
                    var result = await Send(0xC367, Page(page));
                    Check(result is { Length: 60 } && U16(result, 6) == 0xC368, "C367 returns only native C368/60");
                    Check(Active(session) && !Armed(session), "selector4 entry completes visibility before movement");
                    Check(Pets(session).Length == (character.EquippedPetItemCode == 0 ? 0 : 1), "entry queues one equipped pet attachment after its response");
                }
                async Task Activity() => Check(await Send(0xCB21, Move()) is null, "CB21 stays one-way");

                await Activity();
                Check(!Active(session) && Pets(session).Length == 0, "ambient activity before C367 cannot create scene");
                await Enter();
                await Send(0xCB21, new byte[15]);
                Check(!Armed(session) && Active(session), "malformed movement preserves completed entry");
                await Activity();
                Check(Active(session) && !Armed(session), "first selector4 activity completes scene without C36C");
                var pet = Pets(session).Single();
                var carrier = (byte[])Get(pet, "Payload")!;
                Check(carrier.SequenceEqual(NetworkAdapterService.BuildUserDataChangePayload(character))
                    && carrier.Length == 44 && U32(carrier, 36) == 15000001,
                    "native C47F/52 full UID, stage, experience and appearance preserved");
                Check(ReferenceEquals(Get(pet, "Target"), presence), "attachment targets owning session, not a peer");
                var schedule = (HealthRecoverySchedule)Get(session, "HealthRecovery")!;
                Check(schedule.TryGetActiveScene(out var scene) && scene == HealthRecoveryScene.Town,
                    "missing C36C also initializes town recovery rather than leaving partial scene");
                await Activity(); await Send(0xC36C, []);
                Check(Pets(session).Length == 1, "repeated CB21 and late C36C cannot recreate PET");

                Pending(session).Clear();
                await Enter(19); await Send(0xC36C, []); await Activity();
                Check(Pets(session).Length == 1 && !Armed(session), "C36C-first ordering emits one attachment");

                Pending(session).Clear();
                character.EquippedPetItemCode = 15000002;
                await Enter();
                await Activity();
                Check(U32((byte[])Get(Pets(session).Single(), "Payload")!, 36) == 15000002,
                    "loading-time selection change uses current PET, not stale snapshot");
                Pending(session).Clear(); character.EquippedPetItemCode = 0;
                await Enter();
                await Activity();
                Check(Active(session) && Pets(session).Length == 0, "unequipped PET is not resurrected");
                character.EquippedPetItemCode = 15000001;

                foreach (var reason in new[] { "town change", "apartment enter", "village shop enter", "trade-room enter", "dungeon connection", "town connection leave" })
                {
                    Pending(session).Clear(); await Enter();
                    ServiceType.GetMethod("LeaveTownScene", Private)!.Invoke(service, [session, reason]);
                    Pending(session).Clear();
                    await Activity();
                    Check(!Armed(session) && !Active(session) && Pets(session).Length == 0,
                        "unfinished page is cancelled at " + reason);
                }

                Pending(session).Clear(); await Enter();
                byte[] c365 = new byte[10]; c365[0] = 4; // same-village mode0: no fare
                var c366 = await Send(0xC365, c365);
                Pending(session).Clear();
                await Activity();
                Check(c366 is { Length: 12 } && U16(c366, 6) == 0xC366
                    && !Armed(session) && !Active(session) && Pets(session).Length == 0,
                    "real C365 teardown cancels incomplete scene and emits no actor/PET");

                Pending(session).Clear(); Set(session, "TownId", (byte)0);
                await Send(0xC367, Page(0)); await Activity();
                Check(!Armed(session) && !Active(session) && Pets(session).Length == 0,
                    "ordinary village still waits for C36C");
                await Send(0xC36C, []);
                Check(Active(session) && Pets(session).Length == 1, "ordinary village completion unchanged");

                Pending(session).Clear(); Set(session, "TownId", (byte)4); await Enter();
                var unrelated = Activator.CreateInstance(SessionType, nonPublic: true)!;
                Set(unrelated, "OnlineTracked", true); Set(unrelated, "Character", character); Set(unrelated, "TownId", (byte)4);
                await ServiceType.GetMethod("CompleteTownPetSceneOnActivityAsync", Private)!
                    .InvokeAsync(service, unrelated);
                Check(!Armed(session) && !Armed(unrelated) && Pets(unrelated).Length == 0,
                    "page gate cannot leak to a new connection/session");
                // New C367 replaces the pending page rather than queuing an old attachment.
                Pending(session).Clear(); await Enter(20); await Activity();
                Check(Pets(session).Length == 1 && (byte)Get(session, "TownPage")! == 20,
                    "rapid C367/C367/CB21 attaches once to latest page");
                Pending(session).Clear(); await Enter(20); Pending(session).Clear(); await Enter(20);
                await Send(0xC367, new byte[7]);
                byte[] invalidPage = Page(0); BinaryPrimitives.WriteInt32LittleEndian(invalidPage, 256);
                await Send(0xC367, invalidPage);
                Check(Active(session) && !Armed(session) && (byte)Get(session, "TownPage")! == 20,
                    "invalid/truncated C367 cannot replace an armed page");
                byte[] sentinelActivity = Move();
                BinaryPrimitives.WriteUInt16LittleEndian(sentinelActivity.AsSpan(8), ushort.MaxValue);
                BinaryPrimitives.WriteUInt16LittleEndian(sentinelActivity.AsSpan(10), ushort.MaxValue);
                await Send(0xCB21, sentinelActivity);
                Check(Pets(session).Length == 1 && !Armed(session)
                    && character.PositionX == 400 && character.PositionY == 96,
                    "duplicate same-page C367 has one pending attachment; activity sentinel cannot poison position");
                Pending(session).Clear(); await Enter(20); await Activity();
                Check(Pets(session).Length == 1,
                    "C367 after completion reconstructs page and permits one fresh attachment");
                Console.WriteLine($"TOWN_PET_SCENE_CHECKS_PASS checks={_checks} construction-only");
            }
            finally { presences.GetType().GetMethod("Clear")!.Invoke(presences, null); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("open-nanaimo-town-pet-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected PET test directory");
            Directory.Delete(full, recursive: true);
        }
    }
    private static async Task InvokeAsync(this MethodInfo method, object service, object session)
        => await (Task)method.Invoke(service, [session, CancellationToken.None])!;
    private static object? Get(object instance, string name) => instance.GetType().GetProperty(name)!.GetValue(instance);
    private static void Set(object instance, string name, object value) => instance.GetType().GetProperty(name)!.SetValue(instance, value);
    private static IList Pending(object session) => (IList)Get(session, "PendingBroadcasts")!;
    private static object[] Pets(object session) => Pending(session).Cast<object>().Where(p => (ushort)Get(p, "Opcode")! == 0xC47F).ToArray();
    private static bool Armed(object session) => (bool)Get(session, "TownPetSceneCompletionPending")!;
    private static bool Active(object session) => (bool)Get(session, "TownSceneActive")!;
    private static ushort U16(byte[] p, int off) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off, 2));
    private static uint U32(byte[] p, int off) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(off, 4));
    private static byte[] Page(byte page)
    { byte[] p = new byte[8]; p[0] = page; BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), 400); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), 96); return p; }
    private static byte[] Move()
    { byte[] p = new byte[16]; BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), 400); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(10), 96); return p; }
    private static void Check(bool valid, string name)
    { ++_checks; if (!valid) throw new InvalidDataException("TOWN_PET_SCENE_CHECK_FAILED " + name); Console.WriteLine("TOWN_PET_SCENE_CHECK_PASS " + name); }
}
