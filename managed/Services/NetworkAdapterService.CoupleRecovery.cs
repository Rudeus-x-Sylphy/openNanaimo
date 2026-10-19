using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private bool CanUseInventoryFood(ConnectionSession session)
    {
        if (session.NativeDungeon is not null)
            return !session.NativeDungeonExitRequested && !session.NativeDungeonDeathLatched
                && (session.NativeBattleResources?.CurrentHp ?? session.Character?.CurrentHp) > 0
                && !session.NativeDungeonNextTransitionAuthorized && !session.NativeDungeonTownTransitionAuthorized
                && (IsNativeReadyRoomInventory(session) || IsNativeDungeonRecoveryActive(session)
                    || session.NativeDungeonSettlementAwaitingAction
                        && session.NativeBattleResources is { CurrentHp: > 0 });
        if (session.DungeonRoomId == 0)
            return true;
        lock (_dungeonRoomGate)
            return _dungeonRooms.TryGetValue(session.DungeonRoomId, out var room)
                && room.Members.ContainsKey(session.SessionId) && !room.HasPendingTransition
                && session.Character is { CurrentHp: > 0 } character
                && (room.Battle.State == DungeonBattleState.Prepared
                    || room.Battle.ParticipantCharacterIds.Contains(character.Id)
                        && !room.Battle.DeadCharacters.Contains(character.Id));
    }

    private bool IsCoupleFoodContext(ConnectionSession source, ConnectionSession partner)
    {
        if (!IsTrackedWorldSession(source) || !IsTrackedWorldSession(partner)
            || HasAuxiliaryInventoryContext(source) || HasAuxiliaryInventoryContext(partner)
            || source.ChannelId != partner.ChannelId || !CanUseInventoryFood(partner)
            || (partner.NativeBattleResources ?? partner.NonCombatResourceSnapshot)?.CurrentHp == 0
            || partner.Character is not { CurrentHp: > 0 })
            return false;
        if (source.DungeonRoomId > 0 || partner.DungeonRoomId > 0)
        {
            lock (_dungeonRoomGate)
                return source.DungeonRoomId > 0 && source.DungeonRoomId == partner.DungeonRoomId
                    && _dungeonRooms.TryGetValue(source.DungeonRoomId, out var room)
                    && room.Members.ContainsKey(source.SessionId) && room.Members.ContainsKey(partner.SessionId);
        }
        lock (_partyGate)
            if (source.PartyId <= 0 || source.PartyId != partner.PartyId
                || !_parties.TryGetValue(source.PartyId, out var party)
                || !party.Members.ContainsKey(source.SessionId) || !party.Members.ContainsKey(partner.SessionId))
                return false;
        if (source.NativeDungeon is not null || partner.NativeDungeon is not null)
            return source.NativeDungeon is not null && partner.NativeDungeon is not null
                && source.NativeLease is { } lease && partner.NativeLease is { } peerLease
                && lease.Port == peerLease.Port && lease.Generation == peerLease.Generation
                && (source.NativeDungeonSelectionValid && partner.NativeDungeonSelectionValid
                    ? HasSameNativeCoupleStage(source, partner)
                    : IsNativeReadyRoomInventory(source) && IsNativeReadyRoomInventory(partner));
        return source.TownSceneActive && partner.TownSceneActive;
    }

    private async Task<(DungeonQuickItemConsumeResult Result, byte[] Frames)> ConsumeCoupleFoodAsync(
        byte[] request, ConnectionSession source, uint itemCode, uint identity, CancellationToken token)
    {
        if (source.Character is null || !CanUseInventoryFood(source))
            return (DungeonQuickItemConsumeResult.Failed, []);
        ConnectionSession? partner = null;
        if (IsTrackedWorldSession(source))
        {
            var relation = await _database.GetActiveCoupleRelationAsync(source.Character.Id, token);
            if (relation is not null)
                partner = _activeWorldSessions.Values.Select(p => p.Session).FirstOrDefault(peer =>
                    peer.Character?.Id == relation.GetPartnerId(source.Character.Id) && IsCoupleFoodContext(source, peer));
        }
        if (partner?.NativeDungeon is not null)
            await CommitNativeCheckpointAsync(partner, null, token);

        DungeonQuickItemConsumeResult result;
        var members = partner is null ? new[] { source } : new[] { source, partner };
        var locks = new List<IDisposable>();
        try
        {
            foreach (var member in members.OrderBy(member => member.SessionId, StringComparer.Ordinal))
                locks.Add(await LockNativeDungeonResourcesAsync(member, token));
            if (!CanUseInventoryFood(source)
                || partner is not null && !IsCoupleFoodContext(source, partner)
                || !TryResolveSessionInventoryIdentity(source, identity, out var ordinal, out var code) || code != itemCode)
                return (DungeonQuickItemConsumeResult.Failed, []);
            var sourceResources = ResolveInventoryVitals(source.Character,
                source.NativeBattleResources ?? (source.DungeonRoomId == 0 ? source.NonCombatResourceSnapshot : null));
            var partnerResources = partner is null ? null : ResolveInventoryVitals(partner.Character!,
                partner.NativeBattleResources ?? (partner.DungeonRoomId == 0 ? partner.NonCombatResourceSnapshot : null));
            result = await _database.ConsumeInventoryFoodAsync(source.AccountId, source.Character.Id,
                source.InventorySessionId, itemCode, ordinal, token, sourceResources.CurrentHp, sourceResources.CurrentMp,
                partner is null ? null : new DungeonQuickItemTarget(partner.AccountId, partner.Character!.Id, partner.SessionId)
                {
                    AuthoritativeCurrentHp = partnerResources!.CurrentHp,
                    AuthoritativeCurrentMp = partnerResources.CurrentMp
                });
            if (!result.Success) return (result, []);
            foreach (var member in members)
            {
                var restored = result.Targets.Single(target => target.CharacterId == member.Character!.Id);
                member.Character!.CurrentHp = restored.CurrentHp;
                member.Character.CurrentMp = restored.CurrentMp;
                AdoptPersistedInventoryVitals(member);
                if (member.NativeBattleResources is { SettlementFrozen: false } resources)
                    member.NativeBattleResources = resources with { HpAuthority = BattleHpAuthority.LocalDamage };
            }
        }
        finally
        {
            for (var index = locks.Count - 1; index >= 0; index--) locks[index].Dispose();
        }
        if (partner is not null && IsCoupleFoodContext(source, partner))
        {
            // Import the changed ledger before another worker checkpoint. F108
            // remains ready-room-only; never rebuild a combat/settlement actor.
            var refresh = await SynchronizeNativeInventoryAsync(partner, token);
            if (_activeWorldSessions.TryGetValue(partner.SessionId, out var presence))
            {
                for (var offset = 0; offset < refresh.Length;)
                {
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(refresh.AsSpan(offset + 4));
                    var opcode = BinaryPrimitives.ReadUInt16LittleEndian(refresh.AsSpan(offset + 6));
                    source.PendingBroadcasts.Add(new PendingNativeBroadcast(presence, opcode,
                        refresh.AsSpan(offset + 8, length - 8).ToArray(), "couple ready-room food resources"));
                    offset += length;
                }
                if (source.NativeDungeon is null && source.DungeonRoomId == 0)
                    source.PendingBroadcasts.Add(new PendingNativeBroadcast(presence, 0xD8FF,
                        BuildSceneAutoHealingPayload(partner, partner.Character!, partner.NonCombatResourceSnapshot),
                        "couple town food absolute resources"));
            }
        }
        return (result, BuildInventoryFoodRecoveryFrames(request, source, members, result, itemCode));
    }

    private byte[] BuildInventoryFoodRecoveryFrames(byte[] request, ConnectionSession source,
        ConnectionSession[] members, DungeonQuickItemConsumeResult result, uint itemCode)
    {
        if (HasAuxiliaryInventoryContext(source)) return [];
        if (source.NativeDungeon is null && source.DungeonRoomId == 0)
            return BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)) == 0xC43D ? []
                : BuildNativeFrame(request, 0xD8FF, BuildSceneAutoHealingPayload(source, source.Character!,
                    source.NonCombatResourceSnapshot), source);
        if (IsNativeReadyRoomInventory(source))
            return []; // The source wrapper and partner import publish request-bound F108/CF72.
        var prepared = source.NativeDungeon is null && GetDungeonRoom(source)?.Battle.State == DungeonBattleState.Prepared;
        var frames = new List<byte[]>();
        foreach (var member in members)
        {
            if (member != source && !IsCoupleFoodContext(source, member)) continue;
            var target = result.Targets.Single(row => row.CharacterId == member.Character!.Id);
            var uid = member.NativeCheckpoint is { } checkpoint
                ? checked((ushort)checkpoint.Get(4)) : GetSceneEntityId(member.Character!);
            var opcode = prepared ? (ushort)0xCF72 : (ushort)0xCF94;
            var payload = prepared ? BuildFoodReadyProfile(member, uid)
                : BuildDungeonQuickItemUsePayload(uid, ushort.MaxValue, itemCode, target.HpRestored, target.MpRestored);
            frames.Add(BuildNativeFrame(request, opcode, payload, source));
            if (source.NativeDungeon is null)
                QueueDungeonBroadcast(source, opcode, payload, false, "couple inventory food resources");
            else
                foreach (var recipient in _activeWorldSessions.Values)
                    if (recipient.Session != source && IsTrackedWorldSession(recipient.Session)
                        && recipient.Session.NativeDungeon is not null && HasSameNativeCoupleStage(source, recipient.Session))
                        source.PendingBroadcasts.Add(new PendingNativeBroadcast(recipient, opcode, payload.ToArray(),
                            "couple native inventory food resources"));
        }
        return CombineNativeFrames(frames.ToArray());
    }

    private static byte[] BuildFoodReadyProfile(ConnectionSession session, ushort uid)
    {
        var character = session.Character!;
        var resources = ResolveInventoryVitals(character);
        var payload = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, uid);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), resources.MaximumHp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), resources.MaximumMp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), resources.CurrentHp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), resources.CurrentMp);
        BuildStoredAppearance(character).CopyTo(payload, 0x14 - 8);
        foreach (var slot in character.QuickSlots.Where(slot => slot.Slot <= 5))
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x38 - 8 + slot.Slot * 4), slot.ItemCode);
        var expanded = SkillSlotExpansionTime.TryDecode(character.QuickSlotExpansionExpires, out var expiry) && expiry > DateTime.Now;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x56 - 8), expanded ? (ushort)1 : (ushort)0);
        var pet = PetProgression.GetState(character, GetEquippedPetItemCode(character));
        payload[0x66 - 8] = pet.CurrentStage;
        payload[0x67 - 8] = pet.MaximumStage;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x6C - 8), GetPetBattleDurability(character));
        payload[0x72 - 8] = 1;
        payload[0x73 - 8] = character.RevivalUseCount;
        return payload; // apply=0; ready-room-only because even apply0 rebuilds the six slots.
    }
}
