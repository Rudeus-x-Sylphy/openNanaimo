using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class LiveDungeonExperienceChecks
{
    public static async Task RunAsync()
    {
        await CheckControlDuringExchangeAsync();
        for (int level=1;level<=99;level++)
        {
            var lower=CharacterProgression.ExperienceRequiredForLevel(level);
            var next=CharacterProgression.NextExperienceThreshold(level);
            Check(next>lower && (float)next>(float)lower, $"curve and float HUD interval {level}");
            Check(CharacterProgression.CalculateLevel(lower)==level, $"threshold {level}");
            Check(CharacterProgression.CalculateLevel(next-1)==level, $"below threshold {level}");
            Check(CharacterProgression.CalculateLevel(next)==Math.Min(99,level+1), $"cross threshold {level}");
        }
        Check(CharacterProgression.MaximumExperience==1_483_748_900L, "full user curve checksum");
        Check(CharacterProgression.CalculateLevel(long.MaxValue)==99, "cannot become level100");
        var costume=new CharacterRecord { Appearance=new byte[36] };
        BinaryPrimitives.WriteUInt32LittleEndian(costume.Appearance,10030458);
        Check(AvatarEquipmentCatalog.GetExperiencePercent(costume.Appearance)==18, "authored AVATA EXP type10");
        Check(DungeonExperiencePolicy.ScaleEquipment(2500,costume)==2950, "equipped EXP clothing");
        BinaryPrimitives.WriteUInt32LittleEndian(costume.Appearance.AsSpan(24),10030458);
        Check(DungeonExperiencePolicy.ScaleEquipment(2500,costume)==3400, "selected effect percentages additive");
        Check(DungeonExperiencePolicy.BaseSettlementExperience(10003)==2500, "settlement floor(score/4)");
        var root=Path.Combine(Path.GetTempPath(),"nanaimo-live-exp-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); using(File.Create(Path.Combine(root,"game.db"))) {}
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token=timeout.Token;
        try
        {
            var db=new DatabaseService(root);await db.InitializeAsync(token);
            var account=await db.OpenLocalAccountAsync("live-exp",token);
            var id=await db.CreateLocalCharacterAsync(account,"LiveExp",1,token);
            const string session="live-kills";
            Check(await db.BeginWorldSessionAsync(account,id,session,1,"127.0.0.1",token),"session owns character");
            async Task<CharacterRecord> Character()=>(await db.GetCharacterAsync(account,token))!;
            async Task Sql(string sql)
            {
                await using var connection=new SqliteConnection($"Data Source={Path.Combine(root,"game.db")}");
                await connection.OpenAsync(token);await using var command=connection.CreateCommand();
                command.CommandText=sql;await command.ExecuteNonQueryAsync(token);
            }
            async Task<uint> Kill(string battle,uint score)=>
                (await db.ApplyLiveDungeonExperienceAsync(account,id,session,battle,score,token)).AddedExperience;
            await Sql($"UPDATE Characters SET CurrentHp=711,CurrentMp=31,LastSavedAt='2026-10-06T00:00:00Z' WHERE Id={id}");
            var initial=await Character();
            Check(await Kill("1:1",3)==0,"round cumulative, do not round each increment");
            Check(await Kill("1:1",4)==1,"fraction carries to next kill");
            Check(await Kill("1:1",3960)==989,"absolute score high water increment");
            Check((await Character()).Level==1,"below first level");
            Check(await Kill("1:1",4000)==10,"kill crosses level in same battle");
            var grown=await Character();
            Check(grown.Level==2&&grown.Experience==1000,"level and EXP committed immediately");
            Check(grown.Strength==initial.Strength+1&&grown.Vitality==initial.Vitality+1
                &&grown.Agility==initial.Agility+1&&grown.Intelligence==initial.Intelligence+1
                &&grown.Luck==initial.Luck+1,"all five growth attributes");
            Check(grown.MaxHp>initial.MaxHp&&grown.MaxMp>initial.MaxMp&&grown.CurrentHp==711&&grown.CurrentMp==31,
                "maxima grow without healing");
            Check(await Kill("1:1",4000)==0&&await Kill("1:1",10)==0,"duplicate and out-of-order kills");
            var reopened=new DatabaseService(root);
            Check((await reopened.ApplyLiveDungeonExperienceAsync(account,id,session,"1:1",4000,token)).AddedExperience==0,
                "receipt survives DB reopen");
            Check(!(await db.ApplyLiveDungeonExperienceAsync(account,id,"wrong","1:1",999999,token)).Authorized,
                "wrong session cannot grant");
            Check(await Kill("1:2",4000)==1000,"next native battle epoch gets independent receipt");
            var before=NativeDungeonState.Create(await Character(),[],[]);
            var settlement=new NativeDungeonSettlementRecord(0,0,0,0,0,5,4000,
                CharacterExperienceAward:1000,SettlementId:"1:2");
            await db.ApplyNativeDungeonDeltaAsync(account,id,session,before,new(before.Bytes.ToArray()),token,"end-a",settlement:settlement);
            Check((await Character()).Experience==3000,"terminal awards another 25 percent, not a deduction");
            await db.ApplyNativeDungeonDeltaAsync(account,id,session,before,new(before.Bytes.ToArray()),token,"end-b",settlement:settlement);
            Check((await Character()).Experience==3000,"end receipt distinct and idempotent");
            var stale=new NativeDungeonState(before.Bytes.ToArray());
            BinaryPrimitives.WriteUInt32LittleEndian(stale.Bytes.AsSpan(12),uint.MaxValue);
            await db.ApplyNativeDungeonDeltaAsync(account,id,session,before,stale,token,"old-snapshot");
            Check((await Character()).Experience==3000,"stale snapshots neither erase nor mint kill EXP");
            var level98=CharacterProgression.ExperienceRequiredForLevel(99)-1;
            await Sql($"UPDATE Characters SET Level=98,Experience={level98},CurrentHp=0 WHERE Id={id}");
            Check(await Kill("1:3",4)==1,"98 to 99 on a kill");
            Check((await Character()).Level==99&&(await Character()).CurrentHp==0,"dead actor never revived by earned level");
            await Kill("1:3",uint.MaxValue);
            Check((await Character()).Level==99&&(await Character()).Experience==CharacterProgression.MaximumExperience,
                "99 stays99 with large reward and bounded display");
            // One-time curve migration preserves level and fractional progress,
            // archives exact old values, and never reapplies on later startups.
            await Sql($"UPDATE Characters SET Level=80,Experience={50L*79*80+4000} WHERE Id={id}; DELETE FROM SchemaMigrations WHERE Name='character-exp-score-v2'");
            await db.InitializeAsync(token);
            var migrated=(await Character()).Experience;
            Check((await Character()).Level==80 && migrated==CharacterProgression.MigrateLegacyExperience(80,50L*79*80+4000),
                "legacy curve migration retains level and half-bar");
            await db.InitializeAsync(token);
            Check((await Character()).Experience==migrated,"curve migration once only");
            Console.WriteLine("LIVE_DUNGEON_EXPERIENCE_HOST_PASS");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
    private static async Task CheckControlDuringExchangeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback,0);listener.Start();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendControl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeDungeonClient? worker = null;
        async Task Receive(byte[] frame)
        {
            received.TrySetResult();
            await sendControl.Task.WaitAsync(token);
            await worker!.SendControlAsync(NativeDungeonClient.Frame(0xF10B,new byte[32]),token);
        }
        try
        {
            await using var native = worker = new NativeDungeonClient(Receive,((IPEndPoint)listener.LocalEndpoint).Port);
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync(token);
                var stream = accepted.GetStream();
                await stream.WriteAsync(NativeDungeonClient.Frame(0xF10A,new byte[12]),token);
                async Task<ushort> Read()
                {
                    var header = new byte[8];await stream.ReadExactlyAsync(header,token);
                    var tail = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4))-8];
                    await stream.ReadExactlyAsync(tail,token);return BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
                }
                Check(await Read()==0xF101,"concurrent exchange enters before reader control send");
                sendControl.TrySetResult();
                Check(await Read()==0xF10B,"reader control bypasses exchange gate without interleaving bytes");
                var state = new byte[NativeDungeonState.Size];state[0]=1;
                await stream.WriteAsync(NativeDungeonClient.Frame(0xF102,state),token);
                await Task.Delay(100,token);
            },token);
            await native.ConnectAsync(token);await received.Task.WaitAsync(token);
            var result=await native.ExchangeCapturedAsync(null,null,token);
            Check(result.State.Get(0)==1,"no reader/exchange deadlock");
            await server;
        }
        finally {listener.Stop();}
    }

    private static void Check(bool ok,string label)
    { if(!ok)throw new InvalidOperationException("LIVE_EXP_CHECK_FAILED "+label);Console.WriteLine("PASS "+label); }
}
