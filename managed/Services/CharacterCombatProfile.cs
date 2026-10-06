using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal static class CharacterCombatProfile
{
    internal static ushort EffectiveDefense(CharacterRecord character)
    {
        long total = CharacterCombatProgression.NativeDefense(
            character.Vitality, character.Strength, character.DefenseFlat);
        var pet = PetProgression.GetState(character, character.EquippedPetItemCode);
        foreach (var code in new[] { pet.Accessory0, pet.Accessory1, pet.Accessory2 })
        {
            if (code == 0 || !ShopCatalog.TryGet(code, out var item)) continue;
            foreach (var effect in item.PetAccessoryEffects)
                if (effect.Enabled && effect.Type == 4 && float.IsFinite(effect.FixedValue))
                    total += (long)Math.Clamp(effect.FixedValue, 0, ushort.MaxValue);
        }
        return (ushort)Math.Min(ushort.MaxValue, total);
    }

    internal static void WriteProfileStats(Span<byte> payload, CharacterRecord? character)
    {
        if (payload.Length < 50) throw new ArgumentException("Profile payload is too short.", nameof(payload));
        // Defense is absolute; the attack word is a base before client display additions.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(38, 2),
            character is null ? (ushort)5 : EffectiveDefense(character));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(48, 2),
            (ushort)Math.Clamp(character?.Attack ?? 10, 0, ushort.MaxValue));
    }
}
