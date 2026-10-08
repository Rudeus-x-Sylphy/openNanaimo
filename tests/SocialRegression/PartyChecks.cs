using System.Buffers.Binary;
using System.Reflection;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckPartyAsync(Fixture fixture)
    {
        Broadcasts(fixture.First).Clear();
        Character(fixture.First).Level = 37;
        Character(fixture.First).Experience = CharacterProgression.ExperienceRequiredForLevel(37);
        Character(fixture.First).DungeonGrade = 8;
        Character(fixture.Second).Level = 12;
        Character(fixture.Second).Experience = CharacterProgression.ExperienceRequiredForLevel(12);
        Character(fixture.Second).DungeonGrade = 3;
        var invitation = new byte[24]; invitation[20] = 99;
        PrivateChatProtocol.WriteText(invitation.AsSpan(0, 16), "Forged");
        var ownerUid = WireIdentityAllocator.GetSceneEntityId(Character(fixture.First).Id);
        BinaryPrimitives.WriteUInt16LittleEndian(invitation.AsSpan(16), ownerUid);
        BinaryPrimitives.WriteUInt16LittleEndian(invitation.AsSpan(22), WireIdentityAllocator.GetSceneEntityId(Character(fixture.Second).Id));
        Check(await Dispatch(fixture, fixture.First, 0xC4E0, invitation) is null && Broadcasts(fixture.First).Count == 1,
            "party invitation reaches the visible target");
        var projected = (byte[])Get(Broadcasts(fixture.First)[0]!, "Payload")!;
        Check(PrivateChatProtocol.TryReadText(projected.AsSpan(0, 16), out var name) && name == "Alice", "party inviter name is authenticated");
        var answer = new byte[4]; answer[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(answer.AsSpan(2), ownerUid);
        Check(await Dispatch(fixture, fixture.Third, 0xC4E1, answer) is null, "party acceptance belongs to the invitee");
        var accepted = await Dispatch(fixture, fixture.Second, 0xC4E1, answer);
        Check(accepted is not null && (int)Get(fixture.First, "PartyId")! > 0
            && Equals(Get(fixture.First, "PartyId"), Get(fixture.Second, "PartyId")), "party acceptance binds both members");
        Check(accepted![27] == 37 && accepted[28] == 8 && accepted[50] == 12 && accepted[51] == 3,
            "party union projects independent levels and titles in the native fields");
        Check(accepted[29] == Character(fixture.First).CurrentMapId,
            "invitation level cannot become the party leader map");
        Broadcasts(fixture.First).Clear();
        Invoke<object?>(fixture.Service, "QueuePartyStatusUpdate", fixture.First);
        var status = (byte[])Get(Broadcasts(fixture.First)[0]!, "Payload")!;
        Check(status[6] == 37 && status[7] == 8, "party status updates carry level and title independently");
        Invoke<object?>(fixture.Service, "QueuePartyStatusUpdate", fixture.First);
        Check(Broadcasts(fixture.First).Count == 1, "unchanged party status is published once");
        await using (var worker = new NativeDungeonClient(_ => Task.CompletedTask))
        await using (var pool = new NativeDungeonPool("unused", fixture.Root))
        {
            var lease = new NativeDungeonPool.Lease(pool, "test", 60001, Guid.NewGuid());
            Set(fixture.First, "NativeDungeon", worker); Set(fixture.First, "NativeLease", lease);
            Set(fixture.First, "NativeDungeonSelectionValid", true);
            Set(fixture.First, "NativeContinuationRosterRequested", true);
            var map = NativeDungeonClient.Frame(0xCFEB, new byte[4]);
            Check(Invoke<bool>(fixture.Service, "DeferNativePartyMap", fixture.First, map),
                "owner map waits for the party member transport");
            Set(fixture.Second, "NativeDungeon", worker); Set(fixture.Second, "NativeLease", lease);
            Check(Invoke<bool>(fixture.Service, "DeferNativePartyMap", fixture.First, map),
                "owner map waits for the party member ready roster");
            Set(fixture.Second, "NativeContinuationRosterRequested", true);
            Check(!Invoke<bool>(fixture.Service, "DeferNativePartyMap", fixture.First, map),
                "both owned ready rosters release the common map");
            var reloadTable = typeof(NetworkAdapterService).GetField("_nativeContinuationRooms", PrivateInstance)!.GetValue(fixture.Service)!;
            var reloadType = typeof(NetworkAdapterService).GetNestedType("NativeContinuationReload", BindingFlags.NonPublic)!;
            foreach (var peer in new[] { fixture.First, fixture.Second })
            {
                Set(peer, "NativeContinuationRosterRequested", false);
                var state = reloadTable.GetType().GetMethod("GetOrCreateValue")!.Invoke(reloadTable, [peer])!;
                var reload = Activator.CreateInstance(reloadType, [(long)Get(peer, "NativeBattleEpoch")!, (byte)0, (byte)0]);
                state.GetType().GetField("Reload")!.SetValue(state, reload);
            }
            Check(!Invoke<bool>(fixture.Service, "DeferNativePartyMap", fixture.First, map),
                "authorized continuation releases both members without a second ready-room roster");
            foreach (var peer in new[] { fixture.First, fixture.Second })
            {
                Set(peer, "NativeContinuationRosterRequested", true);
                Invoke<object?>(fixture.Service, "ResetNativeDungeonContinuationRoom", peer);
            }
            Set(fixture.First, "NativeCheckpoint", NativeDungeonState.Create(Character(fixture.First), [], []));
            Set(fixture.Second, "NativeCheckpoint", NativeDungeonState.Create(Character(fixture.Second), [], []));
            Set(fixture.First, "NativeDungeonEpisode", (byte)2);
            await Invoke<Task>(fixture.Service, "HandleNativeWorkerFrameAsync", fixture.Second,
                NativeDungeonClient.Frame(0xCFEC, new byte[800]), (long)Get(fixture.Second, "NativeBattleEpoch")!, Token);
            Check((bool)Get(fixture.Second, "NativeDungeonSelectionValid")!
                && (byte)Get(fixture.Second, "NativeDungeonEpisode")! == 2,
                "common map binds a member whose roster preceded the owner selection");
            foreach (var session in new[] { fixture.First, fixture.Second })
            {
                Set(session, "NativeDungeon", null); Set(session, "NativeLease", null);
                Set(session, "NativeDungeonSelectionValid", false);
                Set(session, "NativeDungeonEpisode", (byte)0);
                Set(session, "NativeCheckpoint", null);
                Set(session, "NativeContinuationRosterRequested", false);
            }
        }
        Check(await Dispatch(fixture, fixture.Second, 0xC4E1, answer) is null, "party agreement idempotence");
        Check(await Dispatch(fixture, fixture.First, 0xC4E7, []) is not null, "party owner departure resolves member ownership");
        await Dispatch(fixture, fixture.Second, 0xC4E7, []);
        Check((int)Get(fixture.First, "PartyId")! == 0 && (int)Get(fixture.Second, "PartyId")! == 0, "party departure releases both memberships");
    }

    private static async Task CheckManagedCouplesAsync(Fixture fixture)
    {
        var creation = new byte[44];
        BinaryPrimitives.WriteUInt16LittleEndian(creation.AsSpan(24), 100);
        Check(await Dispatch(fixture, fixture.First, 0xCF6C, creation) is not null, "managed dungeon creation is request-driven");
        var room = Invoke<object>(fixture.Service, "GetDungeonRoom", fixture.First);
        var enter = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(enter.AsSpan(2), checked((ushort)(int)Get(room, "Id")!));
        Check(await Dispatch(fixture, fixture.Second, 0xCF75, enter) is not null
            && Equals(Get(fixture.First, "DungeonRoomId"), Get(fixture.Second, "DungeonRoomId")), "managed dungeon members share their selected room");
        Check(await Dispatch(fixture, fixture.First, 0xCF7F, []) is null, "dungeon start waits for member readiness");
        Check(await Dispatch(fixture, fixture.Second, 0xCF7D, new byte[] { 1, 0, 0, 0 }) is not null, "dungeon readiness is acknowledged");
        Broadcasts(fixture.First).Clear();
        var start = await Dispatch(fixture, fixture.First, 0xCF7F, []);
        Check(start is { Length: 20 }
            && BinaryPrimitives.ReadUInt16LittleEndian(start.AsSpan(6)) == 0xC588
            && BinaryPrimitives.ReadUInt16LittleEndian(start.AsSpan(8)) == WireIdentityAllocator.GetSceneEntityId(Character(fixture.Second).Id)
            && BinaryPrimitives.ReadUInt16LittleEndian(start.AsSpan(18)) == 0xCF80, "managed start publishes the partner scene identity before its final acknowledgment");
        var preload = CoupleNoticesFor(fixture.First, fixture.Second);
        Check(preload.All(item => (ushort)Get(item, "Opcode")! is not (0xCF80 or 0xC588)),
            "member map preparation cannot start combat or publish identity before its CF7F");
        var preloadData = (byte[])Get(preload.Single(item => (ushort)Get(item, "Opcode")! == 0xCFEC), "Payload")!;
        Check(preloadData.Length == 800 && preloadData[0x325 - 8] == 1,
            "member loads the couple encounter gate before its final start acknowledgment");
        var profile = await Dispatch(fixture, fixture.First, 0xCFEB, new byte[4]);
        Check(profile is { Length: 808 } && profile[0x325] == 1, "managed dungeon couple special encounter requires current participants");
        Set(fixture.First, "ResponseTransportSequence", (byte)0);
        Invoke<object?>(fixture.Service, "FinalizeNativeFramesForSend", start!, fixture.First);
        Check(((BinaryPrimitives.ReadUInt16LittleEndian(start) >> 5) & 127) == 0
            && ((BinaryPrimitives.ReadUInt16LittleEndian(start.AsSpan(12)) >> 5) & 127) == 1,
            "managed couple start preserves ordered transport sequence");
        var memberStart = await Dispatch(fixture, fixture.Second, 0xCF7F, []);
        Check(memberStart is { Length: 20 }
            && BinaryPrimitives.ReadUInt16LittleEndian(memberStart.AsSpan(8)) == WireIdentityAllocator.GetSceneEntityId(Character(fixture.First).Id),
            "managed member start publishes the owner's partner scene identity");
        Check(await Dispatch(fixture, fixture.Second, 0xCF7F, []) is { Length: 8 }, "managed partner identity is published once per battle");
        var battle = Get(room, "Battle")!;
        var participantIds = (HashSet<long>)Get(battle, "ParticipantCharacterIds")!;
        participantIds.Remove(Character(fixture.Second).Id);
        var nonParticipantProfile = await Dispatch(fixture, fixture.First, 0xCFEB, new byte[4]);
        Check(nonParticipantProfile is { Length: 808 } && nonParticipantProfile[0x325] == 0,
            "managed special encounter excludes a non-participant room member");
        ((Dictionary<long, int>)Get(battle, "HitScores")!)[Character(fixture.First).Id] = 400;
        var ordinaryReward = new DungeonSettlementReward(5, 100, 80, 250);
        var nonParticipantReward = await Invoke<Task<DungeonSettlementReward>>(fixture.Service, "ApplyManagedCoupleRewardAsync",
            fixture.First, battle, ordinaryReward, true, Token);
        Check(nonParticipantReward == ordinaryReward, "managed couple benefit excludes a non-participant room member");
        participantIds.Add(Character(fixture.Second).Id);
        var restoredProfile = await Dispatch(fixture, fixture.First, 0xCFEB, new byte[4]);
        Check(restoredProfile is { Length: 808 } && restoredProfile[0x325] == 1,
            "managed special encounter restores when both participants are present");
        var reward = new DungeonSettlementReward(5, 100, 80, 250);
        var improved = await Invoke<Task<DungeonSettlementReward>>(fixture.Service, "ApplyManagedCoupleRewardAsync",
            fixture.First, battle, reward, true, Token);
        Check(improved.CharacterExperience == 120 && improved.RelationshipBonusScore == 80 && improved.PetExperience == 80 && improved.Hans == 250,
            "managed couple reward adds bonus score before the quarter-score conversion");
        var failed = await Invoke<Task<DungeonSettlementReward>>(fixture.Service, "ApplyManagedCoupleRewardAsync",
            fixture.First, battle, reward, false, Token);
        Check(failed == reward, "managed failed settlement retains ordinary reward policy");
        await Dispatch(fixture, fixture.Second, 0xCF1D, []);
        await Dispatch(fixture, fixture.First, 0xCF1D, []);
        Check((int)Get(fixture.First, "DungeonRoomId")! == 0 && (int)Get(fixture.Second, "DungeonRoomId")! == 0,
            "managed dungeon disconnection releases both room contexts");
    }
}
