using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal static class CharacterTitleState
{
    internal const byte MaximumGrade = 42;

    internal static byte GetGrade(CharacterRecord? character)
        => character is null ? (byte)0 : Normalize(character.DungeonGrade);

    internal static byte Normalize(byte grade)
        => grade <= MaximumGrade ? grade : (byte)0;
}
