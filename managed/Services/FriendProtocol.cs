using System.Buffers.Binary;
using System.IO;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal static class FriendProtocol
{
    public const uint BridgeVersion = 1;
    public const uint BridgeCall = 1;
    public const uint BridgeSubscribe = 2;
    public const uint BridgeStatusSuccess = 0;
    public const uint NanaimoGameCode = 0x00017007;
    public const uint DefaultVirtualId = 1;

    public const uint GetFriendList = 0x4001;
    public const uint GetFriendInfo = 0x4002;
    public const uint RequestNewFriend = 0x4003;
    public const uint ConfirmNewFriend = 0x4004;
    public const uint BlockFriend = 0x4005;
    public const uint ChangeFriendMemo = 0x4006;
    public const uint AddFriendToCategory = 0x4007;
    public const uint DeleteFriendFromCategory = 0x4008;
    public const uint MoveFriendCategory = 0x4009;
    public const uint AddCategory = 0x4101;
    public const uint DeleteCategory = 0x4102;
    public const uint ChangeCategoryName = 0x4103;
    public const uint ChangeCategoryProperty = 0x4104;
    public const uint ChangeCategoryAllowType = 0x4105;

    public const uint RequestNewFriendEvent = 0x1005;
    public const byte ConfirmOk = 1;
    public const byte ConfirmDenied = 2;
    public const byte ConfirmLater = 3;
    public const byte CategoryDefault = 1;
    public const byte CategoryNotDelete = 2;
    public const byte AllowFromAll = 1;
    public const uint UserFlagBlocked = 0x00000001;
    public const uint UserFlagWaitingConfirm = 0x00000002;
    public const uint StatusOnline = 10;
    public const uint StatusOffline = 14;

    private const uint EnvelopeBegin = 0x001FCA34;
    private const uint EnvelopeEnd = 0x008A119E;
    private const uint FunctionObjectHeader = 0x666F0101; // CNMFunc('fo', 1, 1)
    private const uint FriendInfoObjectHeader = 0x75690301; // CNMFriendInfo('ui', 3, 1)
    private const uint AvatarObjectHeader = 0x61690101; // CNMAvatarItemInfo('ai', 1, 1)
    private const uint CategoryObjectHeader = 0x63690201; // CNMCateFriendInfo('ci', 2, 1)
    private const uint RequestDataObjectHeader = 0x72660101; // CNMRequestNewFriendData('rf', 1, 1)
    private const uint EventObjectHeader = 0x656F0101; // CNMEvent('eo', 1, 1)

    public static bool IsFriendFunction(uint functionCode) => functionCode is
        >= GetFriendList and <= MoveFriendCategory
        or >= AddCategory and <= ChangeCategoryAllowType;

    internal static void VerifyStaticContract()
    {
        const string expectedSuccess = "34CA1F000C00000001016F6604000000010000009E118A00";
        if (!Convert.ToHexString(BuildFunctionResponse(true)).Equals(expectedSuccess, StringComparison.Ordinal))
            throw new InvalidOperationException("NMCO function response vector does not match the official envelope.");

        var strings = new[] { string.Empty, new string('A', 63), new string('B', 64), new string('C', 16_383) };
        var writer = new NmcoWriter();
        foreach (var value in strings)
            writer.WriteString(value);
        var reader = new NmcoReader(writer.ToArray());
        foreach (var value in strings)
        {
            if (!reader.TryReadString(value.Length, out var decoded) || decoded != value)
                throw new InvalidOperationException("NMCO variable-length UTF-16 string vector failed.");
        }
        if (!reader.IsComplete)
            throw new InvalidOperationException("NMCO vector reader did not consume the exact object boundary.");

        var notification = new FriendRequestNotification(
            7, "target", "source", "飞行员", 1, 0, "hello", false);
        var eventEnvelope = BuildRequestEvent(notification, 1);
        if (!TryUnwrapEnvelope(eventEnvelope, out var eventObject)
            || BinaryPrimitives.ReadUInt32LittleEndian(eventObject.AsSpan(0, 4)) != EventObjectHeader
            || BinaryPrimitives.ReadUInt32LittleEndian(eventObject.AsSpan(8, 4)) != RequestDataObjectHeader)
            throw new InvalidOperationException("NMCO friend event object headers do not match the official contract.");
    }

    public static bool TryOpenCall(byte[] envelope, out uint serialKey, out NmcoReader body)
    {
        serialKey = 0;
        body = NmcoReader.Empty;
        if (!TryUnwrapEnvelope(envelope, out var serialized)
            || !TryUnwrapObject(serialized, FunctionObjectHeader, out var payload))
            return false;

        var reader = new NmcoReader(payload);
        if (!reader.TryReadUInt32(out serialKey))
            return false;
        body = reader;
        return true;
    }

    public static byte[] BuildFunctionResponse(bool success, Action<NmcoWriter>? writeReturn = null)
    {
        var payload = new NmcoWriter();
        payload.WriteUInt32(success ? 1u : 0u);
        writeReturn?.Invoke(payload);
        return WrapEnvelope(WrapObject(FunctionObjectHeader, payload.ToArray()));
    }

    public static byte[] BuildFriendListResponse(
        FriendListSnapshot snapshot,
        Func<long, bool> isOnline,
        uint ownerVirtualId)
        => BuildFunctionResponse(true, writer =>
        {
            writer.WriteUInt32((uint)snapshot.Categories.Count);
            foreach (var category in snapshot.Categories)
            {
                writer.WriteBytes(BuildCategoryObject(category, isOnline, ownerVirtualId));
            }
            writer.WriteUInt32((uint)snapshot.Unrelated.Count);
            foreach (var friend in snapshot.Unrelated)
                writer.WriteBytes(BuildFriendObject(friend, isOnline(friend.CharacterId), ownerVirtualId));
        });

    public static byte[] BuildFriendInfoResponse(
        FriendListRecord? friend,
        bool online,
        uint ownerVirtualId)
        => friend is null
            ? BuildFunctionResponse(false)
            : BuildFunctionResponse(true, writer =>
                writer.WriteBytes(BuildFriendObject(friend, online, ownerVirtualId)));

    public static byte[] BuildRequestEvent(
        FriendRequestNotification request,
        uint requesteeVirtualId)
    {
        var data = new NmcoWriter();
        data.WriteUInt32(request.SerialNo);
        data.WriteString(request.RequestId);
        data.WriteUInt32(NanaimoGameCode);
        WriteVirtualKey(data, NanaimoGameCode, requesteeVirtualId);
        WriteVirtualKey(data, NanaimoGameCode, request.RequesterVirtualId);
        data.WriteString(request.RequesterUsername);
        data.WriteString(request.RequesterCharacterName);
        data.WriteUInt32(request.InsertCategoryCode);
        data.WriteString(request.Message);
        data.WriteByte(request.AddToNxFriend ? (byte)1 : (byte)0);

        var eventPayload = new NmcoWriter();
        eventPayload.WriteBytes(WrapObject(RequestDataObjectHeader, data.ToArray()));
        return WrapEnvelope(WrapObject(EventObjectHeader, eventPayload.ToArray()));
    }

    public static bool TryReadFriendKey(NmcoReader reader, out FriendKey key)
    {
        key = default;
        if (!reader.TryReadUInt64(out var idCode)
            || !reader.TryReadUInt32(out var ownerGameCode)
            || !reader.TryReadUInt32(out var ownerVirtualId)
            || !reader.TryReadUInt32(out var friendGameCode)
            || !reader.TryReadUInt32(out var friendVirtualId))
            return false;
        key = new FriendKey(idCode, ownerGameCode, ownerVirtualId, friendGameCode, friendVirtualId);
        return true;
    }

    public static bool TryReadVirtualKey(NmcoReader reader, out VirtualKey key)
    {
        key = default;
        if (!reader.TryReadUInt32(out var gameCode)
            || !reader.TryReadUInt32(out var virtualId))
            return false;
        key = new VirtualKey(gameCode, virtualId);
        return true;
    }

    public static bool TryReadRequestData(NmcoReader reader, out FriendRequestCall request)
    {
        request = default;
        if (!reader.TryReadObject(RequestDataObjectHeader, out var objectReader)
            || !objectReader.TryReadUInt32(out var serialNo)
            || !objectReader.TryReadString(31, out var requestId)
            || !objectReader.TryReadUInt32(out var requesteeGameCode)
            || !TryReadVirtualKey(objectReader, out var toVirtual)
            || !TryReadVirtualKey(objectReader, out var fromVirtual)
            || !objectReader.TryReadString(31, out var fromLoginId)
            || !objectReader.TryReadString(31, out var fromNickName)
            || !objectReader.TryReadUInt32(out var insertCategoryCode)
            || !objectReader.TryReadString(255, out var message)
            || !objectReader.TryReadByte(out var addToNxFriend)
            || !objectReader.IsComplete)
            return false;
        request = new FriendRequestCall(
            serialNo,
            requestId,
            requesteeGameCode,
            toVirtual,
            fromVirtual,
            fromLoginId,
            fromNickName,
            insertCategoryCode,
            message,
            addToNxFriend != 0);
        return true;
    }

    public static long CharacterIdFromIdCode(ulong idCode)
        => (long)(uint)(idCode >> 32);

    public static ulong BuildIdCode(long characterId)
        => ((ulong)(uint)characterId << 32) | NanaimoGameCode;

    public static bool IsExpectedGameCode(uint gameCode)
        => gameCode is 0 or NanaimoGameCode;

    public static bool TryUnwrapEnvelope(byte[] envelope, out byte[] serialized)
    {
        serialized = [];
        if (envelope.Length < 20
            || BinaryPrimitives.ReadUInt32LittleEndian(envelope.AsSpan(0, 4)) != EnvelopeBegin)
            return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(envelope.AsSpan(4, 4));
        if (length > 1024 * 1024 || length != envelope.Length - 12)
            return false;
        var endOffset = checked(8 + (int)length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(envelope.AsSpan(endOffset, 4)) != EnvelopeEnd)
            return false;
        serialized = envelope.AsSpan(8, (int)length).ToArray();
        return true;
    }

    private static bool TryUnwrapObject(byte[] serialized, uint expectedHeader, out byte[] payload)
    {
        payload = [];
        if (serialized.Length < 8
            || BinaryPrimitives.ReadUInt32LittleEndian(serialized.AsSpan(0, 4)) != expectedHeader)
            return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(serialized.AsSpan(4, 4));
        if (length != serialized.Length - 8)
            return false;
        payload = serialized.AsSpan(8).ToArray();
        return true;
    }

    private static byte[] BuildCategoryObject(
        FriendCategoryRecord category,
        Func<long, bool> isOnline,
        uint ownerVirtualId)
    {
        var payload = new NmcoWriter();
        payload.WriteUInt32(category.CategoryCode);
        WriteVirtualKey(payload, NanaimoGameCode, ownerVirtualId);
        payload.WriteString(category.CategoryName);
        payload.WriteUInt32((uint)category.Friends.Count);
        foreach (var friend in category.Friends)
            payload.WriteBytes(BuildFriendObject(friend, isOnline(friend.CharacterId), ownerVirtualId));
        payload.WriteByte(category.Property);
        payload.WriteByte(category.AllowType);
        return WrapObject(CategoryObjectHeader, payload.ToArray());
    }

    private static byte[] BuildFriendObject(FriendListRecord friend, bool online, uint ownerVirtualId)
    {
        var payload = new NmcoWriter();
        payload.WriteString(friend.Username);
        payload.WriteUInt64(BuildIdCode(friend.CharacterId));
        var flags = (friend.IsBlocked ? UserFlagBlocked : 0u)
            | (friend.IsWaitingConfirmation ? UserFlagWaitingConfirm : 0u);
        payload.WriteUInt32(flags);
        payload.WriteString(string.Empty);
        payload.WriteUInt32(online ? StatusOnline : StatusOffline);
        payload.WriteUInt32(0);
        payload.WriteUInt32(0);
        payload.WriteUInt16(0);
        payload.WriteString(friend.Memo);

        var avatar = new NmcoWriter();
        avatar.WriteByte(0);
        avatar.WriteUInt32(0);
        payload.WriteBytes(WrapObject(AvatarObjectHeader, avatar.ToArray()));

        payload.WriteUInt64(BuildIdCode(friend.CharacterId));
        WriteVirtualKey(payload, NanaimoGameCode, ownerVirtualId);
        WriteVirtualKey(payload, NanaimoGameCode, DefaultVirtualId);
        payload.WriteString(friend.CharacterName);
        payload.WriteString(string.Empty);
        payload.WriteByte(online ? (byte)1 : (byte)0);
        payload.WriteUInt32((uint)Math.Max(0, friend.Level));
        payload.WriteUInt32(0);
        payload.WriteByte(0);
        payload.WriteByte(0);
        payload.WriteByte(0);
        return WrapObject(FriendInfoObjectHeader, payload.ToArray());
    }

    private static void WriteVirtualKey(NmcoWriter writer, uint gameCode, uint virtualId)
    {
        writer.WriteUInt32(gameCode);
        writer.WriteUInt32(virtualId);
    }

    private static byte[] WrapObject(uint header, byte[] payload)
    {
        var writer = new NmcoWriter();
        writer.WriteUInt32(header);
        writer.WriteUInt32((uint)payload.Length);
        writer.WriteBytes(payload);
        return writer.ToArray();
    }

    private static byte[] WrapEnvelope(byte[] serialized)
    {
        var writer = new NmcoWriter();
        writer.WriteUInt32(EnvelopeBegin);
        writer.WriteUInt32((uint)serialized.Length);
        writer.WriteBytes(serialized);
        writer.WriteUInt32(EnvelopeEnd);
        return writer.ToArray();
    }
}

