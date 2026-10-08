using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenNanaimo.Adapter.Services;

// Read-only audit of the published assembly. Known rejections are findings,
// NOT passing end-to-end behavior. No sockets, database writes or game sessions.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var checks = new List<object>();
foreach (uint card in new uint[] { 22000001, 22000010, 22000011, 22000018 })
foreach (ushort key in new ushort[] { 10, 20, 30, 40 })
{
    var p = new byte[20];
    BinaryPrimitives.WriteUInt16LittleEndian(p, 40);
    BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), key);
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), card);
    bool accepted = ExperienceCardPolicy.TryParseActivation(p, out var code);
    checks.Add(new { card, key, request = Convert.ToHexString(p), accepted_as_experience = accepted, parsed_code = code });
}
var failure = ExperienceCardPolicy.BuildActivationResult(false, default);
var resultCode = BinaryPrimitives.ReadUInt32LittleEndian(failure);
var dll = typeof(DatabaseService).Assembly.Location;
Console.WriteLine(JsonSerializer.Serialize(new {
    kind = "published-parser-audit-not-runtime-acceptance",
    assembly = dll, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))),
    cases = checks,
    experience_failure = new { result = resultCode, payload = Convert.ToHexString(failure) },
    runtime_acceptance = false
}, new JsonSerializerOptions { WriteIndented = true }));
