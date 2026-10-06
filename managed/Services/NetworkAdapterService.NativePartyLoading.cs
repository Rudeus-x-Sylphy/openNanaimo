using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private async Task PublishNativePartyIdentityAsync(ConnectionSession session, CancellationToken token)
    {
        if (session.PartyId <= 0 || session.NativeDungeon is not { } worker || session.NativeCheckpoint is not { } state) return;
        var actorUid = checked((ushort)state.Get(4));
        ushort ownerUid = actorUid;
        uint slot = 0;
        lock (_partyGate)
        {
            if (session.PartyId > 0)
            {
                if (!_parties.TryGetValue(session.PartyId, out var party)
                    || !party.Members.ContainsKey(session.SessionId)
                    || !party.Members.TryGetValue(party.OwnerSessionId, out var owner)
                    || owner.Session.Character is not { } character) return;
                ownerUid = GetSceneEntityId(character);
                if (owner.Session.SessionId != session.SessionId)
                    slot = checked((uint)(1 + party.Members.Values
                        .Where(member => member.Session.SessionId != party.OwnerSessionId)
                        .OrderBy(member => member.JoinOrder)
                        .TakeWhile(member => member.Session.SessionId != session.SessionId).Count()));
            }
        }
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, actorUid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), ownerUid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), slot);
        await worker.SendAsync(NativeDungeonClient.Frame(0xF109, payload), token);
    }

    private sealed record NativePartyLoad(ConnectionSession Owner, long Epoch, byte[] Request);
    private readonly Dictionary<int, NativePartyLoad> _nativePartyLoads = [];

    private bool NativePartyReadyForMap(Party party, ConnectionSession owner)
        => owner.NativeLease is { } lease && party.Members.Values.All(member =>
            IsTrackedWorldSession(member.Session) && member.Session.NativeDungeon is not null
            && member.Session.NativeContinuationRosterRequested
            && member.Session.NativeLease?.Port == lease.Port
            && member.Session.NativeLease?.Generation == lease.Generation);

    private bool DeferNativePartyMap(ConnectionSession session, byte[] frame)
    {
        if (frame.Length != 12 || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) != 0xCFEB
            || session.PartyId <= 0 || !session.NativeDungeonSelectionValid) return false;
        lock (_partyGate)
        {
            if (!_parties.TryGetValue(session.PartyId, out var party) || party.OwnerSessionId != session.SessionId)
                return false;
            if (NativePartyReadyForMap(party, session))
            {
                _nativePartyLoads.Remove(party.Id);
                return false;
            }
            _nativePartyLoads[party.Id] = new(session, session.NativeBattleEpoch, frame.ToArray());
            return true;
        }
    }

    private async Task ReleaseReadyNativePartyMapsAsync(CancellationToken token)
    {
        ConnectionSession[] owners;
        lock (_partyGate) owners = _nativePartyLoads.Values.Select(p => p.Owner).ToArray();
        foreach (var owner in owners) await ReleaseNativePartyMapAsync(owner, token);
    }

    private void ClearNativePartyLoad(ConnectionSession session)
    {
        lock (_partyGate)
            foreach (var key in _nativePartyLoads.Where(p => ReferenceEquals(p.Value.Owner, session)).Select(p => p.Key).ToArray())
                _nativePartyLoads.Remove(key);
    }

    private async Task ReleaseNativePartyMapAsync(ConnectionSession member, CancellationToken token)
    {
        NativePartyLoad? pending = null;
        lock (_partyGate)
        {
            if (!_nativePartyLoads.TryGetValue(member.PartyId, out var candidate)) return;
            if (!_parties.TryGetValue(member.PartyId, out var party)
                || candidate.Owner.NativeBattleEpoch != candidate.Epoch
                || candidate.Owner.NativeDungeon is null || party.OwnerSessionId != candidate.Owner.SessionId)
            {
                _nativePartyLoads.Remove(member.PartyId);
                return;
            }
            if (NativePartyReadyForMap(party, candidate.Owner))
            {
                pending = candidate;
                _nativePartyLoads.Remove(member.PartyId);
            }
        }
        if (pending?.Owner.NativeDungeon is { } worker && pending.Owner.NativeBattleEpoch == pending.Epoch)
            await worker.SendAsync(pending.Request, token);
    }
}
