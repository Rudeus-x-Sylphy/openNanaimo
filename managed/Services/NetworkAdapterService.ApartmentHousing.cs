using System.Buffers.Binary;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private async Task<byte[]?> HandleApartmentHousingAsync(byte[] frame, ushort opcode, byte[] payload,
        ConnectionSession session, CancellationToken token)
    {
        if (!session.OnlineTracked || session.Character is null) return null;
        switch (opcode)
        {
            case 0xC36E:
            {
                if (payload.Length != 4) return null;
                var page = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                var slot = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2));
                var result = session.TownSceneActive && page == session.TownPage
                    ? await _database.PurchaseApartmentHouseAsync(session.AccountId, session.Character.Id,
                        session.SessionId, session.TownId, page, slot, token) : 40u;
                if (result == 10)
                {
                    AccountStateChanged?.Invoke();
                    await QueueApartmentHousePageAsync(session, includePeers: true, token);
                }
                return BuildNativeFrame(frame, 0xC36F, ApartmentDword(result), session);
            }
            case 0xC407:
                return payload.Length == 4
                    ? BuildNativeFrame(frame, 0xC408, ApartmentHousingPolicy.Catalog(
                        BinaryPrimitives.ReadUInt16LittleEndian(payload),
                        BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2))), session) : null;
            case 0xC425:
                return payload.Length == 0
                    ? BuildNativeFrame(frame, 0xC426, BuildApartmentExteriorInfoPayload(
                        await _database.GetApartmentExteriorStateAsync(session.Character.Id, token)), session) : null;
            case 0xC40D:
            {
                if (payload.Length != 8) return null;
                var code = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
                // The request constructor assigns the item code; the preceding DWORD is reserved.
                var result = await _database.PurchaseApartmentExteriorAsync(session.AccountId,
                    session.Character.Id, session.SessionId, code, token);
                await RefreshSessionCharacterAsync(session, token);
                if (result == 10) AccountStateChanged?.Invoke();
                var response = new byte[4]; response[0] = result;
                response[2] = result == 10 ? ApartmentHousingPolicy.Find(code)!.Value.DecorationPoints : (byte)0;
                return CombineNativeFrames(BuildNativeFrame(frame, 0xC40E, response, session),
                    BuildNativeFrame(frame, 0xC37B, await BuildApartmentBalancesAsync(session.Character!, token), session));
            }
            case 0xC414:
            {
                if (payload.Length != 54) return null;
                var saved = TryReadApartmentBanner(payload.AsSpan(8), out var text)
                    && await _database.SaveApartmentExteriorAsync(session.AccountId, session.Character.Id,
                        session.SessionId, payload.AsSpan(0,8).ToArray(), text, token);
                if (saved)
                {
                    AccountStateChanged?.Invoke();
                    await QueueApartmentHousePageAsync(session, includePeers: true, token);
                }
                return BuildNativeFrame(frame, 0xC415, ApartmentDword(saved ? 2000u : 1000u), session);
            }
        }
        return null;
    }

    private static byte[] ApartmentDword(uint value)
    {
        var result = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(result,value); return result;
    }

    internal static bool TryReadApartmentBanner(ReadOnlySpan<byte> bytes, out string text)
    {
        text = "";
        var end=bytes.IndexOf((byte)0);
        if(end<0 || end>45) return false;
        try
        {
            var encoding=Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
            text=encoding.GetString(bytes[..end]);
            if (text.Length==0) return true;
            if (!text.StartsWith('#')) return false;
            var lines=text[1..].Split('#');
            return lines.Length<=3 && lines.All(line => encoding.GetByteCount(line)<=14
                && !line.Any(char.IsControl));
        }
        catch (DecoderFallbackException) { return false; }
    }

    internal static byte[] BuildApartmentExteriorInfoPayload(ApartmentExteriorState state)
    {
        var payload=new byte[62];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,state.HasHouse?1u:0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),state.Exterior);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8),state.Banner);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12),ApartmentHousingPolicy.Find(state.Exterior)?.Index ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14),ApartmentHousingPolicy.Find(state.Banner)?.Index ?? 0);
        WriteFixedGbk(payload.AsSpan(16,46),state.Text);
        return payload;
    }

    internal static byte[] BuildApartmentExteriorInventoryPayload(ApartmentExteriorState state)
    {
        var payload=new byte[4+12*state.Items.Count]; payload[0]=30;payload[1]=1;payload[3]=(byte)state.Items.Count;
        for(var i=0;i<state.Items.Count;i++)
        {
            var item=state.Items[i];var row=payload.AsSpan(4+12*i,12);
            BinaryPrimitives.WriteUInt32LittleEndian(row,item.Code);
            BinaryPrimitives.WriteUInt16LittleEndian(row[4..],item.Code==state.Exterior || item.Code==state.Banner ? (ushort)1:(ushort)0);
            BinaryPrimitives.WriteUInt16LittleEndian(row[6..],item.Index);
        }
        return payload;
    }

    internal static byte[] BuildApartmentHouseMarkerPayload(ApartmentHouse house)
    {
        var payload=new byte[80];
        WriteFixedGbk(payload.AsSpan(0,16),house.OwnerName);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16),house.Exterior==0 ? 31000001u:house.Exterior);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20),house.Banner);
        WriteFixedGbk(payload.AsSpan(24,46),house.Text);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(70),WireIdentityAllocator.GetCharacterUid(house.CharacterId));
        payload[72]=house.Page;payload[73]=house.Slot;payload[74]=house.Town;
        payload[75]=(byte)Math.Clamp(house.Gender,0,1);payload[76]=100;
        return payload;
    }

    private async Task QueueApartmentHousePageAsync(ConnectionSession session, bool includePeers, CancellationToken token)
    {
        // Only the changed address is sent to players currently viewing its page.
        if (includePeers)
        {
            var house = await _database.GetOwnedApartmentHouseAsync(session.Character!.Id, token);
            if (house is null) return;
            foreach (var target in _activeWorldSessions.Values.Where(p => p.Session.OnlineTracked
                && p.Session.TownSceneActive && p.Session.TownId == house.Town && p.Session.TownPage == house.Page))
                session.PendingBroadcasts.Add(new PendingNativeBroadcast(target, 0xC36D,
                    BuildApartmentHouseMarkerPayload(house), "apartment street address"));
            return;
        }
        if (!_activeWorldSessions.TryGetValue(session.SessionId, out var presence)) return;
        foreach (var house in await _database.GetApartmentHousesAsync(session.TownId, session.TownPage, token))
            session.PendingBroadcasts.Add(new PendingNativeBroadcast(presence, 0xC36D,
                BuildApartmentHouseMarkerPayload(house), "apartment street address"));
    }

    private async Task<CharacterRecord?> ResolveStreetApartmentOwnerAsync(ConnectionSession session, ushort uid, CancellationToken token)
    {
        if(!session.TownSceneActive) return null;
        var houses=await _database.GetApartmentHousesAsync(session.TownId,session.TownPage,token);
        var house=houses.FirstOrDefault(h=>WireIdentityAllocator.GetCharacterUid(h.CharacterId)==uid);
        return house is null ? null : await _database.GetCharacterByIdAsync(house.CharacterId,token);
    }

    private async Task<byte[]> BuildApartmentBalancesAsync(CharacterRecord character, CancellationToken token)
    {
        var result=new byte[24]; BuildShopMovePayload(character).CopyTo(result,0);
        var points=await _database.GetApartmentRecommendationPointsAsync(character.Id,token);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16),(ulong)Math.Max(0,points));
        return result;
    }
}
