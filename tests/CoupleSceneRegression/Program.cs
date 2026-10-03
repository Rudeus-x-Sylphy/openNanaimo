using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly Type PresenceType = typeof(NetworkAdapterService).GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
    private static int _checks;

    private static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WireIdentityAllocator.Reset();
        CheckNativeStartLayout();
        CheckTownLayout();
        var local = NetworkAdapterService.BuildLoadNecessityPayload(new CharacterRecord(), [], [], [], null);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(0x2D4 - 8)) == 2,
            "C355 restores local couple title display mode before C368 constructs the actor");
        await using var fixture = await Fixture.CreateAsync();
        foreach (var ring in new uint[] { 43000001, 43000002, 43000003 })
        {
            var owner = await fixture.AddAsync("Owner" + ring, 0);
            var partner = await fixture.AddAsync("Peer" + ring, 1);
            var observer = await fixture.AddAsync("Viewer" + ring, 0);
            Set(owner, "LastReportedPositionX", (ushort)417);
            Set(owner, "LastReportedPositionY", (ushort)99);
            Set(partner, "LastReportedPositionX", (ushort)521);
            Set(partner, "LastReportedPositionY", (ushort)181);
            Character(owner).PositionX = 417; Character(owner).PositionY = 99;
            Character(partner).PositionX = 521; Character(partner).PositionY = 181;
            var hidden = await fixture.AddAsync("Hidden" + ring, 0);
            Set(hidden, "TownPage", (byte)34);
            await CheckRefusalAsync(fixture, owner, partner, ring);
            await MarryAsync(fixture, owner, partner, observer, ring);
            if (ring == 43000001)
                await SeparateAsync(fixture, owner, partner, observer, false, false);
            else
                await SeparateAsync(fixture, owner, partner, observer, true, ring == 43000003);
        }
        await CheckGenericSeparationAsync(fixture);
        await CheckNativeStartOrderAsync(fixture);
        await CheckVisibilityAsync(fixture);
        await CheckTownOptionsAsync(fixture);
        Console.WriteLine($"CoupleSceneRegression PASS ({_checks} checks)");
    }

    private static async Task CheckTownOptionsAsync(Fixture fixture)
    {
        var session = await fixture.AddAsync("TitleOptions", 0);
        var character = Character(session);
        Check(character.TownTitleDisplayMode == 2 && character.TownOptionFlags == 0,
            "legacy and new profiles default to retail option 2/0");
        foreach (var mode in new ushort[] { 0, 1, 2 })
        {
            var options = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(options, mode);
            BinaryPrimitives.WriteUInt16LittleEndian(options.AsSpan(2), 0x35);
            Clear(fixture);
            Check(await Dispatch(fixture, session, 0xC5BC, options) is null,
                "option save is one-way and never invents an acknowledgment");
            Check(Broadcasts(session).Count == 0, "option save never recreates a local or remote actor");
            var saved = (await fixture.Database.GetCharacterByIdAsync(character.Id))!;
            Check(saved.TownTitleDisplayMode == mode && saved.TownOptionFlags == 0x35,
                "the authenticated character's explicit display choice survives reload");
            var local = NetworkAdapterService.BuildLoadNecessityPayload(saved, [], [], [], null);
            Check(BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(0x2D4 - 8)) == mode
                && BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(0x2D6 - 8)) == 0x35,
                "C355 restores the title mode and six option bits without coupling them to marriage");
            var peer = TownPayload(saved);
            Check(BinaryPrimitives.ReadUInt16LittleEndian(peer.AsSpan(102)) == mode,
                "C36A uses the subject's persisted display mode even when its UID differs");
        }
        foreach (var invalid in new byte[][] { [], [2, 0], [2, 0, 0, 0, 0], [3, 0, 0, 0], [2, 0, 64, 0] })
            Check(await Dispatch(fixture, session, 0xC5BC, invalid) is null,
                "malformed or out-of-range option saves are ignored without a packet");
        var prior = (await fixture.Database.GetCharacterAsync(character.AccountId))!;
        Check(prior.TownTitleDisplayMode == 2 && prior.TownOptionFlags == 0x35,
            "invalid option packets cannot overwrite the previous settings");
        Check(!await fixture.Database.SaveTownOptionsAsync(character.AccountId, character.Id, "stale-session", 0, 0),
            "a replaced or disconnected session cannot change stored options");
        Check(!await fixture.Database.SaveTownOptionsAsync(character.AccountId + 999, character.Id,
            Get<string>(session, "SessionId"), 0, 0), "an unrelated account cannot change another character's options");
        Set(session, "OnlineTracked", false);
        Check(await Dispatch(fixture, session, 0xC5BC, new byte[4]) is null,
            "untracked option request is ignored");
        prior = (await fixture.Database.GetCharacterAsync(character.AccountId))!;
        Check(prior.TownTitleDisplayMode == 2 && prior.TownOptionFlags == 0x35,
            "rejected sessions preserve the stored display settings");
        Set(session, "OnlineTracked", true);
    }

    private static void CheckNativeStartLayout()
    {
        foreach (var opcode in new ushort[] { 0xCF80, 0xCFEC, 0xC588 })
        foreach (var length in new[] { 0, 7, 8, 12, 771, 772, 800, 808 })
        {
            var frame = new byte[length];
            if (length >= 8)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)length);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), opcode);
            }
            Check(CoupleProtocol.IsNativeStartResponse(frame) == (opcode == 0xCF80 && length == 8),
                "only the final empty start acknowledgment publishes couple identity");
        }
        var mismatched = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(mismatched.AsSpan(4), 772);
        BinaryPrimitives.WriteUInt16LittleEndian(mismatched.AsSpan(6), 0xCF80);
        Check(!CoupleProtocol.IsNativeStartResponse(mismatched), "a mismatched start length does not publish identity");
    }

    private static void CheckTownLayout()
    {
        foreach (var ring in new uint[] { 0, 43000001, 43000002, 43000003, 43000004, uint.MaxValue })
        {
            var subject = new CharacterRecord
            {
                Id = 70001, AccountId = 70001, Name = "Subject", Level = 52, DungeonGrade = 16,
                Appearance = new byte[36], ActiveCoupleRingItemCode = ring,
                ActiveCouplePartnerName = "\u4f34\u4fa3\u7532", TutorialCompleted = true
            };
            var payload = TownPayload(subject);
            var valid = ring is >= 43000001 and <= 43000003;
            Check(payload.Length == 104, "town character state has its complete length");
            Check(ReadName(payload.AsSpan(84, 16)) == (valid ? subject.ActiveCouplePartnerName : ""),
                "town relationship name belongs to the subject and requires a valid ring");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(100)) == (valid ? ring - 43000000 : 0),
                "town relationship name and ring tier agree");
            var control = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(52));
            Check((control >> 20) == WireIdentityAllocator.GetSceneEntityId(subject.Id)
                && ((control >> 13) & 127) == 52 && ((control >> 6) & 127) == 16,
                "relationship projection preserves identity, level and dungeon title");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(80)) == (417u << 2 | 99u << 12)
                && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(102)) == 2,
                "town tail is display mode 2, not a character UID");
            if (!valid) continue;
            subject.ActiveCouplePartnerName = new string('\u4f34', 10);
            payload = TownPayload(subject);
            Check(payload[99] == 0 && ReadName(payload.AsSpan(84, 16)) == new string('\u4f34', 7),
                "long multibyte names remain terminated without splitting a character");
            subject.ActiveCouplePartnerName = "";
            payload = TownPayload(subject);
            Check(payload.AsSpan(84, 16).IndexOfAnyExcept((byte)0) < 0,
                "empty relationship name leaves no stale text");
        }
    }

    private static byte[] TownPayload(CharacterRecord subject)
        => (byte[])typeof(NetworkAdapterService).GetMethod("BuildTownTitleUserInfoPayload", Static)!
            .Invoke(null, [subject, (ushort)417, (ushort)99])!;

    private static async Task CheckRefusalAsync(Fixture f, object owner, object peer, uint ring)
    {
        var request = await RequestAsync(f, owner, peer, ring);
        Clear(f);
        Check(await Dispatch(f, owner, 0xC583, request) is null, "proposal waits for the selected partner");
        var answer = Answer(owner, request, false, 20);
        var response = await Dispatch(f, peer, 0xC584, answer);
        Check(Opcodes(response).SequenceEqual(new ushort[] { 0xC584 }), "refusal returns only its decision");
        Check(await f.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is null,
            "refusal does not create a relationship");
        Check(Broadcasts(peer).Cast<object>().All(item => Get<ushort>(item, "Opcode") == 0xC584),
            "refusal does not refresh any character or profile");
    }

    private static async Task MarryAsync(Fixture f, object owner, object peer, object observer, uint ring)
    {
        var request = await RequestAsync(f, owner, peer, ring);
        Clear(f);
        await Dispatch(f, owner, 0xC583, request);
        Clear(f);
        var response = await Dispatch(f, peer, 0xC584, Answer(owner, request, false, 10));
        Check(Opcodes(response).SequenceEqual(new ushort[] { 0xC584 }), "acceptance changes the relationship without reloading the page");
        CheckDecision(response!, 0xC584, Character(owner));
        CheckParticipantNotices(peer, owner, 0xC584, true);
        CheckObserverNotices(peer, observer, owner, peer, ring);
        CheckUnmoved(owner, 417, 99); CheckUnmoved(peer, 521, 181);
        Check(Character(owner).ActiveCouplePartnerName == Character(peer).Name
            && Character(peer).ActiveCouplePartnerName == Character(owner).Name,
            "both active character snapshots contain their partner name");
        await Invoke<Task>(f.Service, "RefreshSessionCharacterAsync", owner, CancellationToken.None);
        Check(Character(owner).ActiveCouplePartnerName == Character(peer).Name
            && ReadName(TownPayload(Character(owner)).AsSpan(84, 16)) == Character(peer).Name,
            "persisted relationships restore their town label");
        Clear(f);
        Check(await Dispatch(f, peer, 0xC584, Answer(owner, request, false, 10)) is null
            && Broadcasts(peer).Count == 0, "a repeated acceptance cannot recreate characters or spend another ring");
    }

    private static async Task SeparateAsync(Fixture f, object owner, object peer, object observer, bool forced, bool offline)
    {
        var request = await RequestAsync(f, owner, peer, forced ? 43100002u : 43100001u);
        if (offline) Set(peer, "OnlineTracked", false);
        Clear(f);
        byte[]? response;
        object queueOwner;
        if (forced)
        {
            var use = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(use, 43100002);
            BinaryPrimitives.WriteUInt32LittleEndian(use.AsSpan(4), BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(20)));
            var opened = await Dispatch(f, owner, 0xC46D, use);
            Check(Opcodes(opened).SequenceEqual(new ushort[] { 0xC46E })
                && await f.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is not null,
                "opening forced separation preserves the current relationship");
            Clear(f);
            response = await Dispatch(f, owner, 0xC585, request);
            queueOwner = owner;
            Check(Opcodes(response).SequenceEqual(new ushort[] { 0xC586, 0xC430 }),
                "forced separation updates only the relationship and consumed inventory");
            if (!offline) CheckParticipantNotices(owner, peer, 0xC586, false);
        }
        else
        {
            await Dispatch(f, owner, 0xC585, request);
            Clear(f);
            response = await Dispatch(f, peer, 0xC586, Answer(owner, request, true, 10));
            queueOwner = peer;
            Check(Opcodes(response).SequenceEqual(new ushort[] { 0xC586 }),
                "mutual separation returns only the relationship decision");
            CheckParticipantNotices(peer, owner, 0xC586, true);
        }
        CheckDecision(response!, 0xC586, Character(forced ? peer : owner));
        CheckObserverNotices(queueOwner, observer, owner, offline ? null : peer, 0);
        CheckUnmoved(owner, 417, 99); CheckUnmoved(peer, 521, 181);
        Check(await f.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is null,
            "separation clears the persisted relationship");
        await Invoke<Task>(f.Service, "RefreshSessionCharacterAsync", peer, CancellationToken.None);
        Check(Character(owner).ActiveCouplePartnerName == "" && Character(peer).ActiveCouplePartnerName == ""
            && Character(owner).ActiveCoupleRingItemCode == 0 && Character(peer).ActiveCoupleRingItemCode == 0,
            "separation clears both online and returning partner snapshots");
        var cleared = TownPayload(Character(owner));
        Check(cleared.AsSpan(84, 18).IndexOfAnyExcept((byte)0) < 0, "separation removes the complete town label");
        Set(peer, "OnlineTracked", true);
    }

    private static void CheckDecision(byte[] response, ushort opcode, CharacterRecord partner)
    {
        var payload = response.AsSpan(8, CoupleProtocol.GetPayloadLength(opcode));
        Check(CoupleProtocol.ReadStatus(opcode, payload) == 10 && ReadName(payload[..16]) == partner.Name,
            "accepted decisions identify the real partner");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(payload[(opcode == 0xC584 ? 26 : 22)..])
            == WireIdentityAllocator.GetSceneEntityId(partner.Id), "decisions target the partner's assigned scene identity");
    }

    private static void CheckParticipantNotices(object queueOwner, object recipient, ushort opcode, bool inventory)
    {
        var notices = Broadcasts(queueOwner).Cast<object>().Where(item =>
            ReferenceEquals(Get<object>(Get<object>(item, "Target"), "Session"), recipient)).ToArray();
        var expected = inventory
            ? new ushort[] { 0xC36B, 0xC36A, 0xC47F, 0xCB21, opcode, 0xC430 }
            : new ushort[] { 0xC36B, 0xC36A, 0xC47F, 0xCB21, opcode };
        Check(notices.Select(item => Get<ushort>(item, "Opcode")).SequenceEqual(expected),
            "participants receive an same-identity town actor replacement before the relationship result");
        var townPayload = Get<byte[]>(notices[1], "Payload");
        var subject = Character(queueOwner);
        var expectedPartner = subject.ActiveCoupleRingItemCode == 0 ? "" : Character(recipient).Name;
        Check(ReadName(townPayload.AsSpan(84, 16)) == expectedPartner
            && BinaryPrimitives.ReadUInt16LittleEndian(townPayload.AsSpan(100))
                == (subject.ActiveCoupleRingItemCode == 0 ? 0 : subject.ActiveCoupleRingItemCode - 43000000),
            "participant town actor refresh carries the current partner marker and ring tier");
        Check(Get<IList>(queueOwner, "PendingSessionBroadcasts").Count == 0,
            "relationship changes do not queue unscoped character replacements");
    }

    private static void CheckObserverNotices(object queueOwner, object observer, object first, object? second, uint ring)
    {
        var notices = Broadcasts(queueOwner).Cast<object>().Where(item =>
            ReferenceEquals(Get<object>(Get<object>(item, "Target"), "Session"), observer)).ToArray();
        var subjects = second is null ? new[] { first } : new[] { first, second };
        Check(notices.Length == subjects.Length * 4, "an observer receives exactly the changed visible characters");
        for (var i = 0; i < subjects.Length; i++)
        {
            var subject = subjects[i];
            var payload = Get<byte[]>(notices[i * 4 + 1], "Payload");
            Check(Get<ushort>(notices[i * 4 + 1], "Opcode") == 0xC36A && Get<ushort>(notices[i * 4 + 2], "Opcode") == 0xC47F,
                "observer construction precedes appearance attachment");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(52)) >> 20
                == WireIdentityAllocator.GetSceneEntityId(Character(subject).Id), "observer state belongs to the changed subject");
            Check(ReadName(payload.AsSpan(84, 16)) == (ring == 0 ? "" : Character(subject).ActiveCouplePartnerName)
                && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(100)) == (ring == 0 ? 0 : ring - 43000000),
                "observers see the new relationship name and ring together");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(80))
                == ((uint)Get<ushort>(subject, "LastReportedPositionX") << 2 | (uint)Get<ushort>(subject, "LastReportedPositionY") << 12),
                "observer updates use active coordinates rather than scene entry positions");
        }
    }

    private static async Task CheckGenericSeparationAsync(Fixture f)
    {
        var owner = await f.AddAsync("GenericOwner", 0);
        var peer = await f.AddAsync("GenericPeer", 1);
        var observer = await f.AddAsync("GenericViewer", 0);
        Character(owner).PositionX = 417; Character(owner).PositionY = 99;
        Character(peer).PositionX = 417; Character(peer).PositionY = 99;
        var proposal = await RequestAsync(f, owner, peer, 43000001);
        await Dispatch(f, owner, 0xC583, proposal);
        await Dispatch(f, peer, 0xC584, Answer(owner, proposal, false, 10));
        var request = await RequestAsync(f, owner, peer, 43100002);
        var generic = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(generic, 43100002);
        BinaryPrimitives.WriteUInt32LittleEndian(generic.AsSpan(4), BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(20)));
        Clear(f);
        var response = await Dispatch(f, owner, 0xC46D, generic);
        Check(Opcodes(response).SequenceEqual(new ushort[] { 0xC46E }) && Broadcasts(owner).Count == 0
            && await f.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is not null,
            "ordinary inventory use opens confirmation while preserving the relationship");
        response = await Dispatch(f, owner, 0xC585, request);
        Check(Opcodes(response).SequenceEqual(new ushort[] { 0xC586, 0xC430 }),
            "confirmation updates the relationship and consumes exactly one selected item");
        CheckObserverNotices(owner, observer, owner, peer, 0);
        var partnerNative = Broadcasts(owner).Cast<object>().Where(item =>
            ReferenceEquals(Get<object>(Get<object>(item, "Target"), "Session"), peer)).Select(item => Get<ushort>(item, "Opcode"));
        var partnerSession = Get<IList>(owner, "PendingSessionBroadcasts").Cast<object>().Where(item =>
            ReferenceEquals(Get<object>(item, "Target"), peer)).Select(item => Get<ushort>(item, "Opcode"));
        Check(partnerNative.Concat(partnerSession).SequenceEqual(new ushort[] { 0xC36B, 0xC36A, 0xC47F, 0xCB21, 0xC586 }),
            "ordinary inventory separation refreshes the existing peer before its relationship decision");
        CheckUnmoved(owner, 417, 99); CheckUnmoved(peer, 417, 99);
        Check(Character(owner).ActiveCouplePartnerName == "" && Character(peer).ActiveCouplePartnerName == "",
            "ordinary inventory separation clears both active labels");
    }

    private static async Task CheckNativeStartOrderAsync(Fixture f)
    {
        var session = await f.AddAsync("NativeStart", 0);
        Set(session, "NativeBattleEpoch", 17L);
        Set(session, "NativeForwarding", true);
        Set(session, "NativeCoupleStartRequested", false);
        byte[] Frame(ushort opcode, int length)
        {
            var frame = new byte[length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)length);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), opcode);
            return frame;
        }
        var start = Frame(0xCF80, 8);
        await Invoke<Task>(f.Service, "PatchValidatedNativeCoupleFrameAsync", session, start, CancellationToken.None);
        Check(Drain(session).Length == 0, "identity waits for this session's final start request");
        Set(session, "NativeCoupleStartRequested", true);
        foreach (var frame in new[] { Frame(0xCFEC, 808), Frame(0xCF80, 772) })
        {
            await Invoke<Task>(f.Service, "PatchValidatedNativeCoupleFrameAsync", session, frame, CancellationToken.None);
            Check(Drain(session).Length == 0 && !Get<bool>(session, "NativeCoupleIdentityPublished"),
                "game data and extended start frames do not consume final identity eligibility");
        }
        await Invoke<Task>(f.Service, "HandleNativeWorkerFrameAsync", session, start, 17L, CancellationToken.None);
        var writes = Drain(session);
        Check(writes.SelectMany(Opcodes).SequenceEqual(new ushort[] { 0xC588, 0xCF80 })
            && writes[^1].Length == 8, "couple identity precedes the empty final start acknowledgment");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(writes[0].AsSpan(8)) == 0,
            "an unpaired participant clears any prior-stage partner identity");
        await Invoke<Task>(f.Service, "HandleNativeWorkerFrameAsync", session, start, 17L, CancellationToken.None);
        Check(Drain(session).SelectMany(Opcodes).SequenceEqual(new ushort[] { 0xCF80 }),
            "repeated final start does not duplicate couple identity");
        await Invoke<Task>(f.Service, "HandleNativeWorkerFrameAsync", session, start, 16L, CancellationToken.None);
        Check(Drain(session).Length == 0, "stale stage output publishes neither identity nor start");
    }

    private static byte[][] Drain(object session)
    {
        var reader = Get<object>(Get<object>(session, "OutboundWrites"), "Reader");
        var method = reader.GetType().GetMethod("TryRead")!;
        var result = new List<byte[]>(); var arguments = new object?[] { null };
        while ((bool)method.Invoke(reader, arguments)!) result.Add(Get<byte[]>(arguments[0]!, "Frames"));
        return result.ToArray();
    }

    private static async Task CheckVisibilityAsync(Fixture f)
    {
        var first = await f.AddAsync("ScopeOwner", 0);
        var peer = await f.AddAsync("ScopePeer", 1);
        var visible = await f.AddAsync("ScopeViewer", 0);
        var hidden = new List<object>();
        foreach (var (name, property, value) in new (string, string, object)[]
        {
            ("Page", "TownPage", (byte)34), ("Channel", "ChannelId", 2), ("Town", "TownId", (byte)2),
            ("Offline", "OnlineTracked", false), ("Inactive", "TownSceneActive", false)
        })
        {
            var session = await f.AddAsync(name, 0); Set(session, property, value); hidden.Add(session);
        }
        Clear(f);
        var members = Array.CreateInstance(SessionType, 3);
        members.SetValue(first, 0); members.SetValue(peer, 1); members.SetValue(first, 2);
        Invoke<object?>(f.Service, "QueueCoupleTownSceneRefresh", first, members);
        var notices = Broadcasts(first).Cast<object>().ToArray();
        Check(notices.Count(item => ReferenceEquals(Get<object>(Get<object>(item, "Target"), "Session"), visible)) == 8,
            "duplicate participants do not duplicate observer updates");
        Check(notices.Count(item => ReferenceEquals(Get<object>(Get<object>(item, "Target"), "Session"), first)) == 4
            && notices.Count(item => ReferenceEquals(Get<object>(Get<object>(item, "Target"), "Session"), peer)) == 4,
            "both couple participants receive the other actor's same-identity marker replacement");
        Check(notices.All(item => !hidden.Contains(Get<object>(Get<object>(item, "Target"), "Session"))),
            "relationship refresh excludes hidden and other scene scopes");
        Check(notices.All(item => Get<ushort>(item, "Opcode") is 0xC36B or 0xC36A or 0xC47F or 0xCB21),
            "participant and observer refreshes contain no whole-page state");
        var saved = Get<byte[]>(notices[0], "Payload").ToArray();
        Character(first).ActiveCouplePartnerName = "Changed";
        Invoke<object?>(f.Service, "QueueCoupleTownSceneRefresh", first, members);
        Check(Get<byte[]>(notices[0], "Payload").SequenceEqual(saved), "queued relationship snapshots remain immutable");
    }

    private static void CheckUnmoved(object session, int x, int y)
        => Check(Get<ushort>(session, "LastReportedPositionX") == x && Get<ushort>(session, "LastReportedPositionY") == y
            && Character(session).PositionX == x && Character(session).PositionY == y
            && Get<byte>(session, "TownPage") == 33 && Get<bool>(session, "TownSceneActive"),
            "a relationship change preserves active position and scene state");

    private static async Task<byte[]> RequestAsync(Fixture f, object owner, object peer, uint itemCode)
    {
        Check((await f.Database.GrantInventoryItemToAccountAsync(Character(owner).AccountId, itemCode, 1)).Success,
            "the initiator owns the selected item");
        await Invoke<Task>(f.Service, "RefreshSessionCharacterAsync", owner, CancellationToken.None);
        var items = (uint[])typeof(NetworkAdapterService).GetMethod("GetGameInventoryItemCodes", Static)!.Invoke(null, [Character(owner)])!;
        var identities = Get<object>(owner, "GameInventoryIdentities");
        identities.GetType().GetMethod("Synchronize", Instance)!.Invoke(identities, [items]);
        var identity = (byte)identities.GetType().GetMethod("Wire", Instance)!.Invoke(identities, [Array.IndexOf(items, itemCode)])!;
        var payload = new byte[24];
        WriteName(payload, Character(peer).Name);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), itemCode);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), identity);
        return payload;
    }

    private static byte[] Answer(object owner, byte[] request, bool separation, ushort status)
    {
        var payload = new byte[separation ? 24 : 28]; request.CopyTo(payload, 0);
        WriteName(payload, Character(owner).Name);
        if (separation) payload[21] = (byte)status;
        else BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), status);
        return payload;
    }

    private static void WriteName(byte[] payload, string name)
    {
        payload.AsSpan(0, 16).Clear(); Encoding.GetEncoding(936).GetBytes(name).CopyTo(payload, 0);
    }
    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0); return Encoding.GetEncoding(936).GetString(end < 0 ? field : field[..end]);
    }
    private static ushort[] Opcodes(byte[]? response)
    {
        var result = new List<ushort>();
        for (var offset = 0; response is not null && offset < response.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 4));
            Check(length >= 8 && offset + length <= response.Length, "response frames remain bounded");
            result.Add(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 6))); offset += length;
        }
        return result.ToArray();
    }
    private static Task<byte[]?> Dispatch(Fixture f, object session, ushort opcode, byte[] payload)
    {
        var frame = new byte[payload.Length + 8]; payload.CopyTo(frame, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)frame.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), opcode);
        return Invoke<Task<byte[]?>>(f.Service, "HandleNativeFrameAsync", frame, opcode,
            "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None);
    }
    private static void Clear(Fixture f)
    {
        foreach (var session in f.Sessions)
        { Broadcasts(session).Clear(); Get<IList>(session, "PendingSessionBroadcasts").Clear(); }
    }
    private static CharacterRecord Character(object session) => Get<CharacterRecord>(session, "Character");
    private static IList Broadcasts(object session) => Get<IList>(session, "PendingBroadcasts");
    private static T Get<T>(object target, string property) => (T)target.GetType().GetProperty(property)!.GetValue(target)!;
    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static T Invoke<T>(NetworkAdapterService service, string method, params object[] arguments)
        => (T)typeof(NetworkAdapterService).GetMethod(method, Instance)!.Invoke(service, arguments)!;
    private static void Check(bool condition, string description)
    { _checks++; if (!condition) throw new InvalidOperationException(description); }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required DatabaseService Database { get; init; }
        public required NetworkAdapterService Service { get; init; }
        public List<object> Sessions { get; } = [];
        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "NanaimoCoupleScene", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var database = new DatabaseService(root); await database.InitializeAsync();
            return new Fixture { Root = root, Database = database, Service = new(database, _ => { }, root) };
        }
        public async Task<object> AddAsync(string name, int gender)
        {
            var account = await Database.OpenLocalAccountAsync("scene-" + name);
            await Database.CreateLocalCharacterAsync(account, name, gender);
            var character = (await Database.GetCharacterAsync(account))!;
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            var id = Get<string>(session, "SessionId");
            Check(await Database.BeginWorldSessionAsync(account, character.Id, id, 1, "127.0.0.1"), "owned active test session");
            Set(session, "AccountId", account); Set(session, "Username", "scene-" + name);
            Set(session, "Character", character); Set(session, "OnlineTracked", true); Set(session, "TownSceneActive", true);
            Set(session, "ChannelId", 1); Set(session, "TownId", (byte)1); Set(session, "TownPage", (byte)33);
            Set(session, "LastReportedPositionX", (ushort)417); Set(session, "LastReportedPositionY", (ushort)99);
            Set(session, "ListenerPort", 30000); Set(session, "RemoteIp", "127.0.0.1");
            var presence = Activator.CreateInstance(PresenceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [session, id, account, character.Id, "scene-" + name, name, "127.0.0.1", 1,
                    DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })], null)!;
            var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", Instance)!.GetValue(Service)!;
            Check((bool)active.GetType().GetMethod("TryAdd")!.Invoke(active, [id, presence])!, "unique active test presence");
            Sessions.Add(session); return session;
        }
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync(); SqliteConnection.ClearAllPools();
            var root = Path.GetFullPath(Root);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NanaimoCoupleScene")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid isolated test directory.");
            Directory.Delete(root, true);
        }
    }
}
