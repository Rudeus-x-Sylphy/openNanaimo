using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private readonly object _coupleSelectionGate = new();
    private readonly Dictionary<(string Responder, ushort Opcode, string RequesterName), BoundCoupleSelection> _coupleSelections = new();

    private readonly Dictionary<string, ForcedSeparationSelection> _forcedSeparationSelections = new(StringComparer.Ordinal);

    private sealed record ForcedSeparationSelection(
        WorldPresence Requester, long PartnerId, long RelationId,
        DatabaseService.CoupleInventorySelection Inventory, ushort InventoryIdentity);

    private sealed record BoundCoupleSelection(
        WorldPresence Requester, WorldPresence Responder,
        DatabaseService.CoupleInventorySelection Inventory, ushort InventoryIdentity, long RelationId);

    private bool IsCurrentCouplePresence(WorldPresence presence)
        => presence.Session.OnlineTracked
            && presence.Session.AccountId == presence.AccountId
            && presence.Session.Character?.Id == presence.CharacterId
            && presence.Session.SessionId == presence.SessionId
            && _activeWorldSessions.TryGetValue(presence.SessionId, out var current)
            && ReferenceEquals(current.Session, presence.Session)
            && current.CharacterId == presence.CharacterId && current.AccountId == presence.AccountId;

    private void PruneCoupleSelectionsLocked()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _forcedSeparationSelections.Where(entry =>
                     entry.Value.Inventory.ExpiresAtUtc <= now
                     || !IsCurrentCouplePresence(entry.Value.Requester)).ToArray())
            _forcedSeparationSelections.Remove(entry.Key);
        foreach (var entry in _coupleSelections.Where(entry =>
                     entry.Value.Inventory.ExpiresAtUtc <= now
                     || !IsCurrentCouplePresence(entry.Value.Requester)
                     || !IsCurrentCouplePresence(entry.Value.Responder)).ToArray())
            _coupleSelections.Remove(entry.Key);
    }

    private void InvalidateCoupleSelections(long firstId, long secondId)
    {
        lock (_coupleSelectionGate)
        {
            foreach (var entry in _forcedSeparationSelections.Where(entry =>
                         entry.Value.Requester.CharacterId == firstId || entry.Value.Requester.CharacterId == secondId
                         || entry.Value.PartnerId == firstId || entry.Value.PartnerId == secondId).ToArray())
                _forcedSeparationSelections.Remove(entry.Key);
            foreach (var entry in _coupleSelections.Where(entry =>
                         entry.Value.Requester.CharacterId == firstId || entry.Value.Requester.CharacterId == secondId
                         || entry.Value.Responder.CharacterId == firstId || entry.Value.Responder.CharacterId == secondId).ToArray())
                _coupleSelections.Remove(entry.Key);
        }
    }

    private async Task<byte[]> PrepareForcedSeparationAsync(
        byte[] frame, uint itemCode, uint identity, ConnectionSession session, CancellationToken token)
    {
        byte[] Reply(bool success) => BuildNativeFrame(frame, 0xC46E,
            BuildTokenUseResultPayload(success, itemCode, identity), session);
        if (itemCode != 43_100_002 || session.Character is null
            || !_activeWorldSessions.TryGetValue(session.SessionId, out var requester)
            || !IsCurrentCouplePresence(requester)) return Reply(false);
        await RefreshSessionCharacterAsync(session, token);
        if (!TryResolveSessionInventoryIdentity(session, identity, out var ordinal, out var selectedCode)
            || selectedCode != itemCode) return Reply(false);
        var selection = await _database.CaptureCoupleInventorySelectionAsync(
            requester.AccountId, requester.CharacterId, requester.SessionId, itemCode, ordinal, token);
        var relation = await _database.GetActiveCoupleRelationAsync(requester.CharacterId, token);
        if (selection is null || relation is null) return Reply(false);
        lock (_coupleSelectionGate)
        {
            PruneCoupleSelectionsLocked();
            if (!IsCurrentCouplePresence(requester)) return Reply(false);
            // Repeated use does not extend the confirmation deadline or replace its inventory snapshot.
            if (_forcedSeparationSelections.TryGetValue(session.SessionId, out var pending)
                && pending.InventoryIdentity == identity && pending.RelationId == relation.Id)
                return Reply(true);
            _forcedSeparationSelections[session.SessionId] = new ForcedSeparationSelection(
                requester, relation.GetPartnerId(requester.CharacterId), relation.Id, selection, checked((ushort)identity));
        }
        // Opening the confirmation dialog leaves the relationship and inventory unchanged.
        return Reply(true);
    }

    private async Task<byte[]?> HandleBoundCoupleRequestAsync(
        byte[] frame, ushort opcode, byte[] payload, ConnectionSession session,
        string channel, string remote, CancellationToken token)
    {
        if (session.Character is null || !_activeWorldSessions.TryGetValue(session.SessionId, out var requester)
            || !IsCurrentCouplePresence(requester)
            || !CoupleProtocol.IsRequestOpcode(opcode)
            || payload.Length != CoupleProtocol.GetPayloadLength(opcode)
            || !CoupleProtocol.TryReadPeerName(payload, out var peerName)) return null;
        var responseOpcode = CoupleProtocol.GetResponseOpcode(opcode);
        byte[] Failure(ushort status, CharacterRecord? peer = null) => BuildNativeFrame(frame, responseOpcode,
            CoupleProtocol.BuildResponse(responseOpcode, peerName, CoupleProtocol.ReadItemCode(payload),
                CoupleProtocol.ReadInventorySlot(opcode, payload), status, peer ?? session.Character!), session);
        if (!CoupleProtocol.TryReadRequest(opcode, payload, out var request))
            return Failure(CoupleProtocol.Unavailable);
        await RefreshSessionCharacterAsync(session, token);
        if (!TryResolveSessionInventoryIdentity(session, request.InventorySlot, out var inventoryOrdinal, out var inventoryCode)
            || inventoryCode != request.ItemCode) return Failure(CoupleProtocol.Unavailable);
        var selection = await _database.CaptureCoupleInventorySelectionAsync(
            requester.AccountId, requester.CharacterId, requester.SessionId,
            request.ItemCode, inventoryOrdinal, token);
        if (selection is null || !IsCurrentCouplePresence(requester)) return Failure(CoupleProtocol.Unavailable);
        var separation = opcode == CoupleProtocol.SeparationRequestOpcode;
        var ownRelation = await _database.GetActiveCoupleRelationAsync(requester.CharacterId, token);
        if (separation && (ownRelation is null
                || !string.Equals(ownRelation.GetPartnerName(requester.CharacterId), peerName, StringComparison.Ordinal)))
            return Failure(CoupleProtocol.InvalidPartner);
        if (!separation && ownRelation is not null) return Failure(CoupleProtocol.AlreadyRelated);
        if (separation && request.ItemCode == 43_100_002)
        {
            var partnerId = ownRelation!.GetPartnerId(requester.CharacterId);
            lock (_coupleSelectionGate)
            {
                PruneCoupleSelectionsLocked();
                if (!_forcedSeparationSelections.TryGetValue(session.SessionId, out var confirmation)
                    || !ReferenceEquals(confirmation.Requester.Session, session)
                    || confirmation.InventoryIdentity != request.InventorySlot
                    || confirmation.Inventory.ItemCode != request.ItemCode
                    || confirmation.RelationId != ownRelation.Id || confirmation.PartnerId != partnerId)
                    return Failure(CoupleProtocol.Unavailable);
                selection = confirmation.Inventory;
                _forcedSeparationSelections.Remove(session.SessionId);
            }
            var partner = await _database.GetCharacterByIdAsync(partnerId, token);
            if (partner is null) return Failure(CoupleProtocol.InvalidPartner);
            var result = await _database.CommitCoupleSelectionAsync(selection, partner.AccountId, partnerId,
                null, true, ownRelation.Id, token);
            if (!result.Success) return Failure(CoupleProtocol.InvalidPartner, partner);
            session.GameInventoryIdentities.Remove(request.InventorySlot);
            InvalidateCoupleSelections(requester.CharacterId, partnerId);
            await RefreshSessionCharacterAsync(session, token);
            AccountStateChanged?.Invoke();
            var activePartner = _activeWorldSessions.Values.FirstOrDefault(p =>
                p.CharacterId == partnerId && IsCurrentCouplePresence(p));
            if (activePartner is not null)
                await RefreshSessionCharacterAsync(activePartner.Session, token);
            QueueCoupleTownSceneRefresh(session, activePartner is null
                ? [session]
                : [session, activePartner.Session]);
            if (activePartner is not null)
                session.PendingBroadcasts.Add(new PendingNativeBroadcast(activePartner, responseOpcode,
                    CoupleProtocol.BuildResponse(responseOpcode, session.Character!.Name, request.ItemCode,
                        request.InventorySlot, CoupleProtocol.Accepted, session.Character), "couple separation confirmed"));
            return CombineNativeFrames(BuildNativeFrame(frame, responseOpcode,
                    CoupleProtocol.BuildResponse(responseOpcode, partner.Name, request.ItemCode,
                        request.InventorySlot, CoupleProtocol.Accepted, partner), session),
                BuildNativeFrame(frame, 0xC430, BuildGameInventoryPayload(session.Character), session));
        }
        var responder = FindCoupleRequestTarget(session, peerName);
        if (responder is null || !IsCurrentCouplePresence(responder)
            || responder.AccountId == requester.AccountId || responder.CharacterId == requester.CharacterId)
            return Failure(CoupleProtocol.Unavailable);
        if (separation && ownRelation!.GetPartnerId(requester.CharacterId) != responder.CharacterId)
            return Failure(CoupleProtocol.InvalidPartner, responder.Session.Character);
        if (!separation && await _database.GetActiveCoupleRelationAsync(responder.CharacterId, token) is not null)
            return Failure(CoupleProtocol.AlreadyRelated, responder.Session.Character);
        var pending = new BoundCoupleSelection(requester, responder, selection, request.InventorySlot, ownRelation?.Id ?? 0);
        lock (_coupleSelectionGate)
        {
            PruneCoupleSelectionsLocked();
            if (!IsCurrentCouplePresence(requester) || !IsCurrentCouplePresence(responder)
                || _coupleSelections.Values.Any(p => p.Requester.CharacterId == requester.CharacterId
                    || p.Responder.CharacterId == requester.CharacterId || p.Requester.CharacterId == responder.CharacterId
                    || p.Responder.CharacterId == responder.CharacterId))
                return Failure(CoupleProtocol.Unavailable, responder.Session.Character);
            _coupleSelections.Add((responder.SessionId, responseOpcode, session.Character!.Name), pending);
        }
        session.PendingBroadcasts.Add(new PendingNativeBroadcast(responder, opcode,
            CoupleProtocol.BuildRequestRelay(opcode, payload, session.Character!), "couple request confirmation"));
        return null;
    }

    private async Task<byte[]?> HandleBoundCoupleResponseAsync(
        byte[] frame, ushort opcode, byte[] payload, ConnectionSession session,
        string channel, string remote, CancellationToken token)
    {
        if (session.Character is null || !CoupleProtocol.TryReadAnswer(opcode, payload, out var answer)) return null;
        BoundCoupleSelection pending;
        var key = (session.SessionId, opcode, answer.Request.PeerName);
        lock (_coupleSelectionGate)
        {
            PruneCoupleSelectionsLocked();
            if (!_coupleSelections.TryGetValue(key, out var candidate)
                || !ReferenceEquals(candidate.Responder.Session, session)
                || !IsCurrentCouplePresence(candidate.Responder)
                || !IsCurrentCouplePresence(candidate.Requester)
                || candidate.Inventory.ItemCode != answer.Request.ItemCode
                || candidate.InventoryIdentity != answer.Request.InventorySlot) return null;
            pending = candidate;
            _coupleSelections.Remove(key);
        }
        var finalStatus = answer.Status;
        if (finalStatus == CoupleProtocol.Accepted)
        {
            var separation = opcode == CoupleProtocol.SeparationResponseOpcode;
            var result = await _database.CommitCoupleSelectionAsync(pending.Inventory,
                pending.Responder.AccountId, pending.Responder.CharacterId, pending.Responder.SessionId,
                separation, pending.RelationId, token);
            if (!result.Success) finalStatus = separation ? CoupleProtocol.InvalidPartner : CoupleProtocol.AlreadyRelated;
            else
            {
                pending.Requester.Session.GameInventoryIdentities.Remove(pending.InventoryIdentity);
                InvalidateCoupleSelections(pending.Requester.CharacterId, pending.Responder.CharacterId);
                await RefreshSessionCharacterAsync(pending.Requester.Session, token);
                await RefreshSessionCharacterAsync(session, token);
                QueueCoupleTownSceneRefresh(session, pending.Requester.Session, session);
            }
        }
        if (IsCurrentCouplePresence(pending.Requester))
        {
            session.PendingBroadcasts.Add(new PendingNativeBroadcast(pending.Requester, opcode,
                CoupleProtocol.BuildResponse(opcode, session.Character!.Name, pending.Inventory.ItemCode,
                    pending.InventoryIdentity, finalStatus, session.Character), "couple decision confirmed"));
            if (finalStatus == CoupleProtocol.Accepted)
            {
                session.PendingBroadcasts.Add(new PendingNativeBroadcast(pending.Requester, 0xC430,
                    BuildGameInventoryPayload(pending.Requester.Session.Character), "couple inventory refreshed"));
            }
        }
        var responderResponse = BuildNativeFrame(frame, opcode, CoupleProtocol.BuildResponse(opcode,
            pending.Requester.Session.Character!.Name, pending.Inventory.ItemCode, pending.InventoryIdentity,
            finalStatus, pending.Requester.Session.Character!), session);
        return responderResponse;
    }

    private void QueueCoupleTownSceneRefresh(
        ConnectionSession queueOwner,
        params ConnectionSession[] members)
    {
        var active = members
            .Where(item => _activeWorldSessions.TryGetValue(item.SessionId, out var presence)
                && ReferenceEquals(item, presence.Session)
                && IsCurrentCouplePresence(presence))
            .DistinctBy(item => item.SessionId)
            .ToArray();
        // A relationship decision changes the native town actor state in-place.  The
        // responder/requester do not reconstruct their peer from the C584/C586 answer;
        // they need the same C36A -> C47F refresh as every other observer.  Previously
        // this loop explicitly skipped both participants, so the database/profile was
        // correct while the already-instantiated town actors kept an empty/stale
        // partner-name/ring projection (and separation left the old marker visible).
        foreach (var recipient in _activeWorldSessions.Values)
        {
            if (!IsCurrentCouplePresence(recipient))
                continue;
            foreach (var subject in active)
            {
                if (recipient.SessionId == subject.SessionId)
                    continue;
                QueueTownPeerSnapshot(queueOwner, recipient, subject);
            }
        }
    }

}
