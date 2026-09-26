using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentLeaseChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "open-nanaimo-apartment-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var suite = new Suite();
            suite.CheckPolicy();
            suite.CheckCalendarState();
            await suite.CheckLeaseAsync(Path.Combine(root, "lease"));
            await suite.CheckMigrationAsync(Path.Combine(root, "migration"));
            await suite.CheckConcurrencyAsync(Path.Combine(root, "concurrency"));
            Console.WriteLine($"APARTMENT_LEASE_CHECKS_PASS checks={suite.Checks}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("open-nanaimo-apartment-lease-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected apartment lease test directory.");
            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class Suite
    {
        internal int Checks { get; private set; }
        private void Check(bool condition, string description)
        {
            Checks++;
            if (!condition) throw new InvalidDataException("APARTMENT_LEASE_CHECK_FAILED " + description);
        }

        internal void CheckPolicy()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            Check(ApartmentHousingPolicy.LeaseDays == 14 && ApartmentHousingPolicy.LeaseDuration == TimeSpan.FromDays(14),
                "the lease lasts fourteen days");
            Check(ApartmentHousingPolicy.GetRemainingSeconds(now, now) == 0
                && ApartmentHousingPolicy.GetRemainingSeconds(now.AddTicks(-1), now) == 0,
                "the expiration boundary and past leases have zero remaining time");
            Check(ApartmentHousingPolicy.GetRemainingSeconds(now.AddTicks(1), now) == 1
                && ApartmentHousingPolicy.GetRemainingSeconds(now.AddSeconds(1), now) == 1
                && ApartmentHousingPolicy.GetRemainingSeconds(now.AddSeconds(1).AddTicks(1), now) == 2,
                "fractional seconds round upward without extending exact seconds");
            Check(ApartmentHousingPolicy.GetRemainingSeconds(now + ApartmentHousingPolicy.LeaseDuration, now) == 1_209_600,
                "a full lease has the expected remaining seconds");
            Check(ApartmentHousingPolicy.GetRemainingSeconds(now.ToOffset(TimeSpan.FromHours(8)), now) == 0,
                "remaining time compares instants rather than local clocks");
            Check(Parse("2026-09-26T14:39:09.5249710Z") + ApartmentHousingPolicy.LeaseDuration
                    == Parse("2026-10-10T14:39:09.5249710Z"), "legacy UTC timestamps retain subsecond precision across the lease period");
            Check(new ApartmentHouse(1, 0, 7, 0, 0, 0, "", "Owner", 0).ExpiresAt == default
                && new ApartmentExteriorState(false, 0, 0, "", []).RemainingSeconds == 0,
                "existing record constructors remain compatible");
        }

        internal void CheckCalendarState()
        {
            var now = new DateTimeOffset(2026, 9, 26, 23, 59, 59, TimeSpan.Zero);
            Check(NetworkAdapterService.EncodeApartmentHouseTime(now) == 2026092623,
                "calendar encoding is YYYYMMDDHH rather than elapsed seconds");
            Check(NetworkAdapterService.EncodeApartmentHouseTime(now.ToOffset(TimeSpan.FromHours(-7))) == 2026092623,
                "calendar encoding normalizes the same instant to UTC");
            var leap = new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero);
            Check(NetworkAdapterService.EncodeApartmentHouseTime(leap) == 2028022900,
                "calendar encoding preserves leap days and midnight");
            var house = new ApartmentHouse(1, 2, 7, 3, 0, 0, "", "Owner", 0, now.AddDays(14));
            foreach (var owner in new[] { true, false })
            {
                var payload = Enumerable.Repeat((byte)0xA5, 104).ToArray();
                NetworkAdapterService.ApplyApartmentHouseState(payload, owner, house, now);
                Check(payload[1] == (owner ? 10 : 30) && payload[24] == 2 && payload[25] == 0
                    && payload[26] == 7 && payload[27] == 3,
                    "active owner and visitor roles share the exact street address");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(32)) == 2026101023
                    && BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(36)) == 2026092623,
                    "entry carries expiration and current calendar values at their separate offsets");
                Check(payload[0] == 0xA5 && payload[23] == 0xA5 && payload[28] == 0xA5
                    && payload[31] == 0xA5 && payload[40] == 0xA5 && payload[103] == 0xA5,
                    "housing projection preserves actor coordinates balances and furniture fields");
                foreach (var absent in new ApartmentHouse?[] { null, house with { ExpiresAt = now }, house with { ExpiresAt = now.AddTicks(-1) } })
                {
                    NetworkAdapterService.ApplyApartmentHouseState(payload, owner, absent, now);
                    Check(payload[1] == (owner ? 20 : 40) && payload.AsSpan(24, 4).ToArray().All(b => b == 0)
                        && payload.AsSpan(32, 8).ToArray().All(b => b == 0),
                        "absent or expired street address clears both calendar values while retaining room ownership");
                }
            }
        }

        internal async Task CheckLeaseAsync(string root)
        {
            var f = await Fixture.CreateAsync(root, 4);
            Check(await f.Exterior(0, 31000010) == 10, "a character without a house can buy warehouse exteriors");
            Check((await f.Db.GetApartmentExteriorStateAsync(f.Id(0))) is { HasHouse: false, RemainingSeconds: 0, Items.Count: 1 },
                "warehouse inventory does not imply an active lease");
            await f.Fund(0);
            var before = DateTimeOffset.UtcNow;
            Check(await f.Buy(0, 7, 0) == 10, "a funded character can lease a street address");
            var after = DateTimeOffset.UtcNow;
            var house = (await f.Db.GetOwnedApartmentHouseAsync(f.Id(0)))!;
            Check(house.ExpiresAt.Offset == TimeSpan.Zero && house.ExpiresAt >= before + ApartmentHousingPolicy.LeaseDuration
                && house.ExpiresAt <= after + ApartmentHousingPolicy.LeaseDuration, "new lease expiration is UTC and fourteen days from purchase");
            var purchased = Parse(await f.Scalar("SELECT PurchasedAt FROM CharacterApartmentHouses WHERE CharacterId=$id", ("$id", f.Id(0))));
            Check(house.ExpiresAt - purchased == ApartmentHousingPolicy.LeaseDuration, "persisted purchase and expiration use the same clock sample");
            Check((await f.Scalar("SELECT ExpiresAt FROM CharacterApartmentHouses WHERE CharacterId=$id", ("$id", f.Id(0)))).EndsWith("Z", StringComparison.Ordinal),
                "expiration is persisted in canonical UTC format");
            Check(await f.WalletIs(0, Fixture.Hans - 9800, Fixture.Points - 200), "leasing debits Hans and recommendation points but not Cash");
            Check(await f.Buy(0, 7, 1) == 50 && await f.Buy(0, 7, 0) == 50,
                "an active tenant cannot purchase either the same or a different address");
            Check(await f.Buy(1, 7, 0) == 40 && await f.WalletIs(1, Fixture.Hans, Fixture.Points),
                "another tenant sees occupied-address failure without debit");
            Check(await f.Db.SaveApartmentExteriorAsync(f.Account(0), f.Id(0), f.Session(0), [1, 0, 0, 0, 0, 0, 0, 0], "#Lease"),
                "an active tenant can apply an owned exterior");
            var state = await f.Db.GetApartmentExteriorStateAsync(f.Id(0));
            Check(state.HasHouse && state.Exterior == 31000010 && state.Text == "#Lease"
                && state.RemainingSeconds > 0 && state.RemainingSeconds <= 1_209_600, "active exterior state includes remaining lease time");
            var reopened = new DatabaseService(root);
            Check((await reopened.GetOwnedApartmentHouseAsync(f.Id(0)))!.ExpiresAt == house.ExpiresAt
                && (await reopened.GetApartmentExteriorStateAsync(f.Id(0))).Exterior == 31000010,
                "lease expiration and equipment survive reopening");

            await f.Expire(0);
            Check((await f.Db.GetApartmentHousesAsync(0, 7)).Count == 0
                && await f.Db.GetOwnedApartmentHouseAsync(f.Id(0)) is null,
                "expired houses disappear from the street and owned-house lookup");
            state = await f.Db.GetApartmentExteriorStateAsync(f.Id(0));
            Check(!state.HasHouse && state.Exterior == 0 && state.Banner == 0 && state.Text == ""
                && state.RemainingSeconds == 0 && state.Items.Count == 1, "expiration clears effective equipment but retains warehouse inventory");
            Check(!await f.Db.SaveApartmentExteriorAsync(f.Account(0), f.Id(0), f.Session(0), new byte[8], "#Expired"),
                "an expired tenant cannot save even a text-only exterior change");
            Check(await f.CountHouses() == 1 && await f.Scalar("SELECT BannerText FROM CharacterApartmentHouses WHERE CharacterId=$id", ("$id", f.Id(0))) == "#Lease",
                "reads and rejected saves do not clean up expired occupancy or alter equipment");
            Check(await f.Db.PurchaseApartmentHouseAsync(f.Account(1), f.Id(1), "stale", 0, 7, 0) == 40
                && await f.Db.PurchaseApartmentHouseAsync(f.Account(1), f.Id(0), f.Session(0), 0, 7, 0) == 40
                && await f.CountHouses() == 1, "unauthorized purchases cannot clean expired occupancy");
            Check(await f.Exterior(0, 32000001) == 10, "an expired tenant can still buy warehouse items");

            Check(await f.Buy(1, 7, 0) == 10 && await f.Db.GetOwnedApartmentHouseAsync(f.Id(0)) is null,
                "another tenant can purchase an expired address");
            Check((await f.Db.GetApartmentExteriorStateAsync(f.Id(0))).Items.Count == 2,
                "replacing expired occupancy preserves the former tenant's inventory");
            Check(await f.Buy(0, 7, 1) == 10, "an expired former tenant can move to a different address");
            Check((await f.Db.GetApartmentExteriorStateAsync(f.Id(0))) is { HasHouse: true, Exterior: 0, Items.Count: 2 },
                "a new lease retains inventory without inheriting expired equipment");
            Check(await f.Buy(2, 7, 2) == 10, "create unrelated lease");
            await f.Expire(0); await f.Expire(1); await f.Expire(2);
            await f.Fund(0, 9799, 200);
            Check(await f.Buy(0, 7, 0) == 20 && await f.WalletIs(0, 9799, 200) && await f.CountHouses() == 3,
                "Hans failure rolls back points and cleanup of both old and target addresses");
            await f.Fund(0, 9800, 199);
            Check(await f.Buy(0, 7, 0) == 30 && await f.WalletIs(0, 9800, 199) && await f.CountHouses() == 3,
                "point failure rolls back expired occupancy cleanup");
            await f.Fund(0, 9800, 200);
            await f.Sql("CREATE TRIGGER RejectLeaseInsert BEFORE INSERT ON CharacterApartmentHouses BEGIN SELECT RAISE(ABORT, 'lease rollback test'); END;");
            var aborted = false;
            try { await f.Buy(0, 7, 0); } catch (SqliteException) { aborted = true; }
            await f.Sql("DROP TRIGGER RejectLeaseInsert;");
            Check(aborted && await f.WalletIs(0, 9800, 200) && await f.CountHouses() == 3,
                "an insert error rolls back both wallets and all targeted cleanup");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var canceled = false;
                try { await f.Db.PurchaseApartmentHouseAsync(f.Account(0), f.Id(0), f.Session(0), 0, 7, 0, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && await f.WalletIs(0, 9800, 200) && await f.CountHouses() == 3,
                    "a canceled purchase cannot debit or clean occupancy");
            }
            Check(await f.Buy(0, 7, 0) == 10 && await f.WalletIs(0, 0, 0) && await f.CountHouses() == 2,
                "successful relocation cleans only the buyer's old address and the target address");
            Check(await f.Scalar("SELECT COUNT(*) FROM CharacterApartmentHouses WHERE CharacterId=$id", ("$id", f.Id(2))) == "1"
                && (await f.Db.GetApartmentHousesAsync(0, 7)).Count == 1,
                "unrelated expired rows remain stored but invisible");
            Check((await new DatabaseService(root).GetOwnedApartmentHouseAsync(f.Id(0))) is { Page: 7, Slot: 0 }
                && (await f.Db.GetApartmentExteriorStateAsync(f.Id(0))).Items.Count == 2,
                "replacement and preserved inventory survive reopening");
        }

        internal async Task CheckMigrationAsync(string root)
        {
            var f = await Fixture.CreateAsync(root, 3);
            await f.Sql("""
                DROP TABLE CharacterApartmentHouses;
                CREATE TABLE CharacterApartmentHouses (
                    CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id) ON DELETE CASCADE,
                    Town INTEGER NOT NULL, Page INTEGER NOT NULL, Slot INTEGER NOT NULL,
                    Exterior INTEGER NOT NULL DEFAULT 0, Banner INTEGER NOT NULL DEFAULT 0,
                    BannerText TEXT NOT NULL DEFAULT '', PurchasedAt TEXT NOT NULL,
                    UNIQUE(Town,Page,Slot));
                """);
            var activePurchase = DateTimeOffset.UtcNow.AddDays(-2).ToOffset(TimeSpan.FromHours(8));
            var oldPurchase = DateTimeOffset.UtcNow.AddDays(-30);
            for (var i = 0; i < 3; i++)
                await f.Sql("INSERT INTO CharacterApartmentHouses(CharacterId,Town,Page,Slot,Exterior,BannerText,PurchasedAt) VALUES($id,0,7,$slot,31000010,'#Old',$purchased)",
                    ("$id", f.Id(i)), ("$slot", i), ("$purchased", i == 0 ? activePurchase.ToString("O") : i == 1 ? Stamp(oldPurchase) : "invalid-date"));
            await f.Sql("INSERT INTO CharacterApartmentExteriors(CharacterId,ItemCode) VALUES($id,31000010)", ("$id", f.Id(1)));
            var cold = new DatabaseService(root);
            var malformed = false;
            try { await cold.GetApartmentHousesAsync(0, 7); } catch (FormatException) { malformed = true; }
            Check(malformed && await f.Scalar("SELECT COUNT(*) FROM pragma_table_info('CharacterApartmentHouses') WHERE name='ExpiresAt'") == "0"
                && await f.CountHouses() == 3, "invalid legacy timestamps roll back the entire schema migration");
            await f.Sql("UPDATE CharacterApartmentHouses SET PurchasedAt=$purchased WHERE CharacterId=$id", ("$purchased", Stamp(oldPurchase)), ("$id", f.Id(2)));
            await f.Sql($"CREATE TRIGGER RejectLeaseMigration BEFORE UPDATE ON CharacterApartmentHouses WHEN OLD.CharacterId={f.Id(1)} BEGIN SELECT RAISE(ABORT, 'migration rollback test'); END;");
            var aborted = false;
            try { await cold.GetApartmentHousesAsync(0, 7); } catch (SqliteException) { aborted = true; }
            await f.Sql("DROP TRIGGER RejectLeaseMigration;");
            Check(aborted && await f.Scalar("SELECT COUNT(*) FROM pragma_table_info('CharacterApartmentHouses') WHERE name='ExpiresAt'") == "0",
                "a migration update failure rolls back its column and previously updated rows");
            var services = new[] { cold, cold, new DatabaseService(root), new DatabaseService(root) };
            var migrated = await Task.WhenAll(services.Select(db => Task.Run(() => db.GetApartmentHousesAsync(0, 7))));
            Check(migrated.All(rows => rows.Count == 1 && rows[0].CharacterId == f.Id(0)
                && rows[0].ExpiresAt == activePurchase + ApartmentHousingPolicy.LeaseDuration),
                "concurrent first calls and service instances migrate once and retain the original purchase date");
            Check(Parse(await f.Scalar("SELECT ExpiresAt FROM CharacterApartmentHouses WHERE CharacterId=$id", ("$id", f.Id(1))))
                    == oldPurchase + ApartmentHousingPolicy.LeaseDuration && await f.CountHouses() == 3,
                "expired legacy rows are migrated without deleting occupancy");
            Check(await f.Scalar("SELECT PurchasedAt FROM CharacterApartmentHouses WHERE CharacterId=$id", ("$id", f.Id(0))) == activePurchase.ToString("O")
                && (await f.Scalar("SELECT ExpiresAt FROM CharacterApartmentHouses WHERE CharacterId=$id", ("$id", f.Id(0)))).EndsWith("Z", StringComparison.Ordinal),
                "migration preserves original purchase text and normalizes expiration to UTC");
            Check((await cold.GetApartmentExteriorStateAsync(f.Id(1))) is { HasHouse: false, RemainingSeconds: 0, Items.Count: 1 },
                "migration retains expired owners' warehouse items");
            Check((await new DatabaseService(root).GetOwnedApartmentHouseAsync(f.Id(0)))!.ExpiresAt
                    == activePurchase + ApartmentHousingPolicy.LeaseDuration,
                "reopening does not renew a migrated lease");
            Check(await cold.PurchaseApartmentHouseAsync(f.Account(1), f.Id(1), f.Session(1), 0, 7, 2) == 10,
                "an expired legacy tenant can replace another expired legacy address");
            Check((await cold.GetApartmentExteriorStateAsync(f.Id(1))) is { HasHouse: true, Items.Count: 1 }
                && await f.CountHouses() == 2, "legacy relocation preserves inventory and cleans only affected rows");
        }

        internal async Task CheckConcurrencyAsync(string root)
        {
            var f = await Fixture.CreateAsync(root, 8);
            var cold = new DatabaseService(root);
            var sameTenant = await Task.WhenAll(Enumerable.Range(0, 5).Select(slot => Task.Run(() =>
                cold.PurchaseApartmentHouseAsync(f.Account(0), f.Id(0), f.Session(0), 0, 7, (ushort)slot))));
            Check(sameTenant.Count(code => code == 10) == 1 && sameTenant.Count(code => code == 50) == 4
                && await f.WalletIs(0, Fixture.Hans - 9800, Fixture.Points - 200),
                "concurrent first purchases for one tenant produce one lease and one debit");
            var expiredOwner = (await cold.GetOwnedApartmentHouseAsync(f.Id(0)))!;
            await f.Expire(0);
            var contention = await Task.WhenAll(Enumerable.Range(1, 7).Select(index => Task.Run(() =>
                new DatabaseService(root).PurchaseApartmentHouseAsync(f.Account(index), f.Id(index), f.Session(index), 0, 7, expiredOwner.Slot))));
            Check(contention.Count(code => code == 10) == 1 && contention.Count(code => code == 40) == 6
                && await f.CountHouses() == 1, "cross-instance contention replaces one expired address exactly once");
            for (var index = 1; index < 8; index++)
                Check(await f.WalletIs(index, Fixture.Hans - (contention[index - 1] == 10 ? 9800 : 0),
                    Fixture.Points - (contention[index - 1] == 10 ? 200 : 0)), "only the winning tenant is charged under contention");
            var winner = Array.IndexOf(contention, 10u) + 1;
            var original = (await cold.GetOwnedApartmentHouseAsync(f.Id(winner)))!.ExpiresAt;
            var reads = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => cold.GetApartmentExteriorStateAsync(f.Id(winner)))));
            Check(reads.All(state => state.HasHouse && state.RemainingSeconds > 0)
                && (await cold.GetOwnedApartmentHouseAsync(f.Id(winner)))!.ExpiresAt == original,
                "parallel reads do not reinitialize or renew leases");
        }
    }

    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed class Fixture(string root)
    {
        internal const long Hans = 100000;
        internal const long Points = 2000;
        internal DatabaseService Db { get; } = new(root);
        private readonly List<CharacterRecord> _characters = [];
        private readonly List<string> _sessions = [];
        internal long Id(int index) => _characters[index].Id;
        internal long Account(int index) => _characters[index].AccountId;
        internal string Session(int index) => _sessions[index];

        internal static async Task<Fixture> CreateAsync(string root, int characters)
        {
            Directory.CreateDirectory(root);
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var fixture = new Fixture(root);
            await fixture.Db.InitializeAsync();
            for (var i = 0; i < characters; i++)
            {
                var account = await fixture.Db.OpenLocalAccountAsync("lease-" + i);
                await fixture.Db.CreateLocalCharacterAsync(account, "Lease" + i, i % 2);
                var actor = (await fixture.Db.GetCharacterAsync(account))!;
                var session = Guid.NewGuid().ToString("N");
                if (!await fixture.Db.BeginWorldSessionAsync(account, actor.Id, session, i % 2 + 1, "127.0.0.1"))
                    throw new InvalidOperationException("Cannot establish lease test session.");
                fixture._characters.Add(actor); fixture._sessions.Add(session);
                await fixture.Fund(i);
            }
            return fixture;
        }

        internal Task<uint> Buy(int index, ushort page, ushort slot)
            => Db.PurchaseApartmentHouseAsync(Account(index), Id(index), Session(index), 0, page, slot);
        internal Task<byte> Exterior(int index, uint code)
            => Db.PurchaseApartmentExteriorAsync(Account(index), Id(index), Session(index), code);
        internal Task Expire(int index) => Sql("UPDATE CharacterApartmentHouses SET ExpiresAt=$expired WHERE CharacterId=$id",
            ("$expired", Stamp(DateTimeOffset.UtcNow.AddMinutes(-1))), ("$id", Id(index)));
        internal Task Fund(int index, long hans = Hans, long points = Points)
            => Sql("UPDATE Characters SET Hans=$hans,Cash=777 WHERE Id=$id; "
                + "INSERT INTO CharacterApartmentProfile(CharacterId,RecommendationPoints) VALUES($id,$points) "
                + "ON CONFLICT(CharacterId) DO UPDATE SET RecommendationPoints=excluded.RecommendationPoints;",
                ("$id", Id(index)), ("$hans", hans), ("$points", points));
        internal async Task<bool> WalletIs(int index, long hans, long points)
        {
            var actor = (await Db.GetCharacterByIdAsync(Id(index)))!;
            return actor.Hans == hans && actor.Cash == 777 && await Db.GetApartmentRecommendationPointsAsync(Id(index)) == points;
        }
        internal async Task<long> CountHouses() => long.Parse(await Scalar("SELECT COUNT(*) FROM CharacterApartmentHouses"), CultureInfo.InvariantCulture);
        internal async Task Sql(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection($"Data Source={Db.DatabasePath};Foreign Keys=True;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
        internal async Task<string> Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection($"Data Source={Db.DatabasePath};Foreign Keys=True;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
        }
    }
}
