using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentProtocolChecks
{
    public static void Run()
    {
        var empty = NetworkAdapterService.BuildMiniRoomObjectInfoPayload([]);
        Check(empty.Length == 1012, "C393 payload is fixed at 1012 bytes");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(empty) == 0,
            "empty C393 snapshot has zero objects");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(empty.AsSpan(2, 2)) == 2000,
            "empty C393 snapshot carries final-info 2000");
        Check(empty.AsSpan(4).ToArray().All(value => value == 0),
            "empty C393 object area is zero-filled");

        var mixed = new ApartmentPlacementRecord[]
        {
            Row(0, 11000001, 0, 0, 0, 0, 0),
            Row(1, 11100001, 0, 0, 0, 0, 1),
            Row(2, 11200001, -123, 456, 7, 1, 2),
            Row(3, 11300001, 100, -200, 8, 0, 3),
            Row(4, 11400001, 300, 400, 9, 1, 4)
        };
        var payload = NetworkAdapterService.BuildMiniRoomObjectInfoPayload(mixed);
        Check(payload.Length == 1012
            && BinaryPrimitives.ReadUInt16LittleEndian(payload) == 3
            && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2)) == 2000,
            "C393 filters surfaces and retains a fixed final snapshot");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4)) == 11200001
            && BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(8, 2)) == -123
            && BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(10, 2)) == 456
            && payload[12] == 7
            && payload[13] == 2
            && payload[14] == 1
            && payload[15] == 2,
            "C393 first object uses the 12-byte C424-compatible layout");
        Check(payload.AsSpan(40).ToArray().All(value => value == 0),
            "C393 unused object rows remain zero-filled");

        var overflow = Enumerable.Range(0, 85)
            .Select(index => Row(checked((byte)index), 11210000u + checked((uint)index),
                checked((short)index), checked((short)(index + 1)), 2, 0, 2))
            .ToArray();
        var capped = NetworkAdapterService.BuildMiniRoomObjectInfoPayload(overflow);
        Check(capped.Length == 1012
            && BinaryPrimitives.ReadUInt16LittleEndian(capped) == 84,
            "C393 caps the fixed snapshot at 84 objects");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(capped.AsSpan(1000, 4)) == 11210083u
            && capped[1011] == 83,
            "C393 object 84 exactly fills the final 12-byte row");

        Console.WriteLine("APARTMENT_PROTOCOL_CHECKS_PASS c393_payload=1012 frame=1020 final=2000 rows=84 zero_fill=PASS");
    }

    private static ApartmentPlacementRecord Row(
        byte slot, uint code, short x, short y, byte layer, byte mirror, byte type)
        => new()
        {
            SlotIndex = slot,
            ItemCode = code,
            X = x,
            Y = y,
            Layer = layer,
            Mirror = mirror,
            InteriorType = type
        };

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