internal readonly record struct VirtualKey(uint GameCode, uint VirtualId);

internal readonly record struct FriendKey(
    ulong IdCode,
    uint OwnerGameCode,
    uint OwnerVirtualId,
    uint FriendGameCode,
    uint FriendVirtualId);

internal readonly record struct FriendRequestCall(
    uint SerialNo,
    string RequestId,
    uint RequesteeGameCode,
    VirtualKey ToVirtual,
    VirtualKey FromVirtual,
    string FromLoginId,
    string FromNickName,
    uint InsertCategoryCode,
    string Message,
    bool AddToNxFriend);

internal sealed class NmcoReader
{
    private readonly byte[] _buffer;
    private int _position;

    public static NmcoReader Empty { get; } = new([]);

    public NmcoReader(byte[] buffer) => _buffer = buffer;
    public int Remaining => _buffer.Length - _position;
    public bool IsComplete => _position == _buffer.Length;

    public bool TryReadByte(out byte value)
    {
        value = 0;
        if (Remaining < 1)
            return false;
        value = _buffer[_position++];
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        value = 0;
        if (Remaining < 2)
            return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_position, 2));
        _position += 2;
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        value = 0;
        if (Remaining < 4)
            return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_position, 4));
        _position += 4;
        return true;
    }

    public bool TryReadUInt64(out ulong value)
    {
        value = 0;
        if (Remaining < 8)
            return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(_position, 8));
        _position += 8;
        return true;
    }

    public bool TryReadString(int maximumCharacters, out string value)
    {
        value = string.Empty;
        if (!TryReadLength(out var characters)
            || characters > maximumCharacters
            || characters > int.MaxValue / 2
            || Remaining < (int)characters * 2)
            return false;
        var byteLength = (int)characters * 2;
        value = Encoding.Unicode.GetString(_buffer, _position, byteLength);
        _position += byteLength;
        return value.IndexOf('\0') < 0;
    }

    public bool TryReadObject(uint expectedHeader, out NmcoReader reader)
    {
        reader = Empty;
        if (!TryReadUInt32(out var header)
            || header != expectedHeader
            || !TryReadUInt32(out var length)
            || length > int.MaxValue
            || Remaining < (int)length)
            return false;
        var payload = _buffer.AsSpan(_position, (int)length).ToArray();
        _position += (int)length;
        reader = new NmcoReader(payload);
        return true;
    }

    private bool TryReadLength(out uint value)
    {
        value = 0;
        if (!TryReadByte(out var first))
            return false;
        var marker = first & 3;
        if (marker == 0)
        {
            value = (uint)(first >> 2);
            return true;
        }
        if (marker == 1)
        {
            if (!TryReadByte(out var second))
                return false;
            value = (uint)((first | second << 8) >> 2);
            return true;
        }
        if (marker == 2)
        {
            if (Remaining < 3)
                return false;
            var encoded = (uint)(first
                | _buffer[_position] << 8
                | _buffer[_position + 1] << 16
                | _buffer[_position + 2] << 24);
            _position += 3;
            value = encoded >> 2;
            return true;
        }
        return TryReadUInt32(out value);
    }
}

internal sealed class NmcoWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteByte(byte value) => _stream.WriteByte(value);

    public void WriteUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteString(string value)
    {
        value ??= string.Empty;
        WriteLength((uint)value.Length);
        WriteBytes(Encoding.Unicode.GetBytes(value));
    }

    public void WriteBytes(byte[] value) => _stream.Write(value);
    public byte[] ToArray() => _stream.ToArray();

    private void WriteLength(uint value)
    {
        if (value < 1u << 6)
        {
            WriteByte((byte)(value << 2));
            return;
        }
        if (value < 1u << 14)
        {
            WriteUInt16((ushort)((value << 2) | 1));
            return;
        }
        if (value < 1u << 30)
        {
            WriteUInt32((value << 2) | 2);
            return;
        }
        WriteByte(3);
        WriteUInt32(value);
    }
}
