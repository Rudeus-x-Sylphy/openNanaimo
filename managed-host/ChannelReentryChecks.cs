using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;
using Microsoft.Data.Sqlite;

internal static class ChannelReentryChecks
{
    internal static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string root = Path.Combine(Path.GetTempPath(), "open-nanaimo-channel-reentry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            await database.EnsureLocalInitialGrantSettingsAsync();
            long firstAccount = await database.OpenLocalAccountAsync("channel-reentry-a");
            await database.CreateLocalCharacterAsync(firstAccount, "ReentryA", 1);
            long secondAccount = await database.OpenLocalAccountAsync("channel-reentry-b");
            await database.CreateLocalCharacterAsync(secondAccount, "ReentryB", 1);
            var firstCharacter = (await database.GetCharacterAsync(firstAccount))!;
            var secondCharacter = (await database.GetCharacterAsync(secondAccount))!;

            await using var service = new NetworkAdapterService(database, _ => { }, root);
            typeof(NetworkAdapterService).GetField("_endpoints", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, new List<AdapterEndpoint>
                {
                    new() { Id = 1, Host = "127.0.0.1", Port = 12050, Enabled = true, Capacity = 227 }
                });
            var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var presenceType = typeof(NetworkAdapterService).GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
            var dispatch = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var disconnect = typeof(NetworkAdapterService).GetMethod("TrackDisconnectedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var cacheTicket = typeof(NetworkAdapterService).GetMethod("CacheLoginTicket", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var presenceField = typeof(NetworkAdapterService).GetField("_activeWorldSessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object presences = presenceField.GetValue(service)!;
            MethodInfo addPresence = presences.GetType().GetMethod("TryAdd")!;
            MethodInfo clearPresences = presences.GetType().GetMethod("Clear")!;

            object NewSession(long accountId, string username, object character, bool online)
            {
                object session = Activator.CreateInstance(sessionType, nonPublic: true)!;
                Set(session, "AccountId", accountId);
                Set(session, "Username", username);
                Set(session, "Character", character);
                Set(session, "ChannelId", 1);
                Set(session, "ListenerPort", 12050);
                Set(session, "RemoteIp", "127.0.0.1");
                Set(session, "OnlineTracked", online);
                return session;
            }

            void AddPresence(object session, long accountId, long characterId, string username, string characterName)
            {
                string sessionId = (string)Get(session, "SessionId")!;
                var ctor = presenceType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
                object presence = ctor.Invoke([
                    session, sessionId, accountId, characterId, username, characterName,
                    "127.0.0.1", 1, DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })
                ]);
                Check((bool)addPresence.Invoke(presences, [sessionId, presence])!, "active presence fixture added");
            }

