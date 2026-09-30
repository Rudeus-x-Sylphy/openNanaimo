using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static uint Code(byte[] result) => BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(8));
    private static ushort Opcode(byte[] result) => BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(6));
    private static T Get<T>(object value, string property) => (T)value.GetType().GetProperty(property)!.GetValue(value)!;
    private static void Set(object value, string property, object data)
    {
        var info = value.GetType().GetProperty(property)!;
        if (info.SetMethod is not null) info.SetValue(value, data);
        else value.GetType().GetField("<" + property + ">k__BackingField", PrivateInstance)!.SetValue(value, data);
    }
    private static T Invoke<T>(NetworkAdapterService service, string method, params object?[] arguments)
        => (T)typeof(NetworkAdapterService).GetMethod(method, PrivateInstance)!.Invoke(service, arguments)!;

    private static Task<byte[]?> DispatchAsync(NetworkAdapterService service, object session,
        ushort opcode, byte[] payload, ushort control)
    {
        var frame = NativeDungeonClient.Frame(opcode, payload);
        BinaryPrimitives.WriteUInt16LittleEndian(frame, control);
        return Invoke<Task<byte[]?>>(service, "HandleCardExchangeFrameAsync", frame, opcode, session, CancellationToken.None);
    }

    private static byte[] Registration(ushort quantity, uint price)
    {
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), quantity);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), Card);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), price);
        return request;
    }

    private static byte[] Purchase(uint number, uint quantity, uint total)
    {
        var request = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(request, number);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), total);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), quantity);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), Card);
        return request;
    }

    private static byte[] Search(byte type, byte category, byte sort, ushort page, byte pageSize,
        ushort number, string? sellerName = null)
    {
        var request = new byte[24]; request[0] = type; request[1] = category; request[2] = sort; request[3] = pageSize;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), page);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), number);
        if (sellerName is not null) Encoding.GetEncoding(936).GetBytes(sellerName).CopyTo(request, 8);
        return request;
    }

    private sealed record Actor(long Account, long Character, string Session);

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required DatabaseService Database { get; init; }
        public required Actor Seller { get; init; }
        public required Actor Buyer { get; init; }
        public required Actor Third { get; init; }
        private static readonly string Parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NanaimoCardExchangeRegression"));

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var database = new DatabaseService(root);
            try
            {
                await database.InitializeAsync();
                await database.InitializeCardExchangeAsync();
                var seller = await CreateActorAsync(database, "exchange-seller", "Alice");
                var buyer = await CreateActorAsync(database, "exchange-buyer", "Bob");
                var third = await CreateActorAsync(database, "exchange-third", "Charlie");
                var fixture = new Fixture { Root = root, Database = database, Seller = seller, Buyer = buyer, Third = third };
                await fixture.ExecuteAsync("DELETE FROM CharacterCards");
                await fixture.CoinsAsync(seller, 100, 700);
                await fixture.CoinsAsync(buyer, 1000, 900);
                await fixture.CoinsAsync(third, 1000, 800);
                return fixture;
            }
            catch { SafeDelete(root); throw; }
        }

        public static async Task<Fixture> ExchangeAsync()
        {
            var fixture = await CreateAsync();
            await fixture.PointsAsync(fixture.Seller, 100, 700);
            await fixture.PointsAsync(fixture.Buyer, 1000, 900);
            await fixture.PointsAsync(fixture.Third, 1000, 800);
            return fixture;
        }

        public Task PointsAsync(Actor actor, long points, long gold) => CoinsAsync(actor, gold, points);
        public async Task<(long NanaPoints, long Coins)> PointWalletAsync(Actor actor)
        {
            var wallet = await WalletAsync(actor);
            return (wallet.Nana, wallet.Coins);
        }

        private static async Task<Actor> CreateActorAsync(DatabaseService database, string username, string name)
        {
            var account = await database.OpenLocalAccountAsync(username);
            await database.CreateLocalCharacterAsync(account, name, 0);
            var character = (await database.GetCharacterAsync(account))!;
            var session = Guid.NewGuid().ToString("N");
            if (!await database.BeginWorldSessionAsync(account, character.Id, session, 1, "127.0.0.1"))
                throw new InvalidOperationException("Unable to create owned exchange session.");
            return new(account, character.Id, session);
        }

        public async Task<object> CreateAdapterSessionAsync(Actor actor)
        {
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            Set(session, "AccountId", actor.Account); Set(session, "SessionId", actor.Session);
            Set(session, "Character", (await Database.GetCharacterAsync(actor.Account))!);
            Set(session, "OnlineTracked", true);
            return session;
        }

        public Task<CardExchangeMutationResult> RegisterAsync(string id, ushort quantity, uint price, uint code = Card, byte type = 0)
            => Database.RegisterCardExchangeListingAsync(Seller.Account, Seller.Character, Seller.Session, id,
                type, code, quantity, price);
        public Task<CardExchangeMutationResult> BuyAsync(Actor actor, string id, ulong number, uint quantity, uint total, uint code = Card)
            => Database.PurchaseCardExchangeListingAsync(actor.Account, actor.Character, actor.Session, id,
                number, total, quantity, code);
        public Task<CardExchangeMutationResult> RetrieveAsync(Actor actor, string id, ulong number, uint type = 1)
            => Database.RetrieveCardExchangeListingAsync(actor.Account, actor.Character, actor.Session, id, type, number);
        public Task<CardExchangeListResult> QueryAsync(Actor actor, CardExchangeQuery query)
            => Database.QueryCardExchangeListingsAsync(actor.Account, actor.Character, actor.Session, query);
        public Task<long> ListingCountAsync() => ScalarAsync("SELECT COUNT(*) FROM AuctionListings");
        public Task<long> QuantityAsync(Actor actor, uint code) => ScalarAsync(
            "SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=$code", ("$id", actor.Character), ("$code", code));

        public async Task CardsAsync(Actor actor, uint code, int quantity)
        {
            await ExecuteAsync("DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode=$code",
                ("$id", actor.Character), ("$code", code));
            if (quantity > 0) await ExecuteAsync("INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$code,$quantity,$now)",
                ("$id", actor.Character), ("$code", code), ("$quantity", quantity), ("$now", DateTime.UtcNow.ToString("O")));
        }
        public Task CoinsAsync(Actor actor, long coins, long nana) => ExecuteAsync(
            "UPDATE Characters SET Hans=$coins,Cash=$nana WHERE Id=$id",
            ("$id", actor.Character), ("$coins", coins), ("$nana", nana));
        public async Task<(long Coins, long Nana)> WalletAsync(Actor actor)
            => (await ScalarAsync("SELECT Hans FROM Characters WHERE Id=$id", ("$id", actor.Character)),
                await ScalarAsync("SELECT Cash FROM Characters WHERE Id=$id", ("$id", actor.Character)));

        private async Task<SqliteConnection> OpenAsync()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Database.DatabasePath, ForeignKeys = true, DefaultTimeout = 15
            }.ToString());
            await connection.OpenAsync();
            return connection;
        }
        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
        public async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        private static void SafeDelete(string directory)
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(directory);
            if (!target.StartsWith(Parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid exchange test workspace.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
        public ValueTask DisposeAsync() { SafeDelete(Root); return ValueTask.CompletedTask; }
    }
}
