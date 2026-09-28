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

    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
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
            await using var service = new NetworkAdapterService(database, _ => { }, root);
            int port = ReserveLoopbackPort();
            listener = service.RunLocalProfileListenerAsync(port, stop.Token, root);

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
            byte[] creation = Creation(chosenName, chosenAppearance);
            CheckResult(await Send(service, session, 0x2717, creation), 30,
                "submitted name and appearance create the character through the login dispatcher");
            var character = (await database.GetCharacterAsync(accountId))!;
            Check(character.Name == chosenName && character.Gender == 1
                && character.Appearance.SequenceEqual(chosenAppearance)
                && ((CharacterRecord)SessionType.GetProperty("Character")!.GetValue(session)!).Id == character.Id,
                "creation commits the selected name/appearance and updates the same login session");
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

            byte[] login = CheckPayload(await Send(service, session, 0x2719, new byte[24]), 0x271A, 60);
            Check(login.Length == 60 && login[4] == 1 && login[6] == 0
                && login.AsSpan(24, 36).SequenceEqual(DatabaseService.CreateDefaultAppearance(1)),
                "271A publishes guide state 0 and the gender-correct starter appearance");

            Check(Encoding.GetEncoding(936).GetString(login.AsSpan(8, 16)).TrimEnd('\0') == chosenName,
                "post-creation context carries the player-selected name, not the account identity");
            CheckPayload(await Send(service, session, 0x271B, new byte[4]), 0x271C);
            CheckResult(await Send(service, session, 0x2717, Creation("Replacement", chosenAppearance)), 10,
                "duplicate creation cannot replace the selected character");
            Check((await database.GetCharacterAsync(accountId))!.Name == chosenName,
                "duplicate creation preserves the original name");
            Check((await RegisterAsync(port, request)).AsSpan().SequenceEqual("NO\n"u8),
                "pure registration refuses to reset an existing character");
            Check(await Send(service, NewSession(), 0x2730, new byte[360]) is null,
                "rejected registration does not enqueue a login or reuse the consumed ticket");

            // Reconnect through the same launcher registration mechanism, without
            // manually supplying an account or character to the new session.
            Check((await RegisterAsync(port, JsonSerializer.SerializeToUtf8Bytes(new { LocalAccount = account })))
                .AsSpan().SequenceEqual("OK\n"u8), "existing character can register for normal login");
            object reconnect = NewSession();
            Check(CheckPayload(await Send(service, reconnect, 0x2730, new byte[360]), 0x2731, 4)
                .AsSpan().SequenceEqual(new byte[] { 1, 0, 1, 0 }),
                "launcher login declares the existing character on relogin");
            Check(CheckPayload(await Send(service, reconnect, 0x2719, new byte[24]), 0x271A, 60)
                .SequenceEqual(login), "relogin restores the persisted character and tutorial context");
            CheckPayload(await Send(service, reconnect, 0x271B, new byte[4]), 0x271C);

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
                && secondCharacter.Hans == 0 && secondCharacter.Cash == 0 && secondCharacter.SkillPoints == 0
                && secondCharacter.Items.Count == 0 && !secondCharacter.TutorialCompleted,
                "second pure character remains isolated with no convenience grants");
            Check((await database.GetCharacterAsync(accountId))!.Appearance.SequenceEqual(chosenAppearance),
                "second launch leaves the first character unchanged");
            CheckPayload(await Send(service, second, 0x271B, new byte[4]), 0x271C);

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
