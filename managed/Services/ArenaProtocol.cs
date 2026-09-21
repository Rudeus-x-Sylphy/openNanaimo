using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OpenNanaimo.Adapter.Services;

internal sealed record ArenaCreateRequest(
    byte[] RawPayload,
    string Title,
    string Password,
    byte Field26,
    byte ResponseField2,
    ushort Metadata,
    byte ResponseField3,
    byte Field33,
    byte Field34);

internal readonly record struct ArenaQuickEnterRequest(
    ushort Mode,
    byte Field2,
    byte Field3,
    byte Field4,
    byte Field5,
    ushort RoomId);

internal readonly record struct ArenaRoomListEntry(
    ushort RoomId,
    string Title,
    string Password,
    ushort Metadata,
    byte MemberCount,
    bool Started);

internal readonly record struct ArenaPvpResultRecord(
    ushort UserUid,
    ushort Win,
    byte LevelUp,
    byte Grade,
    byte PlayerLevel,
    byte ArenaRank,
    uint Star,
    uint AddedExperience,
    uint Hans,
    uint CurrentExperience,
    uint LevelStartExperience,
    uint NextLevelExperience,
    uint ArenaRankMaximum,
    IReadOnlyList<uint> Rewards);

internal readonly record struct ArenaGameEventRequest(
    ushort EventCode,
    ushort PrimaryUid,
    byte ObjectSelector,
    byte ObjectSubSelector,
    ushort ObjectUid,
    ushort ValueAt8,
    byte Argument2,
    byte Argument3,
    byte Argument4)
{
    // Retail event 10 passes the collision object's +0x140 value as the
    // constructor's second argument, serialized at request +2. Request +8
    // is outside every initialized constructor field and is stack residue.
    public ushort PlayerDamage => EventCode == 10 ? PrimaryUid : (ushort)0;

    public ushort TargetDamage => EventCode switch
    {
        // The retail ordinary-target collision path subtracts exactly 30
        // before it emits event 20/30. Bytes +4..+11 in those requests are
        // constructor padding and must never be interpreted as damage.
        20 or 30 => 30,
        // The retail 40/50 constructors place this value at request +8,
        // while their object selector and UID remain at +4 and +6.
        40 or 50 => ValueAt8,
        _ => 0
    };
}

internal static class ArenaProtocol
{
    public const int CreateRequestLength = 44;
    public const int QuickEnterRequestLength = 8;
    public const int QuickEnterResponseLength = 44;
    public const int RoomListRecordLength = 40;
    public const int GameDataResponseLength = 0x320;
    public const ushort ArenaMapCount = 6;
    public const ushort DefaultArenaMap = 0;
    public const int PvpResultRecordLength = 56;
    public const int GameEventRequestLength = 20;
    public const int GameEventResponseLength = 28;

    private static readonly Encoding Gbk = CreateGbkEncoding();

    public static bool TryParseCreateRequest(
        ReadOnlySpan<byte> payload,
        out ArenaCreateRequest? request)
    {
        request = null;
        if (payload.Length != CreateRequestLength
            || BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(24, 2)) != 100
            || payload[35] > 1
            || !TryDecodeFixed(payload.Slice(0, 24), false, out var title))
            return false;

        var password = string.Empty;
        if (payload[35] != 0
            && !TryDecodeFixed(payload.Slice(36, 8), false, out password))
            return false;

