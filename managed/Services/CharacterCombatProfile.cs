using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal static class CharacterCombatProfile
{
    internal static ushort EffectiveDefense(CharacterRecord character)
    {
        long basis = CharacterCombatProgression.NativeDefense(character.Level, character.DefenseFlat);
        var avatar = AvatarEquipmentCatalog.GetDefenseBonuses(character.Appearance);
        long flat = avatar.Flat, percent = avatar.Percent;
        var pet = PetProgression.GetState(character, character.EquippedPetItemCode);
        foreach (var code in new[] { pet.Accessory0, pet.Accessory1, pet.Accessory2 })
        {
            if (code == 0 || !ShopCatalog.TryGet(code, out var item)) continue;
            foreach (var effect in item.PetAccessoryEffects)
                if (effect.Enabled && effect.Type == 4 && float.IsFinite(effect.FixedValue))
                {
                    // As with PA attack rows, a fixed value wins over its percent field.
                    if (effect.FixedValue > 0) flat += (long)Math.Min(effect.FixedValue, ushort.MaxValue);
                    else if (effect.FixedValue == 0) percent += effect.Percent;
                }
        }
        // One common base and one final truncation; slot ordering cannot compound defense.
        return (ushort)Math.Min(ushort.MaxValue, basis + flat + basis * percent / 100);
    }

    internal static void WriteProfileStats(Span<byte> payload, CharacterRecord? character)
    {
        if (payload.Length < 50) throw new ArgumentException("Profile payload is too short.", nameof(payload));
        // Defense is absolute; the attack word is a base before client display additions.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(38, 2),
            character is null ? (ushort)0 : EffectiveDefense(character));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(48, 2),
            (ushort)Math.Clamp(character?.Attack ?? 0, 0, ushort.MaxValue));
    }
}
