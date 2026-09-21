using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace OpenNanaimo.Adapter.Services;

internal sealed record EntertainmentCreateRequest(
    byte[] RawPayload,
    string Title,
    string Password,
    ushort Mode,
    byte Runtime0,
    byte Runtime1,
    byte Runtime2,
    ushort Level,
    byte Runtime3,
    byte Runtime4,
    byte Runtime5);

internal readonly record struct EntertainmentRoomListEntry(
    ushort RoomId,
    string Title,
    string Password,
    byte State,
    byte Mode,
    byte Level);

internal readonly record struct EntertainmentWaitUserEntry(
    ushort UserUid,
    string Name,
    byte GameType);

internal readonly record struct EntertainmentEndGameRecord(
    ushort UserUid,
    byte Level,
    byte PreviousGrade,
    byte CurrentGrade,
    uint Score,
    uint CurrentExperience,
    uint LevelStartExperience,
    uint NextLevelExperience,
    uint GradeProgress,
    uint Reward0,
    uint Reward1,
    uint Reward2,
    byte ResultState,
    byte Flag,
    ushort ExtraFlag);

internal static class EntertainmentProtocol
{
    public const int CreateRequestLength = 44;
    public const int CreateResponseLength = 36;
    public const int EnterRequestLength = 12;
    public const int EnterResponseLength = 64;
    public const int RoomListRequestLength = 4;
    public const int RoomListRecordLength = 40;
    public const int WaitingMemberLength = 80;
    public const int WaitUserRecordLength = 20;
    public const int RankingResponseLength = 288;
    public const int GameDataResponseLength = 508;
    public const int GameDataRecordLength = 84;
    public const int GameDataRecordCount = 6;
    public const int GameDataPageCount = 2;
    public const int EndGameRecordLength = 64;
    public const int MaximumMembers = 6;

    private static readonly Encoding Gbk = CreateGbkEncoding();

    // Retail sub_660080 writes byte-sized lookup keys, not the four-digit
    // resource suffixes. The two client maps contain the 32 packaged animals.
    private const byte PackagedAnimalResourceKeyCount = 32;
    private static readonly (byte First, byte Second)[] ValidAnimalPairs =
        BuildValidAnimalPairs();

    public static bool TryParseCreateRequest(
        ReadOnlySpan<byte> payload,
        out EntertainmentCreateRequest? request)
    {
        request = null;
        if (payload.Length != CreateRequestLength
            || !TryDecodeFixed(payload.Slice(0, 24), false, out var title))
            return false;

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(24, 2));
        if (mode is not (100 or 200) || payload[35] > 1)
            return false;

        var password = string.Empty;
        if (payload[35] != 0
            && !TryDecodeFixed(payload.Slice(36, 8), false, out password))
            return false;

