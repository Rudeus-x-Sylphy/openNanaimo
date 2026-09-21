using System.Buffers.Binary;
using System.IO;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal static class MentorProtocol
{
    internal const ushort CreateSchoolingRoomRequestOpcode = 0xC578;
    internal const ushort CreateSchoolingRoomResponseOpcode = 0xC579;
    internal const ushort ListRequestOpcode = 0xC57D;
    internal const ushort ListResponseOpcode = 0xC57E;
    internal const ushort AdvertiseRequestOpcode = 0xC57F;
    internal const ushort AdvertiseResponseOpcode = 0xC580;
    internal const ushort StopAdvertisingRequestOpcode = 0xC581;
    internal const ushort StopAdvertisingResponseOpcode = 0xC582;
    internal const ushort StudentRequestOpcode = 0xC583;
    internal const ushort StudentResponseOpcode = 0xC584;
    internal const ushort TeacherRequestOpcode = 0xC585;
    internal const ushort TeacherResponseOpcode = 0xC586;

    internal const int AdvertisementRequestPayloadLength = 0;
    internal const int AdvertisementResponsePayloadLength = 4;
    internal const int ListRequestPayloadLength = 4;
    internal const int ListHeaderPayloadLength = 8;
    internal const int ListEntryLength = 20;
    internal const int ListPageSize = 10;
    internal const int MaximumListPage = 3;
    internal const int StudentRequestPayloadLength = 24;
    internal const int StudentResponsePayloadLength = 28;
    internal const int TeacherRequestPayloadLength = 24;
    internal const int TeacherResponsePayloadLength = 24;
    internal const int PeerNameLength = 16;

    internal const ushort Accepted = 10;
    internal const ushort Refused = 20;
    internal const ushort TimedOut = 2;

    private const int PeerNameOffset = 0;
    private const int LessonCodeOffset = 16;
    private const int PeerUidOffset = 20;
    private const int RequestLevelOffset = 22;
    private const int RequestGenderOffset = 23;
    private const int StudentResponseStatusOffset = 22;
    private const int StudentResponseLevelOffset = 24;
    private const int StudentResponseGenderOffset = 25;
    private const int StudentResponsePeerUidOffset = 26;
    private const int TeacherResponseStatusOffset = 21;
    private const int TeacherResponsePeerUidOffset = 22;

    static MentorProtocol()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    internal static bool IsRequestOpcode(ushort opcode)
        => opcode is StudentRequestOpcode or TeacherRequestOpcode;

    internal static bool IsResponseOpcode(ushort opcode)
        => opcode is StudentResponseOpcode or TeacherResponseOpcode;

    internal static int GetExpectedPayloadLength(ushort opcode) => opcode switch
    {
        AdvertiseRequestOpcode or StopAdvertisingRequestOpcode => AdvertisementRequestPayloadLength,
        StudentRequestOpcode => StudentRequestPayloadLength,
        StudentResponseOpcode => StudentResponsePayloadLength,
        TeacherRequestOpcode => TeacherRequestPayloadLength,
        TeacherResponseOpcode => TeacherResponsePayloadLength,
        _ => -1
    };

    internal static ushort GetResponseOpcode(ushort requestOpcode) => requestOpcode switch
    {
        StudentRequestOpcode => StudentResponseOpcode,
        TeacherRequestOpcode => TeacherResponseOpcode,
        _ => throw new ArgumentOutOfRangeException(nameof(requestOpcode))
    };

    internal static byte[] BuildAdvertisementResult(uint result)
    {
        var payload = new byte[AdvertisementResponsePayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, result);
        return payload;
    }

    internal static byte[] BuildListResult(
        uint requestedPage,
        IReadOnlyList<CharacterRecord> advertisingCharacters)
    {
        var page = Math.Min(requestedPage, (uint)MaximumListPage);
        var start = checked((int)page * ListPageSize);
        var count = start >= advertisingCharacters.Count
            ? 0
            : Math.Min(ListPageSize, advertisingCharacters.Count - start);
        var payload = new byte[ListHeaderPayloadLength + (count * ListEntryLength)];

        payload[0] = 0;
        payload[1] = 0;
        payload[2] = page > 0 ? (byte)1 : (byte)0;
        payload[3] = start + count < advertisingCharacters.Count && page < MaximumListPage
            ? (byte)1
            : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)count);

        for (var index = 0; index < count; index++)
        {
            var character = advertisingCharacters[start + index];
            var entry = payload.AsSpan(
                ListHeaderPayloadLength + (index * ListEntryLength),
                ListEntryLength);
            entry[0] = (byte)Math.Clamp(character.Gender, 0, 1);
            entry[1] = (byte)Math.Clamp(character.Level, 1, byte.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2, 2), GetLessonPeerUid(character));
            WriteFixedGbk(entry.Slice(4, PeerNameLength), character.Name);
        }

        return payload;
    }

    internal static bool TryReadPeerName(ReadOnlySpan<byte> payload, out string name)
    {
        name = string.Empty;
        if (payload.Length < PeerNameLength)
            return false;
        var terminator = payload[..PeerNameLength].IndexOf((byte)0);
        var encoded = terminator < 0
            ? payload[..PeerNameLength]
            : payload[..terminator];
        if (encoded.IsEmpty)
            return false;
        try
        {
            var strictGbk = Encoding.GetEncoding(
                936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            name = strictGbk.GetString(encoded);
            return !string.IsNullOrWhiteSpace(name);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static uint ReadLessonCode(ReadOnlySpan<byte> payload)
        => BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(LessonCodeOffset, 4));

    internal static byte ReadRequestedPeerUid(ReadOnlySpan<byte> payload)
        => payload[PeerUidOffset];

    internal static ushort ReadResponseStatus(ushort opcode, ReadOnlySpan<byte> payload)
        => opcode switch
        {
            StudentResponseOpcode => BinaryPrimitives.ReadUInt16LittleEndian(
                payload.Slice(StudentResponseStatusOffset, 2)),
            TeacherResponseOpcode => payload[TeacherResponseStatusOffset],
            _ => throw new ArgumentOutOfRangeException(nameof(opcode))
        };

    internal static bool IsOfficialResponseStatus(ushort status)
        => status is 1 or TimedOut or 3 or 4 or 6 or 7 or Accepted or Refused;

    internal static bool ResponseMatchesRequest(
        ushort responseOpcode,
        ReadOnlySpan<byte> requestPayload,
        ReadOnlySpan<byte> responsePayload)
    {
        if (!IsResponseOpcode(responseOpcode)
            || responsePayload.Length != GetExpectedPayloadLength(responseOpcode)
            || requestPayload.Length != GetExpectedPayloadLength(
                responseOpcode == StudentResponseOpcode
                    ? StudentRequestOpcode
                    : TeacherRequestOpcode))
            return false;

        if (ReadLessonCode(requestPayload) != ReadLessonCode(responsePayload))
            return false;

        return responseOpcode == StudentResponseOpcode
            ? BinaryPrimitives.ReadUInt16LittleEndian(requestPayload.Slice(PeerUidOffset, 2))
              == BinaryPrimitives.ReadUInt16LittleEndian(responsePayload.Slice(PeerUidOffset, 2))
            : requestPayload[PeerUidOffset] == responsePayload[PeerUidOffset];
    }

    internal static byte[] BuildRequestRelay(
        ushort opcode,
        ReadOnlySpan<byte> requestPayload,
        CharacterRecord sender)
    {
        var expectedLength = GetExpectedPayloadLength(opcode);
        if (!IsRequestOpcode(opcode) || requestPayload.Length != expectedLength)
            throw new InvalidDataException($"Invalid mentor request layout for 0x{opcode:X4}.");

        var payload = requestPayload.ToArray();
        WriteFixedGbk(payload.AsSpan(PeerNameOffset, PeerNameLength), sender.Name);
        payload[RequestLevelOffset] = (byte)Math.Clamp(sender.Level, 1, byte.MaxValue);
        payload[RequestGenderOffset] = (byte)Math.Clamp(sender.Gender, 0, 1);
        return payload;
    }

    internal static byte[] BuildResponseRelay(
        ushort opcode,
        ReadOnlySpan<byte> responsePayload,
        CharacterRecord responder)
    {
        var expectedLength = GetExpectedPayloadLength(opcode);
        if (!IsResponseOpcode(opcode) || responsePayload.Length != expectedLength)
            throw new InvalidDataException($"Invalid mentor response layout for 0x{opcode:X4}.");

        var payload = responsePayload.ToArray();
        WriteFixedGbk(payload.AsSpan(PeerNameOffset, PeerNameLength), responder.Name);
        var responderUid = GetLessonPeerUid(responder);
        if (opcode == StudentResponseOpcode)
        {
            payload[StudentResponseLevelOffset] = (byte)Math.Clamp(responder.Level, 1, byte.MaxValue);
            payload[StudentResponseGenderOffset] = (byte)Math.Clamp(responder.Gender, 0, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(StudentResponsePeerUidOffset, 2),
                responderUid);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(TeacherResponsePeerUidOffset, 2),
                responderUid);
        }
        return payload;
    }

    internal static byte[] BuildUnavailableResponse(
        ushort requestOpcode,
        ReadOnlySpan<byte> requestPayload,
        string requestedPeerName)
    {
        if (!IsRequestOpcode(requestOpcode)
            || requestPayload.Length != GetExpectedPayloadLength(requestOpcode))
            throw new InvalidDataException($"Invalid mentor request layout for 0x{requestOpcode:X4}.");

        var responseOpcode = GetResponseOpcode(requestOpcode);
        var payload = new byte[GetExpectedPayloadLength(responseOpcode)];
        WriteFixedGbk(payload.AsSpan(PeerNameOffset, PeerNameLength), requestedPeerName);
        requestPayload.Slice(LessonCodeOffset, 6).CopyTo(payload.AsSpan(LessonCodeOffset, 6));
        if (responseOpcode == StudentResponseOpcode)
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(StudentResponseStatusOffset, 2), TimedOut);
        else
            payload[TeacherResponseStatusOffset] = (byte)TimedOut;
        return payload;
    }

    internal static ushort GetLessonPeerUid(CharacterRecord character)
    {
        // The retail transition consumer takes this WORD and passes only its
        // low byte to the local character lookup.
        return (ushort)Math.Clamp(character.Id, 1L, (long)byte.MaxValue);
    }

    private static void WriteFixedGbk(Span<byte> destination, string value)
    {
        destination.Clear();
        var encoder = Encoding.GetEncoding(936).GetEncoder();
        encoder.Convert(
            value.AsSpan(),
            destination[..^1],
            true,
            out _,
            out _,
            out _);
    }
}
