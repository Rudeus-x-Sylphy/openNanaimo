using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    internal static bool PatchNativeDungeonTitleFrame(byte[] frame, uint characterId, byte grade)
    {
        if (characterId is 0 or > ushort.MaxValue || frame.Length < 12
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2)) != 0xCF88)
            return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8, 2));
        if (count is < 1 or > 6 || frame.Length != 12 + count * 0x34)
            return false;
        for (int index = 0; index < count; index++)
        {
            int offset = 12 + index * 0x34;
            if (BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(offset, 2)) != characterId)
                continue;
            frame[offset + 7] = CharacterTitleState.Normalize(grade);
            RewriteNativeChecksum(frame);
            return true;
        }
        return false;
    }
}
