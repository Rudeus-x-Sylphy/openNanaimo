using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
internal static partial class LauncherTools
{
    sealed record SceneTexture(int Id,byte[] Raw)
    {
        public string Name=>Encoding.ASCII.GetString(Raw,0,Array.IndexOf(Raw,(byte)0)).Replace('\\','/').ToLowerInvariant();
    }
    sealed record VillageScene(int Start,int End,int Grid,List<SceneTexture> Textures,List<List<byte[]>> Layers,byte[] Tail);
    sealed class Village
    {
        public byte[] Bytes;
        public readonly Dictionary<int,int> Directory=new();
        public readonly Dictionary<int,int> Grids=new();
        public Village(byte[] bytes)
        {
            Bytes=bytes;Require(bytes.Length>=13&&Encoding.ASCII.GetString(bytes,0,9)=="NANA_PACK","invalid village pack signature");int count=U32(bytes,9);Require(count>=26&&count<=(bytes.Length-13)/8,"invalid village directory");for(int i=0;i<count;i++){int key=U32(bytes,13+8*i),offset=U32(bytes,17+8*i);Require(Directory.TryAdd(key,offset),"duplicate village id");Require(offset>=13+count*8&&offset<=bytes.Length-4&&U32(bytes,offset)<=bytes.Length-offset-4,"invalid village record bounds");}
            var ranges=Directory.Values.Select(start=>(Start:start,End:start+4+U32(bytes,start))).OrderBy(x=>x.Start).ToArray();for(int i=1;i<ranges.Length;i++)Require(ranges[i-1].End<=ranges[i].Start,"overlapping village records");
            for(int page=0;page<25;page++){Require(Directory.TryGetValue(50000+page,out int start),"missing village page");int end=checked(start+4+U32(bytes,start));Require(end-start>=304&&U32(bytes,start+20)==16&&U32(bytes,start+24)==16&&U32(bytes,start+28)==50&&U32(bytes,start+32)==36,"unexpected village dimensions");int cursor=start+300;TextureTable(bytes,ref cursor,end,64);Require(cursor<=end-64800,"truncated village grid");Grids[page]=cursor;}
        }
        public static List<SceneTexture> TextureTable(byte[] bytes,ref int cursor,int end,int maximum=65536)
        {
            var rows=new List<SceneTexture>();var ids=new HashSet<int>();while(true){Require(cursor<=end-4,"truncated texture table");int id=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));cursor+=4;if(id==-1)return rows;Require(id>=0&&ids.Add(id)&&rows.Count<maximum&&cursor<=end-260,"invalid texture table");var raw=bytes.AsSpan(cursor,260).ToArray();Require(Array.IndexOf(raw,(byte)0)>=0,"unterminated texture name");rows.Add(new(id,raw));cursor+=260;}
        }
        public int Offset(int page,int x,int y,int field)=>Grids[page]+(x*36+y)*36+field*2;
        public int Get(int page,int x,int y,int field)=>BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(Offset(page,x,y,field)));
        public void Put(int page,int x,int y,int field,int value)=>BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(Offset(page,x,y,field)),checked((short)value));
        public VillageScene Scene(int page)
        {
            int start=Directory[50000+page],end=start+4+U32(Bytes,start),grid=Grids[page],cursor=grid+64800;var textures=TextureTable(Bytes,ref cursor,end);var ids=textures.Select(x=>x.Id).ToHashSet();var layers=new List<List<byte[]>>();for(int layer=0;layer<3;layer++){Require(cursor<=end-4,"truncated scene layer");int n=U32(Bytes,cursor);cursor+=4;Require(n<=(end-cursor)/16,"truncated scene objects");var rows=new List<byte[]>();for(int i=0;i<n;i++){var row=Bytes.AsSpan(cursor,16).ToArray();Require(ids.Contains(U32(row,0)),"unknown scene texture reference");rows.Add(row);cursor+=16;}layers.Add(rows);}return new(start,end,grid,textures,layers,Bytes.AsSpan(cursor,end-cursor).ToArray());
        }
        public bool IsNative()
        {
            var flags=new List<bool>();foreach(int page in new[]{18,19}){int start=Directory[50000+page],end=start+4+U32(Bytes,start),cursor=start+300;var floor=TextureTable(Bytes,ref cursor,end);var scene=Scene(page);flags.Add(floor.Concat(scene.Textures).Any(x=>x.Name==$"images/village_images/vill05_gate_ep0{page-11}.im3"));}if(!flags.Any(x=>x))return false;Require(flags.All(x=>x),"partial native L7/L8 layout");foreach(int page in new[]{18,19})for(int x=0;x<50;x++)for(int y=0;y<36;y++){bool action=page==18?x is >=8 and <=11&&y is >=5 and <=9:x is >=7 and <=12&&y is >=7 and <=9;Require((Get(page,x,y,13)==page+148)==action,"native village action grid mismatch");Require((Get(page,x,y,14)==page+151)==(x==10&&y==15),"native village return marker mismatch");}return true;
        }
        public byte[] RestoreScene(JsonObject recipe)
        {
            var scene=Scene(18);var placements=Objects(recipe,"placements").ToArray();var retired=new HashSet<string>(new[]{S(placements[0],"path"),S(placements[3],"path")}.Select(x=>x.ToLowerInvariant()));if(!scene.Textures.Any(x=>retired.Contains(x.Name)))return Bytes;
            var remove=new HashSet<int>();foreach(var placement in placements){string name=S(placement,"path").ToLowerInvariant();int layer=(int)N(placement,"layer");float x=placement["x"]!.GetValue<float>(),y=placement["y"]!.GetValue<float>();var ids=scene.Textures.Where(t=>t.Name==name).Select(t=>t.Id).ToHashSet();
                var matches=scene.Layers.SelectMany((rows,li)=>rows.Where(row=>ids.Contains(U32(row,0))).Select(row=>(Layer:li,Row:row))).ToArray();bool At(byte[] row)=>BitConverter.ToSingle(row,4)==x&&BitConverter.ToSingle(row,8)==y&&BitConverter.ToSingle(row,12)==0;
                if(retired.Contains(name))Require(matches.Length>0&&matches.All(m=>m.Layer==layer&&At(m.Row)),"unknown retired artwork placement; preserve for review");
                foreach(int id in ids){scene.Layers[layer].RemoveAll(row=>U32(row,0)==id&&At(row));if(!scene.Layers.Any(rows=>rows.Any(row=>U32(row,0)==id)))remove.Add(id);}
            }
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);writer.Write(Bytes.AsSpan(scene.Start,scene.Grid+64800-scene.Start));foreach(var texture in scene.Textures)if(!remove.Contains(texture.Id)){writer.Write(texture.Id);writer.Write(texture.Raw);}writer.Write(-1);foreach(var rows in scene.Layers){writer.Write(rows.Count);foreach(var row in rows)writer.Write(row);}writer.Write(scene.Tail);var record=stream.ToArray();BinaryPrimitives.WriteInt32LittleEndian(record,record.Length-4);int delta=record.Length-(scene.End-scene.Start);var result=new byte[Bytes.Length+delta];Bytes.AsSpan(0,scene.Start).CopyTo(result);record.CopyTo(result,scene.Start);Bytes.AsSpan(scene.End).CopyTo(result.AsSpan(scene.Start+record.Length));int count=U32(result,9);for(int i=0;i<count;i++){int off=U32(result,17+8*i);if(off>=scene.End)BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(17+8*i),off+delta);}return result;
        }
    }
    static byte[] PatchVillage(byte[] bytes,JsonObject recipe)
    {
        var pack=new Village(bytes.ToArray());bool native=pack.IsNative();for(int x=38;x<50;x++)for(int y=14;y<21;y++)pack.Put(17,x,y,7,0);for(int y=14;y<21;y++){foreach(int x in new[]{48,49})pack.Put(17,x,y,10,18);pack.Put(17,47,y,11,18);}
        if(!native){for(int x=0;x<50;x++)for(int y=0;y<36;y++){if(pack.Get(18,x,y,13)==166)pack.Put(18,x,y,13,-1);if(pack.Get(18,x,y,14)==169)pack.Put(18,x,y,14,-1);}for(int x=22;x<28;x++)foreach(int y in new[]{3,4})pack.Put(18,x,y,13,166);pack.Put(18,25,6,14,169);}
        var route=A(recipe,"route").Select(x=>(int)Num(x)).ToArray();var neighbors=route.Skip(6).ToDictionary(page=>page,_=>new Dictionary<char,int>());char Side(int source,int target)=>(target-source) switch{1=>'E',-1=>'W',5=>'S',-5=>'N',_=>throw new InvalidDataException("Invalid village route")};for(int i=0;i<route.Length-1;i++){int a=route[i],b=route[i+1];if(native&&a==19&&b==14)continue;if(neighbors.TryGetValue(a,out var an))an[Side(a,b)]=b;if(neighbors.TryGetValue(b,out var bn))bn[Side(b,a)]=a;}
        foreach(var(page,directions) in neighbors)foreach(char side in new[]{'W','E','N','S'})
        {
            var exits=new List<(int X,int Y)>();var markers=new List<(int X,int Y)>();if(side is 'E' or 'W'){foreach(int x in side=='E'?new[]{48,49}:new[]{0,1})for(int y=14;y<21;y++)exits.Add((x,y));for(int y=14;y<21;y++)markers.Add((side=='E'?47:2,y));}else{for(int x=22;x<28;x++){foreach(int y in side=='S'?new[]{34,35}:new[]{0,1})exits.Add((x,y));markers.Add((x,side=='S'?33:2));}}
            bool linked=directions.TryGetValue(side,out var target);foreach(var(x,y) in exits){pack.Put(page,x,y,7,linked?0:1);pack.Put(page,x,y,10,linked?target:-1);}foreach(var(x,y) in markers){if(linked){pack.Put(page,x,y,7,0);pack.Put(page,x,y,10,-1);pack.Put(page,x,y,13,-1);}pack.Put(page,x,y,11,linked?target:-1);}
        }
        return pack.RestoreScene(recipe);
    }
}