            async Task<byte[]?> Dispatch(object session, ushort opcode, byte[] payload, string channel)
            {
                byte[] frame = NativeDungeonClient.Frame(opcode, payload);
                var task = (Task<byte[]?>)dispatch.Invoke(service,
                    [frame, opcode, channel, "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
                return await task;
            }

            object active = NewSession(firstAccount, "channel-reentry-a", firstCharacter, true);
            AddPresence(active, firstAccount, firstCharacter.Id, "channel-reentry-a", firstCharacter.Name);
            object freshLogin = Activator.CreateInstance(sessionType, nonPublic: true)!;
            Set(freshLogin, "ChannelId", 1);
            byte[] channelFrame = (await Dispatch(freshLogin, 0x271B, new byte[4], "GameAdapter"))!;
            Check(ReadOpcode(channelFrame) == 0x271C && channelFrame.Length == 176, "fresh reconnect gets initialized 271C/176");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(channelFrame.AsSpan(10, 2)) == 1, "fresh reconnect channel count is one");
            Check(channelFrame.AsSpan(16, 4).SequenceEqual(new byte[] { 127, 0, 0, 1 }), "fresh reconnect channel IPv4 is initialized");

            object reentered = Activator.CreateInstance(sessionType, nonPublic: true)!;
            Set(reentered, "ChannelId", 1);
            Set(reentered, "ListenerPort", 12050);
            byte[] identity = new byte[16];
            Encoding.GetEncoding(936).GetBytes(firstCharacter.Name).CopyTo(identity, 0);
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                Set(active, "OnlineTracked", false);
                clearPresences.Invoke(presences, null);
            });
            byte[] reconnect = (await Dispatch(reentered, 0xC351, identity, "WorldAdapter"))!;
            Check(ReadOpcode(reconnect) == 0xC352 && reconnect[8] == 100, "channel reentry restores C351/C352 after prior world teardown");
            byte[] profile = (await Dispatch(reentered, 0xC354, [], "WorldAdapter"))!;
            Check(ReadOpcode(profile) == 0xC355
                  && BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(4, 2)) == 728,
                "channel reentry preserves the request-driven C355 chain");
            await (Task)disconnect.Invoke(service, [reentered])!;

            clearPresences.Invoke(presences, null);
            object disconnectFirst = NewSession(secondAccount, "channel-reentry-b", secondCharacter, true);
            AddPresence(disconnectFirst, secondAccount, secondCharacter.Id, "channel-reentry-b", secondCharacter.Name);
            cacheTicket.Invoke(service, [disconnectFirst, true]);
            Set(disconnectFirst, "OnlineTracked", false);
            clearPresences.Invoke(presences, null);
            object disconnectFirstWorld = Activator.CreateInstance(sessionType, nonPublic: true)!;
            Set(disconnectFirstWorld, "ChannelId", 1);
            Set(disconnectFirstWorld, "ListenerPort", 12050);
            byte[] secondIdentity = new byte[16];
            Encoding.GetEncoding(936).GetBytes(secondCharacter.Name).CopyTo(secondIdentity, 0);
            byte[] disconnectFirstConnect = (await Dispatch(disconnectFirstWorld, 0xC351, secondIdentity, "WorldAdapter"))!;
            Check(ReadOpcode(disconnectFirstConnect) == 0xC352 && disconnectFirstConnect[8] == 100,
                "disconnect-first channel selection consumes the short handoff ticket");
            await (Task)disconnect.Invoke(service, [disconnectFirstWorld])!;

            clearPresences.Invoke(presences, null);
            object ambiguousA = NewSession(firstAccount, "channel-reentry-a", firstCharacter, true);
            object ambiguousB = NewSession(secondAccount, "channel-reentry-b", secondCharacter, true);
            AddPresence(ambiguousA, firstAccount, firstCharacter.Id, "channel-reentry-a", firstCharacter.Name);
            AddPresence(ambiguousB, secondAccount, secondCharacter.Id, "channel-reentry-b", secondCharacter.Name);
            object ambiguousLogin = Activator.CreateInstance(sessionType, nonPublic: true)!;
            Set(ambiguousLogin, "ChannelId", 1);
            byte[] ambiguousList = (await Dispatch(ambiguousLogin, 0x271B, new byte[4], "GameAdapter"))!;
            Check(ReadOpcode(ambiguousList) == 0x271C, "same-IP ambiguity still receives the public channel list");
            object ambiguousWorld = Activator.CreateInstance(sessionType, nonPublic: true)!;
            Set(ambiguousWorld, "ChannelId", 1);
            Set(ambiguousWorld, "ListenerPort", 12050);
            byte[] ambiguousConnect = (await Dispatch(ambiguousWorld, 0xC351, identity, "WorldAdapter"))!;
            Check(ReadOpcode(ambiguousConnect) == 0xC352 && ambiguousConnect[8] == 0, "same-IP ambiguity keeps world identities separate");
            clearPresences.Invoke(presences, null);

            Console.WriteLine("CHANNEL_REENTRY_CHECKS_PASS fresh-271B disconnect-first ticket teardown-wait c351-c355 ambiguity");
        }
        finally
        {
            string full = Path.GetFullPath(root);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            SqliteConnection.ClearAllPools();
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
            {
                for (var retry = 0; retry < 5; retry++)
                {
                    try { Directory.Delete(full, true); break; }
                    catch (IOException) when (retry < 4) { await Task.Delay(50); }
                }
            }
        }
    }

    private static ushort ReadOpcode(byte[] frame)
        => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2));

    private static object? Get(object instance, string name)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance);

    private static void Set(object instance, string name, object? value)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void Check(bool passed, string name)
    {
        if (!passed)
            throw new InvalidDataException("CHANNEL_REENTRY_CHECK_FAILED " + name);
        Console.WriteLine("CHANNEL_REENTRY_CHECK_PASS " + name);
    }
}
