using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private readonly ConditionalWeakTable<byte[], object> _coupleExperienceFrames = new();

    private async Task<byte[]?> HandleNativeFriendAsync(
        byte[] frame, ushort opcode, byte[] payload, ConnectionSession session, CancellationToken token)
    {
        if (!IsTrackedWorldSession(session) || session.Character is not { } character) return null;
        if (opcode == 0xC5AC)
        {
            if (payload.Length != 0) return null;
            var contacts = await _database.GetNativeFriendContactsAsync(character.Id, token);
            var result = new byte[4 + contacts.Count * 4];
            BinaryPrimitives.WriteUInt16LittleEndian(result, checked((ushort)contacts.Count));
            for (var index = 0; index < contacts.Count; index++)
                WriteNativeFriendPresence(result.AsSpan(4 + index * 4, 4), contacts[index]);
            return BuildNativeFrame(frame, 0xC5AD, result, session);
        }
        if (payload.Length != 20) return null;
        var operation = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (operation != 1
            || !PrivateChatProtocol.TryReadText(payload.AsSpan(4, 16), out var peerName)) return null;
        var changed = await _database.ApplyNativeFriendCommandAsync(
            session.AccountId, session.Character.Id, session.SessionId, operation, peerName, token);
        if (!changed.Success)
            return BuildNativeFrame(frame, 0xC5AF, BuildNativeFriendResult(operation, null), session);

        var peer = await _database.GetCharacterByIdAsync(changed.PeerId, token);
        if (peer is not null && changed.Added)
        {
            RaiseFriendStateChanged();
            if (operation == 1)
            {
                var presence = _activeWorldSessions.Values.FirstOrDefault(item =>
                    item.CharacterId == peer.Id && IsTrackedWorldSession(item.Session));
                if (presence is not null)
                    session.PendingBroadcasts.Add(new PendingNativeBroadcast(presence, 0xCB25,
                        PrivateChatProtocol.BuildMessage("系统通知", $"{session.Character.Name}已加你为好友"),
                        "friend contact added"));
            }
        }
        // A repeated or reciprocal addition still acknowledges the requested peer.
        // Its operation is independent of the number of persisted contacts.
        return BuildNativeFrame(frame, 0xC5AF, BuildNativeFriendResult(operation, peer), session);
    }

    private byte[] BuildNativeFriendResult(ushort operation, CharacterRecord? peer)
    {
        var payload = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, peer is null ? (ushort)0 : (ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), operation);
        if (peer is not null)
        {
            PrivateChatProtocol.WriteText(payload.AsSpan(4, 16), peer.Name);
            WriteNativeFriendPresence(payload.AsSpan(20, 4), peer);
        }
        return payload;
    }

    private void WriteNativeFriendPresence(Span<byte> field, CharacterRecord character)
    {
        field.Clear();
        var presence = _activeWorldSessions.Values.FirstOrDefault(item =>
            item.CharacterId == character.Id && IsTrackedWorldSession(item.Session));
        if (presence is null) return;
        field[0] = 1;
        field[1] = presence.Session.TownId;
        field[2] = presence.Session.TownPage;
        field[3] = checked((byte)Math.Clamp(presence.Session.ChannelId, 0, byte.MaxValue));
    }

    private async Task<byte[]?> HandlePrivateChatAsync(
        byte[] frame, byte[] payload, ConnectionSession session, CancellationToken token)
    {
        if (!IsTrackedWorldSession(session) || session.Character is not { } character
            || !PrivateChatProtocol.TryReadRequest(payload, out var peerName, out var text))
            return null;
        var peer = _activeWorldSessions.Values.FirstOrDefault(presence =>
            IsTrackedWorldSession(presence.Session)
            && presence.Session.Character is not null
            && string.Equals(presence.CharacterName, peerName, StringComparison.Ordinal));
        if (peer is null)
            return BuildNativeFrame(frame, 0xCB25,
                PrivateChatProtocol.BuildMessage("系统通知", "对方当前离线。"), session);
        if (await _database.IsPrivateChatBlockedAsync(character.Id, peer.CharacterId, token))
            return null;
        session.PendingBroadcasts.Add(new PendingNativeBroadcast(peer, 0xCB25,
            PrivateChatProtocol.BuildMessage(character.Name, text), "private chat"));
        return null;
    }

    private async Task<uint> GetCoupleRingAsync(ConnectionSession session, CancellationToken token)
        => session.Character is null ? 0
            : CoupleBenefitPolicy.NormalizeRingItemCode(
                (await _database.GetActiveCoupleRelationAsync(session.Character.Id, token))?.RingItemCode ?? 0);

    private WorldPresence? FindNativeDungeonPartner(ConnectionSession session, CoupleRelationRecord relation)
    {
        if (!IsTrackedWorldSession(session) || session.Character is null
            || session.NativeDungeon is null || session.NativeLease is null
            || !CoupleBenefitPolicy.TryResolveRingEffect(relation.RingItemCode, out _))
            return null;
        var partnerId = relation.GetPartnerId(session.Character.Id);
        return _activeWorldSessions.Values.FirstOrDefault(presence =>
            presence.CharacterId == partnerId && IsTrackedWorldSession(presence.Session)
            && presence.Session.NativeDungeon is not null
            && presence.Session.NativeLease?.Port == session.NativeLease.Port
            && presence.Session.NativeLease?.Generation == session.NativeLease.Generation
            && presence.Session.NativeDungeonSelectionValid && session.NativeDungeonSelectionValid
            && presence.Session.NativeDungeonHdIndex == session.NativeDungeonHdIndex
            && presence.Session.NativeDungeonEpisode == session.NativeDungeonEpisode
            && presence.Session.NativeDungeonDungeon == session.NativeDungeonDungeon
            && presence.Session.NativeDungeonStage == session.NativeDungeonStage
            && presence.Session.NativeDungeonLogicalDifficulty == session.NativeDungeonLogicalDifficulty);
    }

    private Task RefreshCoupleBenefitsAsync(ConnectionSession session, CancellationToken token)
        => RefreshCoupleBenefitsCoreAsync(session, token, false);

    private async Task RefreshCoupleBenefitsCoreAsync(ConnectionSession session, CancellationToken token, bool force)
    {
        if (session.NativeDungeon is null || session.NativeCheckpoint is null)
            return;
        var relation = session.Character is null ? null
            : await _database.GetActiveCoupleRelationAsync(session.Character.Id, token);
        var ring = CoupleBenefitPolicy.NormalizeRingItemCode(relation?.RingItemCode ?? 0);
        var partner = relation is null ? null : FindNativeDungeonPartner(session, relation);
        var partnerUid = IsNativeDungeonRecoveryActive(session)
            && partner is not null && IsNativeDungeonRecoveryActive(partner.Session)
            && partner.Session.NativeCheckpoint is { } partnerState
                ? checked((ushort)partnerState.Get(4)) : (ushort)0;
        if (!force && session.NativeCheckpoint.Get(CoupleBenefitPolicy.NativeRingOffset) == ring
            && session.NativeCheckpoint.Get(NativeDungeonState.CouplePartnerUidOffset) == partnerUid)
            return;
        await RefreshNativeCoupleMetadataAsync(session, ring, partnerUid, token);
    }

    private async Task ApplyNativeCoupleExperienceAsync(
        ConnectionSession session, byte[] response, CancellationToken token)
    {
        if (session.Character is null || session.NativeCheckpoint is null
            || _coupleExperienceFrames.TryGetValue(response, out _)
            || !TryReadNativeDungeonSettlementFrame(response,
                checked((ushort)session.NativeCheckpoint.Get(4)), out var rating, out _, out var amount)
            || rating == 0 || session.NativeDungeonDeathLatched)
            return;
        var relation = await _database.GetActiveCoupleRelationAsync(session.Character.Id, token);
        var scaled = CoupleBenefitPolicy.ScaleExperience(amount, relation?.RingItemCode ?? 0,
            relation is not null && FindNativeDungeonPartner(session, relation) is not null);
        scaled = await ScaleMentorshipExperienceAsync(session, scaled, token);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8));
        for (var index = 0; index < count; index++)
        {
            var offset = 0x0C + index * 0x34;
            if (BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset))
                != session.NativeCheckpoint.Get(4))
                continue;
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(offset + 0x0C), scaled);
            _coupleExperienceFrames.GetValue(response, static _ => new object());
            break;
        }
    }

    private Task PatchNativeCoupleFrameAsync(
        ConnectionSession session, byte[] response, CancellationToken token)
        => PatchValidatedNativeCoupleFrameAsync(session, response, token);
}
