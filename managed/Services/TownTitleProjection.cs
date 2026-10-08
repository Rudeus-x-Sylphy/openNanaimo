using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

internal static class TownTitleProjection
{
    internal const int UserInfoPayloadLength = 104;
    internal const int ControlOffset = 52;
    internal const int GradeShift = 6;
    internal const uint GradeMask = 0x3Fu << GradeShift;
    internal const int LevelShift = 12;
    internal const uint LevelMask = 0xFFu << LevelShift;
    internal const ushort MaximumSceneId = 0x0FFF;
    internal const int DisplayModeOffset = 102; // C36A full-frame +0x6E, NOT a UID
    internal const int LoadDisplayModeOffset = 0x2D4 - 8;
    internal const int LoadOptionFlagsOffset = 0x2D6 - 8;

    internal static ushort NormalizeDisplayMode(ushort mode) => mode <= 2 ? mode : (ushort)2;

    internal static bool TryReadOptions(ReadOnlySpan<byte> payload, out ushort mode, out ushort flags)
    {
        mode = flags = 0;
        if (payload.Length != 4) return false;
        mode = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        flags = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        return mode <= 2 && flags <= 0x3F;
    }

    internal static void WriteLoadOptions(Span<byte> payload, ushort mode, ushort flags)
    {
        if (payload.Length != 728 - 8) throw new ArgumentException("Invalid C355 payload length.", nameof(payload));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(LoadDisplayModeOffset, 2), NormalizeDisplayMode(mode));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(LoadOptionFlagsOffset, 2), (ushort)(flags & 0x3F));
    }

    internal static bool TryApply(Span<byte> payload, ushort sceneEntityId, byte grade, int? characterLevel = null)
    {
        if (payload.Length != UserInfoPayloadLength || sceneEntityId is 0 or > MaximumSceneId)
            return false;

        var control = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(ControlOffset, 4));
        if (control >> 20 != sceneEntityId)
            return false;

        var projected = (control & ~GradeMask)
            | ((uint)CharacterTitleState.Normalize(grade) << GradeShift);
        if (characterLevel is { } level)
            projected = (projected & ~LevelMask)
                | ((uint)Math.Clamp(level, 1, CharacterProgression.MaximumLevel) << LevelShift);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(ControlOffset, 4), projected);
        return true;
    }
}
