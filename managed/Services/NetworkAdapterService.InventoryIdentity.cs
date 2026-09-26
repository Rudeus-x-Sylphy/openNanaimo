using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

// Wire identities survive removal of another instance. DB quick slots use a
// compact projection; the two representations must not be confused.
internal sealed class InventoryIdentityMap
{
    private readonly Dictionary<byte, (uint Code, long Order)> _items = [];
    private readonly HashSet<byte> _used = [];
    private byte[] _ordered = [];
    private long _nextOrder;

    internal void Synchronize(IReadOnlyList<uint> codes)
    {
        var needed = codes.GroupBy(code => code).ToDictionary(group => group.Key, group => group.Count());
        foreach (var group in _items.GroupBy(row => row.Value.Code).ToArray())
            foreach (var row in group.OrderBy(row => row.Value.Order)
                         .Skip(needed.GetValueOrDefault(group.Key)).ToArray())
                _items.Remove(row.Key);
        foreach (var group in needed)
        {
            var missing = group.Value - _items.Values.Count(row => row.Code == group.Key);
            while (missing-- > 0)
            {
                var available = Enumerable.Range(0, 84).Select(value => (byte)value)
                    .Where(identity => !_items.ContainsKey(identity)).ToArray();
                if (available.Length == 0)
                    throw new InvalidDataException("C430 inventory capacity exceeded");
                var identity = available.FirstOrDefault(value => !_used.Contains(value), available[0]);
                _items[identity] = (group.Key, _nextOrder++);
                _used.Add(identity);
            }
        }
        // Reusing a freed low wire handle must not move a newly acquired same-code
        // instance ahead of its survivors in the compact DB projection.
        _ordered = _items.OrderBy(row => row.Value.Code).ThenBy(row => row.Value.Order)
            .Select(row => row.Key).ToArray();
    }

    internal bool TryStorage(uint wire, out byte index, out uint code)
    {
        index = 0;
        code = 0;
        if (wire > 83 || !_items.TryGetValue((byte)wire, out var row))
            return false;
        var ordinal = Array.IndexOf(_ordered, (byte)wire);
        if (ordinal < 0)
            return false;
        index = (byte)ordinal;
        code = row.Code;
        return true;
    }

    internal byte Wire(int ordinal) => _ordered[ordinal];
    internal void Remove(uint wire)
    {
        if (wire <= 83)
            _items.Remove((byte)wire);
    }
}

public sealed partial class NetworkAdapterService
{
    private static InventoryIdentityMap SessionInventory(ConnectionSession session)
    {
        session.GameInventoryIdentities.Synchronize(GetGameInventoryItemCodes(session.Character));
        return session.GameInventoryIdentities;
    }
    private static bool TryResolveSessionInventoryIdentity(ConnectionSession session,uint wire,out byte ordinal,out uint code)
        =>SessionInventory(session).TryStorage(wire,out ordinal,out code);

    private static void RewriteSessionInventoryIdentities(Span<byte> frame,ConnectionSession session)
    {
        ushort opcode=BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6,2));
        if(session.Character is null || opcode is not (0xC430 or 0xC379)) return;
        var identities=SessionInventory(session);
        if(opcode==0xC430 && frame.Length>=12)
        {
            int count=BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(10,2));
            for(int i=0;i<count;i++) BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(16+i*8,2),identities.Wire(i));
        }
        if(opcode==0xC379 && frame.Length>=300)
            for(int i=0;i<6;i++)
            {
                int offset=228+i*12;
                if(BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset,4))==0)continue;
                int ordinal=(int)BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset+4,4));
                if(ordinal<GetGameInventoryItemCodes(session.Character).Length)
                    BinaryPrimitives.WriteUInt32LittleEndian(frame.Slice(offset+4,4),identities.Wire(ordinal));
            }
    }

    private static bool TryTranslateInventoryEquipment(ConnectionSession session,byte[] payload,out byte[] translated)
    {
        translated=payload.ToArray();
        if(payload.Length!=InventoryChangeRequestPayloadLength || payload[26]>6 || payload[27]>6)return false;
        var map=SessionInventory(session);
        for(int i=0;i<payload[26];i++)
        {
            if(!map.TryStorage(payload[28+i],out var ordinal,out _))return false;
            translated[28+i]=ordinal;
        }
        for(int i=0;i<payload[27];i++)
        {
            int row=36+i*8;
            uint wire=BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(row+4,2));
            uint code=BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(row,4));
            if(!map.TryStorage(wire,out var ordinal,out var owned) || owned!=code)return false;
            BinaryPrimitives.WriteUInt16LittleEndian(translated.AsSpan(row+4,2),ordinal);
        }
        return true;
    }
}
