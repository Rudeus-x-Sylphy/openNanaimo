using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static partial class LauncherTools
{
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    static string SafePath(string root,string relative)
    {
        Require(!string.IsNullOrWhiteSpace(relative)&&!relative.Contains('\\')&&!relative.Contains(':')&&!relative.StartsWith('/')&&relative.Split('/').All(p=>p.Length>0&&p!="."&&p!=".."),"Unsafe resource path");root=Path.GetFullPath(root);var path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));Require(path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Path escaped root");
        for(var part=new DirectoryInfo(Path.GetDirectoryName(path)!);part is not null;part=part.Parent)if(part.Exists)Require((part.Attributes&FileAttributes.ReparsePoint)==0,"Reparse-point resource path refused");if(File.Exists(path))Require((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Resource link refused");return path;
    }
    static void AtomicWrite(string path,byte[] bytes)
    {
        path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(bytes);f.Flush(true);}File.Move(tmp,path,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    static byte[]? ReadOptional(string path)=>File.Exists(path)?File.ReadAllBytes(path):null;
    static bool Same(byte[]? a,byte[]? b)=>a is null?b is null:b is not null&&a.AsSpan().SequenceEqual(b);
    static JsonObject Recipe(){using var stream=typeof(LauncherTools).Assembly.GetManifestResourceStream("Nanaimo.ClientCompatibility.json")??throw new InvalidOperationException("Embedded compatibility recipe missing");return JsonNode.Parse(stream)!.AsObject();}
    static int U16(byte[] b,int p){Require(p>=0&&p<=b.Length-2,"Truncated binary word");return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));}
    static int U32(byte[] b,int p){Require(p>=0&&p<=b.Length-4,"Truncated binary dword");var n=BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));Require(n<=int.MaxValue,"Binary index overflow");return (int)n;}
    static int VaOffset(byte[] bytes,long va,int length)
    {
        Require(bytes.Length>=64&&bytes[0]=='M'&&bytes[1]=='Z',"Invalid executable DOS header");int pe=U32(bytes,60);Require(pe<=bytes.Length-24&&bytes.AsSpan(pe,4).SequenceEqual(new byte[]{80,69,0,0}),"Invalid PE header");int count=U16(bytes,pe+6),optional=U16(bytes,pe+20),start=pe+24;Require(count is >0 and <=96&&optional>=96&&U16(bytes,start)==0x10b,"Expected PE32 image");long imageBase=U32(bytes,start+28),rva=va-imageBase;int table=checked(start+optional);Require(table<=bytes.Length-count*40,"Truncated section table");var matches=new List<int>();for(int i=0;i<count;i++){int row=table+i*40,virtualAddress=U32(bytes,row+12),rawSize=U32(bytes,row+16),rawOffset=U32(bytes,row+20);Require(rawOffset<=bytes.Length&&rawSize<=bytes.Length-rawOffset,"Invalid PE raw section");long delta=rva-virtualAddress;if(delta>=0&&delta+length<=rawSize)matches.Add(checked(rawOffset+(int)delta));}Require(matches.Count==1,"Expected a unique file-backed VA: "+va.ToString("X"));return matches[0];
    }
    static void PatchSite(byte[] exe,JsonObject site,JsonArray operations)
    {
        var target=Convert.FromHexString(S(site,"target"));var offset=VaOffset(exe,N(site,"va"),target.Length);var before=exe.AsSpan(offset,target.Length).ToArray();bool equal=before.AsSpan().SequenceEqual(target);bool known=A(site,"known").Any(n=>before.AsSpan().SequenceEqual(Convert.FromHexString(n!.GetValue<string>())));bool hash=A(site,"known_hashes").Any(n=>n!.GetValue<string>().Equals(Hash(before),StringComparison.OrdinalIgnoreCase));Require(equal||known||hash,"Unreviewed executable bytes: "+S(site,"operation"));target.CopyTo(exe,offset);operations.Add(Obj(new{operation=S(site,"operation"),va=N(site,"va"),changed=!equal,hash_gate_used=false}));
    }
    static void Compatibility(string[] args)
    {
        var source=Path.GetFullPath(Option(args,"--source-root"));var output=Path.GetFullPath(Option(args,"--output-root"));Require(Directory.Exists(source)&&!source.Equals(output,StringComparison.OrdinalIgnoreCase),"Source and output roots must differ");bool apply=Has(args,"--apply"),dry=Has(args,"--dry-run"),overwrite=Has(args,"--overwrite");var recipe=Recipe();var operations=new JsonArray();var files=new Dictionary<string,byte[]?>();var originals=new Dictionary<string,byte[]?>();
        var selected=Objects(recipe,"sites").Where(s=>Has(args,"--"+S(s,"group"))||S(s,"group")=="native-state"&&Has(args,"--revival-display")).ToArray();Require(selected.Length>0||Has(args,"--dungeon7"),"No compatibility operation selected");var exePath=SafePath(source,"game.exe");var originalExe=File.ReadAllBytes(exePath);var exe=originalExe.ToArray();foreach(var site in selected)PatchSite(exe,site,operations);
        if(Has(args,"--dungeon7"))
        {
            var minimap=recipe["minimap"]!;bool l8=File.Exists(SafePath(source,"openNanaimo-l7-l8-resources.json"));if(l8)VerifyLumineos(source);
            PatchSite(exe,new JsonObject{{"target",S(minimap,l8?"lumineos":"legacy")},{"known",Clone(A(minimap,"known"))},{"va",N(minimap,"va")},{"operation","lumineos_minimap"}},operations);
            var villagePath="Village_map_image/Village_map_image.pack";var before=File.ReadAllBytes(SafePath(source,villagePath));files[villagePath]=PatchVillage(before,recipe);originals[villagePath]=before;
            foreach(var alias in Objects(recipe,"aliases"))files[S(alias,"target")]=File.ReadAllBytes(SafePath(source,S(alias,"source")));
            foreach(var retired in Objects(recipe,"retired")){var path=S(retired,"path");var bytes=ReadOptional(SafePath(source,path));if(bytes is not null&&Hash(bytes).Equals(S(retired,"sha256"),StringComparison.OrdinalIgnoreCase))files[path]=null;}
        }
        files["game.exe"]=exe;originals["game.exe"]=originalExe;
        foreach(var key in files.Keys){var current=ReadOptional(SafePath(source,key));if(originals.TryGetValue(key,out var before))Require(Same(current,before),"Source changed during preparation: "+key);else originals[key]=current;}
        // Reapplying the exact recipe to generated bytes must be a no-op.
        var checkedExe=exe.ToArray();foreach(var site in selected)PatchSite(checkedExe,site,new JsonArray());Require(Same(exe,checkedExe),"Generated executable is not stable");if(files.TryGetValue("Village_map_image/Village_map_image.pack",out var village))Require(Same(village,PatchVillage(village!,recipe)),"Generated village is not stable");
        var writes=new JsonArray();var installed=new JsonArray();var report=Obj(new{schema_version=2,source_root=source,output_root=output,hash_gate_used=false,dry_run=dry,apply_requested=apply});report["session_inputs"]=Arr(files.Keys.Concat(Objects(recipe,"aliases").Select(a=>S(a,"source"))).Distinct().Cast<object>());report["operations"]=operations;report["planned_files"]=Arr(files.Keys);report["planned_removals"]=Arr(files.Where(x=>x.Value is null).Select(x=>x.Key));report["planned_verification"]=Obj(new{all_pass=true});
        if(!dry)
        {
            // Preflight every overlay before creating the first output.
            foreach(var(key,value) in files){var target=SafePath(output,key);var old=ReadOptional(target);if(Same(old,value))continue;if(old is not null){Require(overwrite,"Overlay file exists: "+key);if(value is null)Require(Objects(recipe,"retired").Any(r=>S(r,"path")==key&&Hash(old).Equals(S(r,"sha256"),StringComparison.OrdinalIgnoreCase)),"Unknown retired overlay artwork");}}
            foreach(var(key,value) in files){var target=SafePath(output,key);var old=ReadOptional(target);if(!Same(old,value)){if(value is null){if(old is not null){BackupCompatibility(output,key,old);File.Delete(target);}}else AtomicWrite(target,value);}writes.Add(Obj(new{path=key,status=value is null?"remove_on_apply":Same(old,value)?"unchanged":"written",sha256=value is null?null:Hash(value)}));}
            if(apply)
            {
                foreach(var(key,old) in originals){Require(Same(ReadOptional(SafePath(source,key)),old),"Source changed before apply: "+key);if(old is not null&&!Same(old,files[key]))BackupCompatibility(output,key,old);}
                var changed=new List<string>();try
                {
                    foreach(var(key,value) in files){var target=SafePath(source,key);Require(Same(ReadOptional(target),originals[key]),"Concurrent source edit: "+key);bool change=!Same(originals[key],value);if(change){if(value is null)File.Delete(target);else AtomicWrite(target,value);changed.Add(key);}installed.Add(Obj(new{path=key,status=change?"applied":"unchanged",sha256=value is null?null:Hash(value)}));}
                    foreach(var(key,value) in files)Require(Same(ReadOptional(SafePath(source,key)),value),"Post-apply verification failed: "+key);
                }
                catch{foreach(var key in changed.AsEnumerable().Reverse()){var target=SafePath(source,key);Require(Same(ReadOptional(target),files[key]),"Rollback refuses concurrent source edit: "+key);if(originals[key] is {} old)AtomicWrite(target,old);else File.Delete(target);}throw;}
            }
        }
        report["overlay_writes"]=writes;report["apply_results"]=installed;report["verification"]=Obj(new{all_pass=true});if(!dry){var path=SafePath(output,"nanaimo_compatibility_report.json");AtomicWrite(path,Encoding.UTF8.GetBytes(report.ToJsonString()+"\n"));report["report_path"]=path;}Console.WriteLine(report.ToJsonString());
    }
    static void BackupCompatibility(string output,string key,byte[] bytes){var backup=SafePath(output,"backups/"+Hash(bytes)+"/"+key);var old=ReadOptional(backup);if(old is null)AtomicWrite(backup,bytes);else Require(Same(old,bytes),"Content-addressed backup mismatch");}
    static void CheckFile(string root,string relative,long? size,string hash){var path=SafePath(root,relative);Require(File.Exists(path)&&(!size.HasValue||new FileInfo(path).Length==size)&&Hash(File.ReadAllBytes(path)).Equals(hash,StringComparison.OrdinalIgnoreCase),"Installed resource mismatch: "+relative);}
    static JsonObject VerifyLumineos(string root)
    {
        var path=SafePath(root,"openNanaimo-l7-l8-resources.json");var text=File.ReadAllText(path,Encoding.UTF8);var doc=JsonNode.Parse(text)!.AsObject();Require(S(doc,"schema")=="openNanaimo.l7-visual-l8-resources.v1"&&A(doc,"files").Count>0,"Invalid L7/L8 receipt");var regex=new Regex("(?m)^[ \\t]+\"id\": \"([0-9a-f]{64})\",\\r?\\n");Require(regex.Matches(text).Count==1&&Hash(Encoding.UTF8.GetBytes(regex.Replace(text,"",1))).Equals(S(doc,"id"),StringComparison.OrdinalIgnoreCase),"L7/L8 receipt digest mismatch");foreach(var group in new[]{"files","preserve_l7_combat"}){var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var row in Objects(doc,group)){Require(seen.Add(S(row,"path")),"Duplicate resource entry");CheckFile(root,S(row,"path"),row["size"] is null?null:N(row,"size"),S(row,"sha256"));}}return Obj(new{status="LUMINEOS_RESOURCES_PASS"});
    }
    static void VerifyResources(string[] args)
    {
        var root=Path.GetFullPath(Option(args,"--client-root"));var repo=Path.GetFullPath(Option(args,"--root",Directory.GetCurrentDirectory()));var type=Option(args,"--kind");if(type=="lumineos"){Console.WriteLine(VerifyLumineos(root));return;}
        Require(type is "hero" or "korean","Unknown resource verifier");var recipePath=SafePath(repo,type=="hero"?"manifest/hero_dragon_resources.json":"manifest/korean_pet_resources.json");var recipe=ReadJson(recipePath);foreach(var row in Objects(recipe,"resources"))CheckFile(root,S(row,"path"),N(row,"installed_size"),S(row,"installed_sha256"));foreach(var row in Objects(recipe,"shared_resources"))CheckFile(root,S(row,"path"),N(row,"size"),S(row,"sha256"));
        if(type=="korean"){var receipt=ReadJson(SafePath(root,".openNanaimo-korean-pets.json"));Require(S(receipt,"schema")=="openNanaimo.korean-pets-install.v1"&&S(receipt,"recipe_sha256").Equals(Hash(File.ReadAllBytes(recipePath)),StringComparison.OrdinalIgnoreCase)&&N(receipt,"catalog_count")==N(recipe,"catalog_count")&&A(receipt,"pet_codes").Select(x=>Num(x)).SequenceEqual(Objects(recipe,"pets").Select(x=>N(x,"code"))),"Invalid Korean PET receipt");var cat=recipe["installed_catalog"]!;CheckFile(root,"pi._D7",N(cat,"size"),S(cat,"sha256"));}
        else{var receipt=ReadJson(SafePath(root,".openNanaimo-hero-dragon.json"));Require(S(receipt,"schema")=="openNanaimo.hero-dragon-install.v1"&&N(receipt,"item_code")==15003361&&N(receipt,"model_stage")==3&&N(receipt,"required_level")==N(recipe,"required_level")&&N(receipt,"source_required_level")==N(recipe,"source_required_level")&&N(receipt,"max_durability")==N(recipe,"max_durability")&&N(receipt,"catalog_count")>0,"Invalid Hero Dragon receipt");var cat=receipt["pet_catalog"]!;CheckFile(root,"pi._D7",N(cat,"size"),S(cat,"sha256"));}
        Console.WriteLine(Obj(new{status=type=="hero"?"HERO_DRAGON_RESOURCES_PASS":"KOREAN_PET_RESOURCES_PASS",runtime_acceptance=false}).ToJsonString());
    }
}
