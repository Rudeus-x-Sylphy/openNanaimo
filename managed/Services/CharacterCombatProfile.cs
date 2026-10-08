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

    /// <summary>
    /// Panel attack displayed by C376/C377: the character's own base plus the
    /// selected pet's panel attack (base plus its type1 gem increment). Only an
    /// owned, selected pet contributes; a stored zero is an explicit unequip.
    /// The pet's monster-category terms stay on the separate dungeon carriers, so
    /// this sum is a display value and never a damage input.
    /// </summary>
    internal static ushort DisplayAttack(CharacterRecord character)
    {
        long attack = character.Attack;
        var petCode = character.EquippedPetItemCode;
        if (petCode != 0 && character.Items.Any(item => item.ItemCode == petCode && item.Quantity > 0))
            attack += PetProgression.GetTotalAttack(PetProgression.GetState(character, petCode));
        return (ushort)Math.Clamp(attack, 0, ushort.MaxValue);
    }

    internal static void WriteProfileStats(Span<byte> payload, CharacterRecord? character)
    {
        if (payload.Length < 50) throw new ArgumentException("Profile payload is too short.", nameof(payload));
        // Defense is absolute and already includes apparel and gem type4; the attack
        // word carries character growth plus the pet panel attack, both still before
        // the client's own local display addend.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(38, 2),
            character is null ? (ushort)0 : EffectiveDefense(character));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(48, 2),
            character is null ? (ushort)0 : DisplayAttack(character));
    }
}
