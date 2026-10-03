using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private sealed class CardExchangeSessionState
    {
        public CardExchangeQuery? VisibleQuery { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<ushort, (ushort Opcode, string RequestId)> Intents { get; } = [];
    }

    private readonly ConditionalWeakTable<ConnectionSession, CardExchangeSessionState> _cardExchangeSessions = new();

    private void ObserveCardExchangeRequest(byte[] frame, ushort opcode, ConnectionSession session)
    {
        if (frame.Length < 8) return;
        var state = _cardExchangeSessions.GetOrCreateValue(session);
        var control = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        lock (state.Intents)
        {
            if (!CardExchangeProtocol.IsMutation(opcode)
                || !state.Intents.TryGetValue(control, out var intent) || intent.Opcode != opcode)
                state.Intents[control] = (opcode, session.SessionId + ":" + Guid.NewGuid().ToString("N"));
        }
    }

    private async Task<byte[]?> HandleCardExchangeFrameAsync(
        byte[] frame, ushort opcode, ConnectionSession session, CancellationToken token)
    {
        if (!CardExchangeProtocol.IsRequest(opcode) || !session.OnlineTracked || session.Character is null
            || frame.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) != opcode) return null;
        var expected = opcode switch
        {
            0xC5B0 => CardExchangeProtocol.ListLength,
            0xC5B2 => CardExchangeProtocol.BuyLength,
            0xC5B4 => CardExchangeProtocol.RegisterLength,
            _ => CardExchangeProtocol.RetrievalLength
        };
        if (frame.Length != expected + 8) return null;
        var state = _cardExchangeSessions.GetOrCreateValue(session);
        await state.Gate.WaitAsync(token);
        try
        {
            ObserveCardExchangeRequest(frame, opcode, session);
            if (opcode == 0xC5B0)
            {
                var query = CardExchangeProtocol.TryReadQuery(frame.AsSpan(8), out var parsed)
                    ? await _database.QueryCardExchangeListingsAsync(session.AccountId, session.Character.Id,
                        session.SessionId, parsed, token)
                    : new CardExchangeListResult(14, 0, []);
                await Task.Delay(AuctionUiResponseDelay, token);
                var response = BuildNativeFrame(frame, 0xC5B1,
                    CardExchangeProtocol.BuildList(query, session.AccountId, session.Character.Id), session);
                if (query.ResultCode != 1) return response;
                state.VisibleQuery = parsed;
                await RefreshInventoryCharacterAsync(session, token);
                return CombineNativeFrames(response, BuildNativeFrame(frame, 0xC37B,
                    await BuildApartmentBalancesAsync(session.Character!, token), session));
            }

            string requestId;
            lock (state.Intents)
                requestId = state.Intents[BinaryPrimitives.ReadUInt16LittleEndian(frame)].RequestId;
            CardExchangeMutationResult result;
            if (opcode == 0xC5B2)
                result = await _database.PurchaseCardExchangeListingAsync(session.AccountId, session.Character.Id,
                    session.SessionId, requestId, BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)),
                    BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)),
                    BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)), token);
            else if (opcode == 0xC5B4)
                result = await _database.RegisterCardExchangeListingAsync(session.AccountId, session.Character.Id,
                    session.SessionId, requestId, frame[8], BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)),
                    BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(10)),
                    BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)), token);
            else
                result = await _database.RetrieveCardExchangeListingAsync(session.AccountId, session.Character.Id,
                    session.SessionId, requestId, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(16)), token);

            if (result.Success)
            {
                await RefreshInventoryCharacterAsync(session, token);
                if (!result.Replayed) AccountStateChanged?.Invoke();
            }
            // The client installs its selected-row state after sending each mutation.
            // Yield before all acknowledgements so its existing success handler can update the row.
            await Task.Delay(AuctionUiResponseDelay, token);
            var acknowledgement = BuildNativeFrame(frame, (ushort)(opcode + 1), opcode == 0xC5B4
                ? CardExchangeProtocol.BuildRegistration(result) : CardExchangeProtocol.BuildResult(result), session);
            if (!result.Success) return acknowledgement;
            var balances = BuildNativeFrame(frame, 0xC37B,
                await BuildApartmentBalancesAsync(session.Character!, token), session);
            if (opcode == 0xC5B6 && state.VisibleQuery is { RequestType: 1 } visible)
            {
                var refreshed = await _database.QueryCardExchangeListingsAsync(
                    session.AccountId, session.Character!.Id, session.SessionId, visible, token);
                if (refreshed.ResultCode == 1)
                    return CombineNativeFrames(acknowledgement, balances,
                        BuildNativeFrame(frame, 0xC5B1,
                            CardExchangeProtocol.BuildList(refreshed, session.AccountId, session.Character.Id), session));
            }
            return CombineNativeFrames(acknowledgement, balances);
        }
        finally { state.Gate.Release(); }
    }
}
