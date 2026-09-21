using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

// Packet layouts ported from server-csharp's DungeonPackets and EntityPackets.
internal static class DungeonProtocol
{
    public readonly record struct GameSkillRecord(
        ushort EntityUid,
        byte Skill0Grade,
        byte Skill1Grade,
        uint Skill0,
        uint Skill1);

    public const int CreateRequestLength = 44;
    public const int EnterRequestLength = 12;
    public const int QuickEnterRequestLength = 8;
    public const int CollisionRequestLength = 12;
    public const int ObjectEventRequestLength = 20;
    public const int BossRequestLength = 24;
    public const int PickupRequestLength = 8;

    public static byte[] BuildCreateResponse(
        ushort roomId,
        long ownerId,
        string ownerName,
        string password,
        ReadOnlySpan<byte> slotStates)
    {
        if (roomId == 0 || ownerId < 1 || slotStates.Length != 3)
            throw new InvalidDataException("Invalid dungeon room create response fields.");

        var payload = new byte[36];
        payload[0] = 10;
        payload[1] = string.IsNullOrEmpty(password) ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), roomId);
        slotStates.CopyTo(payload.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), checked((uint)ownerId));
        EncodeFixed(ownerName, payload.AsSpan(12, 24));
        return payload;
    }

    public static byte[] BuildEnterResponse(
        ushort roomId,
        long ownerId,
        string ownerName,
        string title,
        string password,
        ushort level,
        byte result)
    {
        var payload = new byte[64];
        payload[0] = result;
        payload[1] = (byte)Math.Min(level, byte.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), roomId);
        EncodeFixed(title, payload.AsSpan(4, 24));
        EncodeFixed(password, payload.AsSpan(28, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(36), checked((uint)ownerId));
        EncodeFixed(ownerName, payload.AsSpan(40, 24));
        return payload;
    }

    public static byte[] BuildRoomMember(
        CharacterRecord character,
        ushort ownerUid,
        byte roomSlot,
        byte roomState,
        uint equippedPetCode,
        long levelStartExperience,
        long nextLevelExperience,
        uint skill0,
        byte skill0Grade,
        uint skill1,
        byte skill1Grade)
    {
        var payload = new byte[0xB8 - 8];
        EncodeFixed(character.Name, payload.AsSpan(0, 16));
        var uid = checked((ushort)character.Id);
        // The first UID identifies the room owner; the second identifies this
        // rendered entity. The client uses the pair to place the owner badge
        // without aliasing the member occupying the slot.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x18 - 8), ownerUid);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x1A - 8), uid);

        var appearance = NormalizeAppearance(character.Appearance);
        appearance.AsSpan(0, 28).CopyTo(payload.AsSpan(0x1C - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x38 - 8), equippedPetCode);

        // CF71 frame+1C is the same 36-byte appearance value used by C368.
        // Its final dword is the avatar gender selector. Runtime entity state
        // is a separate frame+58 field and must not be packed into appearance.
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(0x3C - 8),
            character.Gender == 1 ? 1u : 0u);

        // The retail CF71 consumer stores frame+40 as the absolute next-level
        // threshold and frame+44 as the character's absolute experience. Its
        // HUD then evaluates (current - levelStart) / (next - levelStart).
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x40 - 8),
            (uint)Math.Clamp(nextLevelExperience, levelStartExperience + 1, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x44 - 8),
            (uint)Math.Clamp(character.Experience, levelStartExperience, nextLevelExperience));
        payload[0x48 - 8] = (byte)Math.Clamp(character.Level, 0, 0x7F);
        // Retail CF71 passes frame+0x49 to qz_inter_lv_icon%d. The shipped
        // resource set contains icon bands 1..7, one band per ten levels.
        payload[0x49 - 8] = GetLevelIcon(character.Level);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x4A - 8), ClampStat(character.MaxHp));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x4C - 8), ClampStat(character.MaxMp));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x4E - 8), ClampCurrent(character.CurrentHp, character.MaxHp));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x50 - 8), ClampCurrent(character.CurrentMp, character.MaxMp));
        payload[0x56 - 8] = roomSlot;
        payload[0x57 - 8] = roomState;
        foreach (var quickSlot in character.QuickSlots)
        {
            if (quickSlot.Slot <= 5)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    payload.AsSpan(0x5C - 8 + quickSlot.Slot * 4, 4),
                    quickSlot.ItemCode);
            }
        }
        var expandedQuickSlotsActive = SkillSlotExpansionTime.TryDecode(
                character.QuickSlotExpansionExpires, out var quickSlotExpiration)
            && quickSlotExpiration > DateTime.Now;
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(0x7A - 8, 2),
            expandedQuickSlotsActive ? (ushort)1 : (ushort)0);
        // The retail CF71 consumer passes frame+0xA9 directly to the same
        // expansion switch used by C3E8 before installing the Z/X records.
        var expandedSkillSlotActive = SkillSlotExpansionTime.TryDecode(
                character.SkillSlotExpansionExpires, out var skillSlotExpiration)
            && skillSlotExpiration > DateTime.Now;
        payload[0xA9 - 8] = expandedSkillSlotActive ? (byte)1 : (byte)0;
        // CF71 carries the room-member skill selection and expansion state.
        // The shooting runtime installs the authoritative Z/X records later
        // from CFEC; these packet offsets remain +AA/+AB/+AC/+B0.
        payload[0xAA - 8] = skill0Grade;
        payload[0xAB - 8] = skill1Grade;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0xAC - 8), skill0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0xB0 - 8), skill1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0xB4 - 8),
            (ushort)Math.Clamp(character.Level, 0, ushort.MaxValue));
        return payload;
    }

    private static byte GetLevelIcon(int level) =>
        (byte)Math.Clamp((Math.Max(1, level) - 1) / 10 + 1, 1, 7);

    public static byte[] BuildSlotChange(byte slot, byte state)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, slot);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), state);
        return payload;
    }

    public static byte[] BuildReady(ushort playerUid, bool ready, byte team)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, playerUid);
        payload[2] = ready ? (byte)1 : (byte)0;
        payload[3] = team;
        return payload;
    }

    public static byte[] BuildGameData(
        ushort stageIndex,
        ushort showStageNumber,
        ushort mapIndex,
        byte cardEpisode)
        => BuildGameData(
            stageIndex,
            showStageNumber,
            mapIndex,
            CreateRandomPercentPlan(),
            CreateRandomPercentPlan(),
            CreateUpgradeDropPlan(),
            CreateInDungeonItemDropPlan());

    public static byte[] BuildGameData(
        ushort stageIndex,
        ushort showStageNumber,
        ushort mapIndex,
        ReadOnlySpan<byte> randomPlanA,
        ReadOnlySpan<byte> randomPlanB,
        ReadOnlySpan<byte> upgradeDropSubtypes,
        ReadOnlySpan<byte> inDungeonItemDropCodes)
    {
        if (randomPlanA.Length != 150 || randomPlanB.Length != 150)
            throw new InvalidDataException("CFEC random-percent tables must contain exactly 150 entries each.");
        if (upgradeDropSubtypes.Length != 50)
            throw new InvalidDataException("CFEC upgrade-drop subtype table must contain exactly 50 entries.");
        if (inDungeonItemDropCodes.Length != 50)
            throw new InvalidDataException("CFEC in-dungeon item table must contain exactly 50 entries.");
        foreach (var code in inDungeonItemDropCodes)
        {
            if (code > 5)
                throw new InvalidDataException("CFEC normal-dungeon item codes must be in the retail range 0..5.");
        }
        if (stageIndex > byte.MaxValue || showStageNumber > byte.MaxValue)
            throw new InvalidDataException("CFEC StageIdx/ShowStageNum must fit their retail byte fields.");
        if (mapIndex > 8)
            throw new InvalidDataException("CFEC MapIdx must select one of the nine retail .sstg slots.");

        var payload = new byte[0x320];
        payload[0] = 1;
        // Retail CFEC consumes two interleaved signed-word tables at body
        // +0x02/+0x04. Its offline fallback fills both with rand()%100 before
        // initializing the dungeon runtime, so the server must not leave them
        // as an all-zero table in the network path.
        for (var index = 0; index < 150; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(0x02 + index * 4), randomPlanA[index]);
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(0x04 + index * 4), randomPlanB[index]);
        }
        for (var index = 0; index < 50; index++)
        {
            payload[0x26E + index] = upgradeDropSubtypes[index];
            payload[0x2A0 + index] = inDungeonItemDropCodes[index];
        }
        // Retail sub_6E9850 names the CFEB request fields StageIdx and
        // ShowStageNum. The CFEC consumer writes these bytes back to the
        // matching globals, then consumes the following word as MapIdx for
        // the selected .sstg/.smmo slot. Episode and dungeon never occupy
        // these offsets.
        payload[0x2D2] = (byte)stageIndex;
        payload[0x2D3] = (byte)showStageNumber;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x2D4), mapIndex);
        return payload;
    }

    public static void WriteGameSkillRecords(
        Span<byte> payload,
        IReadOnlyList<GameSkillRecord> records)
    {
        const int firstRecordOffset = 0x2DC;
        const int recordStride = 0x10;
        const int maximumPlayers = 2;

        if (payload.Length != 0x320)
            throw new InvalidDataException("CFEC payload must contain exactly 0x320 bytes.");
        if (records.Count > maximumPlayers)
            throw new InvalidDataException("CFEC supports at most two player skill records.");

        payload.Slice(firstRecordOffset, recordStride * maximumPlayers).Clear();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.EntityUid == 0)
                throw new InvalidDataException("CFEC player skill records require a non-zero entity UID.");

            var offset = firstRecordOffset + index * recordStride;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(offset, 2), record.EntityUid);
            payload[offset + 2] = record.Skill0Grade;
            payload[offset + 3] = record.Skill1Grade;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(offset + 4, 4), record.Skill0);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(offset + 8, 4), record.Skill1);
        }
    }

    public static byte[] CreateRandomPercentPlan()
    {
        var plan = new byte[150];
        for (var index = 0; index < plan.Length; index++)
            plan[index] = checked((byte)RandomNumberGenerator.GetInt32(100));
        return plan;
    }

    public static byte[] CreateUpgradeDropPlan()
    {
        // The retail fallback pre-generates 35 rand()%3+1 entries in a
        // 50-entry table. The CFEC consumer only uses this table when an NPC
        // with internal type 5 dies, then creates a type-40 upgrade entity.
        // ddakg card metadata is unrelated to these subtype values.
        var plan = new byte[50];
        for (var index = 0; index < 35; index++)
            plan[index] = (byte)Random.Shared.Next(1, 4);
        return plan;
    }

    public static byte[] CreateInDungeonItemDropPlan()
    {
        // The retail normal-entry path fills this table with rand()%6. NPCs
        // with internal type 10 consume it sequentially and create D034
        // type-20 entities whose values are decoded by the client.
        var plan = new byte[50];
        for (var index = 0; index < plan.Length; index++)
            plan[index] = checked((byte)RandomNumberGenerator.GetInt32(6));
        return plan;
    }

    public static bool TryGetSpawnedUpgradeDropSubtype(
        ReadOnlySpan<byte> upgradeDropSubtypes,
        ushort dropUid,
        out byte subtype)
    {
        // The client consumes all 50 slots in order, but only non-zero slots
        // create scene entities. D034 identifies those entities with a dense,
        // zero-based UID rather than the original CFEC table index.
        var spawnedUid = 0;
        foreach (var value in upgradeDropSubtypes)
        {
            if (value == 0)
                continue;
            if (spawnedUid == dropUid)
            {
                subtype = value;
                return true;
            }
            spawnedUid++;
        }

        subtype = 0;
        return false;
    }

    public static bool TryGetInDungeonItemDropValue(
        ReadOnlySpan<byte> inDungeonItemDropCodes,
        ushort dropUid,
        out uint value)
    {
        if (inDungeonItemDropCodes.Length != 50 || dropUid >= inDungeonItemDropCodes.Length)
        {
            value = 0;
            return false;
        }

        value = GetInDungeonItemDropValue(inDungeonItemDropCodes[dropUid]);
        return true;
    }

    public static uint GetInDungeonItemDropValue(byte code)
    {
        // sub_669870 preserves all ten retail code mappings. The normal
        // network entry emits 0..5; 6..9 belong to the alternate client path.
        return code switch
        {
            0 => 10u,
            1 => 50u,
            2 => 100u,
            3 => 200u,
            4 => 500u,
            5 => unchecked((uint)-10),
            6 => unchecked((uint)-50),
            7 => unchecked((uint)-100),
            8 => unchecked((uint)-200),
            9 => unchecked((uint)-500),
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
    }

    public static byte[] BuildCollision(
        IReadOnlyList<uint> hitScores,
        uint targetUid,
        bool defeated,
        byte itemType = 0,
        uint eventValue = 0)
    {
        if (hitScores.Count != 3)
            throw new InvalidDataException("D00E collision response requires three score slots.");
        if (targetUid > ushort.MaxValue)
            throw new InvalidDataException("D00D target UID does not fit the native response field.");
        var payload = new byte[40];
        for (var index = 0; index < hitScores.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(index * 4, 4), hitScores[index]);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x10), (ushort)targetUid);
        payload[0x12] = defeated ? (byte)200 : (byte)0;
        payload[0x13] = defeated ? itemType : (byte)0;
        if (defeated)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x14, 4), eventValue);
        return payload;
    }

    public static byte[] BuildBoss(
        IReadOnlyList<uint> hitScores,
        uint bossEnergy,
        ReadOnlySpan<byte> stageStates,
        ReadOnlySpan<byte> clearRanks,
        IReadOnlyList<ushort> hpSteals,
        IReadOnlyList<ushort> mpSteals,
        byte bossObjectIndex,
        byte bossParentIndex,
        ushort bossComponentIndex,
        uint immediateHansReward,
        uint dropItemCode)
    {
        if (hitScores.Count != 3)
            throw new InvalidDataException("D012 boss response requires three score slots.");
        if (stageStates.Length != 4 || clearRanks.Length != 4
            || hpSteals.Count != 4 || mpSteals.Count != 4)
            throw new InvalidDataException("D012 requires four stage, clear-rank, HP-steal, and MP-steal entries.");
        // Retail sub_6EBAC0 resolves the BMO object tree with payload +0x10
        // (scheduled boss), +0x11 (parent) and +0x12 (component). The resource
        // UID is not serialized in D012. The retail dispatcher overwrites its
        // three live HUD score slots from native frame +0x08/+0x0C/+0x10.
        // Remaining native frame offsets: +0x1C immediate Hans reward,
        // +0x20 scene item,
        // +0x24 stage states, +0x28 boss energy, +0x2C HP steal, +0x34 MP steal,
        // and +0x3C..+0x3F the four player clear ranks. The retail consumer
        // reads those final rank bytes to update its live dungeon-clear table.
        var payload = new byte[56];
        for (var index = 0; index < hitScores.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(index * 4, 4), hitScores[index]);
        payload[0x10] = bossObjectIndex;
        payload[0x11] = bossParentIndex;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x12), bossComponentIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x14), immediateHansReward);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x18), dropItemCode);
        stageStates.CopyTo(payload.AsSpan(0x1C));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x20), bossEnergy);
        for (var index = 0; index < 4; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x24 + index * 2), hpSteals[index]);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x2C + index * 2), mpSteals[index]);
            payload[0x34 + index] = clearRanks[index];
        }
        return payload;
    }

    // The retail object-event consumer expects the original 20 bytes unchanged.
    public static byte[] BuildObjectEvent(ReadOnlySpan<byte> requestPayload)
    {
        if (requestPayload.Length != ObjectEventRequestLength)
            throw new InvalidDataException("D00F object event must contain 20 payload bytes.");
        return requestPayload.ToArray();
    }

    private static byte[] NormalizeAppearance(byte[] appearance)
    {
        var normalized = new byte[36];
        appearance.AsSpan(0, Math.Min(appearance.Length, normalized.Length)).CopyTo(normalized);
        return normalized;
    }

    private static ushort ClampStat(int value) => (ushort)Math.Clamp(value, 1, ushort.MaxValue);

    private static ushort ClampCurrent(int value, int maximum) =>
        (ushort)Math.Clamp(value, 0, Math.Clamp(maximum, 0, ushort.MaxValue));

    private static void EncodeFixed(string value, Span<byte> destination)
    {
        destination.Clear();
        var bytes = Encoding.GetEncoding(936).GetBytes(value ?? string.Empty);
        bytes.AsSpan(0, Math.Min(bytes.Length, Math.Max(0, destination.Length - 1))).CopyTo(destination);
    }
}
