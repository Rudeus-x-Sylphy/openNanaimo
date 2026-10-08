using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class Program
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly Type PresenceType = typeof(NetworkAdapterService).GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
    private static int _checks;

    private static async Task Main(string[] arguments)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WireIdentityAllocator.Reset();
        CheckProjection();
        CheckRelationshipProjection();
        CheckFrameCorrection();
        CheckSubjectProjection();
        await CheckIdentityAndBroadcastsAsync();
        if (arguments.Contains("--verify-integration", StringComparer.Ordinal)) CheckBuilderIntegration();
        Console.WriteLine($"TownTitleRegression PASS ({_checks} checks)");
    }

    private static void CheckProjection()
    {
        var payload = Enumerable.Range(0, TownTitleProjection.UserInfoPayloadLength)
            .Select(value => unchecked((byte)(value * 17))).ToArray();
        const ushort sceneId = 0xA3F;
        const uint initialControl = ((uint)sceneId << 20) | (39u << 12) | (63u << 6) | (2u << 3) | 5u;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(TownTitleProjection.ControlOffset), initialControl);
        var initial = payload.ToArray();
        foreach (var grade in Enumerable.Range(0, 256).Select(value => (byte)value))
        {
            Check(TownTitleProjection.TryApply(payload, sceneId, grade), "title projection accepts its subject identity");
            var control = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(TownTitleProjection.ControlOffset));
            Check(((control >> 6) & 63) == CharacterTitleState.Normalize(grade), "title projection uses the normalized grade");
            Check((control & ~TownTitleProjection.GradeMask) == (initialControl & ~TownTitleProjection.GradeMask),
                "title projection preserves level, construction mode and scene identity");
            Check(payload.AsSpan(0, TownTitleProjection.ControlOffset).SequenceEqual(initial.AsSpan(0, TownTitleProjection.ControlOffset))
                && payload.AsSpan(TownTitleProjection.ControlOffset + 4).SequenceEqual(initial.AsSpan(TownTitleProjection.ControlOffset + 4)),
                "title projection preserves name, appearance, coordinates and character UID");
        }
        foreach (var invalidScene in new ushort[] { 0, 1, 0x1000, ushort.MaxValue })
        {
            var candidate = payload.ToArray();
            Check(!TownTitleProjection.TryApply(candidate, invalidScene, 1) && candidate.SequenceEqual(payload),
                "a mismatched or invalid subject identity preserves the original state");
        }
        foreach (var length in new[] { 0, 52, 103, 105 })
            Check(!TownTitleProjection.TryApply(new byte[length], sceneId, 1), "invalid character state length is rejected");
        foreach (var level in new[] { -1, 0, 1, 39, 42, 52, 99, 100, 127, 128, 199, 200, 201, int.MaxValue })
        {
            Check(TownTitleProjection.TryApply(payload, sceneId, 16, level), "complete town projection accepts the subject level");
            var control = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(TownTitleProjection.ControlOffset));
            Check(((control >> 6) & 63) == 16 && ((control >> 12) & 255) == Math.Clamp(level, 1, CharacterProgression.MaximumLevel),
                "complete town projection independently bounds grade and character level");
            Check((control & ~(TownTitleProjection.GradeMask | TownTitleProjection.LevelMask))
                == (initialControl & ~(TownTitleProjection.GradeMask | TownTitleProjection.LevelMask)),
                "complete town projection preserves construction mode and scene identity");
        }
    }

    private static void CheckRelationshipProjection()
    {
        var baseline = Enumerable.Range(0, 104).Select(value => (byte)(value + 1)).ToArray();
        foreach (var ring in new uint[] { 0, 43000001, 43000002, 43000003, 43000004, 43100001 })
        {
            var payload = baseline.ToArray();
            CoupleProtocol.WriteTownRelationship(payload, "Partner", ring);
            var valid = ring is >= 43000001 and <= 43000003;
            Check(payload.AsSpan(0, 84).SequenceEqual(baseline.AsSpan(0, 84))
                && payload.AsSpan(102).SequenceEqual(baseline.AsSpan(102)),
                "relationship projection preserves every non-relationship field");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(100)) == (valid ? ring - 43000000 : 0),
                "relationship projection writes only a valid ring tier");
            Check(valid ? payload.AsSpan(84, 8).SequenceEqual("Partner\0"u8)
                : payload.AsSpan(84, 18).IndexOfAnyExcept((byte)0) < 0,
                "relationship projection writes its partner name or clears the entire label");
            CoupleProtocol.WriteTownRelationship(payload, "", ring);
            Check(payload.AsSpan(84, 18).IndexOfAnyExcept((byte)0) < 0,
                "an absent relationship clears stale name and ring data");
        }
        var longName = baseline.ToArray();
        CoupleProtocol.WriteTownRelationship(longName, new string('\u4f34', 10), 43000003);
        Check(Encoding.GetEncoding(936).GetString(longName, 84, 14) == new string('\u4f34', 7)
            && longName[98] == 0 && longName[99] == 0, "relationship names keep complete multibyte characters");
        foreach (var length in new[] { 0, 83, 100, 103, 105 })
        {
            var candidate = new byte[length];
            var rejected = false;
            try { CoupleProtocol.WriteTownRelationship(candidate, "Partner", 43000001); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && candidate.All(value => value == 0), "malformed town state is rejected without partial changes");
        }
    }

    private static void CheckFrameCorrection()
    {
        var frame = new byte[112];
        frame[0] = 0x71;
        frame[1] = 42;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 112);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), 0xC36A);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(60), (106u << 20) | (39u << 12) | 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(110), 65530);
        Check(NetworkAdapterService.PatchTownTitleFrame(frame, 106, 23), "a complete town character state receives its title");
        Check((BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(60)) >> 6 & 63) == 23,
            "title correction updates the dedicated grade bits");
        Check(frame[0] == 0x71 && frame[1] == 42 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(110)) == 65530,
            "title correction preserves ordered transport and character UID");
        uint sum = 0;
        foreach (var value in frame.AsSpan(4)) sum += value;
        Check(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2)) == (ushort)(sum ^ 0x0E0E),
            "title correction recomputes the integrity field");
        var preserved = frame.ToArray();
        Check(!NetworkAdapterService.PatchTownTitleFrame(frame, 107, 1) && frame.SequenceEqual(preserved),
            "another scene character cannot overwrite this title");
        foreach (var mutation in new[] { 0, 1 })
        {
            var invalid = preserved.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(mutation == 0 ? 6 : 4), mutation == 0 ? (ushort)0xC47F : (ushort)111);
            var before = invalid.ToArray();
            Check(!NetworkAdapterService.PatchTownTitleFrame(invalid, 106, 1) && invalid.SequenceEqual(before),
                "invalid character state preserves its contents");
        }
        Check(!NetworkAdapterService.PatchTownTitleFrame([], 106, 1), "an empty character state is rejected");
        Check(NetworkAdapterService.PatchTownTitleFrame(frame, 106, 16, 52),
            "complete state correction projects the subject title and character level together");
        var completeControl = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(60));
        Check(((completeControl >> 6) & 63) == 16 && ((completeControl >> 12) & 255) == 52,
            "complete state correction restores both independent fields");
    }

    private static void CheckSubjectProjection()
    {
        var viewer = NewSession(NewCharacter(17, "Viewer", 7, 1));
        var subject = NewCharacter(65537, "Subject", 39, 23);
        WireIdentityAllocator.GetCharacterUid(1);
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), 0xC369);
        var response = InvokeStatic<byte[]>("BuildTownPeerInfoResponse", request, viewer, subject, (ushort)411, (ushort)99);
        Check(response.Length == 164, "subject construction precedes its complete attachment state");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xC36A
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(118)) == 0xC47F,
            "subject initialization preserves construction-before-attachment ordering");
        var control = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(60));
        Check(((control >> 12) & 255) == 39 && ((control >> 6) & 63) == 23,
            "the subject's character level and title remain independent of the viewer");
        Check(control >> 20 == WireIdentityAllocator.GetSceneEntityId(subject.Id)
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(120)) == control >> 20,
            "subject construction and attachment share the assigned scene identity");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(110)) == subject.TownTitleDisplayMode,
            "the subject title display mode is independent of its allocated character UID");
        Check(WireIdentityAllocator.GetSceneEntityId(subject.Id) != WireIdentityAllocator.GetCharacterUid(subject.Id),
            "independently assigned scene and character identities are tested as distinct values");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(88)) == (411u << 2 | 99u << 12),
            "subject position comes from the active scene coordinates");
        var profile = InvokeStatic<byte[]>("BuildProfileResponsePayload", subject);
        Check(profile[31] == 39 && profile[33] == 23, "subject profile agrees with its town title");
        subject.DungeonGrade = 42;
        subject.Level = 4;
        response = InvokeStatic<byte[]>("BuildTownPeerInfoResponse", request, viewer, subject, (ushort)499, (ushort)123);
        control = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(60));
        Check(((control >> 6) & 63) == 42 && ((control >> 12) & 255) == 4,
            "a refreshed title remains independent of the character level");
    }

    private static async Task CheckIdentityAndBroadcastsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "NanaimoTownTitles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (File.Create(Path.Combine(root, "game.db"))) { }
        try
        {
            var database = new DatabaseService(root);
            await using var service = new NetworkAdapterService(database, _ => { }, root);
            var viewer = NewSession(NewCharacter(50001, "Viewer", 7, 1));
            var subject = NewSession(NewCharacter(90001, "Subject", 32, 23));
            var otherPage = NewSession(NewCharacter(90002, "OtherPage", 30, 4));
            Set(otherPage, "TownPage", (byte)34);
            var otherChannel = NewSession(NewCharacter(90003, "OtherChannel", 15, 5));
            Set(otherChannel, "ChannelId", 2);
            var offline = NewSession(NewCharacter(90004, "Offline", 15, 5));
            Set(offline, "OnlineTracked", false);
            var inactive = NewSession(NewCharacter(90005, "Inactive", 15, 5));
            Set(inactive, "TownSceneActive", false);
            var viewerPresence = AddPresence(service, viewer);
            var subjectPresence = AddPresence(service, subject);
            foreach (var hidden in new[] { otherPage, otherChannel, offline, inactive }) AddPresence(service, hidden);
            var subjectCharacter = Get<CharacterRecord>(subject, "Character");
            var subjectSceneId = WireIdentityAllocator.GetSceneEntityId(subjectCharacter.Id);
            Check(ReferenceEquals(InvokeInstance<object?>(service, "FindTownPeerBySceneIdentity", viewer, (int)subjectSceneId), subjectPresence),
                "town selection resolves the assigned scene identity instead of the persistent key");
            Check(InvokeInstance<object?>(service, "FindTownPeerBySceneIdentity", viewer, (int)subjectCharacter.Id) is null,
                "a persistent key cannot select an unrelated scene entity");
            Check(InvokeInstance<object?>(service, "FindTownPeerBySceneIdentity", viewer,
                    (int)WireIdentityAllocator.GetSceneEntityId(Get<CharacterRecord>(viewer, "Character").Id)) is null,
                "the viewer cannot select itself as another town character");
            foreach (var hidden in new[] { otherPage, otherChannel, offline, inactive })
                Check(InvokeInstance<object?>(service, "FindTownPeerBySceneIdentity", viewer,
                        (int)WireIdentityAllocator.GetSceneEntityId(Get<CharacterRecord>(hidden, "Character").Id)) is null,
                    "town selection is scoped to the same active channel and page");
            Check(ReferenceEquals(InvokeInstance<object?>(service, "FindTownPeerBySceneIdentity", viewer, 0), subjectPresence),
                "an unqualified town selection chooses a visible peer deterministically");
            var collidingKey = NewSession(NewCharacter(subjectSceneId, "DifferentSubject", 14, 5));
            AddPresence(service, collidingKey);
            Check(ReferenceEquals(InvokeInstance<object?>(service, "FindTownPeerBySceneIdentity", viewer, (int)subjectSceneId), subjectPresence),
                "a persistent key matching another character's scene identity cannot redirect selection");
            Check(WireIdentityAllocator.GetSceneEntityId(subjectSceneId) != subjectSceneId,
                "the colliding persistent key receives its own distinct scene identity");
            Check(InvokeInstance<bool>(service, "QueueTownPeerSnapshot", viewer, viewerPresence, subject, false),
                "a visible subject queues its state to the viewer");
            var queued = Get<IList>(viewer, "PendingBroadcasts");
            Check(queued.Count == 3, "one subject snapshot queues construction, attachment and stationary position");
            Check(Get<ushort>(queued[0]!, "Opcode") == 0xC36A && Get<ushort>(queued[1]!, "Opcode") == 0xC47F,
                "queued town state preserves its initialization ordering");
            Check(ReferenceEquals(Get<object>(queued[0]!, "Target"), viewerPresence)
                && ReferenceEquals(Get<object>(queued[1]!, "Target"), viewerPresence),
                "subject state routes to the viewer rather than its owner");
            var payload = Get<byte[]>(queued[0]!, "Payload");
            Check(((BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(52)) >> 6) & 63) == 23,
                "queued town construction contains the subject's current title");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(Get<byte[]>(queued[1]!, "Payload")) == subjectSceneId,
                "queued attachment uses the subject's scene identity");
            var preserved = payload.ToArray();
            subjectCharacter.DungeonGrade = 39;
            Check(InvokeInstance<bool>(service, "QueueTownPeerSnapshot", viewer, viewerPresence, subject, false)
                && Get<byte[]>(queued[0]!, "Payload").SequenceEqual(preserved),
                "a refreshed subject title preserves an earlier queued snapshot");
            Check(((BinaryPrimitives.ReadUInt32LittleEndian(Get<byte[]>(queued[4]!, "Payload").AsSpan(52)) >> 6) & 63) == 39,
                "the next subject snapshot carries the latest title");
            Check(InvokeInstance<bool>(service, "QueueTownPeerSnapshot", subject, subjectPresence, viewer, false),
                "the reverse direction queues the viewer as the other visible subject");
            var reverseQueued = Get<IList>(subject, "PendingBroadcasts");
            var reversePayload = Get<byte[]>(reverseQueued[0]!, "Payload");
            var reverseControl = BinaryPrimitives.ReadUInt32LittleEndian(reversePayload.AsSpan(52));
            Check(reverseQueued.Count == 3 && ((reverseControl >> 6) & 63) == 1 && ((reverseControl >> 12) & 255) == 7,
                "both directions display each subject's own title and character level");
            Check(ReferenceEquals(Get<object>(reverseQueued[0]!, "Target"), subjectPresence)
                && reverseControl >> 20 == WireIdentityAllocator.GetSceneEntityId(Get<CharacterRecord>(viewer, "Character").Id),
                "reverse-direction state targets its actual observer");
            foreach (var hidden in new[] { viewer, otherPage, otherChannel, offline, inactive })
                Check(!InvokeInstance<bool>(service, "QueueTownPeerSnapshot", viewer, viewerPresence, hidden, false) && queued.Count == 7,
                    "hidden or local subjects leave the viewer's queue unchanged");
            var unregistered = NewSession(NewCharacter(90006, "Unregistered", 15, 5));
            Check(!InvokeInstance<bool>(service, "QueueTownPeerSnapshot", viewer, viewerPresence, unregistered, false) && queued.Count == 7,
                "an unregistered subject leaves the viewer's queue unchanged");
            var staleRecipient = CreatePresence(viewer);
            Check(!InvokeInstance<bool>(service, "QueueTownPeerSnapshot", viewer, staleRecipient, subject, false) && queued.Count == 7,
                "a superseded recipient leaves the viewer's queue unchanged");
            queued.Clear();
            var movement = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(movement.AsSpan(8), 712);
            BinaryPrimitives.WriteUInt16LittleEndian(movement.AsSpan(10), 144);
            BinaryPrimitives.WriteUInt16LittleEndian(movement.AsSpan(14), subjectSceneId);
            Set(subject, "LastTownMovement", movement);
            Set(subject, "LastReportedPositionX", (ushort)712);
            Set(subject, "LastReportedPositionY", (ushort)144);
            Check(InvokeInstance<bool>(service, "QueueTownPeerSnapshot", viewer, viewerPresence, subject, false), "snapshot includes the stationary current position");
            Check(queued.Count == 4 && Get<ushort>(queued[3]!, "Opcode") == 0xCB21,
                "current position follows construction and attachment");
            Check(Get<byte[]>(queued[3]!, "Payload").AsSpan(0, 8).ToArray().All(b => b == 0x44),
                "position snapshot uses the stationary action code");
            movement[8] = 0;
            Check(Get<byte[]>(queued[3]!, "Payload")[8] != 0, "queued position is an independent snapshot");
            queued.Clear();
            for (var attempt = 0; attempt < 3; attempt++)
                InvokeInstance<object?>(service, "QueueTownAttachmentRefreshes", viewer);
            Check(queued.Count == 6 && Enumerable.Range(0, 3).All(i => Get<ushort>(queued[2*i+1]!, "Opcode") == 0xC47F),
                "three observer activities restore stationary peer attachments");
            InvokeInstance<object?>(service, "QueueTownAttachmentRefreshes", viewer);
            Check(queued.Count == 6, "attachment completion has a bounded lifecycle");
            InvokeInstance<bool>(service, "QueueTownPeerSnapshot", viewer, viewerPresence, subject, false);
            queued.Clear(); Set(subject, "TownPage", (byte)34);
            InvokeInstance<object?>(service, "QueueTownAttachmentRefreshes", viewer);
            Check(queued.Count == 0, "leaving the page retires pending attachment work");

        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolvedRoot = Path.GetFullPath(root);
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NanaimoTownTitles")) + Path.DirectorySeparatorChar;
            if (!resolvedRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test directory is outside its isolated workspace.");
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }

    private static CharacterRecord NewCharacter(long characterId, string name, int level, byte grade)
        => new()
        {
            Id = characterId,
            AccountId = characterId,
            Name = name,
            Level = level,
            Experience = CharacterProgression.ExperienceRequiredForLevel(level),
            DungeonGrade = grade,
            TutorialCompleted = true,
            Appearance = new byte[36],
            MaxHp = 160,
            MaxMp = 100
        };

    private static void CheckBuilderIntegration()
    {
        var subject = NewCharacter(160001, "Integrated", 52, 16);
        var builder = typeof(NetworkAdapterService).GetMethod("BuildTownUserInfoPayload", PrivateStatic, null,
            [typeof(CharacterRecord), typeof(ushort), typeof(ushort), typeof(ushort), typeof(ushort)], null)!;
        var payload = (byte[])builder.Invoke(null, [subject, (ushort)3412, (ushort)63211, (ushort)417, (ushort)99])!;
        var control = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(52));
        Check(((control >> 6) & 63) == 16, "the shared town builder projects the persisted title grade");
        Check(((control >> 12) & 255) == 52, "the shared town builder projects the independent character level");
        Check(control >> 20 == 3412 && ((control >> 3) & 7) == 2,
            "the shared town builder preserves scene identity and construction mode");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(102)) == 2,
            "the shared town builder rejects out-of-range display modes instead of serializing a UID");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(80)) == (417u << 2 | 99u << 12),
            "the shared town builder preserves active scene coordinates");
        var profile = InvokeStatic<byte[]>("BuildProfileResponsePayload", subject);
        Check(profile[31] == ((control >> 12) & 255) && profile[33] == ((control >> 6) & 63),
            "shared town construction and profile detail agree on both progression fields");
    }

    private static object NewSession(CharacterRecord character)
    {
        var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
        Set(session, "Character", character);
        Set(session, "OnlineTracked", true);
        Set(session, "TownSceneActive", true);
        Set(session, "ChannelId", 1);
        Set(session, "TownId", (byte)1);
        Set(session, "TownPage", (byte)33);
        Set(session, "LastReportedPositionX", (ushort)411);
        Set(session, "LastReportedPositionY", (ushort)99);
        return session;
    }

    private static object AddPresence(NetworkAdapterService service, object session)
    {
        var sessionId = Get<string>(session, "SessionId");
        var presence = CreatePresence(session);
        var sessions = typeof(NetworkAdapterService).GetField("_activeWorldSessions", PrivateInstance)!.GetValue(service)!;
        Check((bool)sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, [sessionId, presence])!,
            "a town character owns one active presence");
        return presence;
    }

    private static object CreatePresence(object session)
    {
        var character = Get<CharacterRecord>(session, "Character");
        var sessionId = Get<string>(session, "SessionId");
        var presence = Activator.CreateInstance(PresenceType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [session, sessionId, character.AccountId, character.Id, character.Name, character.Name,
                "127.0.0.1", Get<int>(session, "ChannelId"), DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })], null)!;
        return presence;
    }

    private static void Set(object target, string property, object value)
        => target.GetType().GetProperty(property)!.SetValue(target, value);

    private static TResult Get<TResult>(object target, string property)
        => (TResult)target.GetType().GetProperty(property)!.GetValue(target)!;

    private static TResult InvokeStatic<TResult>(string method, params object[] arguments)
        => (TResult)typeof(NetworkAdapterService).GetMethod(method, PrivateStatic)!.Invoke(null, arguments)!;

    private static TResult InvokeInstance<TResult>(NetworkAdapterService service, string method, params object[] arguments)
        => (TResult)typeof(NetworkAdapterService).GetMethod(method, PrivateInstance)!.Invoke(service, arguments)!;

    private static void Check(bool passed, string description)
    {
        _checks++;
        if (!passed) throw new InvalidOperationException(description);
    }
}