        request = new ArenaCreateRequest(
            payload.ToArray(),
            title,
            password,
            payload[26],
            payload[29],
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(30, 2)),
            payload[32],
            payload[33],
            payload[34]);
        return true;
    }

    public static bool TryParseQuickEnterRequest(
        ReadOnlySpan<byte> payload,
        out ArenaQuickEnterRequest request)
    {
        request = default;
        if (payload.Length != QuickEnterRequestLength)
            return false;

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        if (mode is not (10 or 20 or 100))
            return false;

        request = new ArenaQuickEnterRequest(
            mode,
            payload[2],
            payload[3],
            payload[4],
            payload[5],
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)));
        return true;
    }

    public static ArenaCreateRequest CreateAutomaticRoomRequest(
        string title,
        in ArenaQuickEnterRequest quickRequest)
    {
        var payload = new byte[CreateRequestLength];
        EncodeFixed(title, payload.AsSpan(0, 24));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(24, 2), 100);

        // These pairs are written by the same client arena-state getters:
        // request +2 maps to create +26, and request +5 maps to create +30.
        // The adjacent matchmaking selectors become the selected room fields
        // returned by CF78 at +2/+3.
        payload[26] = quickRequest.Field2;
        payload[29] = quickRequest.Field4;
        payload[30] = quickRequest.Field5;
        payload[32] = quickRequest.Field3;
        payload[35] = 0;

        if (!TryParseCreateRequest(payload, out var request) || request is null)
            throw new InvalidDataException("Unable to construct the retail arena automatic-room request.");
        return request;
    }

    public static byte[] BuildRoomList(IReadOnlyList<ArenaRoomListEntry> rooms)
    {
        var payload = new byte[4 + rooms.Count * RoomListRecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)rooms.Count));
        for (var index = 0; index < rooms.Count; index++)
        {
            var room = rooms[index];
            var record = payload.AsSpan(4 + index * RoomListRecordLength, RoomListRecordLength);
            EncodeFixed(room.Title, record.Slice(0, 24));
            EncodeFixed(room.Password, record.Slice(24, 8));
            record[32] = room.Started ? (byte)20 : (byte)10;
            record[33] = 100;
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(34, 2), room.Metadata);
            record[37] = room.MemberCount;
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(38, 2), room.RoomId);
        }
        return payload;
    }

    public static byte[] BuildQuickEnterResponse(
        ushort result,
        ushort roomId,
        byte responseField2,
        byte responseField3,
        ReadOnlySpan<byte> slotStates,
        string password,
        string title)
    {
        if (slotStates.Length != 3)
            throw new InvalidDataException("CF78 requires exactly three room slot states.");

        var payload = new byte[QuickEnterResponseLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), result);
        if (result is not (10 or 100))
            return payload;

        if (roomId == 0)
            throw new InvalidDataException("A successful CF78 response requires a room id.");

        payload[2] = responseField2;
        payload[3] = responseField3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), roomId);
        slotStates.CopyTo(payload.AsSpan(8, 3));
        EncodeFixed(password, payload.AsSpan(12, 8));
        EncodeFixed(title, payload.AsSpan(20, 24));
        return payload;
    }

    public static byte[] BuildGameData(ushort stage, ushort map)
    {
        var payload = new byte[GameDataResponseLength];
        payload[0] = 1;

        // The retail local arena initializer builds the same tables that the
        // network CFEC consumer installs: 150 coordinate pairs, 20 ownership
        // entries, 50 object types and 50 score types. One payload is cached
        // by the room and shared by every participant.
        for (var index = 0; index < 150; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                payload.AsSpan(0x002 + index * 4, 2),
                checked((short)RandomNumberGenerator.GetInt32(0, 100)));
            BinaryPrimitives.WriteInt16LittleEndian(
                payload.AsSpan(0x004 + index * 4, 2),
                checked((short)RandomNumberGenerator.GetInt32(0, 100)));
        }

        // The retail standalone initializer assigns all targets to local
        // authority (zero). Multiplayer clients still receive one identical
        // table, so deterministic P2P simulation is preserved.
        payload.AsSpan(0x25A, 20).Clear();

        // The original initializer creates 35 active item entries and leaves
        // the remaining 15 empty. Object types are the inclusive range 1..3.
        for (var index = 0; index < 35; index++)
            payload[0x26E + index] = checked((byte)RandomNumberGenerator.GetInt32(1, 4));

        // PVP score types 0..5 map to +10,+50,+100,+200,+500,-10.
        for (var index = 0; index < 50; index++)
            payload[0x2A0 + index] = checked((byte)RandomNumberGenerator.GetInt32(0, 6));

        payload[0x2D2] = checked((byte)Math.Min(stage, byte.MaxValue));
        payload[0x2D3] = checked((byte)Math.Min(map, byte.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x2D4, 2), map);
        return payload;
    }

    public static int ResolveScoreDelta(ReadOnlySpan<byte> gameData, ushort targetUid)
    {
        if (gameData.Length != GameDataResponseLength || targetUid >= 50)
            return 0;

        return gameData[0x2A0 + targetUid] switch
        {
            0 => 10,
            1 => 50,
            2 => 100,
            3 => 200,
            4 => 500,
            5 => -10,
            6 => -50,
            7 => -100,
            8 => -200,
            9 => -500,
            _ => 0
        };
    }

    public static (ushort Stage, ushort Map) ResolveGameSelectors(
        ushort requestedStage,
        ushort requestedMap)
    {
        _ = requestedStage;

        // The retail D036 constructor always writes stage 0. Map 6 is the
        // random-entry sentinel. Pin it to packaged stage 0 so every member
        // receives the same in-range selector; explicit maps 0..5 are kept.
        return (0, requestedMap < ArenaMapCount ? requestedMap : DefaultArenaMap);
    }

    public static byte[] BuildPvpResults(IReadOnlyList<ArenaPvpResultRecord> records)
    {
        var payload = new byte[4 + records.Count * PvpResultRecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)records.Count));
        for (var index = 0; index < records.Count; index++)
        {
            var value = records[index];
            var record = payload.AsSpan(4 + index * PvpResultRecordLength, PvpResultRecordLength);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(0, 2), value.UserUid);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(2, 2), value.Win);
            record[4] = value.LevelUp;
            record[6] = value.Grade;
            record[7] = value.PlayerLevel;
            record[9] = value.ArenaRank;
            var rewardCount = Math.Min(value.Rewards.Count, 4);
            record[10] = checked((byte)rewardCount);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(12, 4), value.Star);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(16, 4), value.AddedExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(20, 4), value.Hans);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(24, 4), value.CurrentExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(28, 4), value.LevelStartExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(32, 4), value.NextLevelExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(36, 4), value.ArenaRankMaximum);
            for (var rewardIndex = 0; rewardIndex < rewardCount; rewardIndex++)
                BinaryPrimitives.WriteUInt32LittleEndian(
                    record.Slice(40 + rewardIndex * 4, 4),
                    value.Rewards[rewardIndex]);
        }
        return payload;
    }

    public static bool TryParseGameEvent(
        ReadOnlySpan<byte> payload,
        out ArenaGameEventRequest request)
    {
        request = default;
        if (payload.Length != GameEventRequestLength)
            return false;

        var eventCode = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        if (eventCode is not (10 or 20 or 30 or 40 or 50 or 60 or 70 or 80))
            return false;

        request = new ArenaGameEventRequest(
            eventCode,
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2)),
            payload[4],
            payload[5],
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)),
            payload[10],
            payload[11],
            payload[12]);
        return true;
    }

    public static byte[] BuildGameEventResult(
        ushort playerUid,
        ushort currentHp,
        int currentScore,
        in ArenaGameEventRequest request)
    {
        var payload = new byte[GameEventResponseLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), playerUid);
        payload[2] = currentHp == 0 ? (byte)200 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), currentScore);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), currentHp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10, 2), request.PlayerDamage);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18, 2), request.PrimaryUid);
        payload[21] = checked((byte)request.EventCode);

        // D010 reads these selectors only in the 40/50 boss-object branches.
        if (request.EventCode is 40 or 50)
        {
            payload[23] = request.ObjectSelector;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(24, 2), request.ObjectUid);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(26, 2), request.TargetDamage);
        return payload;
    }

    private static bool TryDecodeFixed(
        ReadOnlySpan<byte> field,
        bool allowEmpty,
        out string value)
    {
        value = string.Empty;
        var terminator = field.IndexOf((byte)0);
        if (terminator < 0 || (!allowEmpty && terminator == 0))
            return false;
        try
        {
            value = Gbk.GetString(field.Slice(0, terminator));
            return allowEmpty || value.Length != 0;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void EncodeFixed(string value, Span<byte> destination)
    {
        destination.Clear();
        var offset = 0;
        foreach (var rune in (value ?? string.Empty).EnumerateRunes())
        {
            var bytes = Gbk.GetBytes(rune.ToString());
            if (offset + bytes.Length >= destination.Length)
                break;
            bytes.CopyTo(destination.Slice(offset));
            offset += bytes.Length;
        }
    }

    private static Encoding CreateGbkEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            936,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
