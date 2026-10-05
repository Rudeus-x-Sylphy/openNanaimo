using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static partial class LauncherTools
{
    static string Root="", Db="", CatalogRoot="";
    static PersistentStateStore Store => new(Db);
    static readonly Encoding Gbk=Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
    static readonly Dictionary<string,Dictionary<long,JsonObject>> Catalog=new();
    static JsonNode Clone(JsonNode n)=>JsonNode.Parse(n.ToJsonString())!;
    static JsonObject Obj(object value)=>(JsonObject)JsonSerializer.SerializeToNode(value)!;
    static JsonArray Arr(IEnumerable<object?> values)=>new(values.Select(x=>x is JsonNode n?Clone(n):JsonSerializer.SerializeToNode(x)).ToArray());
    static long Num(JsonNode? n,long fallback=0)=>n is null?fallback:long.Parse(n.ToJsonString(),CultureInfo.InvariantCulture);
    static long N(JsonNode? n,string key,long fallback=0)=>Num(n?[key],fallback);
    static string S(JsonNode? n,string key,string fallback="")=>n?[key]?.GetValue<string>()??fallback;
    static bool B(JsonNode? n,string key)=>n?[key] is JsonValue v&&(v.TryGetValue<bool>(out var b)?b:Num(v)!=0);
    static JsonArray A(JsonNode? n,string key)=>n?[key] as JsonArray??new();
    static IEnumerable<JsonObject> Objects(JsonNode? n,string key)=>A(n,key).Select(x=>x!.AsObject());
    static void Require(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
    static string Option(string[] args,string key,string fallback=""){var i=Array.IndexOf(args,key);if(i<0)return fallback;Require(i+1<args.Length,"Missing value for "+key);return args[i+1];}
    static bool Has(string[] args,string key)=>args.Contains(key,StringComparer.Ordinal);
    public static async Task RunAsync(string[] args)
    {
        Require(args.Length>0,"Missing tool command");
        if(args[0]=="compatibility"){Compatibility(args[1..]);return;}
        if(args[0]=="verify-resources"){VerifyResources(args[1..]);return;}
        Root=Path.GetFullPath(Option(args,"--root",Directory.GetCurrentDirectory()));Db=Path.GetFullPath(Option(args,"--database",Path.Combine(Root,"adapter_data","game.db")));CatalogRoot=Path.GetFullPath(Option(args,"--catalog-root",Root));
        if(args[0]=="state"){StateCommand(args[1..]);return;}
        Require(args[0]=="inventory"&&args.Length>=2,"Unknown tool command");LoadCatalogs();
        string hex=Option(args,"--name-hex").ToUpperInvariant();long? id=long.TryParse(Option(args,"--character-id"),out var parsed)?parsed:null;
        JsonNode result;
        switch(args[1])
        {
            case "profiles":result=Profiles(hex);break;
            case "snapshot":result=Snapshot(hex,id);break;
            case "apply": result=Apply(hex,ReadJson(Option(args,"--input")),id);break;
            case "clone":
                var source=Snapshot(Option(args,"--source-name-hex"),long.TryParse(Option(args,"--source-character-id"),out var sourceId)?sourceId:null);
                ValidateHex(hex);Require(!Profiles("").Any(p=>S(p,"name_hex")==hex||S(p,"username").Equals(ValidateHex(hex),StringComparison.OrdinalIgnoreCase)),"target profile already exists; select it instead");
                result=Apply(hex,ReadJson(Option(args,"--input")),null,source);break;
            case "selftest":result=Obj(new{status="INVENTORY_ADMIN_BACKEND_PASS",storage="sqlite",runtime="managed"});break;
            default:throw new InvalidDataException("Unknown inventory command");
        }
        var text=result.ToJsonString()+"\n";var output=Option(args,"--output");if(output.Length>0)AtomicWrite(output,Encoding.UTF8.GetBytes(text));else Console.Write(text);await Task.CompletedTask;
    }
    static JsonObject ReadJson(string path)=>(JsonObject)(JsonNode.Parse(File.ReadAllBytes(path))??throw new InvalidDataException("Empty JSON"));
    static string ValidateHex(string hex)
    {
        byte[] bytes;try{bytes=Convert.FromHexString(hex);}catch{throw new InvalidDataException("invalid local profile name");}
        Require(bytes.Length is >=1 and <=14,"local profile name must be 1..14 GBK bytes");var name=Gbk.GetString(bytes);Require(name==name.Trim()&&!name.Any(char.IsControl)&&Convert.ToHexString(Gbk.GetBytes(name))==hex.ToUpperInvariant(),"invalid local profile name");return name;
    }
    static string Hex(string name){try{return Convert.ToHexString(Gbk.GetBytes(name));}catch(EncoderFallbackException){return "";}}
    static string DraftPath(string hex){ValidateHex(hex);return Path.Combine(Root,"inventory_admin_profiles",hex.ToUpperInvariant()+".json");}
    static void LoadCatalogs(){Catalog.Clear();foreach(var pair in new[]{("clothing","inventory_clothing"),("pets","inventory_pets"),("gems","inventory_pet_gems"),("game","inventory_game_items"),("furniture","inventory_furniture"),("cards","inventory_cards")})Catalog[pair.Item1]=JsonNode.Parse(File.ReadAllBytes(Path.Combine(CatalogRoot,"gui_launcher","data",pair.Item2+".json")))!.AsArray().Select(x=>x!.AsObject()).ToDictionary(x=>N(x,"id"));}
    static SqliteConnection OpenDb(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Db,Pooling=false,DefaultTimeout=10}.ToString());c.Open();return c;}
    static SqliteCommand Command(SqliteConnection c,SqliteTransaction? t,string sql,params (string,object?)[] args){var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText=sql;foreach(var(k,v) in args)cmd.Parameters.AddWithValue(k,v??DBNull.Value);return cmd;}
    static List<Dictionary<string,object>> Rows(SqliteConnection c,SqliteTransaction? t,string sql,params (string,object?)[] args){using var cmd=Command(c,t,sql,args);using var r=cmd.ExecuteReader();var rows=new List<Dictionary<string,object>>();while(r.Read()){var row=new Dictionary<string,object>();for(int i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.GetValue(i);rows.Add(row);}return rows;}
    static int Exec(SqliteConnection c,SqliteTransaction? t,string sql,params (string,object?)[] args){using var cmd=Command(c,t,sql,args);return cmd.ExecuteNonQuery();}
    static bool Table(SqliteConnection c,string table)=>Rows(c,null,"SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name",("$name",table)).Count>0;
    static HashSet<string> Columns(SqliteConnection c,string table)=>Rows(c,null,"PRAGMA table_info("+table+")").Select(x=>(string)x["name"]).ToHashSet();
    static long V(Dictionary<string,object> row,string key,long fallback=0)=>row.TryGetValue(key,out var v)&&v!=DBNull.Value?Convert.ToInt64(v):fallback;
    static JsonObject ToJson(Dictionary<string,object> row){var o=new JsonObject();foreach(var(k,v) in row)o[k]=v==DBNull.Value?null:JsonSerializer.SerializeToNode(v);return o;}
    static JsonArray Profiles(string defaultHex)
    {
        var rows=new JsonArray();if(File.Exists(Db)){using var c=OpenDb();if(Table(c,"Accounts")&&Table(c,"Characters"))foreach(var raw in Rows(c,null,"SELECT a.Id account_id,a.Username username,a.IsOnline account_online,c.Id character_id,c.Name character_name,c.Gender gender,c.IsOnline character_online,c.Level level,c.MaxHp hp_max,c.MaxMp mp_max,c.Hans coin,c.Cash nana FROM Accounts a LEFT JOIN Characters c ON c.AccountId=a.Id ORDER BY a.Username COLLATE NOCASE")){var r=ToJson(raw);r["name_hex"]=Hex(S(r,"character_name"));r["display"]=S(r,"username")+" / "+S(r,"character_name","<no character>");rows.Add(r);}}
        foreach(var name in Store.List(Path.Combine(Root,"inventory_admin_profiles"),".json")){var hex=Path.GetFileNameWithoutExtension(name);ValidateHex(hex);if(rows.Any(r=>S(r,"name_hex")==hex))continue;var bytes=Store.Read(DraftPath(hex));if(bytes is not null)rows.Add(DraftRow(hex,JsonNode.Parse(bytes)!,true));}
        if(defaultHex.Length>0&&!rows.Any(r=>S(r,"name_hex")==defaultHex.ToUpperInvariant()))rows.Insert(0,DraftRow(defaultHex,new JsonObject()));return rows;
    }
    static JsonObject DraftRow(string hex,JsonNode state,bool persisted=false){var name=ValidateHex(hex);var r=Obj(new{account_id=(long?)null,username=name,character_id=(long?)null,character_name=name,name_hex=hex,display="本地档案 / "+name,source="sidecar",persisted});foreach(var k in new[]{"gender","level","hp_max","mp_max","attack","defense"})r[k]=state["profile"]?[k] is {} v?Clone(v):null;return r;}
    static JsonObject Snapshot(string hex,long? id)
    {
        if(id is not null)return DbSnapshot(id.Value);var saved=Store.Read(DraftPath(hex));if(saved is not null)return JsonNode.Parse(saved)!.AsObject();return LegacySnapshot(hex);
    }
    static readonly (string Key,string Column)[] ProfileFields=[("hp_max","MaxHp"),("mp_max","MaxMp"),("attack","AttackModifier"),("defense","DefenseFlat"),("apartment_recommendation_points","ApartmentRecommendationPoints")];
    static JsonObject DbSnapshot(long id)
    {
        using var c=OpenDb();var chars=Rows(c,null,"SELECT * FROM Characters WHERE Id=$id",("$id",id));Require(chars.Count==1,"selected character profile no longer exists");var ch=chars[0];var name=(string)ch["Name"];var appearance=ch["Appearance"] as byte[]??[];long Code(int offset)=>appearance.Length>=offset+4?BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(offset)):0;
        var shop=Obj(new{coin=V(ch,"Hans"),nana=V(ch,"Cash"),equipped=new[]{0,4,8,12,20}.Select(Code).ToArray(),effect=Code(24),selected_pet=V(ch,"EquippedPetItemCode"),clothing=Array.Empty<long>(),pets=Array.Empty<long>(),gift_pets=Array.Empty<long>(),cash_items=Array.Empty<long>()});
        var clothes=new JsonArray();var pets=new JsonArray();var games=new JsonArray();var furniture=new JsonArray();var cards=new JsonArray();
        foreach(var row in Rows(c,null,"SELECT * FROM CharacterItems WHERE CharacterId=$id ORDER BY ItemCode",("$id",id))){long code=V(row,"ItemCode"),qty=V(row,"Quantity");Require(qty>=0&&qty<=65535,"invalid inventory quantity");if(Catalog["clothing"].ContainsKey(code)){for(int i=0;i<qty;i++)clothes.Add(code);}else if(Catalog["pets"].TryGetValue(code,out var meta))pets.Add(Obj(new{code,upgrade_material=V(row,"PetCurrentStage")>0?N(meta,"upgrade_material"):0,gems=new[]{V(row,"PetAccessory0"),V(row,"PetAccessory1"),V(row,"PetAccessory2")}}));else if(Catalog["game"].TryGetValue(code,out meta))games.Add(Obj(new{code,count=qty,carrier=S(meta,"carrier","stackable")}));else if(Catalog["furniture"].TryGetValue(code,out meta))for(int i=0;i<qty;i++)furniture.Add(Obj(new{code,index=0,placed=false,type=N(meta,"type"),x=400,y=300,z=0,mirror=0}));}
        if(Table(c,"CharacterApartmentItems"))foreach(var row in Rows(c,null,"SELECT * FROM CharacterApartmentItems WHERE CharacterId=$id ORDER BY SlotIndex",("$id",id))){var code=V(row,"ItemCode");var r=furniture.FirstOrDefault(x=>N(x,"code")==code&&!B(x,"placed"))?.AsObject();if(r is null){r=Obj(new{code});furniture.Add(r);}r["index"]=V(row,"SlotIndex")+1;r["placed"]=true;r["type"]=V(row,"InteriorType");r["x"]=V(row,"PositionX");r["y"]=V(row,"PositionY");r["z"]=V(row,"Layer");r["mirror"]=V(row,"Mirror");}
        var used=furniture.Select(x=>N(x,"index")).ToHashSet();int next=1;foreach(var r in furniture)if(N(r,"index")==0){while(used.Contains(next))next++;r!["index"]=next;used.Add(next++);}
        if(Table(c,"CharacterCards"))foreach(var row in Rows(c,null,"SELECT CardCode,Quantity FROM CharacterCards WHERE CharacterId=$id ORDER BY CardCode",("$id",id)))cards.Add(Obj(new{code=V(row,"CardCode"),count=V(row,"Quantity")}));
        var profile=Obj(new{character_name=name,gender=V(ch,"Gender"),level=V(ch,"Level"),is_online=V(ch,"IsOnline")!=0,coin=V(ch,"Hans"),nana_point=V(ch,"Cash")});foreach(var(key,col) in ProfileFields)if(col!="ApartmentRecommendationPoints"||ch.ContainsKey(col))profile[key]=V(ch,col);
        if(ch.ContainsKey("SelectedSkill0")){profile["skill_slot_z"]=V(ch,"SelectedSkill0");profile["skill_slot_x"]=V(ch,"SelectedSkill1");profile["skill_slot_expiry"]=V(ch,"SkillSlotExpansionExpires");profile["skill_slot_expiry_apply"]=false;var grades=new long[16];foreach(var row in Rows(c,null,"SELECT SkillCode,Grade FROM CharacterSkills WHERE CharacterId=$id",("$id",id))){var index=V(row,"SkillCode")-52000000;if(index is >=0 and <16)grades[index]=V(row,"Grade");}for(int i=0;i<16;i++)profile["skill_grade"+i]=grades[i];profile["skill_projectile_route"]=Route(grades,0);profile["skill_meat_route"]=Route(grades,8);}
        return new JsonObject{{"version",2},{"source","database"},{"character_id",id},{"name_hex",Hex(name)},{"account_suffix","p_"+Hex(name)},{"profile",profile},{"shop",shop},{"clothing",clothes},{"pets",pets},{"game_items",games},{"furniture",furniture},{"cards",cards}};
    }
    static int Route(long[] grades,int offset)=>new[]{2,4,6}.Any(i=>grades[offset+i]>0)?1:new[]{3,5,7}.Any(i=>grades[offset+i]>0)?2:0;
    static IEnumerable<KeyValuePair<string,string>> LegacyLines(string name){var bytes=Store.Read(Path.Combine(Root,name));if(bytes is null)yield break;foreach(var raw in Encoding.ASCII.GetString(bytes).Split('\n')){var pair=raw.Trim().Split('=',2);if(pair.Length==2)yield return new(pair[0].Trim(),pair[1].Trim());}}
    static JsonArray LegacyCounts(string name){var rows=new JsonArray();foreach(var(k,v) in LegacyLines(name))if(uint.TryParse(k,out var code)&&ushort.TryParse(v,out var count)&&code>0&&count>0)rows.Add(Obj(new{code,count}));return rows;}
    static JsonObject LegacySnapshot(string hex)
    {
        var shop=Obj(new{coin=0L,nana=0L,equipped=new long[5],effect=0L,selected_pet=0L,clothing=Array.Empty<long>(),pets=Array.Empty<long>(),gift_pets=Array.Empty<long>(),cash_items=Array.Empty<long>()});
        foreach(var(k,v) in LegacyLines("nanaimo_inventory_state_v1.dat")){if(k is "coin" or "nana"){var a=v.Split(':');Require(a.Length==2,"Invalid legacy wallet");shop[k]=checked((long)(((ulong)uint.Parse(a[0])<<32)|uint.Parse(a[1])));}else if(k.StartsWith("equip")&&int.TryParse(k[5..],out int i)&&i is >=0 and <5)shop["equipped"]![i]=long.Parse(v);else if(k is "effect" or "selected_pet")shop[k]=long.Parse(v);else{var key=k switch{"owned_equipment"=>"clothing","owned_pet"=>"pets","gift_pet"=>"gift_pets","owned_misc"=>"cash_items",_=>""};if(key.Length>0)shop[key]!.AsArray().Add(long.Parse(v));}}
        var suffix="p_"+hex;var petRows=new Dictionary<long,long[]>();foreach(var(k,v) in LegacyLines("adapter_pet_items_"+suffix+".dat"))if(k=="pet"){var parts=v.Split(',').Select(long.Parse).ToArray();Require(parts.Length==5,"Invalid legacy pet");petRows[parts[0]]=parts;}
        var pets=new JsonArray();foreach(var item in A(shop,"pets")){var code=Num(item);petRows.TryGetValue(code,out var row);pets.Add(Obj(new{code,upgrade_material=row?[1]??0,gems=row?[2..5]??new long[3]}));}
        var games=LegacyCounts("card_synthesis_rewards_"+suffix+".dat");foreach(var r in games)r!["carrier"]="stackable";foreach(var group in A(shop,"cash_items").GroupBy(x=>Num(x)))games.Add(Obj(new{code=group.Key,count=group.Count(),carrier="cash"}));
        var furniture=new JsonArray();var bytes=Store.Read(Path.Combine(Root,"nanaimo_apartment_state_v1.dat"));if(bytes is not null){Require(bytes.Length==4076&&BitConverter.ToUInt32(bytes,0)==0x31545041&&BitConverter.ToUInt32(bytes,4)==1&&BitConverter.ToUInt32(bytes,8)<=254,"Invalid legacy apartment");for(int i=0;i<BitConverter.ToUInt32(bytes,8);i++){int p=12+16*i;furniture.Add(Obj(new{code=BitConverter.ToUInt32(bytes,p),index=BitConverter.ToUInt16(bytes,p+4),placed=bytes[p+6]!=0,type=bytes[p+7],x=BitConverter.ToInt16(bytes,p+8),y=BitConverter.ToInt16(bytes,p+10),z=bytes[p+12],mirror=bytes[p+13]}));}}
        return new JsonObject{{"version",2},{"source","sidecar"},{"name_hex",hex},{"account_suffix",suffix},{"shop",shop},{"clothing",Clone(A(shop,"clothing"))},{"pets",pets},{"game_items",games},{"furniture",furniture},{"cards",LegacyCounts("card_inventory_"+suffix+".dat")}};
    }
}
