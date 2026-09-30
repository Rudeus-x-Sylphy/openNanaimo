using System.Buffers.Binary;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckPartyAsync(Fixture fixture)
    {
        Broadcasts(fixture.First).Clear();
        var invitation = new byte[24];
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
        Check(improved.CharacterExperience == 120 && improved.PetExperience == 80 && improved.Hans == 250,
            "managed couple reward changes only character experience");
        var failed = await Invoke<Task<DungeonSettlementReward>>(fixture.Service, "ApplyManagedCoupleRewardAsync",
            fixture.First, battle, reward, false, Token);
        Check(failed == reward, "managed failed settlement retains ordinary reward policy");
        await Dispatch(fixture, fixture.Second, 0xCF1D, []);
        await Dispatch(fixture, fixture.First, 0xCF1D, []);
        Check((int)Get(fixture.First, "DungeonRoomId")! == 0 && (int)Get(fixture.Second, "DungeonRoomId")! == 0,
            "managed dungeon disconnection releases both room contexts");
    }
}
