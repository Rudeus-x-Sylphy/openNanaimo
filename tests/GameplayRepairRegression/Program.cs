using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class Program
{
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++; Console.WriteLine("PASS " + message);
    }
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    static void Main()
    {
        foreach (var (score, bonus, scaled, expected) in new (uint,uint,uint,uint)[]
            { (15,0,18,4), (15,5,22,5), (10003,0,15004,3751), (0,0,0,0),
              (uint.MaxValue,1,uint.MaxValue,1073741823), (100,200,150,37) })
        {
            var row = new byte[0x34]; row[0] = 27; row[11] = 5;
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0x1C), score);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0x28), bonus);
            DungeonExperiencePolicy.WriteSettlementScore(row,score,scaled);
            Check(U32(row,0x0C)==expected && U32(row,0x2C)==scaled,
                "settlement converts the scaled total once at 25 percent");
            Check((ulong)U32(row,0x24)+U32(row,0x28)==U32(row,0x2C)
                && U32(row,0x1C)==scaled && row[0]==27 && row[11]==5,
                "score equals hit plus bonus while identity and rating remain unchanged");
        }
        foreach (var kind in new ushort[] {10,20,30,60})
        foreach (var hp in new ushort[] {0,900})
        {
            var hit=ArenaProtocol.BuildPlayerHitResult(kind,17,23,0,1,hp,100);
            Check(hit.Length==16 && hit[10]==(hp==0?200:100)
                && BinaryPrimitives.ReadUInt16LittleEndian(hit.AsSpan(12))==100
                && BinaryPrimitives.ReadUInt16LittleEndian(hit.AsSpan(14))==100,
                "both player damage display branches carry actual damage");
        }
        foreach (var mode in new ushort[] {100,200})
        {
            var create=new byte[44];Encoding.ASCII.GetBytes("Room").CopyTo(create,0);
            BinaryPrimitives.WriteUInt16LittleEndian(create.AsSpan(24),mode);
            Check(EntertainmentProtocol.TryParseCreateRequest(create,out var request)
                && request!.Mode==mode,"room creation preserves the selected mode");
            var list=EntertainmentProtocol.BuildRoomList([new EntertainmentRoomListEntry(37,"Room","",10,(byte)mode,0)]);
            Check(list.Length==44 && U32(list,40)==37,"room list exposes its exact identifier at record offset 36");
        }
        var sessionType=typeof(NetworkAdapterService).GetNestedType("ConnectionSession",BindingFlags.NonPublic)!;
        var actor=Activator.CreateInstance(sessionType,true)!;
        void Set(string name,object value)=>sessionType.GetProperty(name)!.SetValue(actor,value);
        Set("Character",new CharacterRecord { Id=123 });
        Set("LastReportedPositionX",(ushort)712);Set("LastReportedPositionY",(ushort)144);Set("TownPage",(byte)2);
        Set("LastTownMovement",Enumerable.Repeat((byte)0xFF,16).ToArray());
        var position=(byte[])typeof(NetworkAdapterService).GetMethod("BuildTownCurrentPosition",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[actor])!;
        Check(position.AsSpan(0,8).ToArray().All(b=>b==0x44)
            && BinaryPrimitives.ReadUInt16LittleEndian(position.AsSpan(8))==712
            && BinaryPrimitives.ReadUInt16LittleEndian(position.AsSpan(10))==144,
            "town snapshots use current position with a stationary action");
        Console.WriteLine($"GAMEPLAY_REPAIR_REGRESSION_PASS checks={checks}");
    }
}
