using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var repo = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.Combine(root, "adapter_data"));
var dbPath = Path.Combine(root, "adapter_data", "game.db");
File.WriteAllBytes(dbPath, []);
var database = new DatabaseService(Path.GetDirectoryName(dbPath)!);
await database.InitializeAsync();
var source = await database.OpenLocalAccountAsync("AccountA");
await database.CreateLocalCharacterAsync(source, "Alpha", 1);
var sourceCharacter = (await database.GetCharacterAsync(source))!;
async Task<object?> Sql(string sql)
{
    await using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
    await con.OpenAsync(); await using var cmd = con.CreateCommand(); cmd.CommandText = sql;
    return await cmd.ExecuteScalarAsync();
}
await Sql($"UPDATE Characters SET Hans=321,Cash=654,Level=12,MaxHp=2345,MaxMp=345,AttackModifier=6,DefenseFlat=7 WHERE Id={sourceCharacter.Id}");
await Sql($"INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES({sourceCharacter.Id},12000001,1,'protected'),({sourceCharacter.Id},13000001,2,'catalog')");
var appearance=sourceCharacter.Appearance.ToArray();
BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(20),10150103);
BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(24),10160017);
BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(28),15001003);
await Sql($"UPDATE Characters SET Appearance=X'{Convert.ToHexString(appearance)}',EquippedPetItemCode=15001003 WHERE Id={sourceCharacter.Id}");
await Sql($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES({sourceCharacter.Id},10150103,1,'clothing'),({sourceCharacter.Id},10160017,1,'effect'),({sourceCharacter.Id},15001003,1,'pet'),({sourceCharacter.Id},42000002,3,'item'),({sourceCharacter.Id},11000043,1,'furniture') ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=excluded.Quantity");
await Sql($"INSERT INTO CharacterApartmentItems(CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt) VALUES({sourceCharacter.Id},0,11000043,222,333,0,0,0,'placed')");
var sourceBefore = JsonSerializer.Serialize(await database.GetCharacterAsync(source));
foreach (var name in new[] { "nanaimo_inventory_state_v1.dat", "nanaimo_apartment_state_v1.dat", "launcher_profile.ini" })
    File.WriteAllText(Path.Combine(root, name), "DO_NOT_TOUCH");
// Supply actual catalogs for the production managed editor backend.
var dataDir = Path.Combine(root, "gui_launcher", "data"); Directory.CreateDirectory(dataDir);
foreach (var file in Directory.GetFiles(Path.Combine(repo,"gui_launcher","data"), "inventory_*.json"))
    File.Copy(file, Path.Combine(dataDir,Path.GetFileName(file)));
async Task Run(string program, params string[] arguments)
{
    var info = new ProcessStartInfo(program) { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
    foreach(var argument in arguments) info.ArgumentList.Add(argument);
    using var process=Process.Start(info)!;
    var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if(process.ExitCode!=0) throw new Exception(await stdout+await stderr);
}
var backend = Path.Combine(repo,"adapter_runtime","Nanaimo.Adapter.exe");
var snapshot = Path.Combine(root,"source.json");
await Run(backend,"--tools","inventory","snapshot","--root",root,"--character-id",sourceCharacter.Id.ToString(),"--output",snapshot);
string Hex(string s) => Convert.ToHexString(Encoding.GetEncoding(936).GetBytes(s));
foreach (var name in new[] { "CloneOne", "Rollback", "FreshPure", "RemoteCopy" })
    await Run(backend,"--tools","inventory","clone","--root",root,"--name-hex",Hex(name),"--source-name-hex",Hex("Alpha"),"--source-character-id",sourceCharacter.Id.ToString(),"--input",snapshot);
// Corrupt a catalog item so failure happens inside the database transaction.
var badPath=Path.Combine(root,"inventory_admin_profiles",Hex("Rollback")+".json");
var bad=System.Text.Json.Nodes.JsonNode.Parse(new PersistentStateStore(dbPath).Read(badPath)!)!;
bad["clothing"]!.AsArray().Add(99999999);new PersistentStateStore(dbPath).Put(badPath,Encoding.UTF8.GetBytes(bad.ToJsonString()));
using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(50));
var service=new NetworkAdapterService(database, _=>{}, root);
var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
var listener=service.RunLocalProfileListenerAsync(IPAddress.Loopback,port,cancellation.Token,root);
async Task<string> Register(string name,bool pure=false)
{
    byte[] payload;
    if(pure) payload=JsonSerializer.SerializeToUtf8Bytes(new { LocalAccount=name,PureNewPlayer=true });
    else
    {
        var output=Path.Combine(root,"registration.json");
        await Run("powershell.exe","-NoProfile","-ExecutionPolicy","Bypass","-File",
            Path.Combine(repo,"tests","LocalProfileRegistrationRegression","RegistrationPayload.ps1"),
            "-Repository",repo,"-DataRoot",root,"-Identity",name,"-OutputPath",output);
        payload=File.ReadAllBytes(output);
    }
    using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,port,cancellation.Token);
    var stream=client.GetStream();
    await stream.WriteAsync(BitConverter.GetBytes(payload.Length),cancellation.Token);await stream.WriteAsync(payload,cancellation.Token);
    var result=new byte[3];await stream.ReadExactlyAsync(result,cancellation.Token);return Encoding.ASCII.GetString(result);
}
void Check(bool condition,string message){if(!condition)throw new Exception(message);}
try
{
    Check(await Register("Alpha")=="OK\n","existing registration rejected");
    Check(JsonSerializer.Serialize(await database.GetCharacterAsync(source))==sourceBefore,"existing profile overwritten");
    Check(await Register("CloneOne")=="OK\n","clone registration rejected");
    var account=await database.OpenLocalAccountAsync("CloneOne");var clone=(await database.GetCharacterAsync(account))!;
    Check(clone is not null && clone.Name=="CloneOne" && clone.Id!=sourceCharacter.Id,"clone absent or wrong identity");
    Check(clone!.Hans==321 && clone.Cash==654 && clone.MaxHp==2345 && clone.MaxMp==345 && clone.Level==12,"clone resources lost");
    Check(BinaryPrimitives.ReadUInt32LittleEndian(clone.Appearance.AsSpan(20))==10150103 && BinaryPrimitives.ReadUInt32LittleEndian(clone.Appearance.AsSpan(24))==10160017 && clone.EquippedPetItemCode==15001003,"appearance/pet identity lost");
    foreach(var code in new uint[]{10150103,10160017,15001003,42000002,11000043})
        Check((long)(await Sql($"SELECT Quantity FROM CharacterItems WHERE CharacterId={clone.Id} AND ItemCode={code}"))! == (code==42000002 ? 3 : 1),"cloned item lost: "+code);
    Check((long)(await Sql($"SELECT PositionX FROM CharacterApartmentItems WHERE CharacterId={clone.Id} AND SlotIndex=0"))! == 222,"placed furniture lost");
    Check((long)(await Sql($"SELECT Quantity FROM CharacterCards WHERE CharacterId={clone.Id} AND CardCode=12000001"))! == 1,"protected card lost");
    Check((long)(await Sql($"SELECT Quantity FROM CharacterCards WHERE CharacterId={clone.Id} AND CardCode=13000001"))! == 2,"catalog card lost");
    await Sql($"UPDATE Characters SET Hans=777,CurrentMapId=3,PositionX=456 WHERE Id={clone.Id}");
    Directory.CreateDirectory(Path.Combine(root,"inventory_admin_profiles"));
    File.WriteAllText(Path.Combine(root,"inventory_admin_profiles",Hex("CloneOne")+".json"),"invalid-stale-snapshot");
    Check(await Register("CloneOne")=="OK\n","resume consulted stale clone");
    var resumed=(await database.GetCharacterAsync(account))!;
    Check(resumed.Hans==777 && resumed.CurrentMapId==3 && resumed.PositionX==456,"resume reset persisted progress");
    Check(await Register("Rollback")=="NO\n","bad clone accepted");
    Check((long)(await Sql("SELECT COUNT(*) FROM Accounts WHERE Username='Rollback'"))! == 0,"failed import left an account");
    Check(await Register("FreshPure",true)=="OK\n","pure mode rejected");
    var pureId=await database.OpenPureNewLocalAccountAsync("FreshPure");
    Check(await database.GetCharacterAsync(pureId) is null,"pure mode imported ordinary clone");
    var remoteId=await database.OpenLocalAccountAsync("RemoteCopy","192.0.2.30");
    Check(await database.GetCharacterAsync(remoteId) is null,"remote registration imported host snapshot");
    var queues=(ConcurrentDictionary<string,ConcurrentQueue<(long AccountId,bool PureNewPlayer,DateTime Expires)>>)typeof(NetworkAdapterService).GetField("_localLaunches",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(service)!;
    var queued=queues["127.0.0.1"].ToArray();
    Check(queued.Length==4 && queued[0].AccountId==source && queued[1].AccountId==account && queued[2].AccountId==account && !queued[1].PureNewPlayer && queued[3].PureNewPlayer,"selected account queue mismatch");
    Check(JsonSerializer.Serialize(await database.GetCharacterAsync(source))==sourceBefore,"clone changed source character");
    foreach(var name in new[] { "nanaimo_inventory_state_v1.dat","nanaimo_apartment_state_v1.dat","launcher_profile.ini" })
        Check(File.ReadAllText(Path.Combine(root,name))=="DO_NOT_TOUCH","shared file modified: "+name);
    Console.WriteLine("LOCAL_PROFILE_REGISTRATION_PASS existing=resume clone=transactional cards=preserved shared_files=unchanged pure=isolated rollback=atomic queue=selected_account");
}
finally{cancellation.Cancel();try{await listener;}catch(OperationCanceledException){}SqliteConnection.ClearAllPools();}