        request = new EntertainmentCreateRequest(
            payload.ToArray(),
            title,
            password,
            mode,
            payload[26],
            payload[27],
            payload[28],
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(30, 2)),
            payload[32],
            payload[33],
            payload[34]);
        return true;
    }

    public static bool TryParseRoomListRequest(
        ReadOnlySpan<byte> payload,
        out ushort startRoomId,
        out byte requestType,
        out byte option)
    {
        startRoomId = 0;
        requestType = 0;
        option = 0;
        if (payload.Length != RoomListRequestLength)
            return false;
        startRoomId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        requestType = payload[2];
        option = payload[3];
        return requestType is 100 or 200 && option is 100 or 200;
    }

    public static byte[] BuildLobbyInfo(uint localUid, byte gameType, byte runtime, uint value)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), localUid);
        payload[4] = gameType;
        payload[5] = runtime;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), value);
        return payload;
    }

    public static byte[] BuildRoomList(IReadOnlyList<EntertainmentRoomListEntry> rooms)
    {
        var payload = new byte[4 + rooms.Count * RoomListRecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)rooms.Count));
        for (var index = 0; index < rooms.Count; index++)
        {
            var room = rooms[index];
            var record = payload.AsSpan(4 + index * RoomListRecordLength, RoomListRecordLength);
            EncodeFixed(room.Title, record.Slice(0, 24));
            record[24] = room.State;
            record[25] = room.Mode;
            record[26] = room.Level;
            EncodeFixed(room.Password, record.Slice(28, 8));
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(38, 2), room.RoomId);
        }
        return payload;
    }

    public static byte[] BuildRanking() => new byte[RankingResponseLength];

    public static byte[] BuildWaitUsers(IReadOnlyList<EntertainmentWaitUserEntry> users)
    {
        var payload = new byte[4 + users.Count * WaitUserRecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)users.Count));
        for (var index = 0; index < users.Count; index++)
        {
            var record = payload.AsSpan(4 + index * WaitUserRecordLength, WaitUserRecordLength);
            EncodeFixed(users[index].Name, record.Slice(0, 16));
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(16, 2), users[index].UserUid);
            record[18] = users[index].GameType;
        }
        return payload;
    }

    public static byte[] BuildCreateResponse(
        byte result,
        bool owner,
        ushort roomId,
        uint ownerUid,
        string ownerName)
    {
        var payload = new byte[CreateResponseLength];
        payload[0] = result;
        if (result != 10)
            return payload;
        payload[1] = owner ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), roomId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), ownerUid);
        EncodeFixed(ownerName, payload.AsSpan(12, 24));
        return payload;
    }

    public static byte[] BuildEnterResponse(
        byte result,
        byte gameType,
        ushort roomId,
        string title,
        string password,
        uint ownerUid,
        string ownerName)
    {
        var payload = new byte[EnterResponseLength];
        payload[0] = result;
        if (result != 10)
            return payload;
        payload[1] = gameType;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), roomId);
        EncodeFixed(title, payload.AsSpan(4, 24));
        EncodeFixed(password, payload.AsSpan(28, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(36, 4), ownerUid);
        EncodeFixed(ownerName, payload.AsSpan(40, 24));
        return payload;
    }

    public static byte[] BuildWaitingMember(
        string name,
        ushort ownerUid,
        ushort memberUid,
        bool ready,
        ushort level,
        ushort gauge,
        string secondaryName,
        ReadOnlySpan<byte> appearance)
    {
        if (appearance.Length != 36)
            throw new InvalidDataException("CF6F requires a 36-byte appearance block.");
        var payload = new byte[WaitingMemberLength];
        EncodeFixed(name, payload.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16, 2), ownerUid);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18, 2), memberUid);
        // Payload +20..+22 are present in the wire record but are not read by
        // the retail CF6F consumer. Keep the reserved bytes zero.
        payload[23] = ready ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(24, 2), level);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(26, 2), Math.Min(gauge, (ushort)1000));
        EncodeFixed(secondaryName, payload.AsSpan(28, 16));
        appearance.CopyTo(payload.AsSpan(44, 36));
        return payload;
    }

    public static byte[] BuildGameData()
    {
        var payload = new byte[GameDataResponseLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 1);
        for (var recordIndex = 0; recordIndex < GameDataRecordCount; recordIndex++)
        {
            var record = payload.AsSpan(4 + recordIndex * GameDataRecordLength, GameDataRecordLength);
            var pairs = ValidAnimalPairs.ToArray();
            Random.Shared.Shuffle(pairs);
            for (var cell = 0; cell < 35; cell++)
            {
                record[cell] = pairs[cell].First;
                record[40 + cell] = pairs[cell].Second;
            }
            byte[] specialCells = [0, 1, 2, 3, 4, 5, 6, 7, 8];
            Random.Shared.Shuffle(specialCells);
            record[80] = specialCells[0];
            record[81] = specialCells[1];
            record[82] = specialCells[2];
            // Selector 0 is the official 3x3 layout and is the narrowest
            // packaged board. It avoids selecting an unsupported area.
            record[83] = 0;
        }
        return payload;
    }

    public static byte[] BuildPicnicPlayerState(ushort userUid, ushort state)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), userUid);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), state);
        return payload;
    }

    public static byte[] BuildPicnicCellState(
        uint userUid,
        ushort state,
        uint completionFlag)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), userUid);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), state);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), completionFlag);
        return payload;
    }

    private static (byte First, byte Second)[] BuildValidAnimalPairs()
    {
        var pairs = new List<(byte First, byte Second)>();
        for (byte first = 0; first < PackagedAnimalResourceKeyCount; first++)
        for (byte second = 0; second < PackagedAnimalResourceKeyCount; second++)
        {
            // Map order is 00x0/00x1 through 03x0/03x1. Retail generation
            // rejects a pair when either its two-digit group or animal type
            // matches, then rejects duplicate ordered pairs in the record.
            if (first / 8 == second / 8
                || first / 2 % 4 == second / 2 % 4)
                continue;
            pairs.Add((first, second));
        }
        return pairs.ToArray();
    }

    public static byte[] BuildEndGameInfo(IReadOnlyList<EntertainmentEndGameRecord> records)
    {
        var payload = new byte[8 + records.Count * EndGameRecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)records.Count));
        for (var index = 0; index < records.Count; index++)
        {
            var value = records[index];
            var record = payload.AsSpan(8 + index * EndGameRecordLength, EndGameRecordLength);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(0, 2), value.UserUid);
            record[5] = value.Level;
            record[6] = value.PreviousGrade;
            record[7] = value.CurrentGrade;
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(8, 4), value.Score);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(16, 4), value.CurrentExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(20, 4), value.LevelStartExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(24, 4), value.NextLevelExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(28, 4), value.GradeProgress);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(40, 4), value.Reward0);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(44, 4), value.Reward1);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(48, 4), value.Reward2);
            record[56] = value.ResultState;
            record[57] = value.Flag;
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(58, 2), value.ExtraFlag);
        }
        return payload;
    }

    private static bool TryDecodeFixed(ReadOnlySpan<byte> source, bool allowEmpty, out string value)
    {
        value = string.Empty;
        var terminator = source.IndexOf((byte)0);
        var encoded = terminator >= 0 ? source[..terminator] : source;
        if (encoded.Length == 0)
            return allowEmpty;
        try
        {
            value = Gbk.GetString(encoded);
            return !value.Any(char.IsControl);
        }
        catch (DecoderFallbackException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static void EncodeFixed(string value, Span<byte> destination)
    {
        destination.Clear();
        if (string.IsNullOrEmpty(value))
            return;
        var bytes = Gbk.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
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
