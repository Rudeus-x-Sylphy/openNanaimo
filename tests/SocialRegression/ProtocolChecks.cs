using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static void CheckPolicies()
    {
        foreach (var (ring, recovery, experience) in new[]
            { (0u, 100, 100u), (43000001u, 120, 100u), (43000002u, 150, 120u), (43000003u, 200, 150u) })
        {
            Check((ring == 0) == !CoupleBenefitPolicy.TryResolveRingEffect(ring, out var effect)
                && (ring == 0 || effect.ItemCode == ring), "ring effect resolver " + ring);
            Check(CoupleBenefitPolicy.ScaleRecovery(100, ring) == recovery, "ring recovery tier " + ring);
            Check(CoupleBenefitPolicy.ScaleExperience(100, ring, true) == experience, "same-room experience tier " + ring);
            Check(CoupleBenefitPolicy.ScaleExperience(100, ring, false) == 100, "ordinary experience without partner " + ring);
            Check(CoupleBenefitPolicy.ScaleRecovery(int.MaxValue, ring) == ushort.MaxValue, "bounded food recovery " + ring);
            Check(CoupleBenefitPolicy.ScaleRecovery(-1, ring) == 0, "nonnegative food recovery " + ring);
            for (var code = 0; code <= byte.MaxValue; code++)
                Check(CoupleBenefitPolicy.IsEmotionAllowed((byte)code, 0, ring)
                    == (code <= 30 || code is >= 31 and <= 39 && ring is 43000002u or 43000003u), "emotion authorization " + ring + "/" + code);
        }
        Check(CoupleBenefitPolicy.ScaleExperience(uint.MaxValue, 43000003, true) == uint.MaxValue, "experience saturation");
        Check(!CoupleBenefitPolicy.TryResolveRingEffect(43009999, out _)
            && CoupleBenefitPolicy.NormalizeRingItemCode(43009999) == 0
            && CoupleBenefitPolicy.ScaleRecovery(100, 43009999) == 100
            && CoupleBenefitPolicy.ScaleExperience(100, 43009999, true) == 100
            && !CoupleBenefitPolicy.HasExtraEmotions(43009999), "unknown 43-domain ring fails closed");
        Check(!CoupleBenefitPolicy.IsEmotionAllowed(1, 9, 43000003), "emotion category authorization");
        var character = new CharacterRecord { Id = 1, Name = "Ring", Appearance = new byte[36], MaxHp = 100, MaxMp = 50 };
        var state = NativeDungeonState.Create(character, [], []);
        var original = state.Bytes.ToArray();
        CoupleBenefitPolicy.WriteNativeRing(state, 43000003);
        Check(state.Get(3996) == 43000003, "native ring state");
        Check(state.Bytes.AsSpan(0, 3996).SequenceEqual(original.AsSpan(0, 3996))
            && state.Bytes.AsSpan(4000).SequenceEqual(original.AsSpan(4000)), "native ring isolation from cards and inventory");
        CoupleBenefitPolicy.WriteNativeRing(state, 43009999);
        Check(state.Get(3996) == 0, "unknown ring normalization");
        var partner = new CharacterRecord { Id = 300, Name = "Partner", Level = 25 };
        var response = CoupleProtocol.BuildResponse(0xC584, partner.Name, 43000002, 0, 10, partner);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(26)) == 300, "couple partner identity above 255");
        Check(CoupleBenefitPolicy.BuildPartnerIdentity(300).SequenceEqual(new byte[] { 44, 1, 0, 0 }), "dungeon partner identity layout");
    }

    private static void CheckChatEncoding()
    {
        var request = PrivateChatProtocol.BuildMessage("玩家甲", "你好，岛屿！");
        Check(PrivateChatProtocol.TryReadRequest(request, out var name, out var text)
            && name == "玩家甲" && text == "你好，岛屿！", "private chat fixed GBK fields");
        request.AsSpan(7, 9).Fill(0xFF);
        Check(PrivateChatProtocol.TryReadRequest(request, out name, out text) && name == "玩家甲", "private chat ignores bytes after terminator");
        var clean = PrivateChatProtocol.BuildMessage(name, text);
        Check(clean.AsSpan(7, 9).IndexOfAnyExcept((byte)0) < 0, "private chat response initializes padding");
        foreach (var length in new[] { 0, 47, 49 })
            Check(!PrivateChatProtocol.TryReadRequest(new byte[length], out _, out _), "private chat length " + length);
        var malformed = PrivateChatProtocol.BuildMessage("Peer", "Text");
        malformed[0] = 0x81; malformed[1] = 0;
        Check(!PrivateChatProtocol.TryReadRequest(malformed, out _, out _), "invalid GBK chat recipient");
        Check(!PrivateChatProtocol.TryReadRequest(PrivateChatProtocol.BuildMessage("Peer", "Text\n"), out _, out _), "private chat control characters");
        var bounded = PrivateChatProtocol.BuildMessage(new string('甲', 30), new string('乙', 30));
        Check(PrivateChatProtocol.TryReadRequest(bounded, out name, out text)
            && name.Length == 7 && text.Length == 15, "GBK truncation retains complete characters and terminators");
    }
}
