using System.Buffers.Binary;
using System.Text;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // Dispatch C396/C398 directly to these handlers using frame, payload, session, token.
    private async Task<byte[]?> HandleApartmentRecommendAsync(
        byte[] frame, byte[] payload, ConnectionSession session, CancellationToken token)
    {
        if (!TryDecodeApartmentRecommendationOwner(payload, out var ownerName))
            return null;
        await _presenceGate.WaitAsync(token);
        try
        {
            var characterId = session.Character?.Id ?? 0;
            var ownerId = session.ApartmentOwnerCharacterId;
            bool IsCurrentVisit() => IsApartmentRecommendationSessionCurrent(session)
                && session.Character?.Id == characterId && ownerId > 0
                && session.ApartmentOwnerCharacterId == ownerId;
            if (!IsCurrentVisit())
                return null;
            var result = await _database.RecommendApartmentAsync(session.AccountId, characterId,
                session.SessionId, ownerId, ownerName, IsCurrentVisit, token);
            if (result.Status == ApartmentRecommendationStatus.Rejected || !IsCurrentVisit())
                return null;
            if (result.Status == ApartmentRecommendationStatus.Success)
                await QueueApartmentRecommendationPointsRefreshAsync(session, ownerId, result.OwnerPoints, token);
            return BuildNativeFrame(frame, 0xC397, BuildApartmentRecommendationResultPayload(result.Status), session);
        }
        finally { _presenceGate.Release(); }
    }

    private async Task<byte[]?> HandleApartmentRecommendCountAsync(
        byte[] frame, byte[] payload, ConnectionSession session, CancellationToken token)
    {
        if (payload.Length != 0)
            return null;
        await _presenceGate.WaitAsync(token);
        try
        {
            if (!IsApartmentRecommendationSessionCurrent(session))
                return null;
            var remaining = await _database.GetApartmentRecommendationRemainingAsync(
                session.AccountId, session.Character!.Id, session.SessionId, token);
            if (!remaining.HasValue || !IsApartmentRecommendationSessionCurrent(session))
                return null;
            return BuildNativeFrame(frame, 0xC399, BuildApartmentRecommendCountPayload(remaining.Value), session);
        }
        finally { _presenceGate.Release(); }
    }

    // C397 carries only the result code; C38E payload +56 (full frame +64) is the
    // established room-state carrier for the owner's current recommendation points.
    private async Task QueueApartmentRecommendationPointsRefreshAsync(
        ConnectionSession source, long ownerId, long ownerPoints, CancellationToken token)
    {
        if (ownerId <= 0 || ownerPoints < 0)
            return;
        var owner = await _database.GetCharacterByIdAsync(ownerId, token);
        if (owner is null || source.ApartmentOwnerCharacterId != ownerId
            || !IsApartmentRecommendationSessionCurrent(source))
            return;

        var placements = await _database.GetApartmentPlacementsAsync(ownerId, token);
        var basePayload = BuildMiniRoomMovePayload(owner, true, placements);
        var streetHouse = await _database.GetOwnedApartmentHouseAsync(ownerId, token);
        var now = DateTimeOffset.UtcNow;
        foreach (var target in _activeWorldSessions.Values
                     .Where(item => ReferenceEquals(item.Session, source)
                         || IsSameApartmentRoom(source, item.Session)))
        {
            var targetSession = target.Session;
            if (targetSession.ApartmentOwnerCharacterId != ownerId
                || !IsApartmentRecommendationSessionCurrent(targetSession))
                continue;
            var payload = basePayload.ToArray();
            ApplyApartmentHouseState(payload, targetSession.Character!.Id == ownerId, streetHouse, now);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(56, 8), checked((ulong)ownerPoints));
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                target,
                0xC38E,
                payload,
                "apartment recommendation points refresh"));
        }
    }

    private bool IsApartmentRecommendationSessionCurrent(ConnectionSession session)
        => session.OnlineTracked && session.Character is not null && !session.AuxiliaryGameSession
            && session.DisconnectReason is null && session.ConnectionCancellation?.IsCancellationRequested != true
            && _activeWorldSessions.TryGetValue(session.SessionId, out var presence)
            && ReferenceEquals(presence.Session, session)
            && presence.AccountId == session.AccountId && presence.CharacterId == session.Character.Id;

    internal static bool TryDecodeApartmentRecommendationOwner(ReadOnlySpan<byte> payload, out string ownerName)
    {
        ownerName = string.Empty;
        if (payload.Length != 16)
            return false;
        var terminator = payload.IndexOf((byte)0);
        if (terminator <= 0)
            return false;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            var gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            ownerName = gbk.GetString(payload[..terminator]);
            return !ownerName.Any(char.IsControl);
        }
        catch (DecoderFallbackException) { return false; }
        // Bytes following the C-string terminator are not part of the owner name.
    }

    internal static byte[] BuildApartmentRecommendationResultPayload(ApartmentRecommendationStatus status)
    {
        if (status is not (ApartmentRecommendationStatus.Success or ApartmentRecommendationStatus.Duplicate
            or ApartmentRecommendationStatus.Exhausted))
            throw new ArgumentOutOfRangeException(nameof(status));
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)status);
        return payload;
    }
}
