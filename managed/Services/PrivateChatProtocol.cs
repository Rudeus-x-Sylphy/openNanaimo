using System.Text;

namespace OpenNanaimo.Adapter.Services;

internal static class PrivateChatProtocol
{
    internal const int PayloadLength = 48;
    internal const int NameLength = 16;
    internal const int TextLength = 32;

    internal static bool TryReadRequest(ReadOnlySpan<byte> payload, out string peerName, out string text)
    {
        peerName = string.Empty;
        text = string.Empty;
        return payload.Length == PayloadLength
            && TryReadText(payload[..NameLength], out peerName)
            && TryReadText(payload.Slice(NameLength, TextLength), out text);
    }

    internal static bool TryReadText(ReadOnlySpan<byte> field, out string value)
    {
        value = string.Empty;
        var terminator = field.IndexOf((byte)0);
        if (terminator <= 0)
            return false;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            value = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback).GetString(field[..terminator]);
            return !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static byte[] BuildMessage(string senderName, string text)
    {
        var payload = new byte[PayloadLength];
        WriteText(payload.AsSpan(0, NameLength), senderName);
        WriteText(payload.AsSpan(NameLength, TextLength), text);
        return payload;
    }

    internal static void WriteText(Span<byte> field, string value)
    {
        field.Clear();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoder = Encoding.GetEncoding(936).GetEncoder();
        encoder.Convert(value.AsSpan(), field[..^1], true, out _, out _, out _);
    }
}
