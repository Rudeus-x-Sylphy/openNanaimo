using System.Buffers.Binary;
using System.IO;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal static class CoupleProtocol
{
    internal const ushort RingRequestOpcode = 0xC583;
    internal const ushort RingResponseOpcode = 0xC584;
    internal const ushort SeparationRequestOpcode = 0xC585;
    internal const ushort SeparationResponseOpcode = 0xC586;

    internal const int RingRequestPayloadLength = 24;
    internal const int RingResponsePayloadLength = 28;
    internal const int SeparationRequestPayloadLength = 24;
    internal const int SeparationResponsePayloadLength = 24;
    internal const int PeerNameLength = 16;

    internal const ushort Accepted = 10;
    internal const ushort Refused = 20;
    internal const ushort Unavailable = 2;

    static CoupleProtocol()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static bool IsRequestOpcode(ushort opcode)
        => opcode is RingRequestOpcode or SeparationRequestOpcode;

    internal static bool IsResponseOpcode(ushort opcode)
        => opcode is RingResponseOpcode or SeparationResponseOpcode;

    internal static ushort GetResponseOpcode(ushort requestOpcode) => requestOpcode switch
    {
        RingRequestOpcode => RingResponseOpcode,
        SeparationRequestOpcode => SeparationResponseOpcode,
        _ => throw new ArgumentOutOfRangeException(nameof(requestOpcode))
    };

    internal static int GetPayloadLength(ushort opcode) => opcode switch
    {
        RingRequestOpcode => RingRequestPayloadLength,
        RingResponseOpcode => RingResponsePayloadLength,
        SeparationRequestOpcode => SeparationRequestPayloadLength,
        SeparationResponseOpcode => SeparationResponsePayloadLength,
        _ => -1
    };

    internal static bool TryReadPeerName(ReadOnlySpan<byte> payload, out string name)
    {
        name = string.Empty;
        if (payload.Length < PeerNameLength)
            return false;
        var terminator = payload[..PeerNameLength].IndexOf((byte)0);
        var encoded = terminator < 0 ? payload[..PeerNameLength] : payload[..terminator];
        if (encoded.IsEmpty)
            return false;
        try
        {
            name = Encoding.GetEncoding(
                936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback).GetString(encoded);
            return !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static uint ReadItemCode(ReadOnlySpan<byte> payload)
        => BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));

    internal static ushort ReadInventorySlot(ushort opcode, ReadOnlySpan<byte> payload)
        => opcode is RingRequestOpcode or RingResponseOpcode
            ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(20, 2))
            : payload[20];

    internal static ushort ReadStatus(ushort opcode, ReadOnlySpan<byte> payload)
        => opcode switch
        {
            RingResponseOpcode => BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(22, 2)),
            SeparationResponseOpcode => payload[21],
            _ => throw new ArgumentOutOfRangeException(nameof(opcode))
        };

    internal static bool IsOfficialResponseStatus(ushort status)
        => status is 1 or 2 or 3 or 4 or 6 or 7 or Accepted or Refused;

    internal static byte[] BuildRequestRelay(
        ushort opcode,
        ReadOnlySpan<byte> request,
        CharacterRecord sender)
    {
        if (!IsRequestOpcode(opcode) || request.Length != GetPayloadLength(opcode))
            throw new InvalidDataException($"Invalid couple request layout for 0x{opcode:X4}.");
        var payload = request.ToArray();
        WriteName(payload.AsSpan(0, PeerNameLength), sender.Name);
        payload[22] = (byte)Math.Clamp(sender.Level, 1, byte.MaxValue);
        payload[23] = (byte)Math.Clamp(sender.Gender, 0, 1);
        return payload;
    }

    internal static byte[] BuildResponse(
        ushort responseOpcode,
        string partnerName,
        uint itemCode,
        ushort inventorySlot,
        ushort status,
        CharacterRecord partner)
    {
        if (!IsResponseOpcode(responseOpcode))
            throw new InvalidDataException($"Invalid couple response opcode 0x{responseOpcode:X4}.");
        var payload = new byte[GetPayloadLength(responseOpcode)];
        WriteName(payload.AsSpan(0, PeerNameLength), partnerName);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), itemCode);
        var peerUid = (ushort)Math.Clamp(partner.Id, 1L, byte.MaxValue);
        if (responseOpcode == RingResponseOpcode)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20, 2), inventorySlot);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22, 2), status);
            payload[24] = (byte)Math.Clamp(partner.Level, 1, byte.MaxValue);
            payload[25] = (byte)Math.Clamp(partner.Gender, 0, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(26, 2), peerUid);
        }
        else
        {
            payload[20] = checked((byte)inventorySlot);
            payload[21] = checked((byte)status);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22, 2), peerUid);
        }
        return payload;
    }

    private static void WriteName(Span<byte> destination, string value)
    {
        destination.Clear();
        var encoder = Encoding.GetEncoding(936).GetEncoder();
        encoder.Convert(value.AsSpan(), destination[..^1], true, out _, out _, out _);
    }
}
