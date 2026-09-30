using System.Buffers.Binary;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckNativeCoupleStartAsync(Fixture fixture)
    {
        foreach (var ring in new uint[] { 43000001, 43000002, 43000003 })
            await CheckNativeCoupleStartRingAsync(fixture, ring);
    }

    private static async Task CheckNativeCoupleStartRingAsync(Fixture fixture, uint ring)
    {
        var bridge = Environment.GetEnvironmentVariable("NANAIMO_COUPLE_BRIDGE");
        if (string.IsNullOrEmpty(bridge))
            bridge = "adapter_runtime/nanaimo_gameplay_bridge.exe";
        bridge = Path.GetFullPath(bridge);
        Check(File.Exists(bridge), "native couple start executable available");
        var owner = await fixture.CreateSessionAsync("native-couple-owner-" + ring, "NativeO" + (ring % 10000), 0);
        var peer = await fixture.CreateSessionAsync("native-couple-peer-" + ring, "NativeP" + (ring % 10000), 1);
        await fixture.Database.GrantInventoryItemToAccountAsync(Character(owner).AccountId, ring, 1);
        var proposal = CoupleRequest(fixture, owner, peer, ring);
        await DispatchCouple(fixture, owner, 0xC583, proposal);
        await DispatchCouple(fixture, peer, 0xC584, CoupleAnswer(owner, proposal, 10));
        await using var pool = new NativeDungeonPool(bridge, Path.Combine(fixture.Root, "couple-start"));
        await using var primaryReservation = await pool.AcquireAsync("couple-primary-reservation", Token);
        await using var ownerLease = await pool.AcquireAsync("couple-active-room", Token);
        await using var peerLease = await pool.AcquireAsync("couple-active-room", Token);
        var ownerFrames = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        var peerFrames = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        await using var ownerNative = new NativeDungeonClient(frame => { ownerFrames.Enqueue(frame); return Task.CompletedTask; }, ownerLease.Port);
        await using var peerNative = new NativeDungeonClient(frame => { peerFrames.Enqueue(frame); return Task.CompletedTask; }, peerLease.Port);
        try
        {
            foreach (var (session, native, lease) in new[] { (owner, ownerNative, ownerLease), (peer, peerNative, peerLease) })
            {
                await native.ConnectAsync(Token);
                var state = NativeDungeonState.Create(Character(session), [], []);
                CoupleBenefitPolicy.WriteNativeRing(state, ring);
                var imported = await native.ExchangeAsync(null, state, Token);
                Set(session, "NativeLease", lease);
                Set(session, "NativeDungeon", native);
                Set(session, "NativeCheckpoint", imported);
                Set(session, "NativeDungeonSelectionValid", true);
                Set(session, "NativeBattleEpoch", 1L);
                Set(session, "NativeForwarding", true);
                var creation = new byte[44];
                BinaryPrimitives.WriteUInt16LittleEndian(creation.AsSpan(24), 100);
                var created = await native.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCF6C, creation), null, Token);
                Check(created.Frames.Any(response => BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xCF6D),
                    "native couple ready room established");
            }
            var decision = new byte[] { 1, 0, 0, 0 };
            foreach (var native in new[] { ownerNative, peerNative })
            {
                var profile = await native.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCFEB, new byte[4]), null, Token);
                Check(profile.Frames.Any(response => BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xCFEC),
                    "native couple room profile consumed");
                await native.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCFD3, []), null, Token);
                await native.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCFD5, decision), null, Token);
            }
            await peerNative.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCF7D, decision), null, Token);
            ownerFrames.Clear(); peerFrames.Clear();
            var ownerWaiting = await ownerNative.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCF7F, []), null, Token);
            Check(ownerWaiting.Frames.All(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) != 0xCF80),
                "the first participant waits for the other final loading confirmation");
            var peerStarted = await peerNative.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCF7F, []), null, Token);
            var ownerStarted = await ownerNative.ExchangeCapturedAsync(null, null, Token);
            var ownerFinal = ownerFrames.Concat(ownerStarted.Frames).ToArray();
            var peerFinal = peerFrames.Concat(peerStarted.Frames).ToArray();
            foreach (var (session, native, partner, started, finalFrames) in new[]
            {
                (owner, ownerNative, peer, ownerStarted, ownerFinal),
                (peer, peerNative, owner, peerStarted, peerFinal)
            })
            {
                Set(session, "NativeCheckpoint", started.State);
                var full = finalFrames.Single(response => BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xCF80);
                Check(full.Length == 8 && BinaryPrimitives.ReadUInt16LittleEndian(full.AsSpan(4)) == full.Length,
                    "native start acknowledges the completed loading barrier without game data");
                Check(CoupleProtocol.IsNativeStartResponse(full), "empty final native start layout accepted");
                Set(session, "NativeCoupleStartRequested", false);
                await Invoke<Task>(fixture.Service, "PatchNativeCoupleFrameAsync", session, full, Token);
                Check(Drain(session).Count == 0, "preload data does not publish the partner identity");
                Set(session, "NativeCoupleStartRequested", true);
                await Invoke<Task>(fixture.Service, "HandleNativeWorkerFrameAsync", session, full, 1L, Token);
                var published = Drain(session);
                Check(published.Count == 2
                    && BinaryPrimitives.ReadUInt16LittleEndian(published[0].AsSpan(6)) == 0xC588
                    && BinaryPrimitives.ReadUInt16LittleEndian(published[0].AsSpan(8)) == ((NativeDungeonState)Get(partner, "NativeCheckpoint")!).Get(4)
                    && BinaryPrimitives.ReadUInt16LittleEndian(published[1].AsSpan(6)) == 0xCF80
                    && published[1].Length == 8,
                    "actual native start publishes partner identity before the final acknowledgment");
                await Invoke<Task>(fixture.Service, "HandleNativeWorkerFrameAsync", session, full, 1L, Token);
                var repeated = Drain(session);
                Check(repeated.Count == 1 && BinaryPrimitives.ReadUInt16LittleEndian(repeated[0].AsSpan(6)) == 0xCF80,
                    "repeated native start does not duplicate partner identity");
                Set(session, "NativeCoupleIdentityPublished", false);
                var malformed = full.ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(4), 12);
                await Invoke<Task>(fixture.Service, "PatchNativeCoupleFrameAsync", session, malformed, Token);
                Check(Drain(session).Count == 0 && !(bool)Get(session, "NativeCoupleIdentityPublished")!,
                    "native start declaration mismatch preserves the pending identity");
                foreach (var length in new[] { 0, 7, 9, 771, 772, 808 })
                {
                    var truncated = length < 8 ? new byte[length] : NativeDungeonClient.Frame(0xCF80, new byte[length - 8]);
                    await Invoke<Task>(fixture.Service, "PatchNativeCoupleFrameAsync", session, truncated, Token);
                    Check(!CoupleProtocol.IsNativeStartResponse(truncated) && Drain(session).Count == 0,
                        "non-final native start length rejected " + length);
                }
                await Invoke<Task>(fixture.Service, "PatchValidatedNativeCoupleFrameAsync", session, full, Token);
                Check(Drain(session).Count == 1, "validated native start publishes one partner identity");
                await Invoke<Task>(fixture.Service, "PatchValidatedNativeCoupleFrameAsync", session, full, Token);
                Check(Drain(session).Count == 0, "validated native identity publication is idempotent");
                await CheckPersistedNativeCoupleExperienceAsync(fixture, session, ring);
                Set(session, "NativeCoupleIdentityPublished", false);
                var queue = Get(session, "OutboundWrites")!;
                var writer = queue.GetType().GetProperty("Writer")!.GetValue(queue)!;
                writer.GetType().GetMethod("TryComplete")!.Invoke(writer, [null]);
                var failedPublication = false;
                try { await Invoke<Task>(fixture.Service, "PatchValidatedNativeCoupleFrameAsync", session, full, Token); }
                catch (System.Threading.Channels.ChannelClosedException) { failedPublication = true; }
                Check(failedPublication && !(bool)Get(session, "NativeCoupleIdentityPublished")!,
                    "failed native identity delivery restores publication eligibility");
            }
        }
        finally
        {
            foreach (var session in new[] { owner, peer })
            {
                Set(session, "NativeDungeon", null); Set(session, "NativeLease", null);
                Set(session, "NativeCheckpoint", null); Set(session, "NativeForwarding", false);
            }
        }
    }

    private static async Task CheckPersistedNativeCoupleExperienceAsync(Fixture fixture, object session, uint ring)
    {
        var before = (NativeDungeonState)Get(session, "NativeCheckpoint")!;
        var character = Character(session);
        var stored = (await fixture.Database.GetCharacterByIdAsync(character.Id))!;
        var payload = new byte[4 + 0x34];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), checked((ushort)before.Get(4)));
        payload[4 + 0x0B] = 5;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + 0x0C), 100);
        var reward = NativeDungeonClient.Frame(0xCF88, payload);
        await Invoke<Task>(fixture.Service, "ApplyNativeCoupleExperienceAsync", session, reward, Token);
        var award = BinaryPrimitives.ReadUInt32LittleEndian(reward.AsSpan(24));
        Check(award == CoupleBenefitPolicy.ScaleExperience(100, ring, true),
            "native character award follows the active ring tier " + ring);
        var after = new NativeDungeonState(before.Bytes.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(after.Bytes.AsSpan(12), before.Get(12) + 100);
        var settlement = new NativeDungeonSettlementRecord(0, 0, 0, 0, 0, 5, 100, null,
            award, "couple-experience-" + Guid.NewGuid().ToString("N"));
        var applied = await fixture.Database.ApplyNativeDungeonDeltaAsync(character.AccountId, character.Id,
            SessionId(session), before, after, Token, Guid.NewGuid().ToString("N"), settlement: settlement);
        var saved = (await fixture.Database.GetCharacterByIdAsync(character.Id))!;
        Check(applied.Applied && saved.Experience == stored.Experience + award,
            "scaled native character experience is persisted " + ring);
        await fixture.Database.ApplyNativeDungeonDeltaAsync(character.AccountId, character.Id,
            SessionId(session), before, after, Token, Guid.NewGuid().ToString("N"), settlement: settlement);
        Check((await fixture.Database.GetCharacterByIdAsync(character.Id))!.Experience == saved.Experience,
            "repeated native settlement preserves a single experience award " + ring);
    }
}
