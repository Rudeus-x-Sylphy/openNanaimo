using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

// Host construction/SQLite/listener lifecycle only. Does not launch or patch a client.
internal static class PureNewPlayerChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService)
        .GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly MethodInfo Dispatch = typeof(NetworkAdapterService)
        .GetMethod("HandleNativeFrameAsync", PrivateInstance)!;
    private static readonly MethodInfo TryLauncherLogin = typeof(NetworkAdapterService)
        .GetMethod("TryLocalLauncherLoginAsync", PrivateInstance)!;
    private static readonly MethodInfo BuildTownUserInfo = typeof(NetworkAdapterService)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(method => method.Name == "BuildTownUserInfoPayload"
            && method.GetParameters().Length == 1);

    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CheckReferralResponseContract();
        string temp = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(temp,
            "open-nanaimo-pure-new-player-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        using (File.Create(Path.Combine(root, "game.db"))) { }
        using var stop = new CancellationTokenSource();
        Task? listener = null;
        try
        {
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            await database.EnsureLocalInitialGrantSettingsAsync();
            Check(!NetworkAdapterService.ResolveLauncherPureNewProfile(IPAddress.Loopback, false)
                && !NetworkAdapterService.ResolveLauncherPureNewProfile(IPAddress.Parse("198.51.100.25"), false)
                && NetworkAdapterService.ResolveLauncherPureNewProfile(IPAddress.Loopback, true)
                && NetworkAdapterService.ResolveLauncherPureNewProfile(IPAddress.Parse("198.51.100.25"), true),
                "launcher registrations preserve the explicit pure-new flag across loopback and social sources");
            long socialAccountId = await database.OpenPureNewLocalAccountAsync(
                "social-ip-check", "198.51.100.25", stop.Token);
            await using (var registration = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.DatabasePath}"))
            {
                await registration.OpenAsync(stop.Token);
                await using var command = registration.CreateCommand();
                command.CommandText = "SELECT RegistrationIp FROM Accounts WHERE Id=$id";
                command.Parameters.AddWithValue("$id", socialAccountId);
                Check((string?)await command.ExecuteScalarAsync(stop.Token) == "198.51.100.25",
                    "pure social launcher accounts persist the actual IPv4 registration source");
            }
            string normalAccount = "normal-" + Guid.NewGuid().ToString("N");
            long normalAccountId = await database.OpenLocalAccountAsync(normalAccount, stop.Token);
            await database.CreateLocalCharacterAsync(normalAccountId, "NormalExisting", 1, stop.Token);
            long attachedNormal = await database.OpenPureNewLocalAccountAsync(normalAccount, stop.Token);
            var preservedNormal = (await database.GetCharacterAsync(normalAccountId, stop.Token))!;
            Check(attachedNormal == normalAccountId && preservedNormal.Name == "NormalExisting"
                && !preservedNormal.PureNewProfile,
                "pure-new request reuses an existing ordinary character without changing profile origin");

            await using var service = new NetworkAdapterService(database, _ => { }, root);
            int port = ReserveLoopbackPort();
            listener = service.RunLocalProfileListenerAsync(IPAddress.Loopback, port, stop.Token, root);

            string account = "pure-" + Guid.NewGuid().ToString("N");
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                LocalAccount = account,
                PureNewPlayer = true
            });
            byte[] ack = await RegisterAsync(port, request);
            Check(ack.AsSpan().SequenceEqual("OK\n"u8),
                "loopback JSON registration accepts PureNewPlayer=true");

            long accountId = (await database.GetAccountIdByUsernameAsync(account))!.Value;
            Check(await database.GetCharacterAsync(accountId) is null,
                "pure registration leaves account characterless for retail creation");

            object wrongSource = NewSession();
            Check(!await TryLogin(service, wrongSource, "127.0.0.2"),
                "launcher registration is isolated by source IP and a mismatch does not consume the queued account");

            object session = NewSession();
            Check(await Send(service, session, 0x2719, new byte[24]) is null,
                "unauthenticated connection cannot obtain a character context");
            CheckResult(await Send(service, session, 0x2717, new byte[52]), 10,
                "unauthenticated creation is retryable and creates no character");
            Check(await Send(service, session, 0x2730, new byte[359]) is null,
                "malformed launcher login does not consume the pending pure account");
            Check(CheckPayload(await Send(service, session, 0x2730, new byte[360]), 0x2731, 4)
                .AsSpan().SequenceEqual(new byte[] { 1, 0, 0, 0 }),
                "launcher login declares no character before naming/appearance creation");
            Check((long)SessionType.GetProperty("AccountId")!.GetValue(session)! == accountId
                && SessionType.GetProperty("Character")!.GetValue(session) is null,
                "launcher login binds the queued pure account without manufacturing a character");
            byte[] creationContext = CheckPayload(
                await Send(service, session, 0x2719, new byte[24]), 0x271A, 60);
            Check(creationContext.Length == 60 && creationContext[4] == 0
                && creationContext[5] == 0 && creationContext[6] == 0
                && creationContext.AsSpan(8, 52).ToArray().All(value => value == 0),
                "characterless login publishes an empty creation projection");
            var prematureChannel = await Send(service, session, 0x271B, new byte[4]);
            Check(prematureChannel is null,
                "characterless account cannot enter channel selection before naming and appearance creation");

            Check(BinaryPrimitives.ReadUInt16LittleEndian(creationContext) == 30,
                "empty character projection retains a successful login context");
            CheckPayload(await Send(service, session, 0x2732, new byte[16]), 0x2733, 4);
            CheckResult(await Send(service, session, 0x2717, new byte[51]), 10,
                "malformed creation remains retryable");
            CheckResult(await Send(service, session, 0x2717, new byte[52]), 10,
                "empty name remains retryable");
            Check(await database.GetCharacterAsync(accountId) is null
                && await Send(service, session, 0x271B, new byte[4]) is null,
                "failed creation leaves storage empty and channel selection locked");

            const string chosenName = "\u65b0\u624b\u4e00\u53f7";
            byte[] chosenAppearance = DatabaseService.CreateDefaultAppearance(1);
            BinaryPrimitives.WriteUInt32LittleEndian(chosenAppearance.AsSpan(0, 4), 10130337u);
            BinaryPrimitives.WriteUInt32LittleEndian(chosenAppearance.AsSpan(8, 4), 10110337u);
            BinaryPrimitives.WriteUInt32LittleEndian(chosenAppearance.AsSpan(24, 4), 10160017u);
            BinaryPrimitives.WriteUInt32LittleEndian(chosenAppearance.AsSpan(28, 4), 15009205u);
            byte[] creation = Creation(chosenName, chosenAppearance);
            CheckResult(await Send(service, session, 0x2717, creation), 30,
                "submitted name and appearance create the character through the login dispatcher");
            var character = (await database.GetCharacterAsync(accountId))!;
            Check(character.Name == chosenName && character.Gender == 1
                && character.Appearance.SequenceEqual(chosenAppearance)
                && character.PureNewProfile
                && ((CharacterRecord)SessionType.GetProperty("Character")!.GetValue(session)!).Id == character.Id,
                "creation commits the selected name/appearance and persistent pure-new origin on the same login session");
            Check(!character.TutorialCompleted && character.Level == 1 && character.Experience == 0,
                "created character enters unfinished level-1 tutorial state");
            Check(character.Hans == 0 && character.Cash == 0 && character.SkillPoints == 0,
                "pure account suppresses configurable local currency/SP grants");
            Check(character.EquippedPetItemCode == 0 && character.Items.Count == 0,
                "pure character has no launcher-selected pet or imported inventory");

            await using (var connection = new SqliteConnection($"Data Source={database.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT a.InitialGrantClaimed,
                           (SELECT COUNT(*) FROM CharacterTasks WHERE CharacterId=c.Id)
                    FROM Accounts a JOIN Characters c ON c.AccountId=a.Id
                    WHERE a.Id=$accountId
                    """;
                command.Parameters.AddWithValue("$accountId", accountId);
                await using var reader = await command.ExecuteReaderAsync();
                Check(await reader.ReadAsync() && reader.GetInt64(0) == 1 && reader.GetInt64(1) == 0,
                    "pure character consumes convenience grant and starts without task rows");
            }

            SessionType.GetProperty("PureNewPlayer")!.SetValue(session, false);
            byte[] login = CheckPayload(await Send(service, session, 0x2719, new byte[24]), 0x271A, 60);
            byte[] tutorialAppearance = chosenAppearance.ToArray();
            tutorialAppearance.AsSpan(24, 8).Clear();
            Check(login.Length == 60 && login[4] == 1 && login[6] == 0
                && login.AsSpan(24, 36).SequenceEqual(tutorialAppearance),
                "271A preserves the player-created avatar while withholding tutorial-unsafe effect/pet slots");

            Check(Encoding.GetEncoding(936).GetString(login.AsSpan(8, 16)).TrimEnd('\0') == chosenName
                && (bool)SessionType.GetProperty("PureNewPlayer")!.GetValue(session)!,
                "post-creation context reloads persistent pure-new origin and carries the selected name");

            byte[] storedAppearance = DatabaseService.NormalizeAppearanceForGender(chosenAppearance, 1, 0);
            byte[] tutorialRoom = NetworkAdapterService.BuildRoomEnterPayload(character, 0, 320, 240);
            Check(tutorialRoom.AsSpan(4, 36).SequenceEqual(storedAppearance),
                "C368 tutorial/village actor creation uses the persisted player appearance");
            byte[] villagePeer = (byte[])BuildTownUserInfo.Invoke(null, [character])!;
            Check(villagePeer.AsSpan(16, 36).SequenceEqual(storedAppearance),
                "C36A village visibility uses the same persisted player appearance before Nemo completion");

            var shortRecommendation = CheckRecommendationResponse(
                await Send(service, session, 0x2725, new byte[15]));
            Check(BinaryPrimitives.ReadUInt32LittleEndian(shortRecommendation) == 10,
                "short recommender input receives native result 10 (not a client UI retry guarantee)");
            CheckPostRecommendationContext(
                await Send(service, session, 0x2719, new byte[24]), login, "short input");

            var emptyRecommendation = CheckRecommendationResponse(
                await Send(service, session, 0x2725, new byte[16]));
            Check(BinaryPrimitives.ReadUInt32LittleEndian(emptyRecommendation) == 10
                && emptyRecommendation.AsSpan(4).ToArray().All(value => value == 0),
                "empty recommender name receives the client-defined nonexistent result without closing the connection");
            CheckPostRecommendationContext(
                await Send(service, session, 0x2719, new byte[24]), login, "empty input");
            var missingRecommendation = CheckRecommendationResponse(
                await Send(service, session, 0x2725, Recommendation("MissingReferrer")));
            Check(BinaryPrimitives.ReadUInt32LittleEndian(missingRecommendation) == 10,
                "unknown recommender name receives the client-defined nonexistent result");
            CheckPostRecommendationContext(
                await Send(service, session, 0x2719, new byte[24]), login, "unknown name");
            var selfRecommendation = CheckRecommendationResponse(
                await Send(service, session, 0x2725, Recommendation(chosenName)));
            Check(BinaryPrimitives.ReadUInt32LittleEndian(selfRecommendation) == 20,
                "the created character cannot recommend itself and keeps the session usable");
            CheckPostRecommendationContext(
                await Send(service, session, 0x2719, new byte[24]), login, "self recommendation");

            // Validate malformed/self request construction and login-session scope.
            foreach (var malformed in new[]
            {
                new byte[17], Enumerable.Repeat((byte)'A', 16).ToArray(),
                new byte[] { 0x81, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                Recommendation("Bad\nName")
            })
            {
                var rejected = CheckRecommendationResponse(await Send(service, session, 0x2725, malformed));
                Check(BinaryPrimitives.ReadUInt32LittleEndian(rejected) == 10
                    && rejected.AsSpan(4).ToArray().All(value => value == 0),
                    "malformed referral has one zero-tailed native failure, never a fabricated success");
            }
            Check(await Send(service, NewSession(), 0x2725, Recommendation("MissingReferrer")) is null,
                "unauthenticated referral does not push a result or login context");
            var wrongChannel = (Task<byte[]?>)Dispatch.Invoke(service,
                [NativeDungeonClient.Frame(0x2725, Recommendation(chosenName)), (ushort)0x2725,
                    "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
            Check(await wrongChannel is null, "referral remains scoped to the login connection");
            CheckPostRecommendationContext(
                await Send(service, session, 0x2719, new byte[24]), login, "malformed/wrong-channel probes");

            const string referrerName = "ReferrerOne";
            long referrerAccount = await database.OpenLocalAccountAsync("referrer-" + Guid.NewGuid().ToString("N"));
            long referrerCharacterId = await database.CreateLocalCharacterAsync(referrerAccount, referrerName, 0);
            Check(await database.GetApartmentRecommendationPointsAsync(referrerCharacterId) == 0,
                "new recommender profile starts without referral points");
            var acceptedRecommendation = CheckRecommendationResponse(
                await Send(service, session, 0x2725, Recommendation(referrerName)));
            Check(BinaryPrimitives.ReadUInt32LittleEndian(acceptedRecommendation) == 30
                && Encoding.GetEncoding(936).GetString(acceptedRecommendation.AsSpan(4, 16)).TrimEnd('\0') == referrerName,
                "existing recommender receives the exact result-30/name response required by the client");
            CheckPostRecommendationContext(
                await Send(service, session, 0x2719, new byte[24]), login, "successful recommendation");
            Check(await database.GetApartmentRecommendationPointsAsync(referrerCharacterId) == 200,
                "first successful character recommendation grants the referrer profile 200 recommendation points");
            var repeatedRecommendation = CheckRecommendationResponse(
                await Send(service, session, 0x2725, Recommendation(referrerName)));
            Check(BinaryPrimitives.ReadUInt32LittleEndian(repeatedRecommendation) == 30
                && await database.GetApartmentRecommendationPointsAsync(referrerCharacterId) == 200,
                "recommendation replay reuses the accepted response without duplicating the 200-point grant");

            var differentReplay = CheckRecommendationResponse(
                await Send(service, session, 0x2725, Recommendation("OtherMissing")));
            Check(differentReplay.SequenceEqual(acceptedRecommendation)
                && await database.GetApartmentRecommendationPointsAsync(referrerCharacterId) == 200,
                "accepted referral is immutable across changed-name replay and rewards only once");
            for (byte tag = 0; tag < 32; tag++)
            {
                var taggedRequest = NativeDungeonClient.Frame(0x2725, Recommendation(referrerName));
                taggedRequest[0] = tag;
                var taggedResponse = await (Task<byte[]?>)Dispatch.Invoke(service,
                    [taggedRequest, (ushort)0x2725, "GameAdapter", "127.0.0.1:30000",
                        "127.0.0.1", session, CancellationToken.None])!;
                Check(CheckRecommendationResponse(taggedResponse).SequenceEqual(acceptedRecommendation)
                    && (taggedResponse![0] & 0x1F) == tag,
                    "all request-low tags preserve a single result, not an unsolicited context burst");
            }

            CheckPayload(await Send(service, session, 0x271B, new byte[4]), 0x271C);
            CheckResult(await Send(service, session, 0x2717, Creation("Replacement", chosenAppearance)), 10,
                "duplicate creation cannot replace the selected character");
            Check((await database.GetCharacterAsync(accountId))!.Name == chosenName,
                "duplicate creation preserves the original name");
            Check((await RegisterAsync(port, request)).AsSpan().SequenceEqual("OK\n"u8),
                "pure registration reopens an existing character without resetting it");

            // Reconnect through the same pure-new-player registration mechanism,
            // without manually supplying an account or character to the new session.
            object reconnect = NewSession();
            Check(CheckPayload(await Send(service, reconnect, 0x2730, new byte[360]), 0x2731, 4)
                .AsSpan().SequenceEqual(new byte[] { 1, 0, 1, 0 }),
                "launcher login declares the existing character on relogin");
            Check(CheckPayload(await Send(service, reconnect, 0x2719, new byte[24]), 0x271A, 60)
                .SequenceEqual(login), "relogin restores the persisted character and tutorial context");
            CheckPayload(await Send(service, reconnect, 0x271B, new byte[4]), 0x271C);

            Check((await RegisterAsync(port, JsonSerializer.SerializeToUtf8Bytes(
                    new { LocalAccount = chosenName, PureNewPlayer = true })))
                .AsSpan().SequenceEqual("OK\n"u8),
                "legacy generated pure account can be recovered by its persisted character name");
            object legacyReconnect = NewSession();
            Check(CheckPayload(await Send(service, legacyReconnect, 0x2730, new byte[360]), 0x2731, 4)
                .AsSpan().SequenceEqual(new byte[] { 1, 0, 1, 0 })
                && (long)SessionType.GetProperty("AccountId")!.GetValue(legacyReconnect)! == accountId,
                "legacy character-name recovery binds the original pure account");

            string secondAccount = "pure-" + Guid.NewGuid().ToString("N");
            Check((await RegisterAsync(port, JsonSerializer.SerializeToUtf8Bytes(
                new { LocalAccount = secondAccount, PureNewPlayer = true }))).AsSpan().SequenceEqual("OK\n"u8),
                "a second pure launch registers a separate account");
            object second = NewSession();
            Check(CheckPayload(await Send(service, second, 0x2730, new byte[360]), 0x2731, 4)
                .AsSpan().SequenceEqual(new byte[] { 1, 0, 0, 0 }),
                "launcher login declares no character before naming/appearance creation");
            Check(CheckPayload(await Send(service, second, 0x2719, new byte[24]), 0x271A, 60)
                .SequenceEqual(creationContext), "another pure launch starts with an empty character context");
            CheckResult(await Send(service, second, 0x2717, creation), 10,
                "a taken name is retryable for a different pure account");
            Check(await Send(service, second, 0x271B, new byte[4]) is null,
                "name conflict does not unlock channel selection");
            byte[] femaleAppearance = DatabaseService.CreateDefaultAppearance(0);
            CheckResult(await Send(service, second, 0x2717, Creation("\u65b0\u624b\u4e8c\u53f7", femaleAppearance)), 30,
                "retry with a unique name creates the other gender");
            long secondId = (await database.GetAccountIdByUsernameAsync(secondAccount))!.Value;
            var secondCharacter = (await database.GetCharacterAsync(secondId))!;
            Check(secondId != accountId && secondCharacter.Gender == 0
                && secondCharacter.Appearance.SequenceEqual(femaleAppearance)
                && secondCharacter.PureNewProfile
                && secondCharacter.Hans == 0 && secondCharacter.Cash == 0 && secondCharacter.SkillPoints == 0
                && secondCharacter.Items.Count == 0 && !secondCharacter.TutorialCompleted,
                "second pure character remains isolated with no convenience grants");
            Check((await database.GetCharacterAsync(accountId))!.Appearance.SequenceEqual(chosenAppearance),
                "second launch leaves the first character unchanged");
            CheckPayload(await Send(service, second, 0x271B, new byte[4]), 0x271C);

            string existingSocialAccount = "social-existing-" + Guid.NewGuid().ToString("N");
            long existingSocialId = await database.OpenLocalAccountAsync(existingSocialAccount, stop.Token);
            byte[] existingAppearance = DatabaseService.CreateDefaultAppearance(0);
            BinaryPrimitives.WriteUInt32LittleEndian(existingAppearance.AsSpan(0, 4), 10130337u - 100000u);
            BinaryPrimitives.WriteUInt32LittleEndian(existingAppearance.AsSpan(8, 4), 10110337u - 100000u);
            var existingCreation = await database.CreateCharacterAsync(
                existingSocialId, "OldSocial", 0, 0, existingAppearance, pureNewProfile: false, cancellationToken: stop.Token);
            Check(existingCreation.Success, "ordinary social account fixture creates its persisted character");
            byte[] existingBefore = (await database.GetCharacterAsync(existingSocialId, stop.Token))!.Appearance.ToArray();
            Check((await RegisterAsync(port, JsonSerializer.SerializeToUtf8Bytes(
                    new { LocalAccount = existingSocialAccount, PureNewPlayer = true }))).AsSpan().SequenceEqual("OK\n"u8),
                "social registration accepts a pure-new request for an existing account");
            object existingSession = NewSession();
            Check(CheckPayload(await Send(service, existingSession, 0x2730, new byte[360]), 0x2731, 4)
                .AsSpan().SequenceEqual(new byte[] { 1, 0, 1, 0 }),
                "existing social account login reports its persisted character");
            byte[] existingLogin = CheckPayload(await Send(service, existingSession, 0x2719, new byte[24]), 0x271A, 60);
            byte[] expectedExistingGuide = DatabaseService.NormalizeAppearanceForGender(existingBefore, 0, 0);
            expectedExistingGuide.AsSpan(24, 8).Clear();
            Check(existingLogin.AsSpan(24, 36).SequenceEqual(expectedExistingGuide)
                && (await database.GetCharacterAsync(existingSocialId, stop.Token))!.Appearance.SequenceEqual(existingBefore),
                "existing ordinary social account keeps its original appearance through 271A and tutorial reentry");

            Console.WriteLine("PURE_NEW_PLAYER_CHECKS_PASS registration=no-profile creation_projection=empty premature_channel=rejected character=dispatcher-created grants=0 tasks=0 tutorial=unfinished; host construction only");
        }
        finally
        {
            stop.Cancel();
            if (listener is not null)
            {
                try { await listener; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            SqliteConnection.ClearAllPools();
            if (root.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("open-nanaimo-pure-new-player-", StringComparison.Ordinal)
                && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }


    private static Task<bool> TryLogin(NetworkAdapterService service, object session, string remoteIp)
        => (Task<bool>)TryLauncherLogin.Invoke(service, [session, remoteIp, CancellationToken.None])!;

    private static object NewSession()
    {
        object session = Activator.CreateInstance(SessionType, nonPublic: true)!;
        SessionType.GetProperty("ListenerPort")!.SetValue(session, 11005);
        return session;
    }

    private static byte[] Creation(string name, byte[] appearance)
    {
        var payload = new byte[52];
        Encoding.GetEncoding(936).GetBytes(name).CopyTo(payload, 0);
        appearance.CopyTo(payload, 16);
        return payload;
    }

    private static void CheckReferralResponseContract()
    {
        var build = typeof(NetworkAdapterService).GetMethod("BuildFriendRecommendationResultPayload",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[] Build(uint result, string? name) => (byte[])build.Invoke(null, [result, name])!;
        foreach (uint result in new uint[] { 10, 20 })
        {
            var payload = Build(result, "ignored");
            Check(payload.Length == 20 && BinaryPrimitives.ReadUInt32LittleEndian(payload) == result
                && payload.AsSpan(4).ToArray().All(value => value == 0),
                "native referral failures fully initialize the name tail");
        }
        foreach (string name in new[] { "123456789012345", "\u63a8\u8350\u4eba" })
        {
            var payload = Build(30, name);
            Check(payload.Length == 20 && BinaryPrimitives.ReadUInt32LittleEndian(payload) == 30
                && Encoding.GetEncoding(936).GetString(payload.AsSpan(4, 16)).TrimEnd('\0') == name
                && payload[19] == 0, "successful referral preserves the complete GBK name and terminator");
        }
        void Reject(uint result, string? name)
        {
            try { Build(result, name); }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { return; }
            throw new InvalidOperationException("Invalid referral result/name must not produce a wire response.");
        }
        foreach (uint result in new uint[] { 0, 1, 40, 2000, uint.MaxValue }) Reject(result, "name");
        foreach (string? name in new string?[] { null, "", " ", "1234567890123456",
            "a\0b", "a\nb", "\ud83d\ude00", new string('\u63a8', 8) }) Reject(30, name);
        Console.WriteLine("PASS referral response guard: native statuses, complete GBK name, no invented continue result");
    }

    private static byte[] Recommendation(string name)
    {
        var payload = new byte[16];
        Encoding.GetEncoding(936).GetBytes(name).CopyTo(payload, 0);
        return payload;
    }

    private static byte[] CheckRecommendationResponse(byte[]? frame)
    {
        const int recommendationFrameLength = 28;
        Check(frame is { Length: recommendationFrameLength },
            "recommendation returns exactly one complete 2726 result");
        var response = frame!;
        Check(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(4, 2)) == recommendationFrameLength
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6, 2)) == 0x2726,
            "recommendation result has the client-required opcode and length");
        return response[8..recommendationFrameLength];
    }

    private static void CheckPostRecommendationContext(
        byte[]? frame,
        byte[] expectedPostLoginPayload,
        string path)
    {
        var payload = CheckPayload(frame, 0x271A, 60);
        Check(payload.SequenceEqual(expectedPostLoginPayload),
            $"{path} leaves the authenticated character context available on the normal request path");
    }

    private static byte[] CheckPayload(byte[]? frame, ushort opcode, int? length = null)
    {
        Check(frame is { Length: >= 8 }
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)) == frame.Length
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == opcode
            && (length is null || frame.Length == length + 8),
            $"single requested response {opcode:X4}, length={length?.ToString() ?? "declared"}");
        return frame![8..];
    }

    private static void CheckResult(byte[]? frame, ushort expected, string message)
    {
        byte[] payload = CheckPayload(frame, 0x2718, 4);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(payload) == expected, message);
    }

    private static Task<byte[]?> Send(NetworkAdapterService service, object session, ushort opcode, byte[] payload)
        => (Task<byte[]?>)Dispatch.Invoke(service,
            [NativeDungeonClient.Frame(opcode, payload), opcode, "GameAdapter", "127.0.0.1:30000",
                "127.0.0.1", session, CancellationToken.None])!;

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<byte[]> RegisterAsync(int port, byte[] request)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();
                await stream.WriteAsync(BitConverter.GetBytes(request.Length));
                await stream.WriteAsync(request);
                var ack = new byte[3];
                await stream.ReadExactlyAsync(ack);
                return ack;
            }
            catch (SocketException ex)
            {
                last = ex;
                await Task.Delay(25);
            }
        }
        throw new InvalidOperationException("Pure-new-player listener did not start.", last);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("PURE_NEW_PLAYER_CHECK_FAILED " + message);
        Console.WriteLine("PURE_NEW_PLAYER_CHECK_PASS " + message);
    }
}
