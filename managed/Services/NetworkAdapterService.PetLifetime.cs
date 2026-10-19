using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private sealed class NativePetLifetime
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string Round = "";
        public uint Code;
        public long Instance;
        public bool MapDelivered;
        public bool EntryApplied;
        public short EntryBefore;
        public int Revivals;
        public bool Exhausted;
        public short? PendingRevivalBefore;
    }

    private readonly ConditionalWeakTable<ConnectionSession, NativePetLifetime> _nativePetLifetimes = new();

    private NativePetLifetime GetNativePetLifetime(ConnectionSession session)
    {
        var state = _nativePetLifetimes.GetOrCreateValue(session);
        var round = NativeDungeonSettlementId(session);
        if (state.Round != round)
        {
            state.Round = round;
            state.Code = 0;
            state.Instance = 0;
            state.MapDelivered = state.EntryApplied = state.Exhausted = false;
            state.Revivals = 0;
            state.PendingRevivalBefore = null;
        }
        return state;
    }

    internal static short GetPetBattleDurability(CharacterRecord? character)
    {
        if (character is null || character.EquippedPetItemCode == 0) return 0;
        // Calendar pets never participate in lifespan billing, including unusual
        // migrated rows with a positive stored balance.
        if (ShopCatalog.TryGet(15, character.EquippedPetItemCode, out var pet)
            && (pet.HasExpiry || pet.PetMaxDurability < 0)) return -1;
        return PetProgression.GetState(character, character.EquippedPetItemCode).Durability;
    }

    internal static bool IsPetLifetimeRevivalBlocked(CharacterRecord? character)
        => character is { EquippedPetItemCode: not 0 }
            && GetPetBattleDurability(character) is >= 0 and <= 2;

    private PetLifetimeDebit? NativePetRevivalDebit(ConnectionSession session)
    {
        var state = GetNativePetLifetime(session);
        return state.MapDelivered && state.Code != 0
            ? new(state.Code, state.Instance, $"{state.Round}:revive:{state.Revivals}", Revival: true) : null;
    }

    private async Task<bool> ObserveNativePetMapAsync(ConnectionSession session, byte[] response,
        long epoch, CancellationToken token)
    {
        if (response.Length != 0x328 || response[8] != 1 || !session.OnlineTracked
            || !session.NativeDungeonSelectionValid || session.Character is null) return true;
        var state = GetNativePetLifetime(session);
        var round = state.Round;
        await state.Gate.WaitAsync(token);
        try
        {
            if (epoch != session.NativeBattleEpoch || round != NativeDungeonSettlementId(session) || state.MapDelivered) return false;
            var character = session.Character;
            state.Code = character.EquippedPetItemCode;
            state.Instance = character.EquippedPetInstanceId;
            state.EntryBefore = GetPetBattleDurability(character);
            if (state.Code != 0)
            {
                var result = await _database.ConsumePetLifetimeAsync(session.AccountId, character.Id,
                    session.SessionId, new(state.Code, state.Instance, $"{state.Round}:load"), token);
                if (!result.Success) throw new InvalidOperationException("Dungeon map lost its selected pet identity.");
                state.EntryApplied = true;
                await RefreshNativeDungeonContinueBillingStateAsync(session, token);
            }
            // Do not publish the new balance before CFEC: the client subtracts
            // one itself. Suppress repeated CFEC within this map generation.
            state.MapDelivered = true;
            await SyncNativePetLifetimeAsync(session, token);
            return true;
        }
        finally { state.Gate.Release(); }
    }

    private async Task SyncNativePetLifetimeAsync(ConnectionSession session, CancellationToken token)
    {
        if (session.NativeDungeon is null || session.Character is not { } character) return;
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, session.NativeCheckpoint?.Get(4) ?? GetSceneEntityId(character));
        // Match the worker's battle pet, which can outlive the persisted slot
        // until CF88 removes it. This control never changes HP/MP or inventory.
        var workerCode = session.NativeCheckpoint?.Get(68) ?? character.EquippedPetItemCode;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), workerCode);
        var state = GetNativePetLifetime(session);
        var durability = workerCode == character.EquippedPetItemCode
            ? GetPetBattleDurability(character)
            : workerCode == state.Code ? PetProgression.GetState(character, state.Code, state.Instance).Durability : (short)0;
        if (ShopCatalog.TryGet(15, workerCode, out var catalog)
            && (catalog.HasExpiry || catalog.PetMaxDurability < 0)) durability = -1;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), durability);
        await session.NativeDungeon.SendControlAsync(NativeDungeonClient.Frame(0xF10C, payload), token);
    }

    private async Task SettleNativePetExhaustionAsync(ConnectionSession session, CancellationToken token)
    {
        var state = GetNativePetLifetime(session);
        if (state.Exhausted || state.Code == 0 || session.Character is not { } character) return;
        var pet = PetProgression.GetState(character, state.Code, state.Instance);
        if (ShopCatalog.TryGet(15, state.Code, out var catalog) && (catalog.HasExpiry || catalog.PetMaxDurability < 0)
            || pet.Durability < 0 || pet.Durability > 2) return;
        // Zero was already debited and unequipped at load. A low-life death
        // spends one last use, then releases the slot even if one use remains.
        var unequipped = pet.Durability == 0;
        if (pet.Durability > 0)
        {
            var result = await _database.ConsumePetLifetimeAsync(session.AccountId, character.Id,
                session.SessionId, new(state.Code, state.Instance, $"{state.Round}:exhaustion", Exhausted: true), token);
            if (!result.Success) throw new InvalidOperationException("Pet exhaustion lost its battle instance.");
            unequipped = result.Unequipped;
            await RefreshNativeDungeonContinueBillingStateAsync(session, token);
        }
        state.Exhausted = unequipped;
        await SyncNativePetLifetimeAsync(session, token);
    }

    private void PatchNativePetLifetimeActor(ConnectionSession viewer, ConnectionSession? owner, byte[] frame)
    {
        if (owner?.Character is not { } character) return;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6));
        if (opcode is not (0xCF71 or 0xCF72)) return;
        var state = GetNativePetLifetime(owner);
        var durability = GetPetBattleDurability(character);
        if (state.EntryApplied && !GetNativePetLifetime(viewer).MapDelivered)
            durability = state.EntryBefore;
        else if (state.PendingRevivalBefore is short before && before >= 0)
            durability = (short)Math.Max(0, before - 1);
        PatchNativePetDurabilityFrame(frame, character, durability);
    }

    private void PatchNativePetExhaustionResult(ConnectionSession viewer, byte[] frame)
    {
        if (frame.Length < 12) return;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8));
        if (count is < 1 or > 3 || frame.Length != 12 + count * 52) return;
        for (var index = 0; index < count; index++)
        {
            var offset = 12 + index * 52;
            var uid = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(offset));
            var owner = viewer.Character is { } local && GetSceneEntityId(local) == uid ? viewer
                : _activeWorldSessions.Values.Select(row => row.Session).FirstOrDefault(peer =>
                    peer.OnlineTracked && viewer.PartyId > 0 && peer.PartyId == viewer.PartyId
                    && viewer.NativeLease is not null && peer.NativeLease?.Port == viewer.NativeLease.Port
                    && peer.Character is { } actor && GetSceneEntityId(actor) == uid);
            if (owner?.Character is not { } character) continue;
            var state = GetNativePetLifetime(owner);
            if (state.Code != 0 && (state.Exhausted
                || state.EntryBefore >= 0 && PetProgression.GetState(character, state.Code, state.Instance).Durability == 0))
                frame[offset + 6] = 1;
        }
        RewriteNativeChecksum(frame);
    }

    internal static bool PatchNativePetDurabilityFrame(byte[] frame, CharacterRecord character, short? durability = null)
    {
        if (frame.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)) != frame.Length) return false;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6));
        var offset = opcode == 0xCF71 && frame.Length == 0xB8 ? 0x58
            : opcode == 0xCF72 && frame.Length == 0x74 ? 0x6C : -1;
        if (offset < 0 || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(opcode == 0xCF71 ? 0x1A : 8))
            != GetSceneEntityId(character)) return false;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset), durability ?? GetPetBattleDurability(character));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(opcode == 0xCF71 ? 0x38 : 0x30),
            character.EquippedPetItemCode);
        RewriteNativeChecksum(frame);
        return true;
    }
}
