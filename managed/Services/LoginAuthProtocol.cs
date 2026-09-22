using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OpenNanaimo.Adapter.Services;

internal sealed class LoginAuthProtocol : IDisposable
{
    public const ushort PublicKeyRequestOpcode = 0x27EF;
    public const ushort PublicKeyResponseOpcode = 0x27EE;
    public const ushort AuthenticateRequestOpcode = 0x27F0;
    public const ushort AuthenticateResponseOpcode = 0x27F1;
    public const byte ProtocolVersion = 1;
    public const int EncryptedRequestLength = 256;
    public static readonly TimeSpan RequestClockTolerance = TimeSpan.FromMinutes(2);

    private static readonly byte[] RequestMagic = "FIA1"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly RSA _rsa;

    public LoginAuthProtocol(string keyPath)
    {
        _rsa = RSA.Create();
        if (File.Exists(keyPath))
        {
            var key = File.ReadAllBytes(keyPath);
            _rsa.ImportPkcs8PrivateKey(key, out var consumed);
            if (consumed != key.Length)
                throw new InvalidDataException("登录认证私钥包含多余数据。");
            return;
        }

        _rsa.KeySize = 2048;
        var createdKey = _rsa.ExportPkcs8PrivateKey();
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        using var output = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(createdKey);
        output.Flush(true);
    }

    public byte[] ExportPublicKey() => _rsa.ExportSubjectPublicKeyInfo();

    public bool TryDecryptRequest(
        ReadOnlySpan<byte> encrypted,
        out LoginAuthRequest request,
        out string error)
    {
        request = default;
        error = "认证请求无效。";
        if (encrypted.Length != EncryptedRequestLength)
            return false;

        byte[] plaintext;
        try
        {
            plaintext = _rsa.Decrypt(encrypted.ToArray(), RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }

        try
        {
            var span = plaintext.AsSpan();
            if (span.Length < 32 || !span[..4].SequenceEqual(RequestMagic) || span[4] != ProtocolVersion)
                return false;
            var timestamp = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(5, 8));
            var nonce = span.Slice(13, 16).ToArray();
            var usernameLength = span[29];
            var passwordLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(30, 2));
            var expectedLength = 32 + usernameLength + passwordLength;
            if (usernameLength is < 1 or > 12 || passwordLength is < 1 or > 64 || span.Length != expectedLength)
                return false;
            var usernameBytes = span.Slice(32, usernameLength);
            if (!IsAsciiDigits(usernameBytes))
                return false;
            var username = Encoding.ASCII.GetString(usernameBytes);
            var password = StrictUtf8.GetString(span.Slice(32 + usernameLength, passwordLength));
            if (password.Length is < 1 or > 16)
                return false;
            request = new LoginAuthRequest(username, password, timestamp, nonce);
            error = string.Empty;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool IsAsciiDigits(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
            if (character is < (byte)'0' or > (byte)'9')
                return false;
        return true;
    }

    public static byte[] BuildResponse(byte status, uint ticket, DateTime expiresAtUtc, string message)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        if (messageBytes.Length > 240)
            messageBytes = messageBytes[..240];
        var payload = new byte[12 + messageBytes.Length];
        payload[0] = ProtocolVersion;
        payload[1] = status;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2, 4), ticket);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(6, 4),
            (uint)Math.Clamp(new DateTimeOffset(expiresAtUtc).ToUnixTimeSeconds(), 0, uint.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10, 2), (ushort)messageBytes.Length);
        messageBytes.CopyTo(payload, 12);
        return payload;
    }

    public void Dispose() => _rsa.Dispose();
}

internal readonly record struct LoginAuthRequest(
    string Username,
    string Password,
    long Timestamp,
    byte[] Nonce);
