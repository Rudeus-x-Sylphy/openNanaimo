using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // Ready-room construction initializes the battle actor's 64-bit wallet.
    // The revival dialog reads this actor field before submitting a payment.
    internal static bool PatchNativeReadyRoomWalletFrame(byte[] frame, CharacterRecord? owner)
    {
        if (owner is null || frame.Length != 0xB8
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2)) != 0xCF71
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0x1A, 2)) != GetSceneEntityId(owner))
            return false;

        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(0xA0, 8), Math.Max(0, owner.Hans));
        RewriteNativeChecksum(frame);
        return true;
    }
}
