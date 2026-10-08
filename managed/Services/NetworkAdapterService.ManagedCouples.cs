using OpenNanaimo.Adapter.Models;
using System.Runtime.CompilerServices;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private readonly ConditionalWeakTable<ConnectionSession, DungeonBattleInstance> _managedCoupleIdentityPhases = new();

    private ConnectionSession? FindManagedDungeonPartner(ConnectionSession session, CoupleRelationRecord relation)
    {
        if (session.Character is null || session.DungeonRoomId == 0
            || !_activeWorldSessions.TryGetValue(session.SessionId, out var owner)
            || !IsCurrentCouplePresence(owner)) return null;
        var partnerId = relation.GetPartnerId(session.Character.Id);
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(session.DungeonRoomId, out var room)
                || !room.Members.ContainsKey(session.SessionId)) return null;
            return room.Members.Values.Select(member => member.Session).FirstOrDefault(peer =>
                peer.Character?.Id == partnerId && peer.DungeonRoomId == room.Id
                && _activeWorldSessions.TryGetValue(peer.SessionId, out var presence)
                && IsCurrentCouplePresence(presence));
        }
    }

    private async Task PatchManagedCoupleGameDataAsync(ConnectionSession session, byte[] payload, CancellationToken token)
    {
        if (payload.Length != 800 || session.Character is null) return;
        payload[CoupleBenefitPolicy.SpecialMonsterFrameOffset - 8] = 0;
        var room = GetDungeonRoom(session);
        var battle = room?.Battle;
        if (room is null || battle is null || !IsTrackedWorldSession(session)) return;
        ConnectionSession[] members;
        lock (_dungeonRoomGate)
            members = room.Members.Values.Select(member => member.Session).ToArray();
        // Map preparation precedes the combat participant snapshot. Encounters
        // belong to the shared room, including its unpaired third member.
        foreach (var member in members)
        {
            if (member.Character is null || !IsTrackedWorldSession(member)) continue;
            var relation = await _database.GetActiveCoupleRelationAsync(member.Character.Id, token);
            if (relation is null || !CoupleBenefitPolicy.IsRingItemCode(relation.RingItemCode)) continue;
            var partner = FindManagedDungeonPartner(member, relation);
            if (partner?.Character is not { } partnerCharacter) continue;
            lock (_dungeonRoomGate)
            {
                if (!ReferenceEquals(GetDungeonRoom(session), room) || !ReferenceEquals(room.Battle, battle)
                    || !room.Members.ContainsKey(session.SessionId)
                    || !room.Members.ContainsKey(member.SessionId)
                    || !room.Members.ContainsKey(partner.SessionId)) return;
                if (battle.State == DungeonBattleState.Prepared
                    || battle.ParticipantCharacterIds.Contains(member.Character.Id)
                        && battle.ParticipantCharacterIds.Contains(partnerCharacter.Id))
                {
                    payload[CoupleBenefitPolicy.SpecialMonsterFrameOffset - 8] = 1;
                    return;
                }
            }
        }
    }

    private async Task<ConnectionSession[]> IncludeManagedCoupleRecoveryAsync(
        ConnectionSession source, DungeonBattleInstance? battle, ShopCatalogItem? item,
        ConnectionSession[] targets, CancellationToken token)
    {
        if (targets.Length == 0 || battle is null || item?.Category != 14
            || source.Character is null) return targets;
        lock (_dungeonRoomGate)
        {
            if (battle.State != DungeonBattleState.Active
                || !ReferenceEquals(GetDungeonRoom(source)?.Battle, battle)
                || !IsLiveManagedCoupleParticipant(source, battle)) return [];
            targets = targets.Where(target => IsLiveManagedCoupleParticipant(target, battle)).ToArray();
        }
        if (targets.Length == 0) return targets;
        var relation = await _database.GetActiveCoupleRelationAsync(source.Character.Id, token);
        if (relation is null || !AreCurrentManagedCoupleParticipants(source, relation, battle)) return targets;
        var partner = FindManagedDungeonPartner(source, relation);
        lock (_dungeonRoomGate)
        {
            if (partner?.Character is null || battle.State != DungeonBattleState.Active
                || !ReferenceEquals(GetDungeonRoom(source)?.Battle, battle)
                || !IsLiveManagedCoupleParticipant(source, battle)
                || !IsLiveManagedCoupleParticipant(partner, battle)
                || targets.Contains(partner)) return targets;
            return targets.Append(partner).ToArray();
        }
    }

    // Caller holds the room gate. Preparation, combat membership and life are
    // independent boundaries; sharing requires all three to remain current.
    private bool IsLiveManagedCoupleParticipant(ConnectionSession session, DungeonBattleInstance battle)
        => session.Character is { CurrentHp: > 0 } character
            && IsTrackedWorldSession(session)
            && ReferenceEquals(GetDungeonRoom(session)?.Battle, battle)
            && battle.ParticipantCharacterIds.Contains(character.Id)
            && !battle.DeadCharacters.Contains(character.Id);

    private async Task<byte[]> BuildManagedDungeonStartResponseAsync(byte[] request, ConnectionSession session, CancellationToken token)
    {
        var battle = GetDungeonRoom(session)?.Battle;
        if (battle is null || _managedCoupleIdentityPhases.TryGetValue(session, out var published) && published == battle)
            return BuildNativeFrame(request, 0xCF80, [], session);
        var relation = session.Character is null ? null
            : await _database.GetActiveCoupleRelationAsync(session.Character.Id, token);
        var partner = relation is not null && AreCurrentManagedCoupleParticipants(session, relation, battle)
            ? FindManagedDungeonPartner(session, relation) : null;
        _managedCoupleIdentityPhases.Remove(session);
        _managedCoupleIdentityPhases.Add(session, battle);
        return CombineNativeFrames(BuildNativeFrame(request, 0xC588,
            CoupleBenefitPolicy.BuildPartnerIdentity(partner?.Character is { } character
                ? WireIdentityAllocator.GetSceneEntityId(character.Id) : (ushort)0), session),
            BuildNativeFrame(request, 0xCF80, [], session));
    }

    private async Task<DungeonSettlementReward> ApplyManagedCoupleRewardAsync(
        ConnectionSession session, DungeonBattleInstance battle, DungeonSettlementReward reward, bool cleared, CancellationToken token)
    {
        if (session.Character is null) return reward;
        var id = session.Character.Id;
        var score = checked((uint)Math.Max(0, battle.HitScores.GetValueOrDefault(id)
            + battle.BossBonusScores.GetValueOrDefault(id)));
        var relation = await _database.GetActiveCoupleRelationAsync(id, token);
        var eligible = cleared && relation is not null
            && AreCurrentManagedCoupleParticipants(session, relation, battle)
            && !battle.DeadCharacters.Contains(id);
        var total = await ScaleSettlementScoreAsync(session, score,
            relation?.RingItemCode ?? 0, eligible, token);
        return reward with
        {
            CharacterExperience = checked((int)(total / 4u)),
            RelationshipBonusScore = checked((int)(total > score ? total - score : 0))
        };
    }

    private bool AreCurrentManagedCoupleParticipants(
        ConnectionSession session,
        CoupleRelationRecord relation,
        DungeonBattleInstance battle)
    {
        if (session.Character is null
            || !CoupleBenefitPolicy.TryResolveRingEffect(relation.RingItemCode, out _))
            return false;

        var partner = FindManagedDungeonPartner(session, relation);
        if (partner?.Character is not { } partnerCharacter)
            return false;

        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(session.DungeonRoomId, out var room)
                || !ReferenceEquals(room.Battle, battle)
                || !room.Members.ContainsKey(session.SessionId)
                || !room.Members.ContainsKey(partner.SessionId))
                return false;

            return battle.ParticipantCharacterIds.Contains(session.Character.Id)
                && battle.ParticipantCharacterIds.Contains(partnerCharacter.Id);
        }
    }
}
