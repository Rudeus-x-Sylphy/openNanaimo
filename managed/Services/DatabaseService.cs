using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenNanaimo.Adapter.Models;
using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public enum FriendRecommendationStatus
{
    Nonexistent = 10,
    Self = 20,
    Success = 30
}

public readonly record struct FriendRecommendationResult(
    FriendRecommendationStatus Status,
    string? RecommendedCharacterName);

public sealed partial class DatabaseService
{
    private const int TutorialMapId = 0;
    private const int DefaultSpawnMapId = 1;
    private const int DefaultTownPage = 33;
    private const int DefaultSpawnX = 320;
    private const int DefaultSpawnY = 240;
    private static readonly uint[] ApartmentStarterItemCodes =
    [
        11_000_028u, // traditional wooden floor, inter._D3 type 0
        11_110_033u, // traditional door/window wallpaper, type 1
        11_250_030u, // traditional square cushion, type 2
        11_340_007u, // wooden door, type 3
        11_470_043u  // student desk lamp, type 4
    ];
    private static readonly byte[] DefaultFemaleAppearance = Convert.FromHexString(
        "B10B99008196980091BD9800A1E498000000000000000000000000000000000000000000");
    private static readonly byte[] DefaultMaleAppearance = Convert.FromHexString(
        "51929A00211D9A0031449A00416B9A000000000000000000000000000000000001000000");

    internal static byte[] CreateDefaultAppearance(int gender)
        => (gender == 1 ? DefaultMaleAppearance : DefaultFemaleAppearance).ToArray();

    internal static byte[] NormalizeAppearanceForGender(
        ReadOnlySpan<byte> source,
        int gender,
        uint equippedPetItemCode)
    {
        var normalizedGender = gender == 1 ? 1 : 0;
        var defaults = normalizedGender == 1 ? DefaultMaleAppearance : DefaultFemaleAppearance;
        var appearance = new byte[36];
        source[..Math.Min(source.Length, appearance.Length)].CopyTo(appearance);

        // Avatar codes 100xxxxx/101xxxxx are the female/male variants used by
        // the client. Repair only incompatible avatar slots; unrestricted and
        // empty slots retain their submitted values.
        for (var offset = 0; offset < 28; offset += sizeof(uint))
        {
            var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(offset, sizeof(uint)));
            if (itemCode / 1_000_000u != 10u)
                continue;

            var itemGender = (itemCode / 100_000u) % 10u;
            if (itemGender <= 1u && itemGender != (uint)normalizedGender)
                defaults.AsSpan(offset, sizeof(uint)).CopyTo(appearance.AsSpan(offset, sizeof(uint)));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(28, sizeof(uint)), equippedPetItemCode);
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(32, sizeof(uint)), (uint)normalizedGender);
        return appearance;
    }

    private const string CharacterColumns = """
        Id, AccountId, Name, Gender, Face, Appearance, TutorialCompleted,
        PetVariant, EquippedPetItemCode, PetLevel, PetExperience,
        Level, Experience, AttributePoints, Strength, Vitality, Agility, Intelligence, Luck,
        MaxHp, MaxMp, CurrentHp, CurrentMp,
        SpawnMapId, SpawnX, SpawnY, CurrentMapId, CurrentTownPage, PositionX, PositionY,
        CurrentChannelId, IsOnline, OnlineSince, LastOfflineAt, LastSavedAt, CreatedAt,
        Hans, Cash, CardGuideStep, CardSummonCount, CardMysteryKeyCount, CardGoldenKeyCount,
        SkillPoints, SelectedSkill0, SelectedSkill1, SkillSlotExpansionExpires,
        MikeChannelUseCount, MikeGlobalUseCount, RevivalUseCount,
        AvatarInventoryExpansionExpires, PetInventoryExpansionExpires,
        GameInventoryExpansionExpires, InteriorInventoryExpansionExpires,
        QuickSlotExpansionExpires, FreeMagicExpansionExpires,
        AttackModifier, DefenseFlat, InitialAttackMode
        """;

    private readonly string _databasePath;
    private readonly string _connectionString;

    public DatabaseService(string baseDirectory, bool readOnly = false)
    {
        _databasePath = Path.Combine(baseDirectory, "game.db");
        if (!File.Exists(_databasePath))
            throw new FileNotFoundException("未找到 adapter 数据库，请确认配置文件夹内存在 game.db。", _databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            DefaultTimeout = 15,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS Accounts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    PasswordSalt BLOB NOT NULL,
                    PasswordHash BLOB NOT NULL,
                    PasswordPlaintext TEXT NULL,
                    IsBanned INTEGER NOT NULL DEFAULT 0,
                    IsWebAdmin INTEGER NOT NULL DEFAULT 0,
                    IsGm INTEGER NOT NULL DEFAULT 0,
                    GmGrantClaimed INTEGER NOT NULL DEFAULT 0,
                    IsOnline INTEGER NOT NULL DEFAULT 0,
                    InitialGrantClaimed INTEGER NOT NULL DEFAULT 0,
                    ActiveSessionId TEXT NULL,
                    CurrentChannelId INTEGER NULL,
                    CreatedAt TEXT NOT NULL,
                    LastLoginAt TEXT NULL,
                    LastIp TEXT NULL,
                    RegistrationIp TEXT NULL,
                    IsLoginAuthorized INTEGER NOT NULL DEFAULT 0,
                    IsRestrictionExempt INTEGER NOT NULL DEFAULT 0,
                    TrialPlayedSeconds INTEGER NOT NULL DEFAULT 0,
                    TrialLimitMinutes INTEGER NULL,
                    OnlineSince TEXT NULL,
                    LastOfflineAt TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Accounts_Username ON Accounts(Username);
                CREATE TABLE IF NOT EXISTS IpBans (
                    IpAddress TEXT PRIMARY KEY,
                    Mode INTEGER NOT NULL CHECK (Mode IN (1, 2)),
                    SourceAccountId INTEGER NULL REFERENCES Accounts(Id) ON DELETE SET NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS Characters (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AccountId INTEGER NOT NULL UNIQUE REFERENCES Accounts(Id) ON DELETE CASCADE,
                    Name TEXT NOT NULL,
                    Gender INTEGER NOT NULL DEFAULT 0,
                    Face INTEGER NOT NULL DEFAULT 0,
                    Appearance BLOB NOT NULL,
                    TutorialCompleted INTEGER NOT NULL DEFAULT 0,
                    CardGuideStep INTEGER NOT NULL DEFAULT 0,
                    CardSummonCount INTEGER NOT NULL DEFAULT 0 CHECK (CardSummonCount BETWEEN 0 AND 255),
                    CardMysteryKeyCount INTEGER NOT NULL DEFAULT 0 CHECK (CardMysteryKeyCount BETWEEN 0 AND 255),
                    CardGoldenKeyCount INTEGER NOT NULL DEFAULT 0 CHECK (CardGoldenKeyCount BETWEEN 0 AND 255),
                    MikeChannelUseCount INTEGER NOT NULL DEFAULT 0 CHECK (MikeChannelUseCount BETWEEN 0 AND 99),
                    MikeGlobalUseCount INTEGER NOT NULL DEFAULT 0 CHECK (MikeGlobalUseCount BETWEEN 0 AND 99),
                    RevivalUseCount INTEGER NOT NULL DEFAULT 0 CHECK (RevivalUseCount BETWEEN 0 AND 255),
                    AvatarInventoryExpansionExpires INTEGER NOT NULL DEFAULT 0 CHECK (AvatarInventoryExpansionExpires BETWEEN 0 AND 4294967295),
                    PetInventoryExpansionExpires INTEGER NOT NULL DEFAULT 0 CHECK (PetInventoryExpansionExpires BETWEEN 0 AND 4294967295),
                    GameInventoryExpansionExpires INTEGER NOT NULL DEFAULT 0 CHECK (GameInventoryExpansionExpires BETWEEN 0 AND 4294967295),
                    InteriorInventoryExpansionExpires INTEGER NOT NULL DEFAULT 0 CHECK (InteriorInventoryExpansionExpires BETWEEN 0 AND 4294967295),
                    QuickSlotExpansionExpires INTEGER NOT NULL DEFAULT 0 CHECK (QuickSlotExpansionExpires BETWEEN 0 AND 4294967295),
                    FreeMagicExpansionExpires INTEGER NOT NULL DEFAULT 0 CHECK (FreeMagicExpansionExpires BETWEEN 0 AND 4294967295),
                    AttackModifier INTEGER NOT NULL DEFAULT 0 CHECK (AttackModifier BETWEEN 0 AND 1000000),
                    DefenseFlat INTEGER NOT NULL DEFAULT 0 CHECK (DefenseFlat BETWEEN 0 AND 65535),
                    InitialAttackMode INTEGER NOT NULL DEFAULT 0 CHECK (InitialAttackMode BETWEEN 0 AND 3),
                    SkillPoints INTEGER NOT NULL DEFAULT 0 CHECK (SkillPoints BETWEEN 0 AND 65535),
                    SelectedSkill0 INTEGER NOT NULL DEFAULT 0,
                    SelectedSkill1 INTEGER NOT NULL DEFAULT 0,
                    SkillSlotExpansionExpires INTEGER NOT NULL DEFAULT 0 CHECK (SkillSlotExpansionExpires BETWEEN 0 AND 4294967295),
                    CardKeyStateVersion INTEGER NOT NULL DEFAULT 1,
                    PetVariant INTEGER NOT NULL DEFAULT 0,
                    EquippedPetItemCode INTEGER NOT NULL DEFAULT 0,
                    PetLevel INTEGER NOT NULL DEFAULT 1,
                    PetExperience INTEGER NOT NULL DEFAULT 0,
                    Level INTEGER NOT NULL DEFAULT 1,
                    Experience INTEGER NOT NULL DEFAULT 0,
                    AttributePoints INTEGER NOT NULL DEFAULT 0,
                    Strength INTEGER NOT NULL DEFAULT 5,
                    Vitality INTEGER NOT NULL DEFAULT 5,
                    Agility INTEGER NOT NULL DEFAULT 5,
                    Intelligence INTEGER NOT NULL DEFAULT 5,
                    Luck INTEGER NOT NULL DEFAULT 5,
                    MaxHp INTEGER NOT NULL DEFAULT 1500,
                    MaxMp INTEGER NOT NULL DEFAULT 100,
                    CurrentHp INTEGER NOT NULL DEFAULT 1500,
                    CurrentMp INTEGER NOT NULL DEFAULT 100,
                    SpawnMapId INTEGER NOT NULL DEFAULT 1,
                    SpawnX INTEGER NOT NULL DEFAULT 320,
                    SpawnY INTEGER NOT NULL DEFAULT 240,
                    CurrentMapId INTEGER NOT NULL DEFAULT 1,
                    CurrentTownPage INTEGER NOT NULL DEFAULT 0,
                    PositionX INTEGER NOT NULL DEFAULT 320,
                    PositionY INTEGER NOT NULL DEFAULT 240,
                    CurrentChannelId INTEGER NULL,
                    IsOnline INTEGER NOT NULL DEFAULT 0,
                    ActiveSessionId TEXT NULL,
                    OnlineSince TEXT NULL,
                    LastOfflineAt TEXT NULL,
                    LastSavedAt TEXT NULL,
                    ApartmentStarterGranted INTEGER NOT NULL DEFAULT 0,
                    Hans INTEGER NOT NULL DEFAULT 0,
                    Cash INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Characters_AccountId ON Characters(AccountId);
                CREATE TABLE IF NOT EXISTS FriendRecommendations (
                    RequesterAccountId INTEGER PRIMARY KEY REFERENCES Accounts(Id) ON DELETE CASCADE,
                    RecommendedAccountId INTEGER NULL REFERENCES Accounts(Id) ON DELETE SET NULL,
                    RecommendedCharacterName TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_FriendRecommendations_RecommendedAccountId
                    ON FriendRecommendations(RecommendedAccountId);
                CREATE TABLE IF NOT EXISTS CharacterItems (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    ItemCode INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL DEFAULT 0 CHECK (Quantity BETWEEN 0 AND 65535),
                    PetDurability INTEGER NULL,
                    PetCurrentStage INTEGER NOT NULL DEFAULT 0,
                    PetMaximumStage INTEGER NOT NULL DEFAULT 0,
                    PetLevel INTEGER NOT NULL DEFAULT 0,
                    PetExperience INTEGER NOT NULL DEFAULT 0,
                    PetAccessory0 INTEGER NOT NULL DEFAULT 0,
                    PetAccessory1 INTEGER NOT NULL DEFAULT 0,
                    PetAccessory2 INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, ItemCode)
                );
                CREATE TABLE IF NOT EXISTS CharacterCashInboxItems (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    ItemCode INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL DEFAULT 0 CHECK (Quantity BETWEEN 0 AND 65535),
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, ItemCode)
                );
                CREATE TABLE IF NOT EXISTS CharacterQuickSlots (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    Slot INTEGER NOT NULL CHECK (Slot BETWEEN 0 AND 5),
                    ItemCode INTEGER NOT NULL CHECK (ItemCode BETWEEN 1 AND 4294967295),
                    InventoryIndex INTEGER NOT NULL CHECK (InventoryIndex BETWEEN 0 AND 83),
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, Slot),
                    UNIQUE (CharacterId, InventoryIndex)
                );
                CREATE TABLE IF NOT EXISTS CharacterCards (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    CardCode INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL DEFAULT 1 CHECK (Quantity BETWEEN 1 AND 255),
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, CardCode)
                );
                CREATE TABLE IF NOT EXISTS AuctionListings (
                    UniqueNumber INTEGER PRIMARY KEY AUTOINCREMENT CHECK (UniqueNumber BETWEEN 1 AND 4294967295),
                    SellerAccountId INTEGER NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                    SellerCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    SellerCharacterName TEXT NOT NULL COLLATE NOCASE,
                    ItemCode INTEGER NOT NULL CHECK (ItemCode BETWEEN 1 AND 4294967295),
                    OriginalQuantity INTEGER NOT NULL CHECK (OriginalQuantity BETWEEN 1 AND 255),
                    RemainingQuantity INTEGER NOT NULL CHECK (RemainingQuantity BETWEEN 0 AND OriginalQuantity),
                    HansPerItem INTEGER NOT NULL CHECK (HansPerItem BETWEEN 1 AND 4294967295),
                    PendingHans INTEGER NOT NULL DEFAULT 0 CHECK (PendingHans BETWEEN 0 AND 4294967295),
                    Status INTEGER NOT NULL DEFAULT 0 CHECK (Status IN (0, 1)),
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_AuctionListings_Public
                    ON AuctionListings(Status, RemainingQuantity, ItemCode, CreatedAt, UniqueNumber);
                CREATE INDEX IF NOT EXISTS IX_AuctionListings_Seller
                    ON AuctionListings(SellerCharacterId, Status, CreatedAt, UniqueNumber);
                CREATE TABLE IF NOT EXISTS CoupleRelations (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Character1Id INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    Character2Id INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    RingItemCode INTEGER NOT NULL CHECK (RingItemCode BETWEEN 43000001 AND 43099999),
                    EstablishedAt TEXT NOT NULL,
                    EndedAt TEXT NULL,
                    EndItemCode INTEGER NULL,
                    CHECK (Character1Id < Character2Id)
                );
                CREATE INDEX IF NOT EXISTS IX_CoupleRelations_Character1
                    ON CoupleRelations(Character1Id, EndedAt);
                CREATE INDEX IF NOT EXISTS IX_CoupleRelations_Character2
                    ON CoupleRelations(Character2Id, EndedAt);
                CREATE TABLE IF NOT EXISTS CharacterSkills (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    SkillCode INTEGER NOT NULL,
                    Grade INTEGER NOT NULL CHECK (Grade BETWEEN 1 AND 5),
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, SkillCode)
                );
                CREATE TABLE IF NOT EXISTS CharacterTasks (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    QuestId INTEGER NOT NULL CHECK (QuestId BETWEEN 0 AND 4294967295),
                    TaskType INTEGER NOT NULL CHECK (TaskType BETWEEN 1 AND 255),
                    RuntimeState INTEGER NOT NULL DEFAULT 0 CHECK (RuntimeState BETWEEN 0 AND 255),
                    State3 INTEGER NOT NULL DEFAULT 0 CHECK (State3 BETWEEN 0 AND 255),
                    Progress1 INTEGER NOT NULL DEFAULT 0 CHECK (Progress1 BETWEEN 0 AND 65535),
                    Progress2 INTEGER NOT NULL DEFAULT 0 CHECK (Progress2 BETWEEN 0 AND 65535),
                    Progress3 INTEGER NOT NULL DEFAULT 0 CHECK (Progress3 BETWEEN 0 AND 4294967295),
                    SlotType INTEGER NOT NULL DEFAULT 0 CHECK (SlotType BETWEEN 0 AND 2),
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, QuestId)
                );
                CREATE INDEX IF NOT EXISTS IX_CharacterTasks_CharacterId_SlotType
                    ON CharacterTasks(CharacterId, SlotType, CreatedAt, QuestId);
                CREATE TABLE IF NOT EXISTS DungeonProgress (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    Episode INTEGER NOT NULL CHECK (Episode BETWEEN 0 AND 19),
                    Difficulty INTEGER NOT NULL CHECK (Difficulty BETWEEN 0 AND 2),
                    ClearMask INTEGER NOT NULL DEFAULT 0 CHECK (ClearMask BETWEEN 0 AND 255),
                    BestRatings INTEGER NOT NULL DEFAULT 0 CHECK (BestRatings BETWEEN 0 AND 255),
                    BestScore INTEGER NOT NULL DEFAULT 0,
                    BestElapsedMinutes INTEGER NULL,
                    ClearedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, Episode, Difficulty)
                );
                CREATE TABLE IF NOT EXISTS DungeonStagePerformance (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    Episode INTEGER NOT NULL CHECK (Episode BETWEEN 0 AND 19),
                    Difficulty INTEGER NOT NULL CHECK (Difficulty BETWEEN 0 AND 2),
                    ArchiveSlot INTEGER NOT NULL CHECK (ArchiveSlot BETWEEN 0 AND 3),
                    BestScore INTEGER NOT NULL DEFAULT 0 CHECK (BestScore >= 0),
                    BestElapsedMinutes INTEGER NULL CHECK (BestElapsedMinutes IS NULL OR BestElapsedMinutes >= 0),
                    ClearedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, Episode, Difficulty, ArchiveSlot)
                );
                CREATE TABLE IF NOT EXISTS CharacterApartmentItems (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    SlotIndex INTEGER NOT NULL CHECK (SlotIndex BETWEEN 0 AND 83),
                    ItemCode INTEGER NOT NULL,
                    PositionX INTEGER NOT NULL,
                    PositionY INTEGER NOT NULL,
                    Layer INTEGER NOT NULL DEFAULT 0 CHECK (Layer BETWEEN 0 AND 255),
                    Mirror INTEGER NOT NULL CHECK (Mirror BETWEEN 0 AND 255),
                    InteriorType INTEGER NOT NULL CHECK (InteriorType BETWEEN 0 AND 4),
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, SlotIndex)
                );
                CREATE TABLE IF NOT EXISTS CharacterInteriorWishlist (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    ItemCode INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL DEFAULT 1 CHECK (Quantity BETWEEN 1 AND 65535),
                    AddedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, ItemCode)
                );
                CREATE TABLE IF NOT EXISTS CharacterShopWishlist (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    ItemCode INTEGER NOT NULL,
                    AddedAt TEXT NOT NULL,
                    UNIQUE (CharacterId, ItemCode)
                );
                CREATE INDEX IF NOT EXISTS IX_CharacterShopWishlist_CharacterId
                    ON CharacterShopWishlist(CharacterId, AddedAt, Id);
                CREATE TABLE IF NOT EXISTS CharacterMentorAdvertisements (
                    CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id) ON DELETE CASCADE,
                    IsAdvertising INTEGER NOT NULL DEFAULT 0 CHECK (IsAdvertising IN (0, 1)),
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS MentorInteractions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RequestOpcode INTEGER NOT NULL CHECK (RequestOpcode IN (50563, 50565)),
                    RequesterCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    TargetCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    LessonCode INTEGER NOT NULL CHECK (LessonCode BETWEEN 0 AND 4294967295),
                    TargetUid INTEGER NOT NULL CHECK (TargetUid BETWEEN 0 AND 255),
                    Status INTEGER NOT NULL DEFAULT 0 CHECK (Status BETWEEN 0 AND 65535),
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_MentorInteractions_Requester
                    ON MentorInteractions(RequesterCharacterId, CreatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_MentorInteractions_Target
                    ON MentorInteractions(TargetCharacterId, CreatedAt DESC);
                CREATE TABLE IF NOT EXISTS FriendRequests (
                    SerialNo INTEGER PRIMARY KEY AUTOINCREMENT CHECK (SerialNo BETWEEN 1 AND 4294967295),
                    RequesterCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    RequesteeCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    Message TEXT NOT NULL DEFAULT '',
                    AddToNxFriend INTEGER NOT NULL DEFAULT 0 CHECK (AddToNxFriend IN (0, 1)),
                    Status INTEGER NOT NULL DEFAULT 0 CHECK (Status IN (0, 1, 2)),
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    CHECK (RequesterCharacterId <> RequesteeCharacterId)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_FriendRequests_PendingPair
                    ON FriendRequests(RequesterCharacterId, RequesteeCharacterId) WHERE Status = 0;
                CREATE INDEX IF NOT EXISTS IX_FriendRequests_RequesteeStatus
                    ON FriendRequests(RequesteeCharacterId, Status, CreatedAt);
                CREATE TABLE IF NOT EXISTS FriendRelations (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FirstCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    SecondCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    CreatedAt TEXT NOT NULL,
                    CHECK (FirstCharacterId < SecondCharacterId),
                    UNIQUE (FirstCharacterId, SecondCharacterId)
                );
                CREATE INDEX IF NOT EXISTS IX_FriendRelations_SecondCharacterId
                    ON FriendRelations(SecondCharacterId, FirstCharacterId);
                CREATE TABLE IF NOT EXISTS FriendCategories (
                    CategoryCode INTEGER PRIMARY KEY AUTOINCREMENT CHECK (CategoryCode BETWEEN 1 AND 4294967295),
                    OwnerCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    CategoryName TEXT NOT NULL COLLATE NOCASE,
                    Property INTEGER NOT NULL DEFAULT 0 CHECK (Property BETWEEN 0 AND 31),
                    AllowType INTEGER NOT NULL DEFAULT 1 CHECK (AllowType BETWEEN 0 AND 4),
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    UNIQUE (OwnerCharacterId, CategoryName)
                );
                CREATE INDEX IF NOT EXISTS IX_FriendCategories_Owner
                    ON FriendCategories(OwnerCharacterId, CategoryCode);
                CREATE TABLE IF NOT EXISTS FriendCategoryMembers (
                    OwnerCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    FriendCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    CategoryCode INTEGER NOT NULL REFERENCES FriendCategories(CategoryCode) ON DELETE CASCADE,
                    AddedAt TEXT NOT NULL,
                    PRIMARY KEY (OwnerCharacterId, FriendCharacterId, CategoryCode),
                    CHECK (OwnerCharacterId <> FriendCharacterId)
                );
                CREATE INDEX IF NOT EXISTS IX_FriendCategoryMembers_Category
                    ON FriendCategoryMembers(CategoryCode, FriendCharacterId);
                CREATE TABLE IF NOT EXISTS FriendBlocks (
                    OwnerCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    FriendCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    CreatedAt TEXT NOT NULL,
                    PRIMARY KEY (OwnerCharacterId, FriendCharacterId),
                    CHECK (OwnerCharacterId <> FriendCharacterId)
                );
                CREATE TABLE IF NOT EXISTS FriendMemos (
                    OwnerCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    FriendCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    Memo TEXT NOT NULL DEFAULT '',
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (OwnerCharacterId, FriendCharacterId),
                    CHECK (OwnerCharacterId <> FriendCharacterId)
                );
                CREATE TABLE IF NOT EXISTS SchemaMigrations (
                    Name TEXT PRIMARY KEY,
                    AppliedAt TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS AdapterSettings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await MigrateLegacyAdapterSettingsAsync(connection, cancellationToken);
        await MigrateQuickSlotIdentitySchemaAsync(connection, cancellationToken);
        await MigrateDungeonProgressToOfficialLayoutAsync(connection, cancellationToken);
        await MigrateDungeonStagePerformanceAsync(connection, cancellationToken);
        await MigrateLegacyDungeonLowDifficultySelectorAsync(connection, cancellationToken);

        await EnsureColumnAsync(connection, "Accounts", "PasswordPlaintext", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "IsWebAdmin", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "IsGm", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "GmGrantClaimed", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "IsOnline", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "InitialGrantClaimed", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "ActiveSessionId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "CurrentChannelId", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "OnlineSince", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "LastOfflineAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "RegistrationIp", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "IsLoginAuthorized", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "IsRestrictionExempt", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "TrialPlayedSeconds", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Accounts", "TrialLimitMinutes", "INTEGER NULL", cancellationToken);

        await using (var accountIndexes = connection.CreateCommand())
        {
            accountIndexes.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_Accounts_CreatedAt ON Accounts(CreatedAt);
                CREATE INDEX IF NOT EXISTS IX_Accounts_RegistrationIp ON Accounts(RegistrationIp);
                CREATE INDEX IF NOT EXISTS IX_Accounts_RegistrationIp_CreatedAt ON Accounts(RegistrationIp, CreatedAt);
                """;
            await accountIndexes.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureColumnAsync(connection, "Characters", "AttributePoints", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "CardGuideStep", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "CardSummonCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "CardMysteryKeyCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CharacterApartmentItems", "Layer", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "CardGoldenKeyCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "MikeChannelUseCount", "INTEGER NOT NULL DEFAULT 0 CHECK (MikeChannelUseCount BETWEEN 0 AND 99)", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "MikeGlobalUseCount", "INTEGER NOT NULL DEFAULT 0 CHECK (MikeGlobalUseCount BETWEEN 0 AND 99)", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "SkillPoints", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "SelectedSkill0", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "SelectedSkill1", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "SkillSlotExpansionExpires", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "RevivalUseCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "AvatarInventoryExpansionExpires", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "PetInventoryExpansionExpires", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "GameInventoryExpansionExpires", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "InteriorInventoryExpansionExpires", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "QuickSlotExpansionExpires", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "FreeMagicExpansionExpires", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "AttackModifier", "INTEGER NOT NULL DEFAULT 0 CHECK (AttackModifier BETWEEN 0 AND 1000000)", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "DefenseFlat", "INTEGER NOT NULL DEFAULT 0 CHECK (DefenseFlat BETWEEN 0 AND 65535)", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "InitialAttackMode", "INTEGER NOT NULL DEFAULT 0 CHECK (InitialAttackMode BETWEEN 0 AND 3)", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "CardKeyStateVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "PetVariant", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "EquippedPetItemCode", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "PetLevel", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "PetExperience", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "MaxHp", "INTEGER NOT NULL DEFAULT 1500", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "MaxMp", "INTEGER NOT NULL DEFAULT 100", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "CurrentHp", "INTEGER NOT NULL DEFAULT 1500", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "CurrentMp", "INTEGER NOT NULL DEFAULT 100", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "ApartmentStarterGranted", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "SpawnMapId", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "SpawnX", "INTEGER NOT NULL DEFAULT 320", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "SpawnY", "INTEGER NOT NULL DEFAULT 240", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "CurrentMapId", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        // A negative default distinguishes legacy rows from a real saved page 0.
        await EnsureColumnAsync(connection, "Characters", "CurrentTownPage", "INTEGER NOT NULL DEFAULT -1", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "PositionX", "INTEGER NOT NULL DEFAULT 320", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "PositionY", "INTEGER NOT NULL DEFAULT 240", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "CurrentChannelId", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "IsOnline", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "ActiveSessionId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "OnlineSince", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "LastOfflineAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "LastSavedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "Hans", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "Characters", "Cash", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CharacterItems", "PetDurability", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(connection, "CharacterItems", "PetCurrentStage", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CharacterItems", "PetMaximumStage", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CharacterItems", "PetLevel", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CharacterItems", "PetExperience", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CharacterItems", "PetAccessory0", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CharacterItems", "PetAccessory1", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CharacterItems", "PetAccessory2", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        await using (var markExistingInitialGrants = connection.CreateCommand())
        {
            markExistingInitialGrants.CommandText = """
                UPDATE Accounts
                SET InitialGrantClaimed = 1
                WHERE InitialGrantClaimed = 0
                  AND EXISTS (SELECT 1 FROM Characters WHERE Characters.AccountId = Accounts.Id)
                """;
            await markExistingInitialGrants.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var webPortMigration = connection.CreateCommand())
        {
            webPortMigration.CommandText = """
                INSERT OR IGNORE INTO SchemaMigrations(Name, AppliedAt)
                VALUES('web-admin-port-22222-v1', $now)
                RETURNING Name
                """;
            var migrationTime = DateTime.UtcNow.ToString("O");
            webPortMigration.Parameters.AddWithValue("$now", migrationTime);
            if (await webPortMigration.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await using var initializeWebPort = connection.CreateCommand();
                initializeWebPort.CommandText = """
                    INSERT INTO AdapterSettings(Key, Value, UpdatedAt)
                    VALUES('WebAdminPort', '22222', $now)
                    ON CONFLICT(Key) DO UPDATE SET
                        Value = CASE WHEN AdapterSettings.Value = '19090' THEN '22222' ELSE AdapterSettings.Value END,
                        UpdatedAt = CASE WHEN AdapterSettings.Value = '19090' THEN excluded.UpdatedAt ELSE AdapterSettings.UpdatedAt END
                    """;
                initializeWebPort.Parameters.AddWithValue("$now", migrationTime);
                await initializeWebPort.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var petStateMigration = connection.CreateCommand())
        {
            petStateMigration.CommandText = """
                INSERT OR IGNORE INTO SchemaMigrations(Name, AppliedAt)
                VALUES('per-pet-progression-v1', $now)
                RETURNING Name
                """;
            petStateMigration.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            if (await petStateMigration.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await using var migrateTutorialPets = connection.CreateCommand();
                migrateTutorialPets.CommandText = """
                    INSERT INTO CharacterItems(
                        CharacterId, ItemCode, Quantity, PetCurrentStage, PetMaximumStage,
                        PetLevel, PetExperience, UpdatedAt)
                    SELECT Id, 15000000 + PetVariant, 1, 1, 2,
                           CASE WHEN PetLevel = 1 AND PetExperience = 0 THEN 0 ELSE MAX(0, PetLevel) END,
                           MAX(0, PetExperience), $now
                    FROM Characters
                    WHERE PetVariant BETWEEN 1 AND 3
                    ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                        Quantity = MAX(1, CharacterItems.Quantity),
                        PetCurrentStage = CASE WHEN CharacterItems.PetCurrentStage = 0 THEN 1 ELSE CharacterItems.PetCurrentStage END,
                        PetMaximumStage = CASE WHEN CharacterItems.PetMaximumStage = 0 THEN 2 ELSE CharacterItems.PetMaximumStage END,
                        PetLevel = CASE
                            WHEN CharacterItems.PetLevel = 0 AND CharacterItems.PetExperience = 0
                            THEN excluded.PetLevel ELSE CharacterItems.PetLevel END,
                        PetExperience = CASE
                            WHEN CharacterItems.PetLevel = 0 AND CharacterItems.PetExperience = 0
                            THEN excluded.PetExperience ELSE CharacterItems.PetExperience END,
                        UpdatedAt = excluded.UpdatedAt;
                    """;
                migrateTutorialPets.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await migrateTutorialPets.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var apartmentFieldMigration = connection.CreateCommand())
        {
            apartmentFieldMigration.CommandText = """
                INSERT OR IGNORE INTO SchemaMigrations(Name, AppliedAt)
                VALUES('apartment-layer-mirror-v1', $now)
                RETURNING Name
                """;
            apartmentFieldMigration.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            var firstApplication = await apartmentFieldMigration.ExecuteScalarAsync(cancellationToken) is not null;
            if (firstApplication)
            {
                await using var repairApartmentFields = connection.CreateCommand();
                repairApartmentFields.CommandText = """
                    UPDATE CharacterApartmentItems
                    SET Layer = Mirror,
                        Mirror = 0
                    """;
                await repairApartmentFields.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = "CREATE INDEX IF NOT EXISTS IX_Characters_Online ON Characters(IsOnline, CurrentChannelId)";
            await indexes.ExecuteNonQueryAsync(cancellationToken);
        }

        var now = DateTime.UtcNow.ToString("O");
        await using (var cardKeyMigration = connection.CreateCommand())
        {
            // Earlier builds stored activated mystery-key uses in the general
            // magic-key byte. Move that value once so existing accounts keep
            // every purchased use while C3EA can select the mystery-key UI.
            cardKeyMigration.CommandText = """
                UPDATE Characters
                SET CardMysteryKeyCount = MIN(255, CardMysteryKeyCount + CardSummonCount),
                    CardSummonCount = 0,
                    CardKeyStateVersion = 1
                WHERE CardKeyStateVersion = 0
                """;
            await cardKeyMigration.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var migration = connection.CreateCommand())
        {
            migration.CommandText = """
                UPDATE Characters
                SET Level = MIN(99, MAX(1, Level)),
                    Experience = MAX(0, Experience),
                    PetVariant = CASE
                        WHEN PetVariant BETWEEN 1 AND 3 THEN PetVariant
                        WHEN TutorialCompleted = 1 THEN 1
                        ELSE 0
                    END,
                    EquippedPetItemCode = CASE
                        WHEN EquippedPetItemCode BETWEEN 15000001 AND 15999999 THEN EquippedPetItemCode
                        WHEN PetVariant BETWEEN 1 AND 3 THEN 15000000 + PetVariant
                        WHEN TutorialCompleted = 1 THEN 15000001
                        ELSE 0
                    END,
                    PetLevel = MIN(99, MAX(1, PetLevel)),
                    PetExperience = MAX(0, PetExperience),
                    AttributePoints = CASE
                        WHEN LastSavedAt IS NULL THEN MAX(
                            MAX(0, AttributePoints),
                            MAX(0,
                                (MIN(99, MAX(1, Level)) - 1) * 5
                                - MAX(0, Strength - 5)
                                - MAX(0, Vitality - 5)
                                - MAX(0, Agility - 5)
                                - MAX(0, Intelligence - 5)
                                - MAX(0, Luck - 5)))
                        ELSE MAX(0, AttributePoints)
                    END,
                    Strength = MAX(0, Strength),
                    Vitality = MAX(0, Vitality),
                    Agility = MAX(0, Agility),
                    Intelligence = MAX(0, Intelligence),
                    Luck = MAX(0, Luck),
                    MaxHp = MAX(
                        MAX(0, MaxHp),
                        1440 + MAX(0, Vitality) * 12 + (MIN(99, MAX(1, Level)) - 1) * 8),
                    MaxMp = MAX(
                        MAX(0, MaxMp),
                        50 + MAX(0, Intelligence) * 10 + (MIN(99, MAX(1, Level)) - 1) * 5),
                    CurrentHp = CASE
                        WHEN LastSavedAt IS NULL THEN MAX(
                            MAX(0, MaxHp),
                            1440 + MAX(0, Vitality) * 12 + (MIN(99, MAX(1, Level)) - 1) * 8)
                        WHEN MaxHp <= 200 AND CurrentHp >= MaxHp THEN
                            1440 + MAX(0, Vitality) * 12 + (MIN(99, MAX(1, Level)) - 1) * 8
                        ELSE MIN(
                            MAX(0, CurrentHp),
                            MAX(
                                MAX(0, MaxHp),
                                1440 + MAX(0, Vitality) * 12 + (MIN(99, MAX(1, Level)) - 1) * 8))
                    END,
                    CurrentMp = CASE
                        WHEN LastSavedAt IS NULL THEN MAX(
                            MAX(0, MaxMp),
                            50 + MAX(0, Intelligence) * 10 + (MIN(99, MAX(1, Level)) - 1) * 5)
                        ELSE MIN(
                            MAX(0, CurrentMp),
                            MAX(
                                MAX(0, MaxMp),
                                50 + MAX(0, Intelligence) * 10 + (MIN(99, MAX(1, Level)) - 1) * 5))
                    END,
                    CurrentMapId = CASE
                        WHEN LastSavedAt IS NULL AND TutorialCompleted = 0 THEN 0
                        WHEN LastSavedAt IS NULL THEN SpawnMapId
                        ELSE MAX(0, CurrentMapId)
                    END,
                    CurrentTownPage = CASE
                        WHEN CurrentTownPage < 0 AND TutorialCompleted = 1 THEN 33
                        WHEN CurrentTownPage < 0 THEN 0
                        ELSE MIN(255, CurrentTownPage)
                    END,
                    PositionX = CASE
                        WHEN LastSavedAt IS NULL THEN SpawnX
                        WHEN PositionX < 0 OR PositionX > $townPositionMaximum
                          OR PositionY < 0 OR PositionY > $townPositionMaximum
                          OR (PositionX = $townPositionMaximum AND PositionY = $townPositionMaximum)
                        THEN $townFallbackX
                        ELSE PositionX
                    END,
                    PositionY = CASE
                        WHEN LastSavedAt IS NULL THEN SpawnY
                        WHEN PositionX < 0 OR PositionX > $townPositionMaximum
                          OR PositionY < 0 OR PositionY > $townPositionMaximum
                          OR (PositionX = $townPositionMaximum AND PositionY = $townPositionMaximum)
                        THEN $townFallbackY
                        ELSE PositionY
                    END,
                    LastSavedAt = COALESCE(LastSavedAt, CreatedAt, $now);
                UPDATE Characters
                SET Appearance = CASE WHEN Gender = 1 THEN $maleAppearance ELSE $femaleAppearance END,
                    Face = CASE WHEN Gender = 1 THEN 10130001 ELSE 10030001 END
                WHERE length(Appearance) <> 36 OR Appearance = zeroblob(36);
                UPDATE Accounts
                SET TrialPlayedSeconds = TrialPlayedSeconds + CASE
                        WHEN IsOnline = 1 AND OnlineSince IS NOT NULL
                        THEN MAX(0, CAST((julianday($now) - julianday(OnlineSince)) * 86400 + 0.999 AS INTEGER))
                        ELSE 0
                    END,
                    LastOfflineAt = CASE WHEN IsOnline = 1 THEN $now ELSE LastOfflineAt END,
                    IsOnline = 0,
                    ActiveSessionId = NULL,
                    CurrentChannelId = NULL,
                    OnlineSince = NULL;
                UPDATE Characters
                SET LastOfflineAt = CASE WHEN IsOnline = 1 THEN $now ELSE LastOfflineAt END,
                    IsOnline = 0,
                    ActiveSessionId = NULL,
                    CurrentChannelId = NULL,
                    OnlineSince = NULL;
                """;
            migration.Parameters.AddWithValue("$now", now);
            migration.Parameters.AddWithValue("$townPositionMaximum", TownPositionPolicy.MaximumPackedCoordinate);
            migration.Parameters.AddWithValue("$townFallbackX", TownPositionPolicy.FallbackX);
            migration.Parameters.AddWithValue("$townFallbackY", TownPositionPolicy.FallbackY);
            migration.Parameters.Add("$femaleAppearance", SqliteType.Blob).Value = DefaultFemaleAppearance;
            migration.Parameters.Add("$maleAppearance", SqliteType.Blob).Value = DefaultMaleAppearance;
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }

    }

    public async Task<int> GetWebAdminPortAsync(
        int fallback = 22222,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AdapterSettings WHERE Key = 'WebAdminPort'";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
               && port is >= 1 and <= 65535
            ? port
            : fallback;
    }

    public async Task SetWebAdminPortAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "WEB 端口必须在 1-65535 之间。");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AdapterSettings(Key, Value, UpdatedAt)
            VALUES('WebAdminPort', $value, $now)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$value", port.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<InitialGrantSettings> GetInitialGrantSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = new InitialGrantSettings();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Key, Value
            FROM AdapterSettings
            WHERE Key IN ('InitialGrantHans', 'InitialGrantCash', 'InitialGrantSkillPoints')
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var value = reader.GetString(1);
            if (key == "InitialGrantHans"
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hans)
                && hans is >= 0 and <= uint.MaxValue)
                settings.Hans = hans;
            else if (key == "InitialGrantCash"
                     && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cash)
                     && cash is >= 0 and <= uint.MaxValue)
                settings.Cash = cash;
            else if (key == "InitialGrantSkillPoints"
                     && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var skillPoints)
                     && skillPoints is >= 0 and <= ushort.MaxValue)
                settings.SkillPoints = skillPoints;
        }
        return settings;
    }

    public async Task SetInitialGrantSettingsAsync(
        InitialGrantSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Hans is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(settings.Hans), "新号赠送 Hans 必须在 0-4294967295 之间。");
        if (settings.Cash is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(settings.Cash), "新号赠送 Cash 必须在 0-4294967295 之间。");
        if (settings.SkillPoints is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(settings.SkillPoints), "新号赠送 SP 必须在 0-65535 之间。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AdapterSettings(Key, Value, UpdatedAt)
            VALUES
                ('InitialGrantHans', $hans, $now),
                ('InitialGrantCash', $cash, $now),
                ('InitialGrantSkillPoints', $skillPoints, $now)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$hans", settings.Hans.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cash", settings.Cash.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$skillPoints", settings.SkillPoints.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GmGrantSettings> GetGmGrantSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadGmGrantSettingsAsync(connection, null, cancellationToken);
    }

    public async Task SetGmGrantSettingsAsync(
        GmGrantSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeGmGrantSettings(settings);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AdapterSettings(Key, Value, UpdatedAt)
            VALUES
                ('GmGrantHans', $hans, $now),
                ('GmGrantCash', $cash, $now),
                ('GmGrantSkillPoints', $skillPoints, $now),
                ('GmGrantItems', $items, $now)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$hans", normalized.Hans.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cash", normalized.Cash.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$skillPoints", normalized.SkillPoints.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$items", JsonSerializer.Serialize(normalized.Items));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<GmGrantSettings> ReadGmGrantSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var settings = new GmGrantSettings();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Key, Value
            FROM AdapterSettings
            WHERE Key IN ('GmGrantHans', 'GmGrantCash', 'GmGrantSkillPoints', 'GmGrantItems')
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var value = reader.GetString(1);
            if (key == "GmGrantHans"
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hans))
                settings.Hans = Math.Clamp(hans, 0L, (long)uint.MaxValue);
            else if (key == "GmGrantCash"
                     && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cash))
                settings.Cash = Math.Clamp(cash, 0L, (long)uint.MaxValue);
            else if (key == "GmGrantSkillPoints"
                     && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var skillPoints))
                settings.SkillPoints = Math.Clamp(skillPoints, 0, ushort.MaxValue);
            else if (key == "GmGrantItems")
            {
                try
                {
                    var items = JsonSerializer.Deserialize<GmGrantItemSettings[]>(value) ?? [];
                    settings.Items = items
                        .Where(item => item.ItemCode != 0
                                       && item.Quantity != 0
                                       && ShopCatalog.TryGet(item.ItemCode, out _))
                        .GroupBy(item => item.ItemCode)
                        .Select(group => new GmGrantItemSettings
                        {
                            ItemCode = group.Key,
                            Quantity = checked((ushort)Math.Min(
                                ushort.MaxValue,
                                group.Sum(item => (int)item.Quantity)))
                        })
                        .OrderBy(item => item.ItemCode)
                        .ToArray();
                }
                catch (JsonException)
                {
                    settings.Items = [];
                }
            }
        }
        return settings;
    }

    private static GmGrantSettings NormalizeGmGrantSettings(GmGrantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Hans is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(settings.Hans), "GM 赠送 Hans 必须在 0-4294967295 之间。");
        if (settings.Cash is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(settings.Cash), "GM 赠送 NaNa/Cash 必须在 0-4294967295 之间。");
        if (settings.SkillPoints is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(settings.SkillPoints), "GM 赠送 SP 必须在 0-65535 之间。");

        var quantities = new Dictionary<uint, int>();
        foreach (var item in settings.Items ?? [])
        {
            if (item.ItemCode == 0 || !ShopCatalog.TryGet(item.ItemCode, out _))
                throw new ArgumentException($"GM 赠送物品 {item.ItemCode} 不在客户端物品目录中。", nameof(settings));
            if (item.Quantity == 0)
                throw new ArgumentException($"GM 赠送物品 {item.ItemCode} 的数量必须在 1-65535 之间。", nameof(settings));
            var total = quantities.GetValueOrDefault(item.ItemCode) + item.Quantity;
            if (total > ushort.MaxValue)
                throw new ArgumentException($"GM 赠送物品 {item.ItemCode} 的合并数量超过 65535。", nameof(settings));
            quantities[item.ItemCode] = total;
        }

        return new GmGrantSettings
        {
            Hans = settings.Hans,
            Cash = settings.Cash,
            SkillPoints = settings.SkillPoints,
            Items = quantities
                .OrderBy(item => item.Key)
                .Select(item => new GmGrantItemSettings
                {
                    ItemCode = item.Key,
                    Quantity = checked((ushort)item.Value)
                })
                .ToArray()
        };
    }

    private static async Task ApplyGmGrantToCharacterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        GmGrantSettings settings,
        string now,
        CancellationToken cancellationToken)
    {
        await using (var balances = connection.CreateCommand())
        {
            balances.Transaction = transaction;
            balances.CommandText = """
                UPDATE Characters
                SET Hans = MIN($currencyMaximum, Hans + $hans),
                    Cash = MIN($currencyMaximum, Cash + $cash),
                    SkillPoints = MIN($skillMaximum, SkillPoints + $skillPoints),
                    LastSavedAt = $now
                WHERE Id = $characterId
                """;
            balances.Parameters.AddWithValue("$currencyMaximum", (long)uint.MaxValue);
            balances.Parameters.AddWithValue("$skillMaximum", ushort.MaxValue);
            balances.Parameters.AddWithValue("$hans", settings.Hans);
            balances.Parameters.AddWithValue("$cash", settings.Cash);
            balances.Parameters.AddWithValue("$skillPoints", settings.SkillPoints);
            balances.Parameters.AddWithValue("$now", now);
            balances.Parameters.AddWithValue("$characterId", characterId);
            if (await balances.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("GM 赠送目标角色不存在。");
        }

        foreach (var item in settings.Items)
        {
            await using var inventory = connection.CreateCommand();
            inventory.Transaction = transaction;
            inventory.CommandText = """
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = MIN(65535, CharacterItems.Quantity + excluded.Quantity),
                    UpdatedAt = excluded.UpdatedAt
                """;
            inventory.Parameters.AddWithValue("$characterId", characterId);
            inventory.Parameters.AddWithValue("$itemCode", item.ItemCode);
            inventory.Parameters.AddWithValue("$quantity", item.Quantity);
            inventory.Parameters.AddWithValue("$now", now);
            await inventory.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<VillageBotSettings> GetVillageBotSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = new VillageBotSettings();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Key, Value
            FROM AdapterSettings
            WHERE Key IN (
                'VillageBotsEnabled', 'VillageBotCount', 'VillageBotSpeechEnabled',
                'VillageBotConversationEnabled', 'VillageBotEmotionEnabled', 'VillageBotMovementEnabled',
                'VillageBotPauseEnabled', 'VillageBotPortalTravelEnabled', 'VillageBotMinimumSpeechSeconds',
                'VillageBotMaximumSpeechSeconds')
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var value = reader.GetString(1);
            if (key == "VillageBotsEnabled")
                settings.Enabled = value == "1" || bool.TryParse(value, out var enabled) && enabled;
            else if (key == "VillageBotCount"
                     && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                     && count is >= 1 and <= 20)
                settings.Count = count;
            else if (key == "VillageBotSpeechEnabled")
                settings.SpeechEnabled = value == "1" || bool.TryParse(value, out var speechEnabled) && speechEnabled;
            else if (key == "VillageBotConversationEnabled")
                settings.ConversationEnabled = value == "1" || bool.TryParse(value, out var conversationEnabled) && conversationEnabled;
            else if (key == "VillageBotEmotionEnabled")
                settings.EmotionEnabled = value == "1" || bool.TryParse(value, out var emotionEnabled) && emotionEnabled;
            else if (key == "VillageBotMovementEnabled")
                settings.MovementEnabled = value == "1" || bool.TryParse(value, out var movementEnabled) && movementEnabled;
            else if (key == "VillageBotPauseEnabled")
                settings.PauseEnabled = value == "1" || bool.TryParse(value, out var pauseEnabled) && pauseEnabled;
            else if (key == "VillageBotPortalTravelEnabled")
                settings.PortalTravelEnabled = value == "1" || bool.TryParse(value, out var portalTravelEnabled) && portalTravelEnabled;
            else if (key == "VillageBotMinimumSpeechSeconds"
                     && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minimumSpeechSeconds)
                     && minimumSpeechSeconds is >= 15 and <= 3600)
                settings.MinimumSpeechSeconds = minimumSpeechSeconds;
            else if (key == "VillageBotMaximumSpeechSeconds"
                     && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var maximumSpeechSeconds)
                     && maximumSpeechSeconds is >= 15 and <= 3600)
                settings.MaximumSpeechSeconds = maximumSpeechSeconds;
        }
        if (settings.MaximumSpeechSeconds < settings.MinimumSpeechSeconds)
            settings.MaximumSpeechSeconds = settings.MinimumSpeechSeconds;
        return settings;
    }

    public async Task SetVillageBotSettingsAsync(
        VillageBotSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(settings.Count), "村庄机器人数量必须在 1-20 之间。");
        if (settings.MinimumSpeechSeconds is < 15 or > 3600
            || settings.MaximumSpeechSeconds is < 15 or > 3600
            || settings.MaximumSpeechSeconds < settings.MinimumSpeechSeconds)
            throw new ArgumentOutOfRangeException(nameof(settings.MinimumSpeechSeconds), "机器人发言间隔必须在 15-3600 秒之间，且最大间隔不能小于最小间隔。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AdapterSettings(Key, Value, UpdatedAt)
            VALUES
                ('VillageBotsEnabled', $enabled, $now),
                ('VillageBotCount', $count, $now),
                ('VillageBotSpeechEnabled', $speechEnabled, $now),
                ('VillageBotConversationEnabled', $conversationEnabled, $now),
                ('VillageBotEmotionEnabled', $emotionEnabled, $now),
                ('VillageBotMovementEnabled', $movementEnabled, $now),
                ('VillageBotPauseEnabled', $pauseEnabled, $now),
                ('VillageBotPortalTravelEnabled', $portalTravelEnabled, $now),
                ('VillageBotMinimumSpeechSeconds', $minimumSpeechSeconds, $now),
                ('VillageBotMaximumSpeechSeconds', $maximumSpeechSeconds, $now)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$enabled", settings.Enabled ? "1" : "0");
        command.Parameters.AddWithValue("$count", settings.Count.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$speechEnabled", settings.SpeechEnabled ? "1" : "0");
        command.Parameters.AddWithValue("$conversationEnabled", settings.ConversationEnabled ? "1" : "0");
        command.Parameters.AddWithValue("$emotionEnabled", settings.EmotionEnabled ? "1" : "0");
        command.Parameters.AddWithValue("$movementEnabled", settings.MovementEnabled ? "1" : "0");
        command.Parameters.AddWithValue("$pauseEnabled", settings.PauseEnabled ? "1" : "0");
        command.Parameters.AddWithValue("$portalTravelEnabled", settings.PortalTravelEnabled ? "1" : "0");
        command.Parameters.AddWithValue("$minimumSpeechSeconds", settings.MinimumSpeechSeconds.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$maximumSpeechSeconds", settings.MaximumSpeechSeconds.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM Accounts";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<AccountRecord>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AccountRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Id, a.Username, a.PasswordPlaintext, a.IsBanned, a.IsOnline, a.CurrentChannelId,
                   a.CreatedAt, a.LastLoginAt, a.LastIp, a.OnlineSince, a.LastOfflineAt,
                   c.Id, c.Name, c.Level, c.TutorialCompleted, c.Hans, c.Cash, c.SkillPoints,
                   COALESCE(ipb.Mode, 0), a.IsWebAdmin, a.RegistrationIp,
                   a.IsLoginAuthorized, a.TrialPlayedSeconds, a.TrialLimitMinutes,
                   a.IsRestrictionExempt, a.IsGm
            FROM Accounts a
            LEFT JOIN Characters c ON c.AccountId = a.Id
            LEFT JOIN IpBans ipb ON ipb.IpAddress = a.LastIp
            ORDER BY a.Id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AccountRecord
            {
                Id = reader.GetInt64(0),
                Username = reader.GetString(1),
                PlaintextPassword = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsBanned = reader.GetInt64(3) != 0,
                IsOnline = reader.GetInt64(4) != 0,
                CurrentChannelId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                CreatedAt = ParseDate(reader.GetString(6)),
                LastLoginAt = ReadNullableDate(reader, 7),
                LastIp = reader.IsDBNull(8) ? null : reader.GetString(8),
                OnlineSince = ReadNullableDate(reader, 9),
                LastOfflineAt = ReadNullableDate(reader, 10),
                CharacterId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                CharacterName = reader.IsDBNull(12) ? null : reader.GetString(12),
                CharacterLevel = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                TutorialCompleted = reader.IsDBNull(14) ? null : reader.GetInt64(14) != 0,
                Hans = reader.IsDBNull(15) ? 0 : reader.GetInt64(15),
                Cash = reader.IsDBNull(16) ? 0 : reader.GetInt64(16),
                SkillPoints = reader.IsDBNull(17) ? (ushort)0 : checked((ushort)reader.GetInt32(17)),
                IpBanMode = (IpBanMode)reader.GetInt32(18),
                IsWebAdmin = reader.GetInt64(19) != 0,
                RegistrationIp = reader.IsDBNull(20) ? null : reader.GetString(20),
                IsLoginAuthorized = reader.GetInt64(21) != 0,
                TrialPlayedSeconds = Math.Max(0, reader.GetInt64(22)),
                TrialLimitMinutes = reader.IsDBNull(23) ? null : reader.GetInt32(23),
                IsRestrictionExempt = reader.GetInt64(24) != 0,
                IsGm = reader.GetInt64(25) != 0
            });
        }
        return result;
    }

    public async Task<bool> SetLoginAuthorizationAsync(
        long accountId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Accounts SET IsLoginAuthorized = $enabled WHERE Id = $id";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> SetRestrictionWhitelistAsync(
        long accountId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Accounts
            SET IsRestrictionExempt = $enabled
            WHERE Id = $id AND IsOnline = 0
            """;
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> SetAccountTrialLimitAsync(
        long accountId,
        int? minutes,
        CancellationToken cancellationToken = default)
    {
        if (minutes is < 1 or > 525600)
            throw new ArgumentOutOfRangeException(nameof(minutes), "试玩分钟必须为 1-525600，或清除单账号限制。");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Accounts SET TrialLimitMinutes = $minutes WHERE Id = $id";
        command.Parameters.AddWithValue("$minutes", (object?)minutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> ResetAccountTrialPlayedAsync(
        long accountId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Accounts SET TrialPlayedSeconds = 0 WHERE Id = $id AND IsOnline = 0";
        command.Parameters.AddWithValue("$id", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<AccountTrialAccess> GetAccountTrialAccessAsync(
        long accountId,
        AccountLoginPolicy policy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TrialPlayedSeconds, TrialLimitMinutes, IsRestrictionExempt FROM Accounts WHERE Id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new(false, AccountAuthenticationStatus.Failed, 0);

        var playedSeconds = Math.Max(0, reader.GetInt64(0));
        var accountMinutes = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
        if (reader.GetInt64(2) != 0)
            return new(false, AccountAuthenticationStatus.Failed, 0);
        if (policy.PerAccountTrialLimitEnabled)
            return accountMinutes is int perAccountMinutes
                ? new(true, AccountAuthenticationStatus.AccountTrialExpired,
                    Math.Max(0, perAccountMinutes * 60L - playedSeconds))
                : new(false, AccountAuthenticationStatus.AccountTrialNotAuthorized, 0);
        if (policy.GlobalTrialLimitEnabled)
            return new(true, AccountAuthenticationStatus.GlobalTrialExpired,
                Math.Max(0, policy.GlobalTrialMinutes * 60L - playedSeconds));
        return new(false, AccountAuthenticationStatus.Failed, 0);
    }

    public async Task<(long Id, string Username, bool IsBanned)?> GetAccountAccessByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Username, IsBanned FROM Accounts WHERE Username = $username LIMIT 1";
        command.Parameters.AddWithValue("$username", username.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0)
            : null;
    }

    public async Task<(long Id, string Username, bool IsBanned, bool IsWebAdmin)?> GetWebAdminAccessByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Username, IsBanned, IsWebAdmin FROM Accounts WHERE Username = $username LIMIT 1";
        command.Parameters.AddWithValue("$username", username.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0)
            : null;
    }

    public async Task<int> GetWebAdminCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Accounts WHERE IsWebAdmin = 1";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<bool> SetWebAdminAsync(long accountId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return false;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Accounts SET IsWebAdmin = $enabled WHERE Id = $id";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<(bool Success, string Error, bool GrantApplied)> SetGmAsync(
        long accountId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return (false, "账号 ID 无效。", false);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long? characterId;
        bool grantClaimed;
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                SELECT a.IsOnline, a.GmGrantClaimed, c.Id
                FROM Accounts a
                LEFT JOIN Characters c ON c.AccountId = a.Id
                WHERE a.Id = $accountId
                LIMIT 1
                """;
            state.Parameters.AddWithValue("$accountId", accountId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "账号不存在。", false);
            }
            if (reader.GetInt64(0) != 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "在线账号不能切换 GM 状态，请先让账号下线。", false);
            }
            grantClaimed = reader.GetInt64(1) != 0;
            characterId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        }

        var grantApplied = false;
        if (enabled && !grantClaimed && characterId is long targetCharacterId)
        {
            var settings = await ReadGmGrantSettingsAsync(connection, transaction, cancellationToken);
            await ApplyGmGrantToCharacterAsync(
                connection,
                transaction,
                targetCharacterId,
                settings,
                DateTime.UtcNow.ToString("O"),
                cancellationToken);
            grantApplied = true;
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Accounts
                SET IsGm = $enabled,
                    GmGrantClaimed = CASE WHEN $grantApplied = 1 THEN 1 ELSE GmGrantClaimed END
                WHERE Id = $accountId AND IsOnline = 0
                """;
            update.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            update.Parameters.AddWithValue("$grantApplied", grantApplied ? 1 : 0);
            update.Parameters.AddWithValue("$accountId", accountId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "账号不存在或上线状态已经变化。", false);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, grantApplied);
    }

    public async Task<(long Id, string Username, bool IsBanned)?> GetAccountAccessByIdAsync(
        long accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Username, IsBanned FROM Accounts WHERE Id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0)
            : null;
    }

    public async Task<(bool AccountBanned, IpBanMode IpBanMode)> GetLoginAccessAsync(
        string username,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE((SELECT IsBanned FROM Accounts WHERE Username = $username LIMIT 1), 0),
                   COALESCE((SELECT Mode FROM IpBans WHERE IpAddress = $ip LIMIT 1), 0)
            """;
        command.Parameters.AddWithValue("$username", username.Trim());
        command.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return (false, IpBanMode.None);
        return (reader.GetInt64(0) != 0, (IpBanMode)reader.GetInt32(1));
    }

    public async Task<long?> GetAccountIdByUsernameAsync(string username, CancellationToken cancellationToken = default)
        => (await GetAccountAccessByUsernameAsync(username, cancellationToken))?.Id;

    public async Task<(bool Success, string Error)> CreateAccountAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        username = username.Trim();
        if (!IsValidGameUsername(username))
            return (false, "账号必须为 6-12 位数字。");
        if (!IsValidGamePassword(password))
            return (false, "密码必须为 6-16 个有效字符。");

        var (salt, hash) = PasswordHasher.Hash(password);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Accounts(Username, PasswordSalt, PasswordHash, PasswordPlaintext, CreatedAt) VALUES ($username, $salt, $hash, $password, $created)";
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.Add("$salt", SqliteType.Blob).Value = salt;
        command.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
        command.Parameters.AddWithValue("$password", password);
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return (true, string.Empty);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return (false, "账号已经存在。");
        }
    }

    public async Task<bool> ValidateLoginAsync(
        string username,
        string password,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, PasswordSalt, PasswordHash, IsBanned FROM Accounts WHERE Username = $username";
        command.Parameters.AddWithValue("$username", username.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt64(3) != 0)
            return false;
        var id = reader.GetInt64(0);
        var salt = (byte[])reader[1];
        var hash = (byte[])reader[2];
        if (!PasswordHasher.Verify(password, salt, hash))
            return false;
        await reader.CloseAsync();
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE Accounts SET PasswordPlaintext = $password WHERE Id = $id";
            update.Parameters.AddWithValue("$password", password);
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await RecordLoginAsync(connection, id, ip, cancellationToken);
        return true;
    }

    public async Task<(bool Success, string Error)> VerifyArchiveAuthorizationAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        username = username.Trim();
        if (!IsValidGameUsername(username) || !IsValidGamePassword(password))
            return (false, "账号或密码错误。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PasswordSalt, PasswordHash FROM Accounts WHERE Username = $username LIMIT 1";
        command.Parameters.AddWithValue("$username", username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return (false, "账号或密码错误。");

        var salt = (byte[])reader[0];
        var hash = (byte[])reader[1];
        return PasswordHasher.Verify(password, salt, hash)
            ? (true, string.Empty)
            : (false, "账号或密码错误。");
    }

    public async Task<AccountAuthenticationResult> AuthenticateOrRegisterAsync(
        string username,
        string password,
        string? ip,
        AccountLoginPolicy policy,
        CancellationToken cancellationToken = default)
    {
        username = username.Trim();
        if (!IsValidGameUsername(username))
            return new(AccountAuthenticationStatus.Failed, 0, username, "账号必须为 6-12 位数字。");
        if (!IsValidGamePassword(password))
            return new(AccountAuthenticationStatus.Failed, 0, username, "密码必须为 6-16 个有效字符。");

        ArgumentNullException.ThrowIfNull(policy);
        ip = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();

        // Registration limits and the first insert share one immediate transaction,
        // so concurrent first-login requests cannot pass the same quota together.
        var (newSalt, newHash) = PasswordHasher.Hash(password);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ipBanMode = IpBanMode.None;
        if (!string.IsNullOrWhiteSpace(ip))
        {
            await using var ipBan = connection.CreateCommand();
            ipBan.Transaction = transaction;
            ipBan.CommandText = "SELECT Mode FROM IpBans WHERE IpAddress = $ip LIMIT 1";
            ipBan.Parameters.AddWithValue("$ip", ip);
            var mode = await ipBan.ExecuteScalarAsync(cancellationToken);
            if (mode is not null)
                ipBanMode = (IpBanMode)Convert.ToInt32(mode, CultureInfo.InvariantCulture);
        }

        var accountExists = false;
        var existingAccountBanned = false;
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT IsBanned FROM Accounts WHERE Username = $username LIMIT 1";
            existing.Parameters.AddWithValue("$username", username);
            var bannedValue = await existing.ExecuteScalarAsync(cancellationToken);
            accountExists = bannedValue is not null;
            existingAccountBanned = accountExists
                && Convert.ToInt32(bannedValue, CultureInfo.InvariantCulture) != 0;
        }
        if (existingAccountBanned)
        {
            await transaction.RollbackAsync(cancellationToken);
            var status = ipBanMode == IpBanMode.Double
                ? AccountAuthenticationStatus.DoubleBanned
                : AccountAuthenticationStatus.Banned;
            return new(status, 0, username, status == AccountAuthenticationStatus.DoubleBanned
                ? "账号和 IP 已被封禁。"
                : "账号已被封禁。");
        }
        if (ipBanMode == IpBanMode.IpOnly)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AccountAuthenticationStatus.IpBanned, 0, username, "当前 IP 已被封禁。");
        }

        var registered = false;
        if (!accountExists)
        {
            var localDayStartUtc = DateTime.Today.ToUniversalTime();
            var localDayEndUtc = localDayStartUtc.AddDays(1);
            if (policy.DailyRegistrationLimitEnabled
                && await CountRegistrationsAsync(connection, transaction, null, localDayStartUtc, localDayEndUtc, cancellationToken)
                   >= policy.DailyRegistrationLimit)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(AccountAuthenticationStatus.DailyRegistrationLimitReached, 0, username, "今日全服注册账号数量已达上限。");
            }
            if (policy.IpRegistrationLimitEnabled && ip is not null
                && await CountRegistrationsAsync(connection, transaction, ip, null, null, cancellationToken)
                   >= policy.IpRegistrationLimit)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(AccountAuthenticationStatus.IpRegistrationLimitReached, 0, username, "当前 IP 注册账号数量已达上限。");
            }
            if (policy.IpDailyRegistrationLimitEnabled && ip is not null
                && await CountRegistrationsAsync(connection, transaction, ip, localDayStartUtc, localDayEndUtc, cancellationToken)
                   >= policy.IpDailyRegistrationLimit)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(AccountAuthenticationStatus.IpDailyRegistrationLimitReached, 0, username, "当前 IP 今日注册账号数量已达上限。");
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Accounts(
                    Username, PasswordSalt, PasswordHash, PasswordPlaintext,
                    RegistrationIp, CreatedAt)
                VALUES ($username, $salt, $hash, $password, $registrationIp, $created)
                """;
            insert.Parameters.AddWithValue("$username", username);
            insert.Parameters.Add("$salt", SqliteType.Blob).Value = newSalt;
            insert.Parameters.Add("$hash", SqliteType.Blob).Value = newHash;
            insert.Parameters.AddWithValue("$password", password);
            insert.Parameters.AddWithValue("$registrationIp", (object?)ip ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            registered = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        long accountId;
        byte[] storedSalt;
        byte[] storedHash;
        bool banned;
        bool isOnline;
        bool isAuthorized;
        long trialPlayedSeconds;
        int? trialLimitMinutes;
        bool isRestrictionExempt;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT Id, Username, PasswordSalt, PasswordHash, IsBanned, IsOnline,
                       IsLoginAuthorized, TrialPlayedSeconds, TrialLimitMinutes,
                       IsRestrictionExempt
                FROM Accounts WHERE Username = $username LIMIT 1
                """;
            query.Parameters.AddWithValue("$username", username);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(AccountAuthenticationStatus.Failed, 0, username, "账号数据读取失败。");
            }
            accountId = reader.GetInt64(0);
            username = reader.GetString(1);
            storedSalt = (byte[])reader[2];
            storedHash = (byte[])reader[3];
            banned = reader.GetInt64(4) != 0;
            isOnline = reader.GetInt64(5) != 0;
            isAuthorized = reader.GetInt64(6) != 0;
            trialPlayedSeconds = Math.Max(0, reader.GetInt64(7));
            trialLimitMinutes = reader.IsDBNull(8) ? null : reader.GetInt32(8);
            isRestrictionExempt = reader.GetInt64(9) != 0;
        }

        if (banned)
        {
            await transaction.RollbackAsync(cancellationToken);
            var status = ipBanMode == IpBanMode.Double
                ? AccountAuthenticationStatus.DoubleBanned
                : AccountAuthenticationStatus.Banned;
            return new(status, 0, username, status == AccountAuthenticationStatus.DoubleBanned
                ? "账号和 IP 已被封禁。"
                : "账号已被封禁。");
        }
        if (!PasswordHasher.Verify(password, storedSalt, storedHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ipBanMode == IpBanMode.Double
                ? new(AccountAuthenticationStatus.DoubleBanned, 0, username, "账号和 IP 已被封禁。")
                : new(AccountAuthenticationStatus.Failed, 0, username, "账号或密码错误。");
        }

        async Task<AccountAuthenticationResult> RejectAfterPasswordAsync(
            AccountAuthenticationStatus status,
            string message)
        {
            if (registered)
                await transaction.CommitAsync(cancellationToken);
            else
                await transaction.RollbackAsync(cancellationToken);
            return new(status, accountId, username, message);
        }

        if (isOnline)
            return await RejectAfterPasswordAsync(AccountAuthenticationStatus.AlreadyOnline, "当前账号已登录，请勿重复登录。");
        if (!isRestrictionExempt && policy.LoginAuthorizationRequired && !isAuthorized)
            return await RejectAfterPasswordAsync(AccountAuthenticationStatus.AuthorizationRequired, "当前账号需要授权后才可以登录。");
        if (!isRestrictionExempt && policy.PerAccountTrialLimitEnabled)
        {
            if (trialLimitMinutes is null)
                return await RejectAfterPasswordAsync(
                    AccountAuthenticationStatus.AccountTrialNotAuthorized,
                    "当前账号未获得试玩授权。");
            if (trialPlayedSeconds >= trialLimitMinutes.Value * 60L)
                return await RejectAfterPasswordAsync(AccountAuthenticationStatus.AccountTrialExpired, "当前账号的试玩时间已到。");
        }
        else if (!isRestrictionExempt && policy.GlobalTrialLimitEnabled
            && trialPlayedSeconds >= policy.GlobalTrialMinutes * 60L)
            return await RejectAfterPasswordAsync(AccountAuthenticationStatus.GlobalTrialExpired, "全服账号试玩时间已到。");

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE Accounts SET PasswordPlaintext = $password, LastLoginAt = $last, LastIp = $ip, IsBanned = $banned WHERE Id = $id";
            update.Parameters.AddWithValue("$password", password);
            update.Parameters.AddWithValue("$last", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
            update.Parameters.AddWithValue("$banned", ipBanMode == IpBanMode.Double ? 1 : 0);
            update.Parameters.AddWithValue("$id", accountId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (ipBanMode == IpBanMode.Double)
            return new(AccountAuthenticationStatus.DoubleBanned, accountId, username, "账号和 IP 已被封禁。");
        return new(
            registered ? AccountAuthenticationStatus.Registered : AccountAuthenticationStatus.Authenticated,
            accountId,
            username,
            string.Empty);
    }

    private static async Task<long> CountRegistrationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? registrationIp,
        DateTime? startUtc,
        DateTime? endUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var filters = new List<string>();
        if (registrationIp is not null)
        {
            filters.Add("RegistrationIp = $registrationIp");
            command.Parameters.AddWithValue("$registrationIp", registrationIp);
        }
        if (startUtc is DateTime start && endUtc is DateTime end)
        {
            filters.Add("CreatedAt >= $start AND CreatedAt < $end");
            command.Parameters.AddWithValue("$start", start.ToString("O"));
            command.Parameters.AddWithValue("$end", end.ToString("O"));
        }
        command.CommandText = "SELECT COUNT(*) FROM Accounts"
                              + (filters.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", filters)}");
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> GetAccountLastIpAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT LastIp FROM Accounts WHERE Id = $accountId LIMIT 1";
        command.Parameters.AddWithValue("$accountId", accountId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static bool TryNormalizeIpv4(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        normalized = address.ToString();
        return true;
    }

    private static bool IsValidGameUsername(string username)
        => username.Length is >= 6 and <= 12
            && username.All(character => character is >= '0' and <= '9');

    private static bool IsValidGamePassword(string password)
        => password.Length is >= 6 and <= 16
            && password.IndexOf('\0') < 0
            && password.All(character => !char.IsControl(character));

    public async Task RecordNativeLoginAsync(long accountId, string? ip, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await RecordLoginAsync(connection, accountId, ip, cancellationToken);
    }

    public async Task SetBannedAsync(long id, bool banned, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Accounts SET IsBanned = $banned WHERE Id = $id";
        command.Parameters.AddWithValue("$banned", banned ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(int AccountsUnbanned, int IpBansRemoved)> ClearAllBansAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        int accountsUnbanned;
        await using (var accounts = connection.CreateCommand())
        {
            accounts.Transaction = transaction;
            accounts.CommandText = "UPDATE Accounts SET IsBanned = 0 WHERE IsBanned <> 0";
            accountsUnbanned = await accounts.ExecuteNonQueryAsync(cancellationToken);
        }

        int ipBansRemoved;
        await using (var ipBans = connection.CreateCommand())
        {
            ipBans.Transaction = transaction;
            ipBans.CommandText = "DELETE FROM IpBans";
            ipBansRemoved = await ipBans.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (accountsUnbanned, ipBansRemoved);
    }

    public async Task<(bool Success, string Error, string IpAddress)> SetIpBanForAccountAsync(
        long accountId,
        bool banned,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ip = await GetAccountLastIpAsync(connection, transaction, accountId, cancellationToken);
        if (!TryNormalizeIpv4(ip, out var normalizedIp))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "该账号没有有效的最后登录 IPv4 地址。", string.Empty);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (banned)
        {
            command.CommandText = """
                INSERT INTO IpBans(IpAddress, Mode, SourceAccountId, CreatedAt)
                VALUES ($ip, $mode, $accountId, $createdAt)
                ON CONFLICT(IpAddress) DO UPDATE SET
                    Mode = excluded.Mode,
                    SourceAccountId = excluded.SourceAccountId,
                    CreatedAt = excluded.CreatedAt
                """;
            command.Parameters.AddWithValue("$mode", (int)IpBanMode.IpOnly);
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        }
        else
            command.CommandText = "DELETE FROM IpBans WHERE IpAddress = $ip";
        command.Parameters.AddWithValue("$ip", normalizedIp);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, normalizedIp);
    }

    public async Task<(bool Success, string Error, string IpAddress)> SetDoubleBanAsync(
        long accountId,
        bool banned,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ip = await GetAccountLastIpAsync(connection, transaction, accountId, cancellationToken);
        if (!TryNormalizeIpv4(ip, out var normalizedIp))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "该账号没有有效的最后登录 IPv4 地址。", string.Empty);
        }

        await using (var account = connection.CreateCommand())
        {
            account.Transaction = transaction;
            account.CommandText = "UPDATE Accounts SET IsBanned = $banned WHERE Id = $accountId";
            account.Parameters.AddWithValue("$banned", banned ? 1 : 0);
            account.Parameters.AddWithValue("$accountId", accountId);
            if (await account.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "账号不存在。", string.Empty);
            }
        }

        await using (var ipBan = connection.CreateCommand())
        {
            ipBan.Transaction = transaction;
            if (banned)
            {
                ipBan.CommandText = """
                    INSERT INTO IpBans(IpAddress, Mode, SourceAccountId, CreatedAt)
                    VALUES ($ip, $mode, $accountId, $createdAt)
                    ON CONFLICT(IpAddress) DO UPDATE SET
                        Mode = excluded.Mode,
                        SourceAccountId = excluded.SourceAccountId,
                        CreatedAt = excluded.CreatedAt
                    """;
                ipBan.Parameters.AddWithValue("$mode", (int)IpBanMode.Double);
                ipBan.Parameters.AddWithValue("$accountId", accountId);
                ipBan.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
            }
            else
                ipBan.CommandText = "DELETE FROM IpBans WHERE IpAddress = $ip";
            ipBan.Parameters.AddWithValue("$ip", normalizedIp);
            await ipBan.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, normalizedIp);
    }

    public async Task<(bool Success, string Error)> ResetPasswordAsync(
        long accountId,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return (false, "账号无效。");
        if (!IsValidGamePassword(password))
            return (false, "密码必须为 6-16 个有效字符。");

        var (salt, hash) = PasswordHasher.Hash(password);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Accounts SET PasswordSalt = $salt, PasswordHash = $hash, PasswordPlaintext = $password WHERE Id = $id";
        command.Parameters.Add("$salt", SqliteType.Blob).Value = salt;
        command.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
        command.Parameters.AddWithValue("$password", password);
        command.Parameters.AddWithValue("$id", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? (true, string.Empty)
            : (false, "账号不存在。");
    }

    public async Task<(bool Success, string Error, long Hans, long Cash)> AdjustShopBalancesAsync(
        long accountId,
        long hansAmount,
        long cashAmount,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return (false, "账号 ID 无效。", 0, 0);
        if (hansAmount < 0 || cashAmount < 0)
            return (false, "充值数值不能为负数。", 0, 0);
        if (hansAmount == 0 && cashAmount == 0)
            return (false, "Hans 和 Cash 至少填写一项正数。", 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET Hans = Hans + $hans,
                    Cash = Cash + $cash
                WHERE AccountId = $accountId
                  AND Hans >= 0 AND Hans <= $maxHans
                  AND Cash >= 0 AND Cash <= $maxCash
                RETURNING Hans, Cash
                """;
            update.Parameters.AddWithValue("$hans", hansAmount);
            update.Parameters.AddWithValue("$cash", cashAmount);
            update.Parameters.AddWithValue("$accountId", accountId);
            update.Parameters.AddWithValue("$maxHans", long.MaxValue - hansAmount);
            update.Parameters.AddWithValue("$maxCash", long.MaxValue - cashAmount);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var hans = reader.GetInt64(0);
                var cash = reader.GetInt64(1);
                await reader.CloseAsync();
                await transaction.CommitAsync(cancellationToken);
                return (true, string.Empty, hans, cash);
            }
        }

        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM Characters WHERE AccountId = $accountId";
        exists.Parameters.AddWithValue("$accountId", accountId);
        var hasCharacter = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
        await transaction.RollbackAsync(cancellationToken);
        return hasCharacter
            ? (false, "充值后余额将超过 64 位整数上限。", 0, 0)
            : (false, "该账号尚未创建角色，无法写入商城余额。", 0, 0);
    }

    public async Task<(bool Success, string Error, long Hans, long Cash, ushort SkillPoints)>
        AdjustShopBalancesAndSkillPointsAsync(
            long accountId,
            long hansAmount,
            long cashAmount,
            int skillPointAmount,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return (false, "账号 ID 无效。", 0, 0, 0);
        if (hansAmount < 0 || cashAmount < 0 || skillPointAmount < 0)
            return (false, "充值数值不能为负数。", 0, 0, 0);
        if (hansAmount == 0 && cashAmount == 0 && skillPointAmount == 0)
            return (false, "Hans、Cash 和 SP 至少填写一项正数。", 0, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET Hans = Hans + $hans,
                    Cash = Cash + $cash,
                    SkillPoints = SkillPoints + $skillPoints,
                    LastSavedAt = $now
                WHERE AccountId = $accountId
                  AND Hans BETWEEN 0 AND $maxHans
                  AND Cash BETWEEN 0 AND $maxCash
                  AND SkillPoints BETWEEN 0 AND $maxSkillPoints
                RETURNING Hans, Cash, SkillPoints
                """;
            update.Parameters.AddWithValue("$hans", hansAmount);
            update.Parameters.AddWithValue("$cash", cashAmount);
            update.Parameters.AddWithValue("$skillPoints", skillPointAmount);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$accountId", accountId);
            update.Parameters.AddWithValue("$maxHans", long.MaxValue - hansAmount);
            update.Parameters.AddWithValue("$maxCash", long.MaxValue - cashAmount);
            update.Parameters.AddWithValue("$maxSkillPoints", ushort.MaxValue - skillPointAmount);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var hans = reader.GetInt64(0);
                var cash = reader.GetInt64(1);
                var skillPoints = checked((ushort)reader.GetInt32(2));
                await reader.CloseAsync();
                await transaction.CommitAsync(cancellationToken);
                return (true, string.Empty, hans, cash, skillPoints);
            }
        }

        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM Characters WHERE AccountId = $accountId";
        exists.Parameters.AddWithValue("$accountId", accountId);
        var hasCharacter = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        await transaction.RollbackAsync(cancellationToken);
        return hasCharacter
            ? (false, "充值后余额或 SP 将超过上限。", 0L, 0L, (ushort)0)
            : (false, "该账号尚未创建角色，无法充值。", 0L, 0L, (ushort)0);
    }

    public async Task<(bool Success, string Error, long Balance)> RechargeHansAsync(
        long accountId,
        long amount,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
            return (false, "Hans 充值数量必须是正整数。", 0);

        var result = await AdjustShopBalancesAndSkillPointsAsync(accountId, amount, 0, 0, cancellationToken);
        return (result.Success, result.Error, result.Hans);
    }

    public async Task<(bool Success, string Error, long Balance)> RechargeCashAsync(
        long accountId,
        long amount,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
            return (false, "Cash 充值数量必须是正整数。", 0);

        var result = await AdjustShopBalancesAndSkillPointsAsync(accountId, 0, amount, 0, cancellationToken);
        return (result.Success, result.Error, result.Cash);
    }

    public async Task<(bool Success, string Error, long Balance)> RechargeSkillPointsAsync(
        long accountId,
        int amount,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0 || amount > ushort.MaxValue)
            return (false, $"SP 充值数量必须是 1-{ushort.MaxValue} 的整数。", 0);

        var result = await AdjustShopBalancesAndSkillPointsAsync(accountId, 0, 0, amount, cancellationToken);
        return (result.Success, result.Error, result.SkillPoints);
    }

    public async Task<IReadOnlyList<CharacterCardRecord>> GetCharacterCardsAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return [];

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CardCode, Quantity FROM CharacterCards WHERE CharacterId = $characterId ORDER BY CardCode";
        command.Parameters.AddWithValue("$characterId", characterId);
        return await ReadCharacterCardsAsync(command, cancellationToken);
    }

    public async Task<(uint ResultCode, long FirstHans, long SecondHans)> CompletePlayerTradeAsync(
        long firstAccountId,
        long firstCharacterId,
        string firstSessionId,
        ulong firstOfferedHans,
        IReadOnlyDictionary<uint, int> firstOfferedCards,
        long secondAccountId,
        long secondCharacterId,
        string secondSessionId,
        ulong secondOfferedHans,
        IReadOnlyDictionary<uint, int> secondOfferedCards,
        CancellationToken cancellationToken = default)
    {
        const uint success = 10;
        const uint peerUnavailable = 20;
        const uint cardOverflow = 30;
        const uint hansOverflow = 40;
        const uint failed = 70;

        if (firstAccountId <= 0
            || secondAccountId <= 0
            || firstCharacterId <= 0
            || secondCharacterId <= 0
            || firstCharacterId == secondCharacterId
            || string.IsNullOrEmpty(firstSessionId)
            || string.IsNullOrEmpty(secondSessionId)
            || firstOfferedHans > uint.MaxValue
            || secondOfferedHans > uint.MaxValue
            || firstOfferedCards.Any(item => item.Value is <= 0 or > byte.MaxValue
                                              || !CardCatalog.TryGet(item.Key, out _))
            || secondOfferedCards.Any(item => item.Value is <= 0 or > byte.MaxValue
                                               || !CardCatalog.TryGet(item.Key, out _)))
            return (failed, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        async Task<long?> ReadHansAsync(long accountId, long characterId, string sessionId)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT character.Hans
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            command.Parameters.AddWithValue("$characterId", characterId);
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null ? null : Convert.ToInt64(value);
        }

        async Task<Dictionary<uint, int>> ReadCardsAsync(long characterId)
        {
            var result = new Dictionary<uint, int>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT CardCode, Quantity FROM CharacterCards WHERE CharacterId = $characterId";
            command.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result[checked((uint)reader.GetInt64(0))] = reader.GetInt32(1);
            return result;
        }

        var firstHans = await ReadHansAsync(firstAccountId, firstCharacterId, firstSessionId);
        var secondHans = await ReadHansAsync(secondAccountId, secondCharacterId, secondSessionId);
        if (firstHans is null || secondHans is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (peerUnavailable, firstHans ?? 0, secondHans ?? 0);
        }

        var firstCards = await ReadCardsAsync(firstCharacterId);
        var secondCards = await ReadCardsAsync(secondCharacterId);
        if (firstOfferedCards.Any(item => firstCards.GetValueOrDefault(item.Key) < item.Value)
            || secondOfferedCards.Any(item => secondCards.GetValueOrDefault(item.Key) < item.Value))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (failed, firstHans.Value, secondHans.Value);
        }

        var cardCodes = firstOfferedCards.Keys.Concat(secondOfferedCards.Keys).Distinct().ToArray();
        var firstFinalCards = cardCodes.ToDictionary(
            code => code,
            code => firstCards.GetValueOrDefault(code)
                    - firstOfferedCards.GetValueOrDefault(code)
                    + secondOfferedCards.GetValueOrDefault(code));
        var secondFinalCards = cardCodes.ToDictionary(
            code => code,
            code => secondCards.GetValueOrDefault(code)
                    - secondOfferedCards.GetValueOrDefault(code)
                    + firstOfferedCards.GetValueOrDefault(code));
        if (firstFinalCards.Values.Any(value => value is < 0 or > byte.MaxValue)
            || secondFinalCards.Values.Any(value => value is < 0 or > byte.MaxValue))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (cardOverflow, firstHans.Value, secondHans.Value);
        }

        var firstNewHans = firstHans.Value - checked((long)firstOfferedHans) + checked((long)secondOfferedHans);
        var secondNewHans = secondHans.Value - checked((long)secondOfferedHans) + checked((long)firstOfferedHans);
        if (firstHans.Value < checked((long)firstOfferedHans)
            || secondHans.Value < checked((long)secondOfferedHans)
            || firstNewHans is < 0 or > uint.MaxValue
            || secondNewHans is < 0 or > uint.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (hansOverflow, firstHans.Value, secondHans.Value);
        }

        var now = DateTime.UtcNow.ToString("O");
        async Task<bool> ApplyCardsAsync(
            long characterId,
            IReadOnlyDictionary<uint, int> oldCards,
            IReadOnlyDictionary<uint, int> finalCards)
        {
            foreach (var item in finalCards)
            {
                var oldQuantity = oldCards.GetValueOrDefault(item.Key);
                if (oldQuantity == item.Value)
                    continue;

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                if (oldQuantity == 0)
                {
                    command.CommandText = """
                        INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
                        VALUES($characterId, $cardCode, $quantity, $now)
                        ON CONFLICT(CharacterId, CardCode) DO NOTHING
                        """;
                }
                else if (item.Value == 0)
                {
                    command.CommandText = """
                        DELETE FROM CharacterCards
                        WHERE CharacterId = $characterId AND CardCode = $cardCode AND Quantity = $oldQuantity
                        """;
                }
                else
                {
                    command.CommandText = """
                        UPDATE CharacterCards
                        SET Quantity = $quantity, UpdatedAt = $now
                        WHERE CharacterId = $characterId AND CardCode = $cardCode AND Quantity = $oldQuantity
                        """;
                }
                command.Parameters.AddWithValue("$characterId", characterId);
                command.Parameters.AddWithValue("$cardCode", item.Key);
                command.Parameters.AddWithValue("$quantity", item.Value);
                command.Parameters.AddWithValue("$oldQuantity", oldQuantity);
                command.Parameters.AddWithValue("$now", now);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    return false;
            }
            return true;
        }

        if (!await ApplyCardsAsync(firstCharacterId, firstCards, firstFinalCards)
            || !await ApplyCardsAsync(secondCharacterId, secondCards, secondFinalCards))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (failed, firstHans.Value, secondHans.Value);
        }

        async Task<bool> ApplyHansAsync(
            long accountId,
            long characterId,
            string sessionId,
            long oldHans,
            long newHans)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Characters
                SET Hans = $newHans, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND Hans = $oldHans
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            command.Parameters.AddWithValue("$newHans", newHans);
            command.Parameters.AddWithValue("$oldHans", oldHans);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$characterId", characterId);
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        if (!await ApplyHansAsync(firstAccountId, firstCharacterId, firstSessionId, firstHans.Value, firstNewHans)
            || !await ApplyHansAsync(secondAccountId, secondCharacterId, secondSessionId, secondHans.Value, secondNewHans))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (peerUnavailable, firstHans.Value, secondHans.Value);
        }

        await transaction.CommitAsync(cancellationToken);
        return (success, firstNewHans, secondNewHans);
    }

    public async Task<IReadOnlyList<CharacterCardRecord>> GetCharacterCardsByAccountAsync(
        long accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return [];

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT card.CardCode, card.Quantity
            FROM CharacterCards AS card
            INNER JOIN Characters AS character ON character.Id = card.CharacterId
            WHERE character.AccountId = $accountId
            ORDER BY card.CardCode
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        return await ReadCharacterCardsAsync(command, cancellationToken);
    }

    public async Task<(bool Success, string Error, byte Quantity)> GrantCardToAccountAsync(
        long accountId,
        uint cardCode,
        byte quantity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return (false, "账号 ID 无效。", 0);
        if (quantity == 0)
            return (false, "发放数量必须大于 0。", 0);
        if (!CardCatalog.TryGet(cardCode, out _))
            return (false, "卡片编号不在客户端卡片目录中。", 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
            SELECT Id, $cardCode, $quantity, $now
            FROM Characters
            WHERE AccountId = $accountId
            ON CONFLICT(CharacterId, CardCode) DO UPDATE SET
                Quantity = MIN(255, CharacterCards.Quantity + excluded.Quantity),
                UpdatedAt = excluded.UpdatedAt
            RETURNING Quantity
            """;
        command.Parameters.AddWithValue("$cardCode", cardCode);
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$accountId", accountId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null
            ? (false, "该账号尚未创建角色，无法发放卡片。", (byte)0)
            : (true, string.Empty, checked((byte)Convert.ToInt32(result)));
    }

    public async Task<(bool Success, byte Quantity)> GrantDungeonCardAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint cardCode,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrWhiteSpace(sessionId)
            || !CardCatalog.TryGet(cardCode, out _))
            return (false, (byte)0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
            SELECT Id, $cardCode, 1, $now
            FROM Characters
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND ActiveSessionId = $sessionId
            ON CONFLICT(CharacterId, CardCode) DO UPDATE SET
                Quantity = MIN(255, CharacterCards.Quantity + 1),
                UpdatedAt = excluded.UpdatedAt
            RETURNING Quantity
            """;
        command.Parameters.AddWithValue("$cardCode", cardCode);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null
            ? (false, (byte)0)
            : (true, checked((byte)Convert.ToInt32(result)));
    }

    public async Task<(bool Success, string Error, byte Quantity, long Hans, long Cash)> SellCharacterCardAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint cardCode,
        byte chapter,
        byte page,
        byte index,
        byte quantity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || quantity == 0
            || !CardCatalog.TryGet(cardCode, out var card)
            || card.Category != chapter
            || card.Page != page
            || card.Slot + 1 != index
            || card.SellHansPrice == 0)
            return (false, "Card sale fields do not match the client catalog.", 0, 0, 0);

        var proceeds = checked((long)card.SellHansPrice * quantity);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        long currentQuantity;
        long currentHans;
        long currentCash;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT card.Quantity, character.Hans, character.Cash
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                INNER JOIN CharacterCards AS card
                    ON card.CharacterId = character.Id AND card.CardCode = $cardCode
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$cardCode", cardCode);
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "Card sale session or inventory row is unavailable.", 0, 0, 0);
            }
            currentQuantity = reader.GetInt64(0);
            currentHans = reader.GetInt64(1);
            currentCash = reader.GetInt64(2);
        }

        if (currentQuantity < quantity || currentHans > long.MaxValue - proceeds)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "Card quantity is insufficient or Hans would overflow.", checked((byte)currentQuantity), currentHans, currentCash);
        }

        var newQuantity = checked((byte)(currentQuantity - quantity));
        await using (var updateCard = connection.CreateCommand())
        {
            updateCard.Transaction = transaction;
            updateCard.CommandText = newQuantity == 0
                ? "DELETE FROM CharacterCards WHERE CharacterId = $characterId AND CardCode = $cardCode AND Quantity = $currentQuantity"
                : "UPDATE CharacterCards SET Quantity = $newQuantity, UpdatedAt = $now WHERE CharacterId = $characterId AND CardCode = $cardCode AND Quantity = $currentQuantity";
            updateCard.Parameters.AddWithValue("$newQuantity", newQuantity);
            updateCard.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            updateCard.Parameters.AddWithValue("$characterId", characterId);
            updateCard.Parameters.AddWithValue("$cardCode", cardCode);
            updateCard.Parameters.AddWithValue("$currentQuantity", currentQuantity);
            if (await updateCard.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "Card quantity changed before the sale completed.", checked((byte)currentQuantity), currentHans, currentCash);
            }
        }

        var newHans = checked(currentHans + proceeds);
        await using (var updateWallet = connection.CreateCommand())
        {
            updateWallet.Transaction = transaction;
            updateWallet.CommandText = """
                UPDATE Characters
                SET Hans = $newHans, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND Hans = $currentHans
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            updateWallet.Parameters.AddWithValue("$newHans", newHans);
            updateWallet.Parameters.AddWithValue("$currentHans", currentHans);
            updateWallet.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            updateWallet.Parameters.AddWithValue("$characterId", characterId);
            updateWallet.Parameters.AddWithValue("$accountId", accountId);
            updateWallet.Parameters.AddWithValue("$sessionId", sessionId);
            if (await updateWallet.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "Wallet changed before the card sale completed.", checked((byte)currentQuantity), currentHans, currentCash);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, newQuantity, newHans, currentCash);
    }

    public async Task<AuctionListQueryResult> QueryAuctionListingsAsync(
        long accountId,
        long characterId,
        string sessionId,
        bool personal,
        byte ddakgiType,
        byte sortType,
        ushort page,
        byte pageSize,
        ushort ddakgiNumber,
        string? sellerCharacterName,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId) || page == 0 || pageSize == 0)
            return new AuctionListQueryResult(14, 0, []);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var session = connection.CreateCommand())
        {
            session.CommandText = """
                SELECT 1
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            session.Parameters.AddWithValue("$characterId", characterId);
            session.Parameters.AddWithValue("$accountId", accountId);
            session.Parameters.AddWithValue("$sessionId", sessionId);
            if (await session.ExecuteScalarAsync(cancellationToken) is null)
                return new AuctionListQueryResult(14, 0, []);
        }

        var filters = new List<string> { "listing.Status = 0" };
        if (personal)
            filters.Add("listing.SellerCharacterId = $characterId");
        else
            filters.Add("listing.RemainingQuantity > 0");
        if (ddakgiType != 0)
            filters.Add("(listing.ItemCode / 1000000) = $ddakgiType");
        if (ddakgiNumber != 0)
            filters.Add("(listing.ItemCode % 1000000) = $ddakgiNumber");
        if (!string.IsNullOrEmpty(sellerCharacterName))
            filters.Add("listing.SellerCharacterName = $sellerCharacterName COLLATE NOCASE");
        var where = string.Join(" AND ", filters);

        long totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM AuctionListings AS listing WHERE {where}";
            AddAuctionQueryParameters(count, characterId, ddakgiType, ddakgiNumber, sellerCharacterName);
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        var totalPages = totalCount == 0 ? 0u : checked((uint)((totalCount + pageSize - 1) / pageSize));
        if (totalPages != 0 && page > totalPages)
            return new AuctionListQueryResult(11, totalPages, []);

        var orderBy = sortType switch
        {
            10 => "listing.HansPerItem DESC, listing.UniqueNumber ASC",
            11 => "listing.HansPerItem ASC, listing.UniqueNumber ASC",
            20 => "(listing.OriginalQuantity - listing.RemainingQuantity) DESC, listing.UniqueNumber ASC",
            21 => "(listing.OriginalQuantity - listing.RemainingQuantity) ASC, listing.UniqueNumber ASC",
            _ => "listing.UniqueNumber DESC"
        };
        var listings = new List<AuctionListingRecord>(pageSize);
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = $"""
                SELECT listing.UniqueNumber,
                       listing.SellerCharacterId,
                       listing.SellerCharacterName,
                       listing.ItemCode,
                       listing.OriginalQuantity,
                       listing.RemainingQuantity,
                       listing.HansPerItem
                FROM AuctionListings AS listing
                WHERE {where}
                ORDER BY {orderBy}
                LIMIT $limit OFFSET $offset
                """;
            AddAuctionQueryParameters(query, characterId, ddakgiType, ddakgiNumber, sellerCharacterName);
            query.Parameters.AddWithValue("$limit", pageSize);
            query.Parameters.AddWithValue("$offset", checked((long)(page - 1) * pageSize));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                listings.Add(new AuctionListingRecord
                {
                    UniqueNumber = checked((uint)reader.GetInt64(0)),
                    SellerCharacterId = reader.GetInt64(1),
                    SellerCharacterName = reader.GetString(2),
                    ItemCode = checked((uint)reader.GetInt64(3)),
                    OriginalQuantity = checked((byte)reader.GetInt64(4)),
                    RemainingQuantity = checked((byte)reader.GetInt64(5)),
                    HansPerItem = checked((uint)reader.GetInt64(6))
                });
            }
        }

        return new AuctionListQueryResult(1, totalPages, listings);
    }

    public async Task<AuctionRegistrationResult> RegisterAuctionListingAsync(
        long accountId,
        long characterId,
        string sessionId,
        byte requestType,
        uint itemCode,
        ushort itemCount,
        uint hansPerItem,
        CancellationToken cancellationToken = default)
    {
        if (requestType != 0)
            return new AuctionRegistrationResult(15, 0, 0);
        if (!CardCatalog.TryGet(itemCode, out _))
            return new AuctionRegistrationResult(12, 0, 0);
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || itemCount is 0 or > byte.MaxValue
            || hansPerItem == 0
            || (ulong)itemCount * hansPerItem > uint.MaxValue)
            return new AuctionRegistrationResult(16, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long currentQuantity;
        long activeListings;
        string sellerName;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT card.Quantity,
                       character.Name,
                       (SELECT COUNT(*) FROM AuctionListings AS listing
                        WHERE listing.SellerCharacterId = character.Id AND listing.Status = 0)
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                INNER JOIN CharacterCards AS card
                    ON card.CharacterId = character.Id AND card.CardCode = $itemCode
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$itemCode", itemCode);
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionRegistrationResult(16, 0, 0);
            }
            currentQuantity = reader.GetInt64(0);
            sellerName = reader.GetString(1);
            activeListings = reader.GetInt64(2);
        }

        if (activeListings >= 3)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionRegistrationResult(11, 0, checked((byte)currentQuantity));
        }
        if (currentQuantity < itemCount)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionRegistrationResult(16, 0, checked((byte)currentQuantity));
        }

        var now = DateTime.UtcNow.ToString("O");
        var remainingInventory = currentQuantity - itemCount;
        await using (var card = connection.CreateCommand())
        {
            card.Transaction = transaction;
            card.CommandText = remainingInventory == 0
                ? "DELETE FROM CharacterCards WHERE CharacterId = $characterId AND CardCode = $itemCode AND Quantity = $currentQuantity"
                : "UPDATE CharacterCards SET Quantity = $remaining, UpdatedAt = $now WHERE CharacterId = $characterId AND CardCode = $itemCode AND Quantity = $currentQuantity";
            card.Parameters.AddWithValue("$remaining", remainingInventory);
            card.Parameters.AddWithValue("$now", now);
            card.Parameters.AddWithValue("$characterId", characterId);
            card.Parameters.AddWithValue("$itemCode", itemCode);
            card.Parameters.AddWithValue("$currentQuantity", currentQuantity);
            if (await card.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionRegistrationResult(16, 0, checked((byte)currentQuantity));
            }
        }

        long uniqueNumber;
        await using (var listing = connection.CreateCommand())
        {
            listing.Transaction = transaction;
            listing.CommandText = """
                INSERT INTO AuctionListings(
                    SellerAccountId, SellerCharacterId, SellerCharacterName, ItemCode,
                    OriginalQuantity, RemainingQuantity, HansPerItem, PendingHans, Status,
                    CreatedAt, UpdatedAt)
                VALUES(
                    $accountId, $characterId, $sellerName, $itemCode,
                    $quantity, $quantity, $hansPerItem, 0, 0,
                    $now, $now)
                RETURNING UniqueNumber
                """;
            listing.Parameters.AddWithValue("$accountId", accountId);
            listing.Parameters.AddWithValue("$characterId", characterId);
            listing.Parameters.AddWithValue("$sellerName", sellerName);
            listing.Parameters.AddWithValue("$itemCode", itemCode);
            listing.Parameters.AddWithValue("$quantity", itemCount);
            listing.Parameters.AddWithValue("$hansPerItem", hansPerItem);
            listing.Parameters.AddWithValue("$now", now);
            uniqueNumber = Convert.ToInt64(await listing.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        if (uniqueNumber is <= 0 or > uint.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionRegistrationResult(16, 0, checked((byte)currentQuantity));
        }
        await transaction.CommitAsync(cancellationToken);
        return new AuctionRegistrationResult(1, checked((uint)uniqueNumber), checked((byte)remainingInventory));
    }

    public async Task<AuctionPurchaseResult> PurchaseAuctionListingAsync(
        long accountId,
        long characterId,
        string sessionId,
        ulong uniqueNumber,
        uint totalHans,
        uint itemCount,
        uint itemCode,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || uniqueNumber is 0 or > uint.MaxValue
            || itemCount is 0 or > byte.MaxValue)
            return new AuctionPurchaseResult(13, 0, 0);
        if (!CardCatalog.TryGet(itemCode, out _))
            return new AuctionPurchaseResult(12, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long buyerHans;
        long buyerCardQuantity;
        await using (var buyer = connection.CreateCommand())
        {
            buyer.Transaction = transaction;
            buyer.CommandText = """
                SELECT character.Hans, COALESCE(card.Quantity, 0)
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                LEFT JOIN CharacterCards AS card
                    ON card.CharacterId = character.Id AND card.CardCode = $itemCode
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            buyer.Parameters.AddWithValue("$itemCode", itemCode);
            buyer.Parameters.AddWithValue("$characterId", characterId);
            buyer.Parameters.AddWithValue("$accountId", accountId);
            buyer.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await buyer.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionPurchaseResult(13, 0, 0);
            }
            buyerHans = reader.GetInt64(0);
            buyerCardQuantity = reader.GetInt64(1);
        }

        long sellerCharacterId;
        long listingItemCode;
        long remainingQuantity;
        long hansPerItem;
        long pendingHans;
        await using (var listing = connection.CreateCommand())
        {
            listing.Transaction = transaction;
            listing.CommandText = """
                SELECT SellerCharacterId, ItemCode, RemainingQuantity, HansPerItem, PendingHans
                FROM AuctionListings
                WHERE UniqueNumber = $uniqueNumber AND Status = 0
                """;
            listing.Parameters.AddWithValue("$uniqueNumber", checked((long)uniqueNumber));
            await using var reader = await listing.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionPurchaseResult(13, checked((byte)buyerCardQuantity), buyerHans);
            }
            sellerCharacterId = reader.GetInt64(0);
            listingItemCode = reader.GetInt64(1);
            remainingQuantity = reader.GetInt64(2);
            hansPerItem = reader.GetInt64(3);
            pendingHans = reader.GetInt64(4);
        }

        if (sellerCharacterId == characterId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionPurchaseResult(16, checked((byte)buyerCardQuantity), buyerHans);
        }
        if (listingItemCode != itemCode)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionPurchaseResult(12, checked((byte)buyerCardQuantity), buyerHans);
        }
        if (remainingQuantity < itemCount || (ulong)hansPerItem * itemCount != totalHans)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionPurchaseResult(14, checked((byte)buyerCardQuantity), buyerHans);
        }
        if (buyerHans < totalHans)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionPurchaseResult(10, checked((byte)buyerCardQuantity), buyerHans);
        }
        if (buyerCardQuantity + itemCount > byte.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionPurchaseResult(11, checked((byte)buyerCardQuantity), buyerHans);
        }
        if (pendingHans + totalHans > uint.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionPurchaseResult(15, checked((byte)buyerCardQuantity), buyerHans);
        }

        var newRemaining = remainingQuantity - itemCount;
        var newPendingHans = pendingHans + totalHans;
        var newBuyerHans = buyerHans - totalHans;
        var newCardQuantity = buyerCardQuantity + itemCount;
        var now = DateTime.UtcNow.ToString("O");
        await using (var updateListing = connection.CreateCommand())
        {
            updateListing.Transaction = transaction;
            updateListing.CommandText = """
                UPDATE AuctionListings
                SET RemainingQuantity = $newRemaining,
                    PendingHans = $newPendingHans,
                    UpdatedAt = $now
                WHERE UniqueNumber = $uniqueNumber
                  AND Status = 0
                  AND RemainingQuantity = $oldRemaining
                  AND PendingHans = $oldPendingHans
                """;
            updateListing.Parameters.AddWithValue("$newRemaining", newRemaining);
            updateListing.Parameters.AddWithValue("$newPendingHans", newPendingHans);
            updateListing.Parameters.AddWithValue("$now", now);
            updateListing.Parameters.AddWithValue("$uniqueNumber", checked((long)uniqueNumber));
            updateListing.Parameters.AddWithValue("$oldRemaining", remainingQuantity);
            updateListing.Parameters.AddWithValue("$oldPendingHans", pendingHans);
            if (await updateListing.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionPurchaseResult(14, checked((byte)buyerCardQuantity), buyerHans);
            }
        }
        await using (var updateBuyer = connection.CreateCommand())
        {
            updateBuyer.Transaction = transaction;
            updateBuyer.CommandText = """
                UPDATE Characters
                SET Hans = $newHans, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND Hans = $oldHans
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            updateBuyer.Parameters.AddWithValue("$newHans", newBuyerHans);
            updateBuyer.Parameters.AddWithValue("$now", now);
            updateBuyer.Parameters.AddWithValue("$characterId", characterId);
            updateBuyer.Parameters.AddWithValue("$accountId", accountId);
            updateBuyer.Parameters.AddWithValue("$oldHans", buyerHans);
            updateBuyer.Parameters.AddWithValue("$sessionId", sessionId);
            if (await updateBuyer.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionPurchaseResult(14, checked((byte)buyerCardQuantity), buyerHans);
            }
        }
        await using (var updateCard = connection.CreateCommand())
        {
            updateCard.Transaction = transaction;
            updateCard.CommandText = """
                INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, CardCode) DO UPDATE SET
                    Quantity = $quantity,
                    UpdatedAt = $now
                """;
            updateCard.Parameters.AddWithValue("$characterId", characterId);
            updateCard.Parameters.AddWithValue("$itemCode", itemCode);
            updateCard.Parameters.AddWithValue("$quantity", newCardQuantity);
            updateCard.Parameters.AddWithValue("$now", now);
            await updateCard.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AuctionPurchaseResult(1, checked((byte)newCardQuantity), newBuyerHans);
    }

    public async Task<AuctionRetrievalResult> RetrieveAuctionListingAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint requestType,
        ulong uniqueNumber,
        CancellationToken cancellationToken = default)
    {
        if (requestType == 2)
            return new AuctionRetrievalResult(14, 0, 0, 0);
        if (requestType != 1)
            return new AuctionRetrievalResult(12, 0, 0, 0);
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || uniqueNumber is 0 or > uint.MaxValue)
            return new AuctionRetrievalResult(13, 0, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long hans;
        long itemCode;
        long currentCardQuantity;
        long remainingQuantity;
        long pendingHans;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT character.Hans,
                       listing.ItemCode,
                       COALESCE(card.Quantity, 0),
                       listing.RemainingQuantity,
                       listing.PendingHans
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                INNER JOIN AuctionListings AS listing
                    ON listing.SellerCharacterId = character.Id
                   AND listing.UniqueNumber = $uniqueNumber
                   AND listing.Status = 0
                LEFT JOIN CharacterCards AS card
                    ON card.CharacterId = character.Id AND card.CardCode = listing.ItemCode
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$uniqueNumber", checked((long)uniqueNumber));
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionRetrievalResult(13, 0, 0, 0);
            }
            hans = reader.GetInt64(0);
            itemCode = reader.GetInt64(1);
            currentCardQuantity = reader.GetInt64(2);
            remainingQuantity = reader.GetInt64(3);
            pendingHans = reader.GetInt64(4);
        }

        if (!CardCatalog.TryGet(checked((uint)itemCode), out _))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionRetrievalResult(12, 0, checked((byte)currentCardQuantity), hans);
        }
        if (currentCardQuantity + remainingQuantity > byte.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionRetrievalResult(11, 0, checked((byte)currentCardQuantity), hans);
        }
        if (hans + pendingHans > uint.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionRetrievalResult(10, 0, checked((byte)currentCardQuantity), hans);
        }

        var newHans = hans + pendingHans;
        var newCardQuantity = currentCardQuantity + remainingQuantity;
        var now = DateTime.UtcNow.ToString("O");
        await using (var close = connection.CreateCommand())
        {
            close.Transaction = transaction;
            close.CommandText = """
                UPDATE AuctionListings
                SET RemainingQuantity = 0,
                    PendingHans = 0,
                    Status = 1,
                    UpdatedAt = $now
                WHERE UniqueNumber = $uniqueNumber
                  AND SellerCharacterId = $characterId
                  AND Status = 0
                  AND RemainingQuantity = $remaining
                  AND PendingHans = $pendingHans
                """;
            close.Parameters.AddWithValue("$now", now);
            close.Parameters.AddWithValue("$uniqueNumber", checked((long)uniqueNumber));
            close.Parameters.AddWithValue("$characterId", characterId);
            close.Parameters.AddWithValue("$remaining", remainingQuantity);
            close.Parameters.AddWithValue("$pendingHans", pendingHans);
            if (await close.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionRetrievalResult(13, 0, checked((byte)currentCardQuantity), hans);
            }
        }
        if (remainingQuantity > 0)
        {
            await using var card = connection.CreateCommand();
            card.Transaction = transaction;
            card.CommandText = """
                INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, CardCode) DO UPDATE SET
                    Quantity = $quantity,
                    UpdatedAt = $now
                """;
            card.Parameters.AddWithValue("$characterId", characterId);
            card.Parameters.AddWithValue("$itemCode", itemCode);
            card.Parameters.AddWithValue("$quantity", newCardQuantity);
            card.Parameters.AddWithValue("$now", now);
            await card.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var wallet = connection.CreateCommand())
        {
            wallet.Transaction = transaction;
            wallet.CommandText = """
                UPDATE Characters
                SET Hans = $newHans, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND Hans = $oldHans
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            wallet.Parameters.AddWithValue("$newHans", newHans);
            wallet.Parameters.AddWithValue("$now", now);
            wallet.Parameters.AddWithValue("$characterId", characterId);
            wallet.Parameters.AddWithValue("$accountId", accountId);
            wallet.Parameters.AddWithValue("$oldHans", hans);
            wallet.Parameters.AddWithValue("$sessionId", sessionId);
            if (await wallet.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionRetrievalResult(13, 0, checked((byte)currentCardQuantity), hans);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new AuctionRetrievalResult(
            1,
            checked((byte)remainingQuantity),
            checked((byte)newCardQuantity),
            newHans);
    }

    public async Task<IReadOnlyList<AuctionListingAdminRecord>> GetAuctionListingsForAdminAsync(
        CancellationToken cancellationToken = default)
    {
        var records = new List<AuctionListingAdminRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT listing.UniqueNumber,
                   listing.SellerAccountId,
                   account.Username,
                   listing.SellerCharacterId,
                   listing.SellerCharacterName,
                   listing.ItemCode,
                   listing.OriginalQuantity,
                   listing.RemainingQuantity,
                   listing.HansPerItem,
                   listing.PendingHans,
                   listing.Status,
                   listing.CreatedAt,
                   listing.UpdatedAt
            FROM AuctionListings AS listing
            INNER JOIN Accounts AS account ON account.Id = listing.SellerAccountId
            ORDER BY listing.Status ASC, listing.UpdatedAt DESC, listing.UniqueNumber DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemCode = checked((uint)reader.GetInt64(5));
            records.Add(new AuctionListingAdminRecord
            {
                UniqueNumber = checked((uint)reader.GetInt64(0)),
                SellerAccountId = reader.GetInt64(1),
                SellerUsername = reader.GetString(2),
                SellerCharacterId = reader.GetInt64(3),
                SellerCharacterName = reader.GetString(4),
                ItemCode = itemCode,
                ItemName = CardCatalog.TryGet(itemCode, out var card) ? card.Name : "未知卡片",
                OriginalQuantity = checked((byte)reader.GetInt64(6)),
                RemainingQuantity = checked((byte)reader.GetInt64(7)),
                HansPerItem = checked((uint)reader.GetInt64(8)),
                PendingHans = checked((uint)reader.GetInt64(9)),
                Status = checked((byte)reader.GetInt64(10)),
                CreatedAtUtc = ParseDate(reader.GetString(11)),
                UpdatedAtUtc = ParseDate(reader.GetString(12))
            });
        }
        return records;
    }

    public async Task<AuctionAdminOperationResult> CancelAuctionListingFromAdminAsync(
        uint uniqueNumber,
        CancellationToken cancellationToken = default)
    {
        if (uniqueNumber == 0)
            return new AuctionAdminOperationResult(false, "挂单编号无效。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long sellerAccountId;
        long sellerCharacterId;
        long itemCode;
        long remainingQuantity;
        long pendingHans;
        long currentCardQuantity;
        long currentHans;
        bool characterOnline;
        bool accountOnline;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT listing.SellerAccountId,
                       listing.SellerCharacterId,
                       listing.ItemCode,
                       listing.RemainingQuantity,
                       listing.PendingHans,
                       COALESCE(card.Quantity, 0),
                       character.Hans,
                       character.IsOnline,
                       account.IsOnline
                FROM AuctionListings AS listing
                INNER JOIN Characters AS character ON character.Id = listing.SellerCharacterId
                INNER JOIN Accounts AS account ON account.Id = listing.SellerAccountId
                LEFT JOIN CharacterCards AS card
                    ON card.CharacterId = listing.SellerCharacterId
                   AND card.CardCode = listing.ItemCode
                WHERE listing.UniqueNumber = $uniqueNumber
                  AND listing.Status = 0
                  AND character.AccountId = listing.SellerAccountId
                """;
            current.Parameters.AddWithValue("$uniqueNumber", uniqueNumber);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionAdminOperationResult(false, "挂单不存在或已经结束。");
            }
            sellerAccountId = reader.GetInt64(0);
            sellerCharacterId = reader.GetInt64(1);
            itemCode = reader.GetInt64(2);
            remainingQuantity = reader.GetInt64(3);
            pendingHans = reader.GetInt64(4);
            currentCardQuantity = reader.GetInt64(5);
            currentHans = reader.GetInt64(6);
            characterOnline = reader.GetBoolean(7);
            accountOnline = reader.GetBoolean(8);
        }

        if (characterOnline || accountOnline)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionAdminOperationResult(false, "卖家当前在线，请先让该账号退出游戏，避免客户端卡片册与数据库状态不同步。");
        }
        if (!CardCatalog.TryGet(checked((uint)itemCode), out _))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionAdminOperationResult(false, "挂单引用的卡片不在官方卡片目录中，未进行返还。");
        }
        if (currentCardQuantity + remainingQuantity > byte.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionAdminOperationResult(false, "返还后卡片数量将超过客户端上限 255，请先处理该角色已有卡片。");
        }
        if (currentHans + pendingHans > uint.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionAdminOperationResult(false, "结算后 Hans 将超过客户端上限，未执行撤单。");
        }

        var newCardQuantity = currentCardQuantity + remainingQuantity;
        var newHans = currentHans + pendingHans;
        var now = DateTime.UtcNow.ToString("O");
        await using (var close = connection.CreateCommand())
        {
            close.Transaction = transaction;
            close.CommandText = """
                UPDATE AuctionListings
                SET RemainingQuantity = 0,
                    PendingHans = 0,
                    Status = 1,
                    UpdatedAt = $now
                WHERE UniqueNumber = $uniqueNumber
                  AND Status = 0
                  AND RemainingQuantity = $remainingQuantity
                  AND PendingHans = $pendingHans
                """;
            close.Parameters.AddWithValue("$now", now);
            close.Parameters.AddWithValue("$uniqueNumber", uniqueNumber);
            close.Parameters.AddWithValue("$remainingQuantity", remainingQuantity);
            close.Parameters.AddWithValue("$pendingHans", pendingHans);
            if (await close.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionAdminOperationResult(false, "挂单状态已经变化，请刷新后重试。");
            }
        }

        if (remainingQuantity > 0)
        {
            await using var card = connection.CreateCommand();
            card.Transaction = transaction;
            card.CommandText = """
                INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, CardCode) DO UPDATE SET
                    Quantity = $quantity,
                    UpdatedAt = $now
                """;
            card.Parameters.AddWithValue("$characterId", sellerCharacterId);
            card.Parameters.AddWithValue("$itemCode", itemCode);
            card.Parameters.AddWithValue("$quantity", newCardQuantity);
            card.Parameters.AddWithValue("$now", now);
            await card.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var wallet = connection.CreateCommand())
        {
            wallet.Transaction = transaction;
            wallet.CommandText = """
                UPDATE Characters
                SET Hans = $newHans, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND Hans = $currentHans
                  AND IsOnline = 0
                """;
            wallet.Parameters.AddWithValue("$newHans", newHans);
            wallet.Parameters.AddWithValue("$now", now);
            wallet.Parameters.AddWithValue("$characterId", sellerCharacterId);
            wallet.Parameters.AddWithValue("$accountId", sellerAccountId);
            wallet.Parameters.AddWithValue("$currentHans", currentHans);
            if (await wallet.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionAdminOperationResult(false, "卖家状态或余额已经变化，撤单已回滚。");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new AuctionAdminOperationResult(true, string.Empty);
    }

    public async Task<int> ClearCompletedAuctionListingsFromAdminAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AuctionListings WHERE Status = 1";
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddAuctionQueryParameters(
        SqliteCommand command,
        long characterId,
        byte ddakgiType,
        ushort ddakgiNumber,
        string? sellerCharacterName)
    {
        command.Parameters.AddWithValue("$characterId", characterId);
        if (ddakgiType != 0)
            command.Parameters.AddWithValue("$ddakgiType", ddakgiType);
        if (ddakgiNumber != 0)
            command.Parameters.AddWithValue("$ddakgiNumber", ddakgiNumber);
        if (!string.IsNullOrEmpty(sellerCharacterName))
            command.Parameters.AddWithValue("$sellerCharacterName", sellerCharacterName);
    }

    public async Task<(bool Success, string Error, uint ItemCode, ushort RewardQuantity, string KeyKind, byte KeyUseCount)> SynthesizeCardItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint recipeToken,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || !CardSynthesisCatalog.TryGet(recipeToken, out var recipe))
            return (false, "Card synthesis parameters are invalid.", 0, 0, "none", 0);

        var rewardDomain = recipe.Output / 1_000_000u;
        ShopCatalogItem? petReward = null;
        if (rewardDomain == 15u
            && (!ShopCatalog.TryGet(15, recipe.Output, out petReward)
                || petReward.Section != InventorySection.Pet))
            return (false, "The synthesized PET is not present in the embedded client catalog.", recipe.Output, 0, "none", 0);
        if (rewardDomain is not (14u or 15u or 17u or 19u or 21u or 41u))
            return (false, "The synthesis reward domain is unsupported.", recipe.Output, 0, "none", 0);

        var materials = recipe.Inputs
            .GroupBy(code => code)
            .ToDictionary(group => group.Key, group => group.Count());
        if (materials.Count is < 1 or > 3)
            return (false, "The synthesis recipe has no valid materials.", recipe.Output, 0, "none", 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        long normalKeys;
        long goldenKeys;
        long mysteryKeys;
        uint freeMagicExpiration;
        long skillPoints;
        int petVariant;
        await using (var authorization = connection.CreateCommand())
        {
            authorization.Transaction = transaction;
            authorization.CommandText = """
                SELECT character.CardSummonCount,
                       character.CardGoldenKeyCount,
                       character.CardMysteryKeyCount,
                       character.FreeMagicExpansionExpires,
                       character.SkillPoints,
                       character.PetVariant
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorization.Parameters.AddWithValue("$characterId", characterId);
            authorization.Parameters.AddWithValue("$accountId", accountId);
            authorization.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await authorization.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The online character session is no longer active.", recipe.Output, 0, "none", 0);
            }
            normalKeys = reader.GetInt64(0);
            goldenKeys = reader.GetInt64(1);
            mysteryKeys = reader.GetInt64(2);
            freeMagicExpiration = checked((uint)reader.GetInt64(3));
            skillPoints = reader.GetInt64(4);
            petVariant = reader.GetInt32(5);
        }

        var ownedMaterials = new Dictionary<uint, long>(materials.Count);
        await using (var owned = connection.CreateCommand())
        {
            owned.Transaction = transaction;
            var names = materials.Keys.Select((_, index) => $"$material{index}").ToArray();
            owned.CommandText = $"SELECT CardCode, Quantity FROM CharacterCards WHERE CharacterId = $characterId AND CardCode IN ({string.Join(",", names)})";
            owned.Parameters.AddWithValue("$characterId", characterId);
            var materialIndex = 0;
            foreach (var material in materials.Keys)
                owned.Parameters.AddWithValue(names[materialIndex++], material);
            await using var reader = await owned.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ownedMaterials[checked((uint)reader.GetInt64(0))] = reader.GetInt64(1);
        }
        foreach (var material in materials)
        {
            if (ownedMaterials.GetValueOrDefault(material.Key) < material.Value)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, $"Card material {material.Key} is missing or insufficient.", recipe.Output, 0, "none", 0);
            }
        }

        long currentRewardQuantity = 0;
        ushort rewardQuantity;
        var skillReward = 0u;
        if (rewardDomain == 15u)
        {
            await using var pets = connection.CreateCommand();
            pets.Transaction = transaction;
            pets.CommandText = """
                SELECT EXISTS(
                           SELECT 1 FROM CharacterItems
                           WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity > 0),
                       (SELECT COUNT(*) FROM CharacterItems
                        WHERE CharacterId = $characterId AND Quantity > 0
                          AND ItemCode / 1000000 = 15)
                """;
            pets.Parameters.AddWithValue("$characterId", characterId);
            pets.Parameters.AddWithValue("$itemCode", recipe.Output);
            await using var reader = await pets.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var duplicate = reader.GetInt32(0) != 0
                || petVariant is >= 1 and <= 3 && recipe.Output == 15_000_000u + checked((uint)petVariant);
            var ownedPetCount = reader.GetInt32(1) + (petVariant is >= 1 and <= 3 ? 1 : 0);
            if (duplicate || ownedPetCount >= 56)
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, duplicate ? "The synthesized PET is already owned." : "The PET inventory is full.", recipe.Output, 0, "none", 0);
            }
            rewardQuantity = 1;
        }
        else if (rewardDomain == 21u)
        {
            skillReward = recipe.Output - 21_000_000u;
            if (skillReward == 0) skillReward = 1;
            if (skillReward > ushort.MaxValue || skillPoints + skillReward > ushort.MaxValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The skill-point reward would exceed the character cap.", recipe.Output, 0, "none", 0);
            }
            rewardQuantity = checked((ushort)(skillPoints + skillReward));
        }
        else
        {
            await using var reward = connection.CreateCommand();
            reward.Transaction = transaction;
            reward.CommandText = "SELECT COALESCE(Quantity, 0) FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            reward.Parameters.AddWithValue("$characterId", characterId);
            reward.Parameters.AddWithValue("$itemCode", recipe.Output);
            currentRewardQuantity = Convert.ToInt64(await reward.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (currentRewardQuantity >= ushort.MaxValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The synthesized item stack is full.", recipe.Output, ushort.MaxValue, "none", 0);
            }
            rewardQuantity = checked((ushort)(currentRewardQuantity + 1));
        }

        var currentWireTime = SkillSlotExpansionTime.Encode(DateTime.Now);
        var usesFreeMagic = freeMagicExpiration != 0 && currentWireTime < freeMagicExpiration;
        string keyKind;
        string? keyColumn;
        byte keyUseCount;
        if (usesFreeMagic)
        {
            keyKind = "free";
            keyColumn = null;
            keyUseCount = 0;
        }
        else if (normalKeys > 0)
        {
            keyKind = "normal";
            keyColumn = "CardSummonCount";
            keyUseCount = checked((byte)(normalKeys - 1));
        }
        else if (goldenKeys > 0)
        {
            keyKind = "gold";
            keyColumn = "CardGoldenKeyCount";
            keyUseCount = checked((byte)(goldenKeys - 1));
        }
        else if (mysteryKeys > 0)
        {
            keyKind = "mystery";
            keyColumn = "CardMysteryKeyCount";
            keyUseCount = checked((byte)(mysteryKeys - 1));
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "No activated magic-key uses remain.", recipe.Output, 0, "none", 0);
        }

        var now = DateTime.UtcNow.ToString("O");
        foreach (var material in materials)
        {
            await using var consume = connection.CreateCommand();
            consume.Transaction = transaction;
            var removeWholeStack = ownedMaterials[material.Key] == material.Value;
            consume.CommandText = removeWholeStack
                ? """
                    DELETE FROM CharacterCards
                    WHERE CharacterId = $characterId
                      AND CardCode = $cardCode
                      AND Quantity = $quantity
                    """
                : """
                    UPDATE CharacterCards
                    SET Quantity = Quantity - $quantity,
                        UpdatedAt = $now
                    WHERE CharacterId = $characterId
                      AND CardCode = $cardCode
                      AND Quantity > $quantity
                    """;
            consume.Parameters.AddWithValue("$quantity", material.Value);
            consume.Parameters.AddWithValue("$now", now);
            consume.Parameters.AddWithValue("$characterId", characterId);
            consume.Parameters.AddWithValue("$cardCode", material.Key);
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "A card material changed during synthesis.", recipe.Output, 0, "none", 0);
            }
        }

        await using (var updateCharacter = connection.CreateCommand())
        {
            updateCharacter.Transaction = transaction;
            var assignments = new List<string>();
            if (keyColumn is not null) assignments.Add($"{keyColumn} = {keyColumn} - 1");
            if (skillReward != 0) assignments.Add("SkillPoints = SkillPoints + $skillReward");
            assignments.Add("LastSavedAt = $now");
            updateCharacter.CommandText = $"""
                UPDATE Characters
                SET {string.Join(", ", assignments)}
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            if (skillReward != 0) updateCharacter.Parameters.AddWithValue("$skillReward", skillReward);
            updateCharacter.Parameters.AddWithValue("$now", now);
            updateCharacter.Parameters.AddWithValue("$characterId", characterId);
            updateCharacter.Parameters.AddWithValue("$accountId", accountId);
            updateCharacter.Parameters.AddWithValue("$sessionId", sessionId);
            if (await updateCharacter.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The character state changed during synthesis.", recipe.Output, 0, "none", 0);
            }
        }

        if (rewardDomain == 15u)
        {
            await using var grantPet = connection.CreateCommand();
            grantPet.Transaction = transaction;
            grantPet.CommandText = """
                INSERT INTO CharacterItems(
                    CharacterId, ItemCode, Quantity, PetCurrentStage, PetMaximumStage,
                    PetLevel, PetExperience, UpdatedAt)
                VALUES($characterId, $itemCode, 1, $currentStage, $maximumStage, 0, 0, $now)
                """;
            grantPet.Parameters.AddWithValue("$characterId", characterId);
            grantPet.Parameters.AddWithValue("$itemCode", recipe.Output);
            grantPet.Parameters.AddWithValue("$currentStage", petReward!.PetModelStage);
            grantPet.Parameters.AddWithValue("$maximumStage", petReward.PetUpgradeStage);
            grantPet.Parameters.AddWithValue("$now", now);
            if (await grantPet.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The synthesized PET could not be persisted.", recipe.Output, 0, "none", 0);
            }
        }
        else if (rewardDomain != 21u)
        {
            await using var grantItem = connection.CreateCommand();
            grantItem.Transaction = transaction;
            grantItem.CommandText = """
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, 1, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = CharacterItems.Quantity + 1,
                    UpdatedAt = excluded.UpdatedAt
                """;
            grantItem.Parameters.AddWithValue("$characterId", characterId);
            grantItem.Parameters.AddWithValue("$itemCode", recipe.Output);
            grantItem.Parameters.AddWithValue("$now", now);
            if (await grantItem.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The synthesized item could not be persisted.", recipe.Output, 0, "none", 0);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, recipe.Output, rewardQuantity, keyKind, keyUseCount);
    }

    private static async Task<IReadOnlyList<CharacterCardRecord>> ReadCharacterCardsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<CharacterCardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var cardCode = checked((uint)reader.GetInt64(0));
            CardCatalog.TryGet(cardCode, out var catalogEntry);
            if (!CardCatalog.TryGetAlbumCoordinate(cardCode, out var category, out var page, out var slot))
                continue;
            result.Add(new CharacterCardRecord
            {
                CardCode = cardCode,
                Quantity = checked((byte)reader.GetInt32(1)),
                Name = catalogEntry?.Name ?? $"Card {cardCode}",
                Category = category,
                Page = page,
                Slot = slot,
                IconPath = catalogEntry?.IconPath ?? string.Empty
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<CharacterInventoryItemRecord>> GetInventoryByAccountAsync(
        long accountId,
        CancellationToken cancellationToken = default)
    {
        var character = await GetCharacterAsync(accountId, cancellationToken);
        if (character is null)
            return [];

        var equippedAppearanceItems = new HashSet<uint>();
        if (character.Appearance is { Length: >= 16 } appearance)
        {
            foreach (var offset in new[] { 0, 8, 12 })
            {
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(offset, 4));
                if (itemCode != 0)
                    equippedAppearanceItems.Add(itemCode);
            }
        }

        var result = new List<CharacterInventoryItemRecord>();
        foreach (var ownedItem in character.Items)
        {
            if (ownedItem.Quantity == 0 || !ShopCatalog.TryGet(ownedItem.ItemCode, out var catalogItem))
                continue;
            result.Add(new CharacterInventoryItemRecord
            {
                ItemCode = ownedItem.ItemCode,
                Quantity = ownedItem.Quantity,
                Name = catalogItem.Name,
                Section = catalogItem.Section,
                IconPath = catalogItem.IconPath,
                IsEquipped = catalogItem.Section switch
                {
                    InventorySection.Clothing => equippedAppearanceItems.Contains(ownedItem.ItemCode),
                    InventorySection.Pet => character.EquippedPetItemCode == ownedItem.ItemCode,
                    _ => false
                }
            });
        }

        if (character.PetVariant is >= 1 and <= 3)
        {
            var tutorialPetCode = 15_000_000u + (uint)character.PetVariant;
            if (result.All(item => item.ItemCode != tutorialPetCode)
                && ShopCatalog.TryGet(tutorialPetCode, out var tutorialPet))
            {
                result.Add(new CharacterInventoryItemRecord
                {
                    ItemCode = tutorialPetCode,
                    Quantity = 1,
                    Name = tutorialPet.Name,
                    Section = InventorySection.Pet,
                    IconPath = tutorialPet.IconPath,
                    IsEquipped = character.EquippedPetItemCode == 0 || character.EquippedPetItemCode == tutorialPetCode,
                    IsProtected = true
                });
            }
        }

        return result
            .OrderBy(item => item.Section)
            .ThenBy(item => item.ItemCode)
            .ToArray();
    }

    public async Task<(bool Success, string Error, ushort Quantity)> GrantInventoryItemToAccountAsync(
        long accountId,
        uint itemCode,
        ushort quantity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return (false, "账号 ID 无效。", 0);
        if (quantity == 0)
            return (false, "发放数量必须大于 0。", 0);
        if (!ShopCatalog.TryGet(itemCode, out _))
            return (false, "物品编号不在客户端资源目录中。", 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
            SELECT Id, $itemCode, $quantity, $now
            FROM Characters
            WHERE AccountId = $accountId
            ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                Quantity = MIN(65535, CharacterItems.Quantity + excluded.Quantity),
                UpdatedAt = excluded.UpdatedAt
            RETURNING Quantity
            """;
        command.Parameters.AddWithValue("$itemCode", itemCode);
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$accountId", accountId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null
            ? (false, "该账号尚未创建角色，无法发放物品。", (ushort)0)
            : (true, string.Empty, checked((ushort)Convert.ToInt32(result)));
    }

    public async Task<(bool Success, string Error, ushort Quantity)> RemoveInventoryItemFromAccountAsync(
        long accountId,
        uint itemCode,
        ushort quantity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || quantity == 0)
            return (false, "账号或扣减数量无效。", 0);
        if (!ShopCatalog.TryGet(itemCode, out var catalogItem))
            return (false, "物品编号不在客户端资源目录中。", 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        long characterId;
        int petVariant;
        uint equippedPetItemCode;
        await using (var characterCommand = connection.CreateCommand())
        {
            characterCommand.Transaction = transaction;
            characterCommand.CommandText = "SELECT Id, PetVariant, EquippedPetItemCode FROM Characters WHERE AccountId = $accountId";
            characterCommand.Parameters.AddWithValue("$accountId", accountId);
            await using var reader = await characterCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, "该账号尚未创建角色。", 0);
            }
            characterId = reader.GetInt64(0);
            petVariant = reader.GetInt32(1);
            equippedPetItemCode = checked((uint)reader.GetInt64(2));
        }

        var tutorialPetItemCode = petVariant is >= 1 and <= 3 ? 15_000_000u + (uint)petVariant : 0u;
        if (catalogItem.Section == InventorySection.Pet && itemCode == tutorialPetItemCode)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "新手教程宠物属于角色基础数据，不能从背包删除。", 1);
        }

        long currentQuantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT Quantity FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$itemCode", itemCode);
            currentQuantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (currentQuantity == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "该账号未持有此物品。", 0);
        }

        var newQuantity = checked((ushort)Math.Max(0L, currentQuantity - quantity));
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = newQuantity == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode"
                : "UPDATE CharacterItems SET Quantity = $quantity, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$itemCode", itemCode);
            if (newQuantity != 0)
            {
                update.Parameters.AddWithValue("$quantity", newQuantity);
                update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (newQuantity == 0 && equippedPetItemCode == itemCode)
        {
            await using var repair = connection.CreateCommand();
            repair.Transaction = transaction;
            repair.CommandText = "UPDATE Characters SET EquippedPetItemCode = $replacement, LastSavedAt = $now WHERE Id = $characterId";
            repair.Parameters.AddWithValue("$replacement", tutorialPetItemCode);
            repair.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            repair.Parameters.AddWithValue("$characterId", characterId);
            await repair.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, newQuantity);
    }

    public async Task<(
        bool Success,
        bool InsufficientBalance,
        string Error,
        short Durability,
        long Cost,
        long Hans)> ChargePetItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        short expectedDurability,
        short maxDurability,
        ushort unitPrice,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || itemCode / 1_000_000 != 15
            || maxDurability <= 0
            || unitPrice == 0
            || expectedDurability < 0
            || expectedDurability >= maxDurability)
            return (false, false, "Pet charge parameters are invalid.", expectedDurability, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        long hans;
        short currentDurability;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT c.Hans, COALESCE(i.PetDurability, $maxDurability)
                FROM Characters c
                JOIN Accounts a ON a.Id = c.AccountId
                JOIN CharacterItems i ON i.CharacterId = c.Id
                WHERE c.Id = $characterId
                  AND c.AccountId = $accountId
                  AND c.IsOnline = 1
                  AND c.ActiveSessionId = $sessionId
                  AND a.IsOnline = 1
                  AND a.ActiveSessionId = $sessionId
                  AND i.ItemCode = $itemCode
                  AND i.Quantity > 0
                """;
            current.Parameters.AddWithValue("$maxDurability", maxDurability);
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            current.Parameters.AddWithValue("$itemCode", itemCode);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, "The online character does not own this pet.", expectedDurability, 0, 0);
            }
            hans = reader.GetInt64(0);
            currentDurability = checked((short)reader.GetInt32(1));
        }

        if (currentDurability != expectedDurability
            || currentDurability < 0
            || currentDurability >= maxDurability)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, false, "Pet durability changed or is already full.", currentDurability, 0, hans);
        }

        var cost = checked((long)(maxDurability - currentDurability) * unitPrice);
        if (hans < cost)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, true, "Hans balance is insufficient.", currentDurability, cost, hans);
        }

        var now = DateTime.UtcNow.ToString("O");
        await using (var durability = connection.CreateCommand())
        {
            durability.Transaction = transaction;
            durability.CommandText = """
                UPDATE CharacterItems
                SET PetDurability = $maxDurability,
                    UpdatedAt = $now
                WHERE CharacterId = $characterId
                  AND ItemCode = $itemCode
                  AND Quantity > 0
                  AND COALESCE(PetDurability, $maxDurability) = $expectedDurability
                """;
            durability.Parameters.AddWithValue("$maxDurability", maxDurability);
            durability.Parameters.AddWithValue("$expectedDurability", expectedDurability);
            durability.Parameters.AddWithValue("$now", now);
            durability.Parameters.AddWithValue("$characterId", characterId);
            durability.Parameters.AddWithValue("$itemCode", itemCode);
            if (await durability.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, "Pet durability changed before charge completed.", currentDurability, cost, hans);
            }
        }

        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = """
                UPDATE Characters
                SET Hans = Hans - $cost,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                  AND Hans >= $cost
                RETURNING Hans
                """;
            debit.Parameters.AddWithValue("$cost", cost);
            debit.Parameters.AddWithValue("$now", now);
            debit.Parameters.AddWithValue("$characterId", characterId);
            debit.Parameters.AddWithValue("$accountId", accountId);
            debit.Parameters.AddWithValue("$sessionId", sessionId);
            var updatedHans = await debit.ExecuteScalarAsync(cancellationToken);
            if (updatedHans is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, "The online session or Hans balance changed before debit.", currentDurability, cost, hans);
            }
            hans = Convert.ToInt64(updatedHans, CultureInfo.InvariantCulture);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false, string.Empty, maxDurability, cost, hans);
    }

    public async Task<(bool Success, string Error, ushort Quantity)> ConsumeTokenItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        CancellationToken cancellationToken = default)
    {
        var result = await ChangeTokenItemAsync(
            accountId,
            characterId,
            sessionId,
            itemCode,
            0,
            0,
            cancellationToken);
        return (result.Success, result.Error, result.Quantity);
    }

    public async Task<(bool Success, string Error, ushort Quantity, byte MysteryKeyCount, byte GoldenKeyCount)> ActivateTokenItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        CancellationToken cancellationToken = default)
    {
        if (!ShopCatalog.TryGet(itemCode, out var catalogItem) || catalogItem.Category != 47)
            return (false, "Token use parameters are invalid.", 0, 0, 0);

        // PR._D27 mode 1 is the mystery-key/lucky-seal mode; mode 0 is
        // the golden key. Field 6 is the exact number of uses to activate.
        var mysteryUses = catalogItem.TokenMode == 1 ? catalogItem.TokenUseCount : (byte)0;
        var goldenUses = catalogItem.TokenMode == 0 ? catalogItem.TokenUseCount : (byte)0;
        return await ChangeTokenItemAsync(
            accountId,
            characterId,
            sessionId,
            itemCode,
            mysteryUses,
            goldenUses,
            cancellationToken);
    }

    private async Task<(bool Success, string Error, ushort Quantity, byte MysteryKeyCount, byte GoldenKeyCount)> ChangeTokenItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        byte mysteryUses,
        byte goldenUses,
        CancellationToken cancellationToken)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || catalogItem.Category != 47)
            return (false, "Token use parameters are invalid.", 0, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        long currentQuantity;
        long currentMysteryKeyCount;
        long currentGoldenKeyCount;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT item.Quantity, character.CardMysteryKeyCount, character.CardGoldenKeyCount
                FROM CharacterItems AS item
                INNER JOIN Characters AS character ON character.Id = item.CharacterId
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE item.CharacterId = $characterId
                  AND item.ItemCode = $itemCode
                  AND item.Quantity > 0
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$itemCode", itemCode);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                currentQuantity = reader.GetInt64(0);
                currentMysteryKeyCount = reader.GetInt64(1);
                currentGoldenKeyCount = reader.GetInt64(2);
            }
            else
            {
                currentQuantity = 0;
                currentMysteryKeyCount = 0;
                currentGoldenKeyCount = 0;
            }
        }

        if (currentQuantity <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The online character does not own this token item.", 0, checked((byte)currentMysteryKeyCount), checked((byte)currentGoldenKeyCount));
        }
        if (currentMysteryKeyCount + mysteryUses > byte.MaxValue
            || currentGoldenKeyCount + goldenUses > byte.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The activated key counter is full.", checked((ushort)currentQuantity), checked((byte)currentMysteryKeyCount), checked((byte)currentGoldenKeyCount));
        }

        var newQuantity = checked((ushort)(currentQuantity - 1));
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = newQuantity == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $currentQuantity"
                : "UPDATE CharacterItems SET Quantity = $quantity, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $currentQuantity";
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$itemCode", itemCode);
            update.Parameters.AddWithValue("$currentQuantity", currentQuantity);
            if (newQuantity != 0)
            {
                update.Parameters.AddWithValue("$quantity", newQuantity);
                update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "Token inventory changed before use completed.", checked((ushort)currentQuantity), checked((byte)currentMysteryKeyCount), checked((byte)currentGoldenKeyCount));
            }
        }

        var newMysteryKeyCount = checked((byte)(currentMysteryKeyCount + mysteryUses));
        var newGoldenKeyCount = checked((byte)(currentGoldenKeyCount + goldenUses));
        if (mysteryUses != 0 || goldenUses != 0)
        {
            await using var activate = connection.CreateCommand();
            activate.Transaction = transaction;
            activate.CommandText = """
                UPDATE Characters
                SET CardMysteryKeyCount = $newMysteryKeyCount,
                    CardGoldenKeyCount = $newGoldenKeyCount,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND CardMysteryKeyCount = $currentMysteryKeyCount
                  AND CardGoldenKeyCount = $currentGoldenKeyCount
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            activate.Parameters.AddWithValue("$newMysteryKeyCount", newMysteryKeyCount);
            activate.Parameters.AddWithValue("$newGoldenKeyCount", newGoldenKeyCount);
            activate.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            activate.Parameters.AddWithValue("$characterId", characterId);
            activate.Parameters.AddWithValue("$accountId", accountId);
            activate.Parameters.AddWithValue("$currentMysteryKeyCount", currentMysteryKeyCount);
            activate.Parameters.AddWithValue("$currentGoldenKeyCount", currentGoldenKeyCount);
            activate.Parameters.AddWithValue("$sessionId", sessionId);
            if (await activate.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The activated key counter changed before token use completed.", checked((ushort)currentQuantity), checked((byte)currentMysteryKeyCount), checked((byte)currentGoldenKeyCount));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, newQuantity, newMysteryKeyCount, newGoldenKeyCount);
    }

    public async Task<(bool Success, string Error, ushort Quantity, byte ChannelUseCount, byte GlobalUseCount)> ActivateMikeItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || catalogItem.Category != 42
            || !string.Equals(catalogItem.Source, "MI._D22", StringComparison.Ordinal)
            || catalogItem.TokenMode is not (0 or 1)
            || catalogItem.TokenUseCount == 0)
            return (false, "Mike item parameters are invalid.", 0, 0, 0);

        // MI._D22 mode 1 is the current-channel mike and mode 0 is the
        // all-channel mike. The retail client caps both counters at 99.
        var channelUses = catalogItem.TokenMode == 1 ? catalogItem.TokenUseCount : (byte)0;
        var globalUses = catalogItem.TokenMode == 0 ? catalogItem.TokenUseCount : (byte)0;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        long currentQuantity;
        long currentChannelUseCount;
        long currentGlobalUseCount;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT item.Quantity, character.MikeChannelUseCount, character.MikeGlobalUseCount
                FROM CharacterItems AS item
                INNER JOIN Characters AS character ON character.Id = item.CharacterId
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE item.CharacterId = $characterId
                  AND item.ItemCode = $itemCode
                  AND item.Quantity > 0
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$itemCode", itemCode);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                currentQuantity = reader.GetInt64(0);
                currentChannelUseCount = reader.GetInt64(1);
                currentGlobalUseCount = reader.GetInt64(2);
            }
            else
            {
                currentQuantity = 0;
                currentChannelUseCount = 0;
                currentGlobalUseCount = 0;
            }
        }

        if (currentQuantity <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The online character does not own this mike item.", 0,
                checked((byte)currentChannelUseCount), checked((byte)currentGlobalUseCount));
        }
        if (currentChannelUseCount + channelUses > 99
            || currentGlobalUseCount + globalUses > 99)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The activated mike counter is full.", checked((ushort)currentQuantity),
                checked((byte)currentChannelUseCount), checked((byte)currentGlobalUseCount));
        }

        var newQuantity = checked((ushort)(currentQuantity - 1));
        await using (var updateItem = connection.CreateCommand())
        {
            updateItem.Transaction = transaction;
            updateItem.CommandText = newQuantity == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $currentQuantity"
                : "UPDATE CharacterItems SET Quantity = $quantity, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $currentQuantity";
            updateItem.Parameters.AddWithValue("$characterId", characterId);
            updateItem.Parameters.AddWithValue("$itemCode", itemCode);
            updateItem.Parameters.AddWithValue("$currentQuantity", currentQuantity);
            if (newQuantity != 0)
            {
                updateItem.Parameters.AddWithValue("$quantity", newQuantity);
                updateItem.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }
            if (await updateItem.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "Mike inventory changed before activation completed.", checked((ushort)currentQuantity),
                    checked((byte)currentChannelUseCount), checked((byte)currentGlobalUseCount));
            }
        }

        var newChannelUseCount = checked((byte)(currentChannelUseCount + channelUses));
        var newGlobalUseCount = checked((byte)(currentGlobalUseCount + globalUses));
        await using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = """
                UPDATE Characters
                SET MikeChannelUseCount = $newChannelUseCount,
                    MikeGlobalUseCount = $newGlobalUseCount,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND MikeChannelUseCount = $currentChannelUseCount
                  AND MikeGlobalUseCount = $currentGlobalUseCount
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            activate.Parameters.AddWithValue("$newChannelUseCount", newChannelUseCount);
            activate.Parameters.AddWithValue("$newGlobalUseCount", newGlobalUseCount);
            activate.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            activate.Parameters.AddWithValue("$characterId", characterId);
            activate.Parameters.AddWithValue("$accountId", accountId);
            activate.Parameters.AddWithValue("$currentChannelUseCount", currentChannelUseCount);
            activate.Parameters.AddWithValue("$currentGlobalUseCount", currentGlobalUseCount);
            activate.Parameters.AddWithValue("$sessionId", sessionId);
            if (await activate.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The activated mike counter changed before activation completed.", checked((ushort)currentQuantity),
                    checked((byte)currentChannelUseCount), checked((byte)currentGlobalUseCount));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, newQuantity, newChannelUseCount, newGlobalUseCount);
    }

    public async Task<(bool Success, string Error, byte RemainingUseCount)> ConsumeMikeUseAsync(
        long accountId,
        long characterId,
        string sessionId,
        bool global,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId))
            return (false, "Mike use parameters are invalid.", 0);

        var column = global ? "MikeGlobalUseCount" : "MikeChannelUseCount";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE Characters
            SET {column} = {column} - 1,
                LastSavedAt = $now
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND {column} > 0
              AND IsOnline = 1
              AND ActiveSessionId = $sessionId
              AND EXISTS (
                  SELECT 1 FROM Accounts
                  WHERE Id = $accountId
                    AND IsOnline = 1
                    AND ActiveSessionId = $sessionId)
            RETURNING {column}
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var remaining = await command.ExecuteScalarAsync(cancellationToken);
        if (remaining is null)
            return (false, "The online character has no remaining mike uses.", 0);
        return (true, string.Empty, checked((byte)Convert.ToInt32(remaining, CultureInfo.InvariantCulture)));
    }

    public async Task<(bool Success, bool InsufficientBalance, string Error, ushort NewQuantity, long Hans, long Cash)> PurchaseShopItemForAccountAsync(
        long accountId,
        uint itemCode,
        ushort quantity,
        uint unitPrice,
        bool payWithCash = false,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || quantity == 0 || unitPrice == 0)
            return (false, false, "商城购买参数无效。", 0, 0, 0);

        var totalPrice = checked((long)quantity * unitPrice);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        long characterId;
        long hans;
        long cash;
        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = payWithCash
                ? """
                    UPDATE Characters
                    SET Cash = Cash - $totalPrice,
                        LastSavedAt = $now
                    WHERE AccountId = $accountId
                      AND Cash >= $totalPrice
                    RETURNING Id, Hans, Cash
                    """
                : """
                    UPDATE Characters
                    SET Hans = Hans - $totalPrice,
                        LastSavedAt = $now
                    WHERE AccountId = $accountId
                      AND Hans >= $totalPrice
                    RETURNING Id, Hans, Cash
                    """;
            debit.Parameters.AddWithValue("$totalPrice", totalPrice);
            debit.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            debit.Parameters.AddWithValue("$accountId", accountId);
            await using var reader = await debit.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                var character = await GetCharacterAsync(accountId, cancellationToken);
                var balance = payWithCash ? character?.Cash : character?.Hans;
                var insufficient = balance is not null && balance < totalPrice;
                var balanceName = payWithCash ? "Cash" : "Hans";
                return (false, insufficient, character is null ? "该账号尚未创建角色。" : $"{balanceName} 余额不足。", 0, character?.Hans ?? 0, character?.Cash ?? 0);
            }
            characterId = reader.GetInt64(0);
            hans = reader.GetInt64(1);
            cash = reader.GetInt64(2);
        }

        long currentQuantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT Quantity FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$itemCode", itemCode);
            currentQuantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (currentQuantity + quantity > ushort.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return payWithCash
                ? (false, false, "物品数量超过客户端库存上限。", (ushort)0, hans, cash + totalPrice)
                : (false, false, "物品数量超过客户端库存上限。", (ushort)0, hans + totalPrice, cash);
        }

        var newQuantity = checked((ushort)(currentQuantity + quantity));
        await using (var inventory = connection.CreateCommand())
        {
            inventory.Transaction = transaction;
            inventory.CommandText = """
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = excluded.Quantity,
                    UpdatedAt = excluded.UpdatedAt
                """;
            inventory.Parameters.AddWithValue("$characterId", characterId);
            inventory.Parameters.AddWithValue("$itemCode", itemCode);
            inventory.Parameters.AddWithValue("$quantity", newQuantity);
            inventory.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await inventory.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false, string.Empty, newQuantity, hans, cash);
    }

    public async Task<(
        bool Success,
        bool InsufficientBalance,
        string Error,
        IReadOnlyList<(uint ItemCode, ushort NewQuantity)> Items,
        long Hans,
        long Cash)> PurchaseInteriorItemsAsync(
        long accountId,
        long characterId,
        string sessionId,
        byte paymentMode,
        IReadOnlyList<(uint ItemCode, ushort Quantity, uint UnitPrice)> items,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || paymentMode != 4
            || items.Count is < 1 or > 40
            || items.Any(item => item.Quantity == 0 || item.UnitPrice == 0)
            || items.Any(item => !ShopCatalog.TryGet(item.ItemCode, out var catalogItem)
                || catalogItem.Category != 11
                || catalogItem.Section != InventorySection.Furniture
                || catalogItem.HansPrice != item.UnitPrice)
            || items.Select(item => item.ItemCode).Distinct().Count() != items.Count)
            return (false, false, "Invalid interior purchase parameters.", [], 0, 0);

        var totalPrice = items.Sum(item => checked((long)item.Quantity * item.UnitPrice));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        long hans;
        long cash;
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT character.Hans, character.Cash
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorize.Parameters.AddWithValue("$characterId", characterId);
            authorize.Parameters.AddWithValue("$accountId", accountId);
            authorize.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await authorize.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, "Interior shop session is no longer valid.", [], 0, 0);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        if (hans < totalPrice)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, true, "Insufficient Hans balance.", [], hans, cash);
        }

        var purchasedItems = new List<(uint ItemCode, ushort NewQuantity)>(items.Count);
        foreach (var item in items)
        {
            long currentQuantity;
            await using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = "SELECT Quantity FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode";
                current.Parameters.AddWithValue("$characterId", characterId);
                current.Parameters.AddWithValue("$itemCode", item.ItemCode);
                currentQuantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
            }
            if (currentQuantity + item.Quantity > ushort.MaxValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, "Interior item quantity exceeds the client inventory limit.", [], hans, cash);
            }
            purchasedItems.Add((item.ItemCode, checked((ushort)(currentQuantity + item.Quantity))));
        }

        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = """
                UPDATE Characters
                SET Hans = Hans - $totalPrice,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                  AND Hans >= $totalPrice
                  AND EXISTS (
                      SELECT 1 FROM Accounts
                      WHERE Id = $accountId
                        AND IsOnline = 1
                        AND ActiveSessionId = $sessionId
                  )
                RETURNING Hans, Cash
                """;
            debit.Parameters.AddWithValue("$totalPrice", totalPrice);
            debit.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            debit.Parameters.AddWithValue("$characterId", characterId);
            debit.Parameters.AddWithValue("$accountId", accountId);
            debit.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await debit.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, "Interior shop session changed before debit completed.", [], hans, cash);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        var now = DateTime.UtcNow.ToString("O");
        foreach (var item in purchasedItems)
        {
            await using var inventory = connection.CreateCommand();
            inventory.Transaction = transaction;
            inventory.CommandText = """
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = excluded.Quantity,
                    UpdatedAt = excluded.UpdatedAt
                """;
            inventory.Parameters.AddWithValue("$characterId", characterId);
            inventory.Parameters.AddWithValue("$itemCode", item.ItemCode);
            inventory.Parameters.AddWithValue("$quantity", item.NewQuantity);
            inventory.Parameters.AddWithValue("$now", now);
            await inventory.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false, string.Empty, purchasedItems, hans, cash);
    }

    public async Task<(
        bool Success,
        bool InsufficientBalance,
        string Error,
        IReadOnlyList<(uint ItemCode, ushort NewQuantity)> Items,
        long Hans,
        long Cash)> PurchaseNanaAvatarItemsAsync(
        long accountId,
        long characterId,
        string sessionId,
        byte paymentMode,
        IReadOnlyList<(uint ItemCode, uint UnitPrice)> items,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || paymentMode != 4
            || items.Count is < 1 or > 10
            || items.Any(item => item.UnitPrice == 0)
            || items.Any(item => !ShopCatalog.TryGet(item.ItemCode, out var catalogItem)
                || catalogItem.Category != 10
                || catalogItem.Section != InventorySection.Clothing
                || catalogItem.CashPrice != item.UnitPrice)
            || items.Select(item => item.ItemCode).Distinct().Count() != items.Count)
            return (false, false, "Invalid NaNa avatar purchase parameters.", [], 0, 0);

        var totalPrice = items.Sum(item => checked((long)item.UnitPrice));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        long hans;
        long cash;
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT character.Hans, character.Cash
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorize.Parameters.AddWithValue("$characterId", characterId);
            authorize.Parameters.AddWithValue("$accountId", accountId);
            authorize.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await authorize.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, "NaNa shop session is no longer valid.", [], 0, 0);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        if (cash < totalPrice)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, true, "Insufficient NaNa/Cash balance.", [], hans, cash);
        }

        var purchasedItems = new List<(uint ItemCode, ushort NewQuantity)>(items.Count);
        foreach (var item in items)
        {
            await using var current = connection.CreateCommand();
            current.Transaction = transaction;
            current.CommandText = "SELECT Quantity FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$itemCode", item.ItemCode);
            var currentQuantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (currentQuantity >= ushort.MaxValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, "NaNa avatar quantity exceeds the client inventory limit.", [], hans, cash);
            }
            purchasedItems.Add((item.ItemCode, checked((ushort)(currentQuantity + 1))));
        }

        var now = DateTime.UtcNow.ToString("O");
        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = """
                UPDATE Characters
                SET Cash = Cash - $totalPrice,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                  AND Cash >= $totalPrice
                  AND EXISTS (
                      SELECT 1 FROM Accounts
                      WHERE Id = $accountId
                        AND IsOnline = 1
                        AND ActiveSessionId = $sessionId
                  )
                RETURNING Hans, Cash
                """;
            debit.Parameters.AddWithValue("$totalPrice", totalPrice);
            debit.Parameters.AddWithValue("$now", now);
            debit.Parameters.AddWithValue("$characterId", characterId);
            debit.Parameters.AddWithValue("$accountId", accountId);
            debit.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await debit.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, "NaNa shop session changed before debit completed.", [], hans, cash);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        foreach (var item in purchasedItems)
        {
            await using var inventory = connection.CreateCommand();
            inventory.Transaction = transaction;
            inventory.CommandText = """
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = excluded.Quantity,
                    UpdatedAt = excluded.UpdatedAt
                """;
            inventory.Parameters.AddWithValue("$characterId", characterId);
            inventory.Parameters.AddWithValue("$itemCode", item.ItemCode);
            inventory.Parameters.AddWithValue("$quantity", item.NewQuantity);
            inventory.Parameters.AddWithValue("$now", now);
            await inventory.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false, string.Empty, purchasedItems, hans, cash);
    }

    public async Task<(bool Success, bool InsufficientBalance, string Error, ushort NewQuantity, long Hans, long Cash)> PurchaseShopItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        byte paymentMode,
        uint itemCode,
        ushort quantity,
        uint unitPrice,
        bool payWithCash,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId))
            return (false, false, "商城会话无效。", 0, 0, 0);
        if (paymentMode is not (0 or 2 or 3 or 4) || quantity == 0 || unitPrice == 0)
            return (false, false, "商城付款参数无效。", 0, 0, 0);

        var totalPrice = checked((long)quantity * unitPrice);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        long currentQuantity;
        await using (var quantityCommand = connection.CreateCommand())
        {
            quantityCommand.Transaction = transaction;
            quantityCommand.CommandText = "SELECT Quantity FROM CharacterCashInboxItems WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            quantityCommand.Parameters.AddWithValue("$characterId", characterId);
            quantityCommand.Parameters.AddWithValue("$itemCode", itemCode);
            currentQuantity = Convert.ToInt64(await quantityCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (currentQuantity + quantity > ushort.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, false, "物品数量超过客户端库存上限。", 0, 0, 0);
        }

        long hans;
        long cash;
        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = payWithCash
                ? """
                    UPDATE Characters
                    SET Cash = Cash - $totalPrice,
                        LastSavedAt = $now
                    WHERE Id = $characterId
                      AND AccountId = $accountId
                      AND IsOnline = 1
                      AND ActiveSessionId = $sessionId
                      AND Cash >= $totalPrice
                      AND EXISTS (
                          SELECT 1 FROM Accounts
                          WHERE Id = $accountId
                            AND IsOnline = 1
                            AND ActiveSessionId = $sessionId
                      )
                    RETURNING Hans, Cash
                    """
                : """
                    UPDATE Characters
                    SET Hans = Hans - $totalPrice,
                        LastSavedAt = $now
                    WHERE Id = $characterId
                      AND AccountId = $accountId
                      AND IsOnline = 1
                      AND ActiveSessionId = $sessionId
                      AND Hans >= $totalPrice
                      AND EXISTS (
                          SELECT 1 FROM Accounts
                          WHERE Id = $accountId
                            AND IsOnline = 1
                            AND ActiveSessionId = $sessionId
                      )
                    RETURNING Hans, Cash
                    """;
            debit.Parameters.AddWithValue("$totalPrice", totalPrice);
            debit.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            debit.Parameters.AddWithValue("$characterId", characterId);
            debit.Parameters.AddWithValue("$accountId", accountId);
            debit.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await debit.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                var character = await GetCharacterAsync(accountId, cancellationToken);
                var balance = payWithCash ? character?.Cash : character?.Hans;
                var insufficient = balance is not null && balance < totalPrice;
                var balanceName = payWithCash ? "Cash" : "Hans";
                return (false, insufficient, insufficient ? $"{balanceName} 余额不足。" : "商城在线会话已失效。", 0, character?.Hans ?? 0, character?.Cash ?? 0);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        var newQuantity = checked((ushort)(currentQuantity + quantity));
        await using (var inventory = connection.CreateCommand())
        {
            inventory.Transaction = transaction;
            inventory.CommandText = """
                INSERT INTO CharacterCashInboxItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = excluded.Quantity,
                    UpdatedAt = excluded.UpdatedAt
                """;
            inventory.Parameters.AddWithValue("$characterId", characterId);
            inventory.Parameters.AddWithValue("$itemCode", itemCode);
            inventory.Parameters.AddWithValue("$quantity", newQuantity);
            inventory.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await inventory.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false, string.Empty, newQuantity, hans, cash);
    }

    public async Task<(bool Success, bool InsufficientBalance, bool QuantityLimit, string Error, byte NewQuantity, long Hans, long Cash)> PurchaseSpecialCardAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint cardCode,
        ushort quantity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || quantity == 0
            || !CardCatalog.TryGet(cardCode, out var card)
            || card.Category != 3
            || !card.IsSpecialShopPurchasable
            || card.SpecialShopPrice == 0)
            return (false, false, false, "Special-card purchase fields do not match Sddakg._D35.", 0, 0, 0);

        var totalPrice = checked((long)quantity * card.SpecialShopPrice);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        long currentQuantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT Quantity FROM CharacterCards WHERE CharacterId = $characterId AND CardCode = $cardCode";
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$cardCode", cardCode);
            currentQuantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (currentQuantity + quantity > byte.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            var character = await GetCharacterAsync(accountId, cancellationToken);
            return (false, false, true, "Special-card quantity exceeds the client limit.", checked((byte)currentQuantity), character?.Hans ?? 0, character?.Cash ?? 0);
        }

        var now = DateTime.UtcNow.ToString("O");
        long hans;
        long cash;
        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = """
                UPDATE Characters
                SET Cash = Cash - $totalPrice, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                  AND Cash >= $totalPrice
                  AND EXISTS (
                      SELECT 1 FROM Accounts
                      WHERE Id = $accountId
                        AND IsOnline = 1
                        AND ActiveSessionId = $sessionId
                  )
                RETURNING Hans, Cash
                """;
            debit.Parameters.AddWithValue("$totalPrice", totalPrice);
            debit.Parameters.AddWithValue("$now", now);
            debit.Parameters.AddWithValue("$characterId", characterId);
            debit.Parameters.AddWithValue("$accountId", accountId);
            debit.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await debit.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                var character = await GetCharacterAsync(accountId, cancellationToken);
                var insufficient = character is not null && character.Cash < totalPrice;
                return (false, insufficient, false,
                    insufficient ? "Cash balance is insufficient." : "Special-card purchase session is no longer active.",
                    checked((byte)currentQuantity), character?.Hans ?? 0, character?.Cash ?? 0);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        var newQuantity = checked((byte)(currentQuantity + quantity));
        await using (var inventory = connection.CreateCommand())
        {
            inventory.Transaction = transaction;
            inventory.CommandText = """
                INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
                VALUES($characterId, $cardCode, $quantity, $now)
                ON CONFLICT(CharacterId, CardCode) DO UPDATE SET
                    Quantity = excluded.Quantity,
                    UpdatedAt = excluded.UpdatedAt
                """;
            inventory.Parameters.AddWithValue("$characterId", characterId);
            inventory.Parameters.AddWithValue("$cardCode", cardCode);
            inventory.Parameters.AddWithValue("$quantity", newQuantity);
            inventory.Parameters.AddWithValue("$now", now);
            await inventory.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false, false, string.Empty, newQuantity, hans, cash);
    }

    public async Task<(
        bool Success,
        bool RecipientFound,
        bool QuantityLimit,
        bool InsufficientBalance,
        string Error,
        byte RecipientQuantity,
        long Hans,
        long Cash)> GiftSpecialCardAsync(
        long senderAccountId,
        long senderCharacterId,
        string sessionId,
        string recipientCharacterName,
        uint cardCode,
        byte quantity,
        CancellationToken cancellationToken = default)
    {
        if (senderAccountId <= 0
            || senderCharacterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || string.IsNullOrWhiteSpace(recipientCharacterName)
            || quantity == 0
            || !CardCatalog.TryGet(cardCode, out var card)
            || card.Category != 3
            || !card.IsSpecialShopPurchasable
            || card.SpecialShopPrice == 0)
            return (false, false, false, false, "Special-card gift fields do not match Sddakg._D35.", 0, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        long hans;
        long cash;
        await using (var sender = connection.CreateCommand())
        {
            sender.Transaction = transaction;
            sender.CommandText = """
                SELECT character.Hans, character.Cash
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                LIMIT 1
                """;
            sender.Parameters.AddWithValue("$characterId", senderCharacterId);
            sender.Parameters.AddWithValue("$accountId", senderAccountId);
            sender.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await sender.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, true, false, false, "Special-card gift session is no longer active.", 0, 0, 0);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        long recipientCharacterId;
        await using (var recipient = connection.CreateCommand())
        {
            recipient.Transaction = transaction;
            recipient.CommandText = "SELECT Id FROM Characters WHERE Name = $name COLLATE NOCASE LIMIT 1";
            recipient.Parameters.AddWithValue("$name", recipientCharacterName);
            var value = await recipient.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, false, false, "Gift recipient does not exist.", 0, hans, cash);
            }
            recipientCharacterId = Convert.ToInt64(value);
        }

        long currentQuantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT Quantity FROM CharacterCards WHERE CharacterId = $characterId AND CardCode = $cardCode";
            current.Parameters.AddWithValue("$characterId", recipientCharacterId);
            current.Parameters.AddWithValue("$cardCode", cardCode);
            currentQuantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (currentQuantity + quantity > byte.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, true, true, false, "Gift recipient already owns the maximum card quantity.", byte.MaxValue, hans, cash);
        }
        var totalPrice = checked((long)quantity * card.SpecialShopPrice);
        if (cash < totalPrice)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, true, false, true, "Cash balance is insufficient.", checked((byte)currentQuantity), hans, cash);
        }

        var now = DateTime.UtcNow.ToString("O");
        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = """
                UPDATE Characters
                SET Cash = Cash - $price, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                  AND Cash >= $price
                RETURNING Hans, Cash
                """;
            debit.Parameters.AddWithValue("$price", totalPrice);
            debit.Parameters.AddWithValue("$now", now);
            debit.Parameters.AddWithValue("$characterId", senderCharacterId);
            debit.Parameters.AddWithValue("$accountId", senderAccountId);
            debit.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await debit.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, true, false, false, "Special-card gift debit did not complete.", checked((byte)currentQuantity), hans, cash);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        var newQuantity = checked((byte)(currentQuantity + quantity));
        await using (var inventory = connection.CreateCommand())
        {
            inventory.Transaction = transaction;
            inventory.CommandText = """
                INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
                VALUES($characterId, $cardCode, $quantity, $now)
                ON CONFLICT(CharacterId, CardCode) DO UPDATE SET
                    Quantity = excluded.Quantity,
                    UpdatedAt = excluded.UpdatedAt
                """;
            inventory.Parameters.AddWithValue("$characterId", recipientCharacterId);
            inventory.Parameters.AddWithValue("$cardCode", cardCode);
            inventory.Parameters.AddWithValue("$quantity", newQuantity);
            inventory.Parameters.AddWithValue("$now", now);
            await inventory.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, true, false, false, string.Empty, newQuantity, hans, cash);
    }

    public async Task<(
        bool Success,
        bool RecipientFound,
        bool GenderMismatch,
        bool QuantityLimit,
        bool InsufficientBalance,
        string Error,
        ushort RecipientInboxQuantity,
        long Hans,
        long Cash)> GiftShopItemAsync(
        long senderAccountId,
        long senderCharacterId,
        string sessionId,
        string recipientCharacterName,
        uint itemCode,
        byte quantity,
        uint unitPrice,
        bool payWithCash,
        int? requiredRecipientGender,
        CancellationToken cancellationToken = default)
    {
        recipientCharacterName = recipientCharacterName.Trim();
        if (senderAccountId <= 0
            || senderCharacterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || recipientCharacterName.Length is < 1 or > 16
            || recipientCharacterName.Any(char.IsControl)
            || quantity == 0
            || unitPrice == 0
            || !ShopCatalog.TryGet(itemCode, out _)
            || requiredRecipientGender is < 0 or > 1)
            return (false, true, false, false, false, "商城赠送参数无效。", 0, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        long hans;
        long cash;
        await using (var sender = connection.CreateCommand())
        {
            sender.Transaction = transaction;
            sender.CommandText = """
                SELECT character.Hans, character.Cash
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                LIMIT 1
                """;
            sender.Parameters.AddWithValue("$characterId", senderCharacterId);
            sender.Parameters.AddWithValue("$accountId", senderAccountId);
            sender.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await sender.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, true, false, false, false, "商城赠送在线会话已失效。", 0, 0, 0);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        long recipientCharacterId;
        int recipientGender;
        await using (var recipient = connection.CreateCommand())
        {
            recipient.Transaction = transaction;
            recipient.CommandText = """
                SELECT Id, Gender
                FROM Characters
                WHERE Name = $name COLLATE NOCASE
                LIMIT 1
                """;
            recipient.Parameters.AddWithValue("$name", recipientCharacterName);
            await using var reader = await recipient.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, false, false, false, "收件角色不存在。", 0, hans, cash);
            }
            recipientCharacterId = reader.GetInt64(0);
            recipientGender = reader.GetInt32(1);
        }

        if (requiredRecipientGender is int requiredGender && recipientGender != requiredGender)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, true, true, false, false, "物品与收件角色性别不匹配。", 0, hans, cash);
        }

        long currentQuantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT Quantity FROM CharacterCashInboxItems WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            current.Parameters.AddWithValue("$characterId", recipientCharacterId);
            current.Parameters.AddWithValue("$itemCode", itemCode);
            currentQuantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (currentQuantity + quantity > ushort.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, true, false, true, false, "收件角色待领取物品数量已达上限。", ushort.MaxValue, hans, cash);
        }

        var totalPrice = checked((long)quantity * unitPrice);
        var availableBalance = payWithCash ? cash : hans;
        if (availableBalance < totalPrice)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, true, false, false, true, payWithCash ? "Cash 余额不足。" : "Hans 余额不足。", checked((ushort)currentQuantity), hans, cash);
        }

        var now = DateTime.UtcNow.ToString("O");
        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = payWithCash
                ? """
                    UPDATE Characters
                    SET Cash = Cash - $price, LastSavedAt = $now
                    WHERE Id = $characterId
                      AND AccountId = $accountId
                      AND IsOnline = 1
                      AND ActiveSessionId = $sessionId
                      AND Cash >= $price
                    RETURNING Hans, Cash
                    """
                : """
                    UPDATE Characters
                    SET Hans = Hans - $price, LastSavedAt = $now
                    WHERE Id = $characterId
                      AND AccountId = $accountId
                      AND IsOnline = 1
                      AND ActiveSessionId = $sessionId
                      AND Hans >= $price
                    RETURNING Hans, Cash
                    """;
            debit.Parameters.AddWithValue("$price", totalPrice);
            debit.Parameters.AddWithValue("$now", now);
            debit.Parameters.AddWithValue("$characterId", senderCharacterId);
            debit.Parameters.AddWithValue("$accountId", senderAccountId);
            debit.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await debit.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, true, false, false, false, "商城赠送扣款时会话或余额发生变化。", checked((ushort)currentQuantity), hans, cash);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        var newQuantity = checked((ushort)(currentQuantity + quantity));
        await using (var inbox = connection.CreateCommand())
        {
            inbox.Transaction = transaction;
            inbox.CommandText = """
                INSERT INTO CharacterCashInboxItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = excluded.Quantity,
                    UpdatedAt = excluded.UpdatedAt
                """;
            inbox.Parameters.AddWithValue("$characterId", recipientCharacterId);
            inbox.Parameters.AddWithValue("$itemCode", itemCode);
            inbox.Parameters.AddWithValue("$quantity", newQuantity);
            inbox.Parameters.AddWithValue("$now", now);
            await inbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, true, false, false, false, string.Empty, newQuantity, hans, cash);
    }

    public async Task<(bool Success, string Error, ushort InboxQuantity, ushort InventoryQuantity)> ClaimCashInboxItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId) || !ShopCatalog.TryGet(itemCode, out _))
            return (false, "待领取物品参数无效。", 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                SELECT 1
                FROM Characters c
                JOIN Accounts a ON a.Id = c.AccountId
                WHERE c.Id = $characterId
                  AND c.AccountId = $accountId
                  AND c.IsOnline = 1
                  AND c.ActiveSessionId = $sessionId
                  AND a.IsOnline = 1
                  AND a.ActiveSessionId = $sessionId
                """;
            session.Parameters.AddWithValue("$characterId", characterId);
            session.Parameters.AddWithValue("$accountId", accountId);
            session.Parameters.AddWithValue("$sessionId", sessionId);
            if (await session.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "商城在线会话已失效。", 0, 0);
            }
        }

        long inboxQuantity;
        long inventoryQuantity;
        await using (var quantities = connection.CreateCommand())
        {
            quantities.Transaction = transaction;
            quantities.CommandText = """
                SELECT
                    COALESCE((SELECT Quantity FROM CharacterCashInboxItems WHERE CharacterId = $characterId AND ItemCode = $itemCode), 0),
                    COALESCE((SELECT Quantity FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode), 0)
                """;
            quantities.Parameters.AddWithValue("$characterId", characterId);
            quantities.Parameters.AddWithValue("$itemCode", itemCode);
            await using var reader = await quantities.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            inboxQuantity = reader.GetInt64(0);
            inventoryQuantity = reader.GetInt64(1);
        }

        if (inboxQuantity <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "待领取列表中没有此物品。", 0, checked((ushort)inventoryQuantity));
        }
        if (inventoryQuantity >= ushort.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "正式背包中的物品数量已达到上限。", checked((ushort)inboxQuantity), ushort.MaxValue);
        }

        var newInboxQuantity = checked((ushort)(inboxQuantity - 1));
        var newInventoryQuantity = checked((ushort)(inventoryQuantity + 1));
        var now = DateTime.UtcNow.ToString("O");
        await using (var updateInbox = connection.CreateCommand())
        {
            updateInbox.Transaction = transaction;
            updateInbox.CommandText = newInboxQuantity == 0
                ? "DELETE FROM CharacterCashInboxItems WHERE CharacterId = $characterId AND ItemCode = $itemCode"
                : "UPDATE CharacterCashInboxItems SET Quantity = $quantity, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            updateInbox.Parameters.AddWithValue("$characterId", characterId);
            updateInbox.Parameters.AddWithValue("$itemCode", itemCode);
            if (newInboxQuantity != 0)
            {
                updateInbox.Parameters.AddWithValue("$quantity", newInboxQuantity);
                updateInbox.Parameters.AddWithValue("$now", now);
            }
            await updateInbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateInventory = connection.CreateCommand())
        {
            updateInventory.Transaction = transaction;
            updateInventory.CommandText = """
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, 1, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = CharacterItems.Quantity + 1,
                    UpdatedAt = excluded.UpdatedAt
                """;
            updateInventory.Parameters.AddWithValue("$characterId", characterId);
            updateInventory.Parameters.AddWithValue("$itemCode", itemCode);
            updateInventory.Parameters.AddWithValue("$now", now);
            await updateInventory.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, newInboxQuantity, newInventoryQuantity);
    }

    public async Task<(bool Success, ushort Quantity)> DeleteInventoryItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        InventorySection expectedSection,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || catalogItem.Section != expectedSection
            || expectedSection is InventorySection.Pet
            || expectedSection == InventorySection.GameItem && catalogItem.Category == 47)
            return (false, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        int gender;
        uint equippedPetItemCode;
        byte[] appearance;
        await using (var characterCommand = connection.CreateCommand())
        {
            characterCommand.Transaction = transaction;
            characterCommand.CommandText = """
                SELECT character.Gender, character.EquippedPetItemCode, character.Appearance
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            characterCommand.Parameters.AddWithValue("$characterId", characterId);
            characterCommand.Parameters.AddWithValue("$accountId", accountId);
            characterCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await characterCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (false, 0);
            }

            gender = reader.GetInt32(0);
            equippedPetItemCode = checked((uint)reader.GetInt64(1));
            appearance = ((byte[])reader[2]).Concat(new byte[36]).Take(36).ToArray();
        }

        long currentQuantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT Quantity
                FROM CharacterItems
                WHERE CharacterId = $characterId
                  AND ItemCode = $itemCode
                  AND Quantity > 0
                """;
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$itemCode", itemCode);
            currentQuantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (currentQuantity <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, 0);
        }

        var remaining = Math.Max(0L, currentQuantity - 1L);
        await using (var updateInventory = connection.CreateCommand())
        {
            updateInventory.Transaction = transaction;
            updateInventory.CommandText = remaining == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode"
                : "UPDATE CharacterItems SET Quantity = $quantity, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            updateInventory.Parameters.AddWithValue("$characterId", characterId);
            updateInventory.Parameters.AddWithValue("$itemCode", itemCode);
            if (remaining != 0)
            {
                updateInventory.Parameters.AddWithValue("$quantity", remaining);
                updateInventory.Parameters.AddWithValue("$now", now);
            }
            if (await updateInventory.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, checked((ushort)Math.Min(currentQuantity, ushort.MaxValue)));
            }
        }

        if (expectedSection == InventorySection.Clothing && remaining == 0)
        {
            var defaults = gender == 1 ? DefaultMaleAppearance : DefaultFemaleAppearance;
            for (var offset = 0; offset < 28; offset += sizeof(uint))
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(offset, sizeof(uint))) == itemCode)
                    defaults.AsSpan(offset, sizeof(uint)).CopyTo(appearance.AsSpan(offset, sizeof(uint)));
            }
            // EquippedPetItemCode is persisted in its own column. Keep the
            // stored appearance block canonical and inject the pet only when
            // building client payloads.
            appearance = NormalizeAppearanceForGender(appearance, gender, 0);
        }

        await using (var touchCharacter = connection.CreateCommand())
        {
            touchCharacter.Transaction = transaction;
            touchCharacter.CommandText = expectedSection == InventorySection.Clothing && remaining == 0
                ? "UPDATE Characters SET Appearance = $appearance, LastSavedAt = $now WHERE Id = $characterId"
                : "UPDATE Characters SET LastSavedAt = $now WHERE Id = $characterId";
            touchCharacter.Parameters.AddWithValue("$now", now);
            touchCharacter.Parameters.AddWithValue("$characterId", characterId);
            if (expectedSection == InventorySection.Clothing && remaining == 0)
                touchCharacter.Parameters.Add("$appearance", SqliteType.Blob).Value = appearance;
            if (await touchCharacter.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, checked((ushort)Math.Min(currentQuantity, ushort.MaxValue)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, checked((ushort)Math.Min(remaining, ushort.MaxValue)));
    }

    public async Task<bool> DeletePetItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || itemCode / 1_000_000 != 15)
            return false;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM CharacterItems
                WHERE CharacterId = $characterId
                  AND ItemCode = $itemCode
                  AND Quantity > 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Characters AS protectedCharacter
                      WHERE protectedCharacter.Id = $characterId
                        AND protectedCharacter.PetVariant BETWEEN 1 AND 3
                        AND $itemCode = 15000000 + protectedCharacter.PetVariant
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM Characters AS character
                      INNER JOIN Accounts AS account ON account.Id = character.AccountId
                      WHERE character.Id = $characterId
                        AND character.AccountId = $accountId
                        AND character.IsOnline = 1
                        AND character.ActiveSessionId = $sessionId
                        AND account.IsOnline = 1
                        AND account.ActiveSessionId = $sessionId
                  )
                """;
            delete.Parameters.AddWithValue("$characterId", characterId);
            delete.Parameters.AddWithValue("$itemCode", itemCode);
            delete.Parameters.AddWithValue("$accountId", accountId);
            delete.Parameters.AddWithValue("$sessionId", sessionId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var repairEquippedPet = connection.CreateCommand())
        {
            repairEquippedPet.Transaction = transaction;
            repairEquippedPet.CommandText = """
                UPDATE Characters
                SET EquippedPetItemCode = CASE
                        WHEN EquippedPetItemCode <> $itemCode THEN EquippedPetItemCode
                        WHEN PetVariant BETWEEN 1 AND 3 THEN 15000000 + PetVariant
                        ELSE COALESCE((
                            SELECT ItemCode
                            FROM CharacterItems
                            WHERE CharacterId = $characterId
                              AND Quantity > 0
                              AND ItemCode BETWEEN 15000000 AND 15999999
                            ORDER BY ItemCode
                            LIMIT 1
                        ), 0)
                    END,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            repairEquippedPet.Parameters.AddWithValue("$itemCode", itemCode);
            repairEquippedPet.Parameters.AddWithValue("$characterId", characterId);
            repairEquippedPet.Parameters.AddWithValue("$accountId", accountId);
            repairEquippedPet.Parameters.AddWithValue("$sessionId", sessionId);
            repairEquippedPet.Parameters.AddWithValue("$now", now);
            if (await repairEquippedPet.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SaveInventoryEquipmentAsync(
        long accountId,
        long characterId,
        string sessionId,
        IReadOnlyList<CharacterQuickSlotRecord> quickSlots,
        uint equippedPetItemCode,
        uint unequippedPetItemCode,
        byte[] appearance,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || quickSlots.Count > 6
            || appearance.Length != 36)
            return false;

        if (quickSlots.Any(slot => slot.Slot > 5 || slot.InventoryIndex > 83 || slot.ItemCode == 0)
            || quickSlots.Select(slot => slot.Slot).Distinct().Count() != quickSlots.Count
            || quickSlots.Select(slot => slot.InventoryIndex).Distinct().Count() != quickSlots.Count)
            return false;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var appearancePetItemCode = BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(28, 4));

        uint currentEquippedPet;
        uint quickSlotExpansionExpires;
        int petVariant;
        int gender;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT character.EquippedPetItemCode, character.PetVariant, character.Gender,
                       character.QuickSlotExpansionExpires
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
            currentEquippedPet = checked((uint)reader.GetInt64(0));
            petVariant = reader.GetInt32(1);
            gender = reader.GetInt32(2);
            quickSlotExpansionExpires = checked((uint)reader.GetInt64(3));
        }

        var expandedQuickSlotsActive = SkillSlotExpansionTime.TryDecode(
                quickSlotExpansionExpires, out var quickSlotExpiration)
            && quickSlotExpiration > DateTime.Now;
        if (!expandedQuickSlotsActive && quickSlots.Any(slot => slot.Slot >= 3))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var gameInventoryItemCodes = new List<uint>(84);
        await using (var inventory = connection.CreateCommand())
        {
            inventory.Transaction = transaction;
            inventory.CommandText = """
                SELECT ItemCode, Quantity
                FROM CharacterItems
                WHERE CharacterId = $characterId AND Quantity > 0
                ORDER BY ItemCode
                """;
            inventory.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await inventory.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken) && gameInventoryItemCodes.Count < 84)
            {
                var itemCode = checked((uint)reader.GetInt64(0));
                var quantity = reader.GetInt32(1);
                if (!ShopCatalog.TryGet(itemCode, out var catalogItem)
                    || catalogItem.Section != InventorySection.GameItem
                    || catalogItem.Category is 42 or 47)
                    continue;
                for (var quantityIndex = 0;
                     quantityIndex < quantity && gameInventoryItemCodes.Count < 84;
                     quantityIndex++)
                    gameInventoryItemCodes.Add(itemCode);
            }
        }

        foreach (var slot in quickSlots)
        {
            if (slot.InventoryIndex >= gameInventoryItemCodes.Count
                || gameInventoryItemCodes[slot.InventoryIndex] != slot.ItemCode
                || !ShopCatalog.TryGet(slot.ItemCode, out var catalogItem)
                || catalogItem.Section != InventorySection.GameItem
                || catalogItem.Category is not (14 or 21)
                || !catalogItem.QuickUsable)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        if (currentEquippedPet == 0 && petVariant is >= 1 and <= 3)
            currentEquippedPet = 15_000_000u + (uint)petVariant;

        var changesPet = equippedPetItemCode != 0 || unequippedPetItemCode != 0;
        if (!changesPet)
        {
            if (appearancePetItemCode != currentEquippedPet)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
            equippedPetItemCode = currentEquippedPet;
        }
        else
        {
            if ((unequippedPetItemCode != 0 && unequippedPetItemCode != currentEquippedPet)
                || equippedPetItemCode / 1_000_000 != 15
                || appearancePetItemCode != equippedPetItemCode)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            var tutorialPetItemCode = petVariant is >= 1 and <= 3
                ? 15_000_000u + (uint)petVariant
                : 0u;
            if (equippedPetItemCode != tutorialPetItemCode)
            {
                await using var ownership = connection.CreateCommand();
                ownership.Transaction = transaction;
                ownership.CommandText = """
                    SELECT COUNT(*)
                    FROM CharacterItems
                    WHERE CharacterId = $characterId
                      AND ItemCode = $itemCode
                      AND Quantity > 0
                    """;
                ownership.Parameters.AddWithValue("$characterId", characterId);
                ownership.Parameters.AddWithValue("$itemCode", equippedPetItemCode);
                if (Convert.ToInt32(await ownership.ExecuteScalarAsync(cancellationToken)) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }
        }

        var storedAppearance = NormalizeAppearanceForGender(appearance, gender, 0);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET Appearance = $appearance,
                    EquippedPetItemCode = $equippedPetItemCode,
                    PetLevel = COALESCE((
                        SELECT PetLevel FROM CharacterItems
                        WHERE CharacterId = $characterId AND ItemCode = $equippedPetItemCode
                    ), 0),
                    PetExperience = COALESCE((
                        SELECT PetExperience FROM CharacterItems
                        WHERE CharacterId = $characterId AND ItemCode = $equippedPetItemCode
                    ), 0),
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            update.Parameters.Add("$appearance", SqliteType.Blob).Value = storedAppearance;
            update.Parameters.AddWithValue("$equippedPetItemCode", equippedPetItemCode);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$accountId", accountId);
            update.Parameters.AddWithValue("$sessionId", sessionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }


        await using (var clearQuickSlots = connection.CreateCommand())
        {
            clearQuickSlots.Transaction = transaction;
            clearQuickSlots.CommandText = "DELETE FROM CharacterQuickSlots WHERE CharacterId = $characterId";
            clearQuickSlots.Parameters.AddWithValue("$characterId", characterId);
            await clearQuickSlots.ExecuteNonQueryAsync(cancellationToken);
        }
        var quickSlotUpdatedAt = DateTime.UtcNow.ToString("O");
        foreach (var slot in quickSlots)
        {
            await using var insertQuickSlot = connection.CreateCommand();
            insertQuickSlot.Transaction = transaction;
            insertQuickSlot.CommandText = """
                INSERT INTO CharacterQuickSlots(CharacterId, Slot, ItemCode, InventoryIndex, UpdatedAt)
                VALUES ($characterId, $slot, $itemCode, $inventoryIndex, $updatedAt)
                """;
            insertQuickSlot.Parameters.AddWithValue("$characterId", characterId);
            insertQuickSlot.Parameters.AddWithValue("$slot", slot.Slot);
            insertQuickSlot.Parameters.AddWithValue("$itemCode", slot.ItemCode);
            insertQuickSlot.Parameters.AddWithValue("$inventoryIndex", slot.InventoryIndex);
            insertQuickSlot.Parameters.AddWithValue("$updatedAt", quickSlotUpdatedAt);
            if (await insertQuickSlot.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<DungeonQuickItemConsumeResult> ConsumeDungeonQuickItemAsync(
        long sourceAccountId,
        long sourceCharacterId,
        string sourceSessionId,
        uint itemCode,
        ushort hpRestore,
        ushort mpRestore,
        IReadOnlyList<DungeonQuickItemTarget> targets,
        CancellationToken cancellationToken = default)
    {
        if (sourceAccountId <= 0
            || sourceCharacterId <= 0
            || string.IsNullOrEmpty(sourceSessionId)
            || itemCode == 0
            || targets.Count is < 1 or > 3
            || targets.Select(target => target.CharacterId).Distinct().Count() != targets.Count
            || !targets.Any(target => target.AccountId == sourceAccountId
                && target.CharacterId == sourceCharacterId
                && target.SessionId == sourceSessionId))
            return DungeonQuickItemConsumeResult.Failed;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            int quantity;
            await using (var source = connection.CreateCommand())
            {
                source.Transaction = transaction;
                source.CommandText = """
                    SELECT item.Quantity
                    FROM CharacterItems AS item
                    INNER JOIN Characters AS character ON character.Id = item.CharacterId
                    INNER JOIN Accounts AS account ON account.Id = character.AccountId
                    WHERE item.CharacterId = $characterId
                      AND item.ItemCode = $itemCode
                      AND item.Quantity > 0
                      AND character.AccountId = $accountId
                      AND character.IsOnline = 1
                      AND character.ActiveSessionId = $sessionId
                      AND account.IsOnline = 1
                      AND account.ActiveSessionId = $sessionId
                    LIMIT 1
                    """;
                source.Parameters.AddWithValue("$characterId", sourceCharacterId);
                source.Parameters.AddWithValue("$itemCode", itemCode);
                source.Parameters.AddWithValue("$accountId", sourceAccountId);
                source.Parameters.AddWithValue("$sessionId", sourceSessionId);
                var storedQuantity = await source.ExecuteScalarAsync(cancellationToken);
                if (storedQuantity is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return DungeonQuickItemConsumeResult.Failed;
                }
                quantity = Convert.ToInt32(storedQuantity, CultureInfo.InvariantCulture);
            }

            var targetStates = new List<(DungeonQuickItemTarget Target, int Hp, int Mp, int MaxHp, int MaxMp)>(targets.Count);
            foreach (var target in targets)
            {
                await using var state = connection.CreateCommand();
                state.Transaction = transaction;
                state.CommandText = """
                    SELECT character.CurrentHp, character.CurrentMp,
                           character.MaxHp, character.MaxMp
                    FROM Characters AS character
                    INNER JOIN Accounts AS account ON account.Id = character.AccountId
                    WHERE character.Id = $characterId
                      AND character.AccountId = $accountId
                      AND character.IsOnline = 1
                      AND character.ActiveSessionId = $sessionId
                      AND account.IsOnline = 1
                      AND account.ActiveSessionId = $sessionId
                    LIMIT 1
                    """;
                state.Parameters.AddWithValue("$characterId", target.CharacterId);
                state.Parameters.AddWithValue("$accountId", target.AccountId);
                state.Parameters.AddWithValue("$sessionId", target.SessionId);
                await using var reader = await state.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await reader.CloseAsync();
                    await transaction.RollbackAsync(cancellationToken);
                    return DungeonQuickItemConsumeResult.Failed;
                }
                targetStates.Add((
                    target,
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3)));
            }

            var targetResults = targetStates.Select(state =>
            {
                var currentHp = Math.Clamp(state.Hp, 0, Math.Max(0, state.MaxHp));
                var currentMp = Math.Clamp(state.Mp, 0, Math.Max(0, state.MaxMp));
                var nextHp = Math.Min(state.MaxHp, currentHp + hpRestore);
                var nextMp = Math.Min(state.MaxMp, currentMp + mpRestore);
                return new DungeonQuickItemTargetResult(
                    state.Target.CharacterId,
                    checked((ushort)Math.Clamp(nextHp - currentHp, 0, ushort.MaxValue)),
                    checked((ushort)Math.Clamp(nextMp - currentMp, 0, ushort.MaxValue)),
                    nextHp,
                    nextMp);
            }).ToArray();
            var now = DateTime.UtcNow.ToString("O");
            foreach (var result in targetResults)
            {
                await using var updateTarget = connection.CreateCommand();
                updateTarget.Transaction = transaction;
                updateTarget.CommandText = """
                    UPDATE Characters
                    SET CurrentHp = $currentHp,
                        CurrentMp = $currentMp,
                        LastSavedAt = $now
                    WHERE Id = $characterId
                    """;
                updateTarget.Parameters.AddWithValue("$currentHp", result.CurrentHp);
                updateTarget.Parameters.AddWithValue("$currentMp", result.CurrentMp);
                updateTarget.Parameters.AddWithValue("$now", now);
                updateTarget.Parameters.AddWithValue("$characterId", result.CharacterId);
                if (await updateTarget.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return DungeonQuickItemConsumeResult.Failed;
                }
            }

            var remainingQuantity = quantity - 1;
            await using (var consume = connection.CreateCommand())
            {
                consume.Transaction = transaction;
                consume.CommandText = remainingQuantity == 0
                    ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity"
                    : "UPDATE CharacterItems SET Quantity = $remaining, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity";
                consume.Parameters.AddWithValue("$characterId", sourceCharacterId);
                consume.Parameters.AddWithValue("$itemCode", itemCode);
                consume.Parameters.AddWithValue("$quantity", quantity);
                if (remainingQuantity != 0)
                {
                    consume.Parameters.AddWithValue("$remaining", remainingQuantity);
                    consume.Parameters.AddWithValue("$now", now);
                }
                if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return DungeonQuickItemConsumeResult.Failed;
                }
            }

            if (remainingQuantity == 0)
            {
                await using var clearQuickSlot = connection.CreateCommand();
                clearQuickSlot.Transaction = transaction;
                clearQuickSlot.CommandText = "DELETE FROM CharacterQuickSlots WHERE CharacterId = $characterId AND ItemCode = $itemCode";
                clearQuickSlot.Parameters.AddWithValue("$characterId", sourceCharacterId);
                clearQuickSlot.Parameters.AddWithValue("$itemCode", itemCode);
                await clearQuickSlot.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new DungeonQuickItemConsumeResult(
                true,
                checked((ushort)remainingQuantity),
                targetResults);
        }
        catch
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<(bool Success, string Error)> ApplyPetSpecialGemAsync(
        long accountId,
        long characterId,
        string sessionId,
        byte operation,
        uint petItemCode,
        byte accessoryPosition,
        uint specialItemCode,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || operation is not (3 or 4)
            || petItemCode / 1_000_000 != 15
            || accessoryPosition > 2
            || specialItemCode != (operation == 3 ? 18_000_001u : 18_000_002u))
            return (false, "invalid special-gem request");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var authorization = connection.CreateCommand())
        {
            authorization.Transaction = transaction;
            authorization.CommandText = """
                SELECT COUNT(*)
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                INNER JOIN CharacterItems AS pet
                    ON pet.CharacterId = character.Id AND pet.ItemCode = $petItemCode AND pet.Quantity > 0
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorization.Parameters.AddWithValue("$petItemCode", petItemCode);
            authorization.Parameters.AddWithValue("$characterId", characterId);
            authorization.Parameters.AddWithValue("$accountId", accountId);
            authorization.Parameters.AddWithValue("$sessionId", sessionId);
            if (Convert.ToInt32(await authorization.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "pet/session is not active");
            }
        }

        var accessories = new uint[3];
        await using (var readPet = connection.CreateCommand())
        {
            readPet.Transaction = transaction;
            readPet.CommandText = """
                SELECT PetAccessory0, PetAccessory1, PetAccessory2
                FROM CharacterItems
                WHERE CharacterId = $characterId AND ItemCode = $petItemCode AND Quantity > 0
                """;
            readPet.Parameters.AddWithValue("$characterId", characterId);
            readPet.Parameters.AddWithValue("$petItemCode", petItemCode);
            await using var reader = await readPet.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "pet state is missing");
            }
            for (var index = 0; index < accessories.Length; index++)
                accessories[index] = checked((uint)reader.GetInt64(index));
        }

        var removedAccessory = accessories[accessoryPosition];
        if (removedAccessory == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "selected accessory slot is empty");
        }

        var now = DateTime.UtcNow.ToString("O");
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE CharacterItems
                SET Quantity = Quantity - 1, UpdatedAt = $now
                WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity > 0
                """;
            consume.Parameters.AddWithValue("$characterId", characterId);
            consume.Parameters.AddWithValue("$itemCode", specialItemCode);
            consume.Parameters.AddWithValue("$now", now);
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "special item is not owned");
            }
        }

        for (var index = accessoryPosition; index + 1 < accessories.Length; index++)
            accessories[index] = accessories[index + 1];
        accessories[^1] = 0;
        await using (var updatePet = connection.CreateCommand())
        {
            updatePet.Transaction = transaction;
            updatePet.CommandText = """
                UPDATE CharacterItems
                SET PetAccessory0 = $accessory0, PetAccessory1 = $accessory1, PetAccessory2 = $accessory2,
                    UpdatedAt = $now
                WHERE CharacterId = $characterId AND ItemCode = $petItemCode AND Quantity > 0
                """;
            updatePet.Parameters.AddWithValue("$accessory0", accessories[0]);
            updatePet.Parameters.AddWithValue("$accessory1", accessories[1]);
            updatePet.Parameters.AddWithValue("$accessory2", accessories[2]);
            updatePet.Parameters.AddWithValue("$now", now);
            updatePet.Parameters.AddWithValue("$characterId", characterId);
            updatePet.Parameters.AddWithValue("$petItemCode", petItemCode);
            if (await updatePet.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "pet accessory update failed");
            }
        }

        if (operation == 3)
        {
            await using var returnGem = connection.CreateCommand();
            returnGem.Transaction = transaction;
            returnGem.CommandText = """
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, 1, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = MIN(65535, CharacterItems.Quantity + 1),
                    UpdatedAt = excluded.UpdatedAt
                """;
            returnGem.Parameters.AddWithValue("$characterId", characterId);
            returnGem.Parameters.AddWithValue("$itemCode", removedAccessory);
            returnGem.Parameters.AddWithValue("$now", now);
            await returnGem.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var removeEmpty = connection.CreateCommand())
        {
            removeEmpty.Transaction = transaction;
            removeEmpty.CommandText = """
                DELETE FROM CharacterItems
                WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = 0
                """;
            removeEmpty.Parameters.AddWithValue("$characterId", characterId);
            removeEmpty.Parameters.AddWithValue("$itemCode", specialItemCode);
            await removeEmpty.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Error)> ChangePetItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        byte operation,
        uint petItemCode,
        byte accessoryPosition,
        uint materialItemCode,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || operation is not (1 or 2)
            || petItemCode / 1_000_000 != 15
            || materialItemCode == 0)
            return (false, "invalid request");
        if (!ShopCatalog.TryGet(15, petItemCode, out var petCatalogItem))
            return (false, "unknown pet");
        if (!ShopCatalog.TryGet(materialItemCode, out var materialItem)
            || materialItem.Section != InventorySection.GameItem)
            return (false, "unknown material");
        if (operation == 1 && (accessoryPosition > 2 || materialItem.Category != 17))
            return (false, "invalid accessory");
        if (operation == 2
            && (materialItem.Category != 19
                || petCatalogItem.PetGoldDustItemCode == 0
                || materialItemCode != petCatalogItem.PetGoldDustItemCode))
            return (false, "incompatible gold dust");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        int petVariant;
        await using (var authorization = connection.CreateCommand())
        {
            authorization.Transaction = transaction;
            authorization.CommandText = """
                SELECT character.PetVariant
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorization.Parameters.AddWithValue("$characterId", characterId);
            authorization.Parameters.AddWithValue("$accountId", accountId);
            authorization.Parameters.AddWithValue("$sessionId", sessionId);
            var result = await authorization.ExecuteScalarAsync(cancellationToken);
            if (result is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "session is not active");
            }
            petVariant = Convert.ToInt32(result);
        }

        var tutorialPetItemCode = petVariant is >= 1 and <= 3
            ? 15_000_000u + (uint)petVariant
            : 0u;
        if (petItemCode != tutorialPetItemCode)
        {
            await using var petOwnership = connection.CreateCommand();
            petOwnership.Transaction = transaction;
            petOwnership.CommandText = """
                SELECT COUNT(*) FROM CharacterItems
                WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity > 0
                """;
            petOwnership.Parameters.AddWithValue("$characterId", characterId);
            petOwnership.Parameters.AddWithValue("$itemCode", petItemCode);
            if (Convert.ToInt32(await petOwnership.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "pet is not owned");
            }
        }

        var now = DateTime.UtcNow.ToString("O");
        if (petItemCode == tutorialPetItemCode)
        {
            await using var ensureTutorialPet = connection.CreateCommand();
            ensureTutorialPet.Transaction = transaction;
            ensureTutorialPet.CommandText = """
                INSERT INTO CharacterItems(
                    CharacterId, ItemCode, Quantity, PetCurrentStage, PetMaximumStage,
                    PetLevel, PetExperience, UpdatedAt)
                VALUES($characterId, $itemCode, 1, $currentStage, $maximumStage, 0, 0, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = MAX(1, CharacterItems.Quantity),
                    PetCurrentStage = CASE WHEN CharacterItems.PetCurrentStage = 0 THEN excluded.PetCurrentStage ELSE CharacterItems.PetCurrentStage END,
                    PetMaximumStage = CASE WHEN CharacterItems.PetMaximumStage = 0 THEN excluded.PetMaximumStage ELSE CharacterItems.PetMaximumStage END,
                    UpdatedAt = excluded.UpdatedAt
                """;
            ensureTutorialPet.Parameters.AddWithValue("$characterId", characterId);
            ensureTutorialPet.Parameters.AddWithValue("$itemCode", petItemCode);
            ensureTutorialPet.Parameters.AddWithValue("$currentStage", petCatalogItem.PetModelStage);
            ensureTutorialPet.Parameters.AddWithValue("$maximumStage", petCatalogItem.PetUpgradeStage);
            ensureTutorialPet.Parameters.AddWithValue("$now", now);
            await ensureTutorialPet.ExecuteNonQueryAsync(cancellationToken);
        }

        byte currentStage;
        byte maximumStage;
        uint oldAccessory;
        await using (var petState = connection.CreateCommand())
        {
            petState.Transaction = transaction;
            var accessoryColumn = accessoryPosition switch
            {
                0 => "PetAccessory0",
                1 => "PetAccessory1",
                _ => "PetAccessory2"
            };
            petState.CommandText = $"""
                SELECT PetCurrentStage, PetMaximumStage, {accessoryColumn}
                FROM CharacterItems
                WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity > 0
                """;
            petState.Parameters.AddWithValue("$characterId", characterId);
            petState.Parameters.AddWithValue("$itemCode", petItemCode);
            await using var reader = await petState.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "pet state is missing");
            }
            currentStage = reader.GetInt32(0) > 0
                ? checked((byte)reader.GetInt32(0))
                : petCatalogItem.PetModelStage;
            maximumStage = reader.GetInt32(1) > 0
                ? checked((byte)reader.GetInt32(1))
                : petCatalogItem.PetUpgradeStage;
            oldAccessory = checked((uint)reader.GetInt64(2));
        }

        if (operation == 2 && maximumStage >= 3)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "pet is already fully strengthened");
        }

        await using (var consumeMaterial = connection.CreateCommand())
        {
            consumeMaterial.Transaction = transaction;
            consumeMaterial.CommandText = """
                UPDATE CharacterItems
                SET Quantity = Quantity - 1, UpdatedAt = $now
                WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity > 0
                """;
            consumeMaterial.Parameters.AddWithValue("$characterId", characterId);
            consumeMaterial.Parameters.AddWithValue("$itemCode", materialItemCode);
            consumeMaterial.Parameters.AddWithValue("$now", now);
            if (await consumeMaterial.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "material is not owned");
            }
        }

        await using (var updatePet = connection.CreateCommand())
        {
            updatePet.Transaction = transaction;
            var accessoryAssignment = accessoryPosition switch
            {
                0 => "PetAccessory0 = $materialItemCode,",
                1 => "PetAccessory1 = $materialItemCode,",
                _ => "PetAccessory2 = $materialItemCode,"
            };
            updatePet.CommandText = operation == 1
                ? $"""
                    UPDATE CharacterItems
                    SET {accessoryAssignment}
                        PetCurrentStage = $currentStage,
                        PetMaximumStage = $maximumStage,
                        UpdatedAt = $now
                    WHERE CharacterId = $characterId AND ItemCode = $petItemCode AND Quantity > 0
                    """
                : """
                    UPDATE CharacterItems
                    SET PetCurrentStage = $maximumStage,
                        PetMaximumStage = 3,
                        UpdatedAt = $now
                    WHERE CharacterId = $characterId AND ItemCode = $petItemCode AND Quantity > 0
                    """;
            updatePet.Parameters.AddWithValue("$characterId", characterId);
            updatePet.Parameters.AddWithValue("$petItemCode", petItemCode);
            updatePet.Parameters.AddWithValue("$materialItemCode", materialItemCode);
            updatePet.Parameters.AddWithValue("$currentStage", currentStage);
            updatePet.Parameters.AddWithValue("$maximumStage", maximumStage);
            updatePet.Parameters.AddWithValue("$now", now);
            if (await updatePet.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "pet state update failed");
            }
        }

        if (operation == 1 && oldAccessory != 0)
        {
            await using var returnAccessory = connection.CreateCommand();
            returnAccessory.Transaction = transaction;
            returnAccessory.CommandText = """
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, 1, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                    Quantity = MIN(65535, CharacterItems.Quantity + 1),
                    UpdatedAt = excluded.UpdatedAt
                """;
            returnAccessory.Parameters.AddWithValue("$characterId", characterId);
            returnAccessory.Parameters.AddWithValue("$itemCode", oldAccessory);
            returnAccessory.Parameters.AddWithValue("$now", now);
            await returnAccessory.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var removeEmptyMaterial = connection.CreateCommand())
        {
            removeEmptyMaterial.Transaction = transaction;
            removeEmptyMaterial.CommandText = """
                DELETE FROM CharacterItems
                WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = 0
                """;
            removeEmptyMaterial.Parameters.AddWithValue("$characterId", characterId);
            removeEmptyMaterial.Parameters.AddWithValue("$itemCode", materialItemCode);
            await removeEmptyMaterial.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty);
    }

    public async Task<(bool Authorized, bool Granted)> EnsureApartmentStarterInventoryAsync(
        long accountId,
        long characterId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId))
            return (false, false);

        foreach (var itemCode in ApartmentStarterItemCodes)
        {
            if (!ShopCatalog.TryGet(itemCode, out var item)
                || item.Section != InventorySection.Furniture
                || item.HansPrice != 0
                || item.InteriorType > 4)
                throw new InvalidDataException($"Apartment starter item {itemCode} does not match inter._D3.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        long starterGranted;
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT character.ApartmentStarterGranted
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorize.Parameters.AddWithValue("$characterId", characterId);
            authorize.Parameters.AddWithValue("$accountId", accountId);
            authorize.Parameters.AddWithValue("$sessionId", sessionId);
            var result = await authorize.ExecuteScalarAsync(cancellationToken);
            if (result is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false);
            }
            starterGranted = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        if (starterGranted != 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return (true, false);
        }

        long ownedFurnitureCount;
        await using (var inventory = connection.CreateCommand())
        {
            inventory.Transaction = transaction;
            inventory.CommandText = "SELECT ItemCode FROM CharacterItems WHERE CharacterId = $characterId AND Quantity > 0";
            inventory.Parameters.AddWithValue("$characterId", characterId);
            ownedFurnitureCount = 0;
            await using var reader = await inventory.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var itemCode = checked((uint)reader.GetInt64(0));
                if (ShopCatalog.TryGet(itemCode, out var item) && item.Section == InventorySection.Furniture)
                {
                    ownedFurnitureCount++;
                    break;
                }
            }
        }

        var now = DateTime.UtcNow.ToString("O");
        if (ownedFurnitureCount == 0)
        {
            foreach (var itemCode in ApartmentStarterItemCodes)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                    VALUES($characterId, $itemCode, 1, $now)
                    ON CONFLICT(CharacterId, ItemCode) DO NOTHING
                    """;
                insert.Parameters.AddWithValue("$characterId", characterId);
                insert.Parameters.AddWithValue("$itemCode", itemCode);
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var markGranted = connection.CreateCommand())
        {
            markGranted.Transaction = transaction;
            markGranted.CommandText = """
                UPDATE Characters
                SET ApartmentStarterGranted = 1,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND ApartmentStarterGranted = 0
                """;
            markGranted.Parameters.AddWithValue("$now", now);
            markGranted.Parameters.AddWithValue("$characterId", characterId);
            markGranted.Parameters.AddWithValue("$accountId", accountId);
            if (await markGranted.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, ownedFurnitureCount == 0);
    }

    public async Task<IReadOnlyList<ApartmentPlacementRecord>> GetApartmentPlacementsAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return [];

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SlotIndex, ItemCode, PositionX, PositionY, Layer, Mirror, InteriorType
            FROM CharacterApartmentItems
            WHERE CharacterId = $characterId
            ORDER BY SlotIndex
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        var result = new List<ApartmentPlacementRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ApartmentPlacementRecord
            {
                SlotIndex = checked((byte)reader.GetInt32(0)),
                ItemCode = checked((uint)reader.GetInt64(1)),
                X = checked((short)reader.GetInt32(2)),
                Y = checked((short)reader.GetInt32(3)),
                Layer = checked((byte)reader.GetInt32(4)),
                Mirror = checked((byte)reader.GetInt32(5)),
                InteriorType = checked((byte)reader.GetInt32(6))
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<(uint ItemCode, ushort Quantity)>> GetInteriorWishlistAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return [];

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ItemCode, Quantity
            FROM CharacterInteriorWishlist
            WHERE CharacterId = $characterId
            ORDER BY AddedAt, ItemCode
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        var result = new List<(uint ItemCode, ushort Quantity)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemCode = checked((uint)reader.GetInt64(0));
            var quantity = checked((ushort)reader.GetInt32(1));
            if (ShopCatalog.TryGet(itemCode, out var item)
                && item.Category == 11
                && item.Section == InventorySection.Furniture)
                result.Add((itemCode, quantity));
        }
        return result;
    }

    public async Task<(bool Success, bool AlreadyExists, bool CapacityReached, string Error)> AddInteriorWishlistItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        int capacity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || capacity <= 0
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || catalogItem.Category != 11
            || catalogItem.Section != InventorySection.Furniture)
            return (false, false, false, "Invalid interior wishlist parameters.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT COUNT(*)
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorize.Parameters.AddWithValue("$characterId", characterId);
            authorize.Parameters.AddWithValue("$accountId", accountId);
            authorize.Parameters.AddWithValue("$sessionId", sessionId);
            if (Convert.ToInt64(await authorize.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, false, "Interior wishlist session is no longer valid.");
            }
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT COUNT(*) FROM CharacterInteriorWishlist WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            existing.Parameters.AddWithValue("$characterId", characterId);
            existing.Parameters.AddWithValue("$itemCode", itemCode);
            if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, true, false, "Interior item is already in the wishlist.");
            }
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM CharacterInteriorWishlist WHERE CharacterId = $characterId";
            count.Parameters.AddWithValue("$characterId", characterId);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= capacity)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, true, "Interior wishlist capacity has been reached.");
            }
        }

        var now = DateTime.UtcNow.ToString("O");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CharacterInteriorWishlist(CharacterId, ItemCode, Quantity, AddedAt)
                VALUES($characterId, $itemCode, 1, $now)
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$itemCode", itemCode);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE Characters SET LastSavedAt = $now WHERE Id = $characterId";
            touch.Parameters.AddWithValue("$now", now);
            touch.Parameters.AddWithValue("$characterId", characterId);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false, false, string.Empty);
    }

    public async Task<bool> DeleteInteriorWishlistItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || catalogItem.Category != 11
            || catalogItem.Section != InventorySection.Furniture)
            return false;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM CharacterInteriorWishlist
                WHERE CharacterId = $characterId
                  AND ItemCode = $itemCode
                  AND EXISTS (
                      SELECT 1
                      FROM Characters AS character
                      INNER JOIN Accounts AS account ON account.Id = character.AccountId
                      WHERE character.Id = $characterId
                        AND character.AccountId = $accountId
                        AND character.IsOnline = 1
                        AND character.ActiveSessionId = $sessionId
                        AND account.IsOnline = 1
                        AND account.ActiveSessionId = $sessionId
                  )
                """;
            delete.Parameters.AddWithValue("$characterId", characterId);
            delete.Parameters.AddWithValue("$itemCode", itemCode);
            delete.Parameters.AddWithValue("$accountId", accountId);
            delete.Parameters.AddWithValue("$sessionId", sessionId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = """
                UPDATE Characters
                SET LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            touch.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            touch.Parameters.AddWithValue("$characterId", characterId);
            touch.Parameters.AddWithValue("$accountId", accountId);
            touch.Parameters.AddWithValue("$sessionId", sessionId);
            if (await touch.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<(uint ItemCode, uint WishlistId)>> GetShopWishlistAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return [];

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ItemCode, Id
            FROM CharacterShopWishlist
            WHERE CharacterId = $characterId
            ORDER BY AddedAt, Id
            LIMIT 8
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        var result = new List<(uint ItemCode, uint WishlistId)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemCode = checked((uint)reader.GetInt64(0));
            var wishlistId = checked((uint)reader.GetInt64(1));
            if (ShopCatalog.TryGet(itemCode, out var item)
                && NetworkAdapterService.IsSupportedShopWishlistCategory(item.Category))
                result.Add((itemCode, wishlistId));
        }
        return result;
    }

    public async Task<IReadOnlyList<uint>> GetNanaWishlistAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return [];

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ItemCode
            FROM CharacterShopWishlist
            WHERE CharacterId = $characterId
              AND ItemCode >= 10000000
              AND ItemCode < 11000000
            ORDER BY AddedAt, Id
            LIMIT 8
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        var result = new List<uint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemCode = checked((uint)reader.GetInt64(0));
            if (ShopCatalog.TryGet(10, itemCode, out var item)
                && item.Section == InventorySection.Clothing)
                result.Add(itemCode);
        }
        return result;
    }

    public async Task<(bool Success, bool AlreadyExists, bool CapacityReached, string Error)> AddNanaWishlistItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        int capacity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || capacity <= 0
            || !ShopCatalog.TryGet(10, itemCode, out var catalogItem)
            || catalogItem.Section != InventorySection.Clothing)
            return (false, false, false, "Invalid NaNa wishlist parameters.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT 1
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorize.Parameters.AddWithValue("$characterId", characterId);
            authorize.Parameters.AddWithValue("$accountId", accountId);
            authorize.Parameters.AddWithValue("$sessionId", sessionId);
            if (await authorize.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, false, "NaNa wishlist session is no longer valid.");
            }
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT 1 FROM CharacterShopWishlist WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            existing.Parameters.AddWithValue("$characterId", characterId);
            existing.Parameters.AddWithValue("$itemCode", itemCode);
            if (await existing.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, true, false, "NaNa item is already in the wishlist.");
            }
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT COUNT(*)
                FROM CharacterShopWishlist
                WHERE CharacterId = $characterId
                  AND ItemCode >= 10000000
                  AND ItemCode < 11000000
                """;
            count.Parameters.AddWithValue("$characterId", characterId);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= capacity)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, true, "NaNa wishlist capacity has been reached.");
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CharacterShopWishlist(CharacterId, ItemCode, AddedAt)
                VALUES($characterId, $itemCode, $now)
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$itemCode", itemCode);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false, false, string.Empty);
    }

    public async Task<bool> DeleteNanaWishlistItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(10, itemCode, out var catalogItem)
            || catalogItem.Section != InventorySection.Clothing)
            return false;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM CharacterShopWishlist
            WHERE CharacterId = $characterId
              AND ItemCode = $itemCode
              AND EXISTS (
                  SELECT 1
                  FROM Characters AS character
                  INNER JOIN Accounts AS account ON account.Id = character.AccountId
                  WHERE character.Id = $characterId
                    AND character.AccountId = $accountId
                    AND character.IsOnline = 1
                    AND character.ActiveSessionId = $sessionId
                    AND account.IsOnline = 1
                    AND account.ActiveSessionId = $sessionId
              )
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$itemCode", itemCode);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<(bool Success, bool AlreadyExists, bool CapacityReached, uint WishlistId, string Error)> AddShopWishlistItemForAccountAsync(
        long accountId,
        uint itemCode,
        int capacity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || capacity <= 0
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || !NetworkAdapterService.IsSupportedShopWishlistCategory(catalogItem.Category))
            return (false, false, false, 0, "后台商城收藏参数无效。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long characterId;
        await using (var character = connection.CreateCommand())
        {
            character.Transaction = transaction;
            character.CommandText = "SELECT Id FROM Characters WHERE AccountId = $accountId LIMIT 1";
            character.Parameters.AddWithValue("$accountId", accountId);
            var value = await character.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, false, 0, "该账号尚未创建角色。");
            }
            characterId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT Id FROM CharacterShopWishlist WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            existing.Parameters.AddWithValue("$characterId", characterId);
            existing.Parameters.AddWithValue("$itemCode", itemCode);
            var existingId = await existing.ExecuteScalarAsync(cancellationToken);
            if (existingId is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, true, false, checked((uint)Convert.ToInt64(existingId, CultureInfo.InvariantCulture)), "该物品已经在商城收藏中。");
            }
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM CharacterShopWishlist WHERE CharacterId = $characterId";
            count.Parameters.AddWithValue("$characterId", characterId);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= capacity)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, true, 0, $"商城收藏最多保存 {capacity} 件物品。");
            }
        }

        var now = DateTime.UtcNow.ToString("O");
        long insertedId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CharacterShopWishlist(CharacterId, ItemCode, AddedAt)
                VALUES($characterId, $itemCode, $now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$itemCode", itemCode);
            insert.Parameters.AddWithValue("$now", now);
            insertedId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, false, false, checked((uint)insertedId), string.Empty);
    }

    public async Task<bool> DeleteShopWishlistItemForAccountAsync(
        long accountId,
        uint wishlistId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || wishlistId == 0)
            return false;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM CharacterShopWishlist
            WHERE Id = $wishlistId
              AND CharacterId = (SELECT Id FROM Characters WHERE AccountId = $accountId LIMIT 1)
            """;
        command.Parameters.AddWithValue("$wishlistId", wishlistId);
        command.Parameters.AddWithValue("$accountId", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<(bool Success, bool AlreadyExists, bool CapacityReached, uint WishlistId, string Error)> AddShopWishlistItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        int capacity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || capacity <= 0
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || !NetworkAdapterService.IsSupportedShopWishlistCategory(catalogItem.Category))
            return (false, false, false, 0, "Invalid shop wishlist parameters.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT COUNT(*)
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorize.Parameters.AddWithValue("$characterId", characterId);
            authorize.Parameters.AddWithValue("$accountId", accountId);
            authorize.Parameters.AddWithValue("$sessionId", sessionId);
            if (Convert.ToInt64(await authorize.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, false, 0, "Shop wishlist session is no longer valid.");
            }
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT Id FROM CharacterShopWishlist WHERE CharacterId = $characterId AND ItemCode = $itemCode";
            existing.Parameters.AddWithValue("$characterId", characterId);
            existing.Parameters.AddWithValue("$itemCode", itemCode);
            var existingId = await existing.ExecuteScalarAsync(cancellationToken);
            if (existingId is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, true, false, checked((uint)Convert.ToInt64(existingId, CultureInfo.InvariantCulture)), "Shop item is already in the wishlist.");
            }
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM CharacterShopWishlist WHERE CharacterId = $characterId";
            count.Parameters.AddWithValue("$characterId", characterId);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= capacity)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, false, true, 0, "Shop wishlist capacity has been reached.");
            }
        }

        var now = DateTime.UtcNow.ToString("O");
        long insertedId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CharacterShopWishlist(CharacterId, ItemCode, AddedAt)
                VALUES($characterId, $itemCode, $now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$itemCode", itemCode);
            insert.Parameters.AddWithValue("$now", now);
            insertedId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE Characters SET LastSavedAt = $now WHERE Id = $characterId";
            touch.Parameters.AddWithValue("$now", now);
            touch.Parameters.AddWithValue("$characterId", characterId);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false, false, checked((uint)insertedId), string.Empty);
    }

    public async Task<bool> DeleteShopWishlistItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        uint wishlistId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || wishlistId == 0)
            return false;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM CharacterShopWishlist
                WHERE Id = $wishlistId
                  AND CharacterId = $characterId
                  AND ItemCode = $itemCode
                  AND EXISTS (
                      SELECT 1
                      FROM Characters AS character
                      INNER JOIN Accounts AS account ON account.Id = character.AccountId
                      WHERE character.Id = $characterId
                        AND character.AccountId = $accountId
                        AND character.IsOnline = 1
                        AND character.ActiveSessionId = $sessionId
                        AND account.IsOnline = 1
                        AND account.ActiveSessionId = $sessionId
                  )
                """;
            delete.Parameters.AddWithValue("$wishlistId", wishlistId);
            delete.Parameters.AddWithValue("$characterId", characterId);
            delete.Parameters.AddWithValue("$itemCode", itemCode);
            delete.Parameters.AddWithValue("$accountId", accountId);
            delete.Parameters.AddWithValue("$sessionId", sessionId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ApplyApartmentChangesAsync(
        long accountId,
        long characterId,
        string sessionId,
        IReadOnlyList<ApartmentPlacementRecord> takeOn,
        IReadOnlyCollection<byte> takeOff,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId))
            return false;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT COUNT(*)
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            authorize.Parameters.AddWithValue("$characterId", characterId);
            authorize.Parameters.AddWithValue("$accountId", accountId);
            authorize.Parameters.AddWithValue("$sessionId", sessionId);
            if (Convert.ToInt64(await authorize.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        foreach (var slotIndex in takeOff.Distinct())
        {
            await using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM CharacterApartmentItems WHERE CharacterId = $characterId AND SlotIndex = $slotIndex";
            remove.Parameters.AddWithValue("$characterId", characterId);
            remove.Parameters.AddWithValue("$slotIndex", slotIndex);
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }

        var now = DateTime.UtcNow.ToString("O");
        foreach (var placement in takeOn.GroupBy(item => item.SlotIndex).Select(group => group.Last()))
        {
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO CharacterApartmentItems(
                    CharacterId, SlotIndex, ItemCode, PositionX, PositionY, Layer, Mirror, InteriorType, UpdatedAt)
                VALUES(
                    $characterId, $slotIndex, $itemCode, $positionX, $positionY, $layer, $mirror, $interiorType, $now)
                ON CONFLICT(CharacterId, SlotIndex) DO UPDATE SET
                    ItemCode = excluded.ItemCode,
                    PositionX = excluded.PositionX,
                    PositionY = excluded.PositionY,
                    Layer = excluded.Layer,
                    Mirror = excluded.Mirror,
                    InteriorType = excluded.InteriorType,
                    UpdatedAt = excluded.UpdatedAt
                """;
            upsert.Parameters.AddWithValue("$characterId", characterId);
            upsert.Parameters.AddWithValue("$slotIndex", placement.SlotIndex);
            upsert.Parameters.AddWithValue("$itemCode", placement.ItemCode);
            upsert.Parameters.AddWithValue("$positionX", placement.X);
            upsert.Parameters.AddWithValue("$positionY", placement.Y);
            upsert.Parameters.AddWithValue("$layer", placement.Layer);
            upsert.Parameters.AddWithValue("$mirror", placement.Mirror);
            upsert.Parameters.AddWithValue("$interiorType", placement.InteriorType);
            upsert.Parameters.AddWithValue("$now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE Characters SET LastSavedAt = $now WHERE Id = $characterId";
            touch.Parameters.AddWithValue("$now", now);
            touch.Parameters.AddWithValue("$characterId", characterId);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ApartmentPlacementRecord>> GetApartmentPlacementsByAccountAsync(
        long accountId,
        CancellationToken cancellationToken = default)
    {
        var character = await GetCharacterAsync(accountId, cancellationToken);
        return character is null
            ? []
            : await GetApartmentPlacementsAsync(character.Id, cancellationToken);
    }

    public async Task<bool> DeleteApartmentPlacementAsync(
        long accountId,
        byte slotIndex,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM CharacterApartmentItems
            WHERE SlotIndex = $slotIndex
              AND CharacterId = (SELECT Id FROM Characters WHERE AccountId = $accountId LIMIT 1)
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$slotIndex", slotIndex);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> ClearApartmentAsync(
        long accountId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM CharacterApartmentItems
            WHERE CharacterId = (SELECT Id FROM Characters WHERE AccountId = $accountId LIMIT 1)
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CharacterRecord?> GetCharacterAsync(long accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {CharacterColumns} FROM Characters WHERE AccountId = $accountId LIMIT 1";
        command.Parameters.AddWithValue("$accountId", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var character = ReadCharacter(reader);
        await reader.CloseAsync();
        character.Items = await GetCharacterItemsAsync(connection, character.Id, cancellationToken);
        character.CashInboxItems = await GetCharacterCashInboxItemsAsync(connection, character.Id, cancellationToken);
        character.QuickSlots = await GetCharacterQuickSlotsAsync(connection, character.Id, cancellationToken);
        character.DungeonGrade = await LoadDungeonGradeAsync(connection, character.Id, cancellationToken);
        return character;
    }

    public async Task<CharacterRecord?> GetCharacterByIdAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {CharacterColumns} FROM Characters WHERE Id = $characterId LIMIT 1";
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var character = ReadCharacter(reader);
        await reader.CloseAsync();
        character.Items = await GetCharacterItemsAsync(connection, character.Id, cancellationToken);
        character.CashInboxItems = await GetCharacterCashInboxItemsAsync(connection, character.Id, cancellationToken);
        character.QuickSlots = await GetCharacterQuickSlotsAsync(connection, character.Id, cancellationToken);
        character.DungeonGrade = await LoadDungeonGradeAsync(connection, character.Id, cancellationToken);
        return character;
    }

    public async Task<FriendRecommendationResult> RecommendFriendAsync(
        long requesterAccountId,
        string recommendedCharacterName,
        CancellationToken cancellationToken = default)
    {
        recommendedCharacterName = recommendedCharacterName.Trim();
        if (requesterAccountId <= 0 || recommendedCharacterName.Length == 0)
            return new FriendRecommendationResult(FriendRecommendationStatus.Nonexistent, null);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT RecommendedCharacterName FROM FriendRecommendations WHERE RequesterAccountId = $requesterAccountId LIMIT 1";
                existing.Parameters.AddWithValue("$requesterAccountId", requesterAccountId);
                if (await existing.ExecuteScalarAsync(cancellationToken) is string storedCharacterName)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new FriendRecommendationResult(FriendRecommendationStatus.Success, storedCharacterName);
                }
            }

            long recommendedAccountId;
            string canonicalCharacterName;
            await using (var target = connection.CreateCommand())
            {
                target.Transaction = transaction;
                target.CommandText = "SELECT AccountId, Name FROM Characters WHERE Name = $name COLLATE NOCASE LIMIT 1";
                target.Parameters.AddWithValue("$name", recommendedCharacterName);
                await using var reader = await target.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new FriendRecommendationResult(FriendRecommendationStatus.Nonexistent, null);
                }
                recommendedAccountId = reader.GetInt64(0);
                canonicalCharacterName = reader.GetString(1);
            }

            if (recommendedAccountId == requesterAccountId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new FriendRecommendationResult(FriendRecommendationStatus.Self, null);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO FriendRecommendations(
                        RequesterAccountId, RecommendedAccountId, RecommendedCharacterName, CreatedAt)
                    VALUES ($requesterAccountId, $recommendedAccountId, $recommendedCharacterName, $createdAt)
                    ON CONFLICT(RequesterAccountId) DO NOTHING
                    """;
                insert.Parameters.AddWithValue("$requesterAccountId", requesterAccountId);
                insert.Parameters.AddWithValue("$recommendedAccountId", recommendedAccountId);
                insert.Parameters.AddWithValue("$recommendedCharacterName", canonicalCharacterName);
                insert.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var stored = connection.CreateCommand())
            {
                stored.Transaction = transaction;
                stored.CommandText = "SELECT RecommendedCharacterName FROM FriendRecommendations WHERE RequesterAccountId = $requesterAccountId LIMIT 1";
                stored.Parameters.AddWithValue("$requesterAccountId", requesterAccountId);
                var storedCharacterName = await stored.ExecuteScalarAsync(cancellationToken) as string
                    ?? throw new InvalidOperationException("好友推荐记录未能持久化。");
                await transaction.CommitAsync(cancellationToken);
                return new FriendRecommendationResult(FriendRecommendationStatus.Success, storedCharacterName);
            }
        }
        catch
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<CharacterRecord>> GetCharactersAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CharacterRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Id, c.AccountId, c.Name, c.Gender, c.Face, c.Appearance, c.TutorialCompleted,
                   c.PetVariant, c.EquippedPetItemCode, c.PetLevel, c.PetExperience,
                   c.Level, c.Experience, c.AttributePoints, c.Strength, c.Vitality, c.Agility, c.Intelligence, c.Luck,
                   c.MaxHp, c.MaxMp, c.CurrentHp, c.CurrentMp,
                   c.SpawnMapId, c.SpawnX, c.SpawnY, c.CurrentMapId, c.CurrentTownPage, c.PositionX, c.PositionY,
                   c.CurrentChannelId, c.IsOnline, c.OnlineSince, c.LastOfflineAt, c.LastSavedAt, c.CreatedAt,
                   c.Hans, c.Cash, c.CardGuideStep, c.CardSummonCount, c.CardMysteryKeyCount, c.CardGoldenKeyCount,
                   c.SkillPoints, c.SelectedSkill0, c.SelectedSkill1, c.SkillSlotExpansionExpires,
                   c.MikeChannelUseCount, c.MikeGlobalUseCount,
                   c.RevivalUseCount,
                   c.AvatarInventoryExpansionExpires, c.PetInventoryExpansionExpires,
                   c.GameInventoryExpansionExpires, c.InteriorInventoryExpansionExpires,
                   c.QuickSlotExpansionExpires, c.FreeMagicExpansionExpires,
                   a.Username
            FROM Characters c
            INNER JOIN Accounts a ON a.Id = c.AccountId
            ORDER BY c.Id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var character = ReadCharacter(reader);
            character.Username = reader.GetString(55);
            result.Add(character);
        }
        return result;
    }

    public async Task<(bool Success, string Error, long CharacterId)> CreateCharacterAsync(
        long accountId,
        string name,
        int gender,
        int face,
        ReadOnlyMemory<byte> appearance,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 16 || name.Any(char.IsControl))
            return (false, "角色名长度需要在 1-16 个字符之间，且不能包含控制字符。", 0);
        var bytes = appearance.ToArray();
        if (bytes.Length != 36 || bytes.All(value => value == 0))
        {
            bytes = (gender == 1 ? DefaultMaleAppearance : DefaultFemaleAppearance).ToArray();
            face = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4));
        }

        var now = DateTime.UtcNow.ToString("O");
        var maxHp = CharacterProgression.CalculateMaxHp(1, 5);
        var maxMp = CharacterProgression.CalculateMaxMp(1, 5);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        try
        {
            var initialGrantClaimed = true;
            var isGm = false;
            var gmGrantClaimed = true;
            await using (var accountState = connection.CreateCommand())
            {
                accountState.Transaction = transaction;
                accountState.CommandText = "SELECT InitialGrantClaimed, IsGm, GmGrantClaimed FROM Accounts WHERE Id = $accountId LIMIT 1";
                accountState.Parameters.AddWithValue("$accountId", accountId);
                await using var reader = await accountState.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return (false, "账号不存在。", 0);
                initialGrantClaimed = reader.GetInt64(0) != 0;
                isGm = reader.GetInt64(1) != 0;
                gmGrantClaimed = reader.GetInt64(2) != 0;
            }

            var initialGrant = new InitialGrantSettings();
            if (!initialGrantClaimed)
            {
                await using var grantSettings = connection.CreateCommand();
                grantSettings.Transaction = transaction;
                grantSettings.CommandText = """
                    SELECT Key, Value
                    FROM AdapterSettings
                    WHERE Key IN ('InitialGrantHans', 'InitialGrantCash', 'InitialGrantSkillPoints')
                    """;
                await using var grantReader = await grantSettings.ExecuteReaderAsync(cancellationToken);
                while (await grantReader.ReadAsync(cancellationToken))
                {
                    var key = grantReader.GetString(0);
                    var value = grantReader.GetString(1);
                    if (key == "InitialGrantHans" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hans))
                        initialGrant.Hans = Math.Clamp(hans, 0L, (long)uint.MaxValue);
                    else if (key == "InitialGrantCash" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cash))
                        initialGrant.Cash = Math.Clamp(cash, 0L, (long)uint.MaxValue);
                    else if (key == "InitialGrantSkillPoints" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var skillPoints))
                        initialGrant.SkillPoints = Math.Clamp(skillPoints, 0, ushort.MaxValue);
                }
            }

            var gmGrant = isGm && !gmGrantClaimed
                ? await ReadGmGrantSettingsAsync(connection, transaction, cancellationToken)
                : new GmGrantSettings();

            await using (var duplicate = connection.CreateCommand())
            {
                duplicate.Transaction = transaction;
                duplicate.CommandText = "SELECT COUNT(*) FROM Characters WHERE Name = $name COLLATE NOCASE";
                duplicate.Parameters.AddWithValue("$name", name);
                if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(cancellationToken)) > 0)
                    return (false, "角色名已经被使用。", 0);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Characters(
                    AccountId, Name, Gender, Face, Appearance, TutorialCompleted,
                    PetVariant, PetLevel, PetExperience,
                    Level, Experience, AttributePoints, Strength, Vitality, Agility, Intelligence, Luck,
                    MaxHp, MaxMp, CurrentHp, CurrentMp,
                    SpawnMapId, SpawnX, SpawnY, CurrentMapId, CurrentTownPage, PositionX, PositionY,
                    Hans, Cash, SkillPoints, IsOnline, LastSavedAt, CreatedAt)
                VALUES (
                    $accountId, $name, $gender, $face, $appearance, 0,
                    0, 1, 0,
                    1, 0, 0, 5, 5, 5, 5, 5,
                    $maxHp, $maxMp, $maxHp, $maxMp,
                    $spawnMap, $spawnX, $spawnY, $tutorialMap, 0, $spawnX, $spawnY,
                    $hans, $cash, $skillPoints, 0, $now, $now)
                """;
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$gender", gender);
            command.Parameters.AddWithValue("$face", face);
            command.Parameters.Add("$appearance", SqliteType.Blob).Value = bytes;
            command.Parameters.AddWithValue("$maxHp", maxHp);
            command.Parameters.AddWithValue("$maxMp", maxMp);
            command.Parameters.AddWithValue("$spawnMap", DefaultSpawnMapId);
            command.Parameters.AddWithValue("$tutorialMap", TutorialMapId);
            command.Parameters.AddWithValue("$spawnX", DefaultSpawnX);
            command.Parameters.AddWithValue("$spawnY", DefaultSpawnY);
            command.Parameters.AddWithValue("$hans", initialGrant.Hans);
            command.Parameters.AddWithValue("$cash", initialGrant.Cash);
            command.Parameters.AddWithValue("$skillPoints", initialGrant.SkillPoints);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using var idCommand = connection.CreateCommand();
            idCommand.Transaction = transaction;
            idCommand.CommandText = "SELECT last_insert_rowid()";
            var id = Convert.ToInt64(await idCommand.ExecuteScalarAsync(cancellationToken));

            if (!initialGrantClaimed)
            {
                await using var claimGrant = connection.CreateCommand();
                claimGrant.Transaction = transaction;
                claimGrant.CommandText = "UPDATE Accounts SET InitialGrantClaimed = 1 WHERE Id = $accountId AND InitialGrantClaimed = 0";
                claimGrant.Parameters.AddWithValue("$accountId", accountId);
                if (await claimGrant.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (false, "新号初始赠送状态更新失败。", 0);
                }
            }

            if (isGm && !gmGrantClaimed)
            {
                await ApplyGmGrantToCharacterAsync(
                    connection,
                    transaction,
                    id,
                    gmGrant,
                    now,
                    cancellationToken);
                await using var claimGmGrant = connection.CreateCommand();
                claimGmGrant.Transaction = transaction;
                claimGmGrant.CommandText = "UPDATE Accounts SET GmGrantClaimed = 1 WHERE Id = $accountId AND GmGrantClaimed = 0";
                claimGmGrant.Parameters.AddWithValue("$accountId", accountId);
                if (await claimGmGrant.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (false, "GM 初始赠送状态更新失败。", 0);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return (true, string.Empty, id);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "该账号已经创建过角色。", 0);
        }
    }

    public async Task<bool> MarkTutorialCompletedAsync(
        long accountId,
        long characterId,
        string sessionId,
        int petVariant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Characters
                SET TutorialCompleted = 1,
                PetVariant = CASE
                    WHEN PetVariant BETWEEN 1 AND 3 THEN PetVariant
                    WHEN $petVariant BETWEEN 1 AND 3 THEN $petVariant
                    ELSE 1
                END,
                EquippedPetItemCode = CASE
                    WHEN EquippedPetItemCode BETWEEN 15000001 AND 15999999 THEN EquippedPetItemCode
                    WHEN PetVariant BETWEEN 1 AND 3 THEN 15000000 + PetVariant
                    WHEN $petVariant BETWEEN 1 AND 3 THEN 15000000 + $petVariant
                    ELSE 15000001
                END,
                PetLevel = MAX(0, PetLevel),
                CurrentMapId = CASE WHEN TutorialCompleted = 0 THEN SpawnMapId ELSE CurrentMapId END,
                CurrentTownPage = CASE WHEN TutorialCompleted = 0 THEN $townPage ELSE CurrentTownPage END,
                PositionX = CASE WHEN TutorialCompleted = 0 THEN SpawnX ELSE PositionX END,
                PositionY = CASE WHEN TutorialCompleted = 0 THEN SpawnY ELSE PositionY END,
                LastSavedAt = $now
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND IsOnline = 1
              AND ActiveSessionId = $sessionId
              AND EXISTS (
                  SELECT 1
                  FROM Accounts AS account
                  WHERE account.Id = $accountId
                    AND account.IsOnline = 1
                    AND account.ActiveSessionId = $sessionId
              )
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$petVariant", petVariant);
        command.Parameters.AddWithValue("$townPage", DefaultTownPage);
        var now = DateTime.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$now", now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using var createPetState = connection.CreateCommand();
        createPetState.Transaction = transaction;
        createPetState.CommandText = """
            INSERT INTO CharacterItems(
                CharacterId, ItemCode, Quantity, PetCurrentStage, PetMaximumStage,
                PetLevel, PetExperience, UpdatedAt)
            SELECT Id, 15000000 + PetVariant, 1, 1, 2, 0, 0, $now
            FROM Characters
            WHERE Id = $characterId AND PetVariant BETWEEN 1 AND 3
            ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                Quantity = MAX(1, CharacterItems.Quantity),
                PetCurrentStage = CASE WHEN CharacterItems.PetCurrentStage = 0 THEN 1 ELSE CharacterItems.PetCurrentStage END,
                PetMaximumStage = CASE WHEN CharacterItems.PetMaximumStage = 0 THEN 2 ELSE CharacterItems.PetMaximumStage END,
                UpdatedAt = excluded.UpdatedAt
            """;
        createPetState.Parameters.AddWithValue("$characterId", characterId);
        createPetState.Parameters.AddWithValue("$now", now);
        if (await createPetState.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<CharacterSkillRecord>> GetCharacterSkillsAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return [];

        var result = new List<CharacterSkillRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SkillCode, Grade FROM CharacterSkills WHERE CharacterId = $characterId ORDER BY SkillCode";
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var skillCode = checked((uint)reader.GetInt64(0));
            var grade = checked((byte)reader.GetInt32(1));
            if (SkillCatalog.TryGet(skillCode, out _) && grade is >= 1 and <= 5)
                result.Add(new CharacterSkillRecord { SkillCode = skillCode, Grade = grade });
        }
        return result;
    }

    public async Task<(bool Success, string Error, ushort Quantity, byte RevivalUseCount)>
        ActivateRevivalItemAsync(
            long accountId,
            long characterId,
            string sessionId,
            uint itemCode,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || catalogItem.Category != 48
            || !string.Equals(catalogItem.Source, "MI._D22", StringComparison.Ordinal)
            || catalogItem.TokenMode != 2
            || catalogItem.TokenUseCount == 0)
            return (false, "Invalid revival-item request.", 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long quantity;
        long revivalUseCount;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT item.Quantity, character.RevivalUseCount
                FROM CharacterItems AS item
                INNER JOIN Characters AS character ON character.Id = item.CharacterId
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE item.CharacterId = $characterId
                  AND item.ItemCode = $itemCode
                  AND item.Quantity > 0
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$itemCode", itemCode);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The online character does not own this revival item.", 0, 0);
            }
            quantity = reader.GetInt64(0);
            revivalUseCount = reader.GetInt64(1);
        }

        if (revivalUseCount + catalogItem.TokenUseCount > byte.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The revival counter is full.", checked((ushort)quantity), checked((byte)revivalUseCount));
        }

        var remaining = quantity - 1;
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = remaining == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity"
                : "UPDATE CharacterItems SET Quantity = $remaining, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity";
            consume.Parameters.AddWithValue("$characterId", characterId);
            consume.Parameters.AddWithValue("$itemCode", itemCode);
            consume.Parameters.AddWithValue("$quantity", quantity);
            if (remaining != 0)
            {
                consume.Parameters.AddWithValue("$remaining", remaining);
                consume.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The revival-item quantity changed.", checked((ushort)quantity), checked((byte)revivalUseCount));
            }
        }

        var newRevivalUseCount = checked((byte)(revivalUseCount + catalogItem.TokenUseCount));
        await using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = """
                UPDATE Characters
                SET RevivalUseCount = $newCount,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND RevivalUseCount = $currentCount
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            activate.Parameters.AddWithValue("$newCount", newRevivalUseCount);
            activate.Parameters.AddWithValue("$currentCount", revivalUseCount);
            activate.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            activate.Parameters.AddWithValue("$characterId", characterId);
            activate.Parameters.AddWithValue("$accountId", accountId);
            activate.Parameters.AddWithValue("$sessionId", sessionId);
            if (await activate.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The revival counter changed.", checked((ushort)quantity), checked((byte)revivalUseCount));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, checked((ushort)remaining), newRevivalUseCount);
    }

    public async Task<(bool Success, string Error, byte RevivalUseCount, int CurrentHp, int CurrentMp)>
        ConsumeRevivalRetryAsync(
            long accountId,
            long characterId,
            string sessionId,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId))
            return (false, "Invalid revival retry request.", 0, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Characters
            SET RevivalUseCount = RevivalUseCount - 1,
                CurrentHp = MaxHp,
                LastSavedAt = $now
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND IsOnline = 1
              AND ActiveSessionId = $sessionId
              AND CurrentHp = 0
              AND RevivalUseCount > 0
              AND EXISTS (
                  SELECT 1
                  FROM Accounts AS account
                  WHERE account.Id = $accountId
                    AND account.IsOnline = 1
                    AND account.ActiveSessionId = $sessionId
              )
            RETURNING RevivalUseCount, CurrentHp, CurrentMp
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return (false, "No current death latch or activated revival use.", 0, 0, 0);
        return (
            true,
            string.Empty,
            checked((byte)reader.GetInt32(0)),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    public async Task<(bool Success, string Error, long Hans, byte RevivalUseCount, int CurrentHp, int CurrentMp)>
        ConsumeDungeonContinueAsync(
            long accountId,
            long characterId,
            string sessionId,
            ushort mode,
            ushort hansCost,
            int restoredHp,
            int restoredMp,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || mode is not (0 or 1)
            || mode == 0 && hansCost == 0
            || restoredHp <= 0
            || restoredMp <= 0)
            return (false, "Invalid dungeon-continue request.", 0, 0, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = mode == 0
            ? """
              UPDATE Characters
              SET Hans = Hans - $hansCost,
                  CurrentHp = MIN(MaxHp, $restoredHp),
                  CurrentMp = MIN(MaxMp, $restoredMp),
                  LastSavedAt = $now
              WHERE Id = $characterId
                AND AccountId = $accountId
                AND IsOnline = 1
                AND ActiveSessionId = $sessionId
                AND Hans >= $hansCost
                AND EXISTS (
                    SELECT 1
                    FROM Accounts AS account
                    WHERE account.Id = $accountId
                      AND account.IsOnline = 1
                      AND account.ActiveSessionId = $sessionId
                )
              RETURNING Hans, RevivalUseCount, CurrentHp, CurrentMp
              """
            : """
              UPDATE Characters
              SET RevivalUseCount = RevivalUseCount - 1,
                  CurrentHp = MIN(MaxHp, $restoredHp),
                  CurrentMp = MIN(MaxMp, $restoredMp),
                  LastSavedAt = $now
              WHERE Id = $characterId
                AND AccountId = $accountId
                AND IsOnline = 1
                AND ActiveSessionId = $sessionId
                AND RevivalUseCount > 0
                AND EXISTS (
                    SELECT 1
                    FROM Accounts AS account
                    WHERE account.Id = $accountId
                      AND account.IsOnline = 1
                      AND account.ActiveSessionId = $sessionId
                )
              RETURNING Hans, RevivalUseCount, CurrentHp, CurrentMp
              """;
        command.Parameters.AddWithValue("$hansCost", hansCost);
        command.Parameters.AddWithValue("$restoredHp", restoredHp);
        command.Parameters.AddWithValue("$restoredMp", restoredMp);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return mode == 0
                ? (false, "The online character does not have enough Hans.", 0L, (byte)0, 0, 0)
                : (false, "The online character has no activated revival uses.", 0L, (byte)0, 0, 0);
        }

        return (
            true,
            string.Empty,
            reader.GetInt64(0),
            checked((byte)reader.GetInt32(1)),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    public async Task<(bool Success, string Error, ushort Quantity, byte[] Appearance)>
        UseFaceCouponAsync(
            long accountId,
            long characterId,
            string sessionId,
            uint itemCode,
            byte[] requestedAppearance,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || requestedAppearance.Length != 36
            || !ShopCatalog.TryGet(itemCode, out var coupon)
            || coupon.Category != 45
            || !string.Equals(coupon.Source, "SF._D21", StringComparison.Ordinal))
            return (false, "Invalid face-coupon request.", 0, []);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        int gender;
        uint equippedPetItemCode;
        byte[] currentAppearance;
        long quantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT character.Gender, character.EquippedPetItemCode,
                       character.Appearance, item.Quantity
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                INNER JOIN CharacterItems AS item
                    ON item.CharacterId = character.Id
                   AND item.ItemCode = $itemCode
                   AND item.Quantity > 0
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$itemCode", itemCode);
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The face coupon or active session was not found.", 0, []);
            }
            gender = reader.GetInt32(0);
            equippedPetItemCode = checked((uint)reader.GetInt64(1));
            currentAppearance = ((byte[])reader[2]).Concat(new byte[36]).Take(36).ToArray();
            quantity = reader.GetInt64(3);
        }

        var normalizedRequested = NormalizeAppearanceForGender(
            requestedAppearance,
            gender,
            equippedPetItemCode);
        if (!normalizedRequested.AsSpan().SequenceEqual(requestedAppearance))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The requested appearance is not valid for the character gender.", checked((ushort)quantity), []);
        }

        var normalizedCurrent = NormalizeAppearanceForGender(currentAppearance, gender, equippedPetItemCode);
        var requestedFace = BinaryPrimitives.ReadUInt32LittleEndian(normalizedRequested.AsSpan(4, sizeof(uint)));
        if (!coupon.FaceOptions.Contains(requestedFace))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The selected face is not provided by this coupon generation.", checked((ushort)quantity), []);
        }

        int[] immutableOffsets = [0, 8, 12, 16, 20, 24, 28, 32];
        foreach (var offset in immutableOffsets)
        {
            if (normalizedRequested.AsSpan(offset, sizeof(uint)).SequenceEqual(
                    normalizedCurrent.AsSpan(offset, sizeof(uint))))
                continue;
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The face-coupon request changed a non-face appearance field.", checked((ushort)quantity), []);
        }

        var remaining = quantity - 1;
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = remaining == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity"
                : "UPDATE CharacterItems SET Quantity = $remaining, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity";
            consume.Parameters.AddWithValue("$characterId", characterId);
            consume.Parameters.AddWithValue("$itemCode", itemCode);
            consume.Parameters.AddWithValue("$quantity", quantity);
            if (remaining != 0)
            {
                consume.Parameters.AddWithValue("$remaining", remaining);
                consume.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The face-coupon quantity changed.", checked((ushort)quantity), []);
            }
        }

        var storedAppearance = NormalizeAppearanceForGender(normalizedRequested, gender, 0);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET Appearance = $appearance,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            update.Parameters.Add("$appearance", SqliteType.Blob).Value = storedAppearance;
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$accountId", accountId);
            update.Parameters.AddWithValue("$sessionId", sessionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The character appearance changed.", checked((ushort)quantity), []);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, checked((ushort)remaining), normalizedRequested);
    }

    public async Task<(bool Success, string Error, uint Expiration, ushort RemainingQuantity)>
        UseInventoryExpansionAsync(
            long accountId,
            long characterId,
            string sessionId,
            uint itemCode,
            DateTime now,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || catalogItem.Section != InventorySection.GameItem
            || catalogItem.InventoryExpansionType > 6
            || catalogItem.DurationDays == 0)
            return (false, "Invalid inventory-expansion request.", 0, 0);

        var stateColumn = catalogItem.InventoryExpansionType switch
        {
            0 => "AvatarInventoryExpansionExpires",
            1 => "PetInventoryExpansionExpires",
            2 => "GameInventoryExpansionExpires",
            3 => "InteriorInventoryExpansionExpires",
            4 => "QuickSlotExpansionExpires",
            5 => "FreeMagicExpansionExpires",
            6 => "SkillSlotExpansionExpires",
            _ => throw new InvalidOperationException("Unsupported inventory-expansion type.")
        };

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        uint currentExpiration;
        long currentQuantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = $"""
                SELECT character.{stateColumn}, item.Quantity
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                INNER JOIN CharacterItems AS item
                    ON item.CharacterId = character.Id
                   AND item.ItemCode = $itemCode
                   AND item.Quantity > 0
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$itemCode", itemCode);
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The expansion ticket or active session was not found.", 0, 0);
            }
            currentExpiration = checked((uint)reader.GetInt64(0));
            currentQuantity = reader.GetInt64(1);
        }

        uint newExpiration;
        try
        {
            newExpiration = SkillSlotExpansionTime.Extend(
                currentExpiration,
                catalogItem.DurationDays,
                now);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The expansion expiration cannot be extended further.", currentExpiration,
                checked((ushort)Math.Min(currentQuantity, ushort.MaxValue)));
        }

        var remaining = currentQuantity - 1;
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = remaining == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = 1"
                : "UPDATE CharacterItems SET Quantity = $remaining, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $currentQuantity";
            consume.Parameters.AddWithValue("$characterId", characterId);
            consume.Parameters.AddWithValue("$itemCode", itemCode);
            if (remaining != 0)
            {
                consume.Parameters.AddWithValue("$remaining", remaining);
                consume.Parameters.AddWithValue("$currentQuantity", currentQuantity);
                consume.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The expansion ticket quantity changed.", currentExpiration,
                    checked((ushort)Math.Min(currentQuantity, ushort.MaxValue)));
            }
        }

        await using (var save = connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = $"""
                UPDATE Characters
                SET {stateColumn} = $newExpiration,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND {stateColumn} = $currentExpiration
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            save.Parameters.AddWithValue("$newExpiration", newExpiration);
            save.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            save.Parameters.AddWithValue("$characterId", characterId);
            save.Parameters.AddWithValue("$accountId", accountId);
            save.Parameters.AddWithValue("$currentExpiration", currentExpiration);
            save.Parameters.AddWithValue("$sessionId", sessionId);
            if (await save.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The expansion state changed.", currentExpiration,
                    checked((ushort)Math.Min(currentQuantity, ushort.MaxValue)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, newExpiration, checked((ushort)remaining));
    }

    public async Task<(bool Success, string Error, ushort RemainingSkillPoints, byte Grade)>
        UpgradeCharacterSkillAsync(
            long accountId,
            long characterId,
            string sessionId,
            uint skillCode,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || !SkillCatalog.TryGet(skillCode, out var skill))
            return (false, "技能请求无效。", 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        int characterLevel;
        ushort currentSkillPoints;
        await using (var character = connection.CreateCommand())
        {
            character.Transaction = transaction;
            character.CommandText = """
                SELECT character.Level, character.SkillPoints
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            character.Parameters.AddWithValue("$characterId", characterId);
            character.Parameters.AddWithValue("$accountId", accountId);
            character.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await character.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "技能升级会话已经失效。", 0, 0);
            }
            characterLevel = reader.GetInt32(0);
            currentSkillPoints = checked((ushort)reader.GetInt32(1));
        }

        var learned = new Dictionary<uint, byte>();
        await using (var skills = connection.CreateCommand())
        {
            skills.Transaction = transaction;
            skills.CommandText = "SELECT SkillCode, Grade FROM CharacterSkills WHERE CharacterId = $characterId";
            skills.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await skills.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var storedCode = checked((uint)reader.GetInt64(0));
                var storedGrade = checked((byte)reader.GetInt32(1));
                if (SkillCatalog.TryGet(storedCode, out _) && storedGrade is >= 1 and <= 5)
                    learned[storedCode] = storedGrade;
            }
        }

        var currentGrade = learned.GetValueOrDefault(skillCode);
        var parentLearned = skill.ParentSkillCode == 0
            || learned.GetValueOrDefault(skill.ParentSkillCode) > 0;
        var conflictingBranch = skill.TreeTier >= 2 && learned.Keys.Any(existingCode =>
            SkillCatalog.TryGet(existingCode, out var existing)
            && existing.SkillFamily == skill.SkillFamily
            && existing.TreeTier >= 2
            && existing.BranchAtFork != skill.BranchAtFork);
        var cost = skill.GetUpgradeCost(currentGrade);
        if (characterLevel < skill.RequiredCharacterLevel
            || currentGrade >= 5
            || !parentLearned
            || conflictingBranch
            || (currentGrade == 0 && learned.Count >= 7)
            || cost > currentSkillPoints)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "角色等级、前置技能、技能分支或 SP 不满足升级条件。", currentSkillPoints, currentGrade);
        }

        var remainingSkillPoints = checked((ushort)(currentSkillPoints - cost));
        var newGrade = checked((byte)(currentGrade + 1));
        var now = DateTime.UtcNow.ToString("O");
        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = """
                UPDATE Characters
                SET SkillPoints = $remainingSkillPoints, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND SkillPoints = $currentSkillPoints
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            debit.Parameters.AddWithValue("$remainingSkillPoints", remainingSkillPoints);
            debit.Parameters.AddWithValue("$now", now);
            debit.Parameters.AddWithValue("$characterId", characterId);
            debit.Parameters.AddWithValue("$accountId", accountId);
            debit.Parameters.AddWithValue("$currentSkillPoints", currentSkillPoints);
            debit.Parameters.AddWithValue("$sessionId", sessionId);
            if (await debit.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "SP 已发生变化，请刷新后重试。", currentSkillPoints, currentGrade);
            }
        }

        await using (var upgrade = connection.CreateCommand())
        {
            upgrade.Transaction = transaction;
            upgrade.CommandText = """
                INSERT INTO CharacterSkills(CharacterId, SkillCode, Grade, UpdatedAt)
                VALUES($characterId, $skillCode, $newGrade, $now)
                ON CONFLICT(CharacterId, SkillCode) DO UPDATE SET
                    Grade = excluded.Grade,
                    UpdatedAt = excluded.UpdatedAt
                WHERE CharacterSkills.Grade = $currentGrade
                """;
            upgrade.Parameters.AddWithValue("$characterId", characterId);
            upgrade.Parameters.AddWithValue("$skillCode", skillCode);
            upgrade.Parameters.AddWithValue("$newGrade", newGrade);
            upgrade.Parameters.AddWithValue("$currentGrade", currentGrade);
            upgrade.Parameters.AddWithValue("$now", now);
            if (await upgrade.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "技能等级已发生变化，请刷新后重试。", currentSkillPoints, currentGrade);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, remainingSkillPoints, newGrade);
    }

    public async Task<(bool Success, string Error, uint Skill0, byte Grade0, uint Skill1, byte Grade1)>
        SaveCharacterSkillSlotsAsync(
            long accountId,
            long characterId,
            string sessionId,
            uint requestedSkill0,
            uint requestedSkill1,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId))
            return (false, "技能槽请求无效。", 0, 0, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        uint storedSkill0;
        uint storedSkill1;
        uint skillSlotExpansionExpires;
        await using (var character = connection.CreateCommand())
        {
            character.Transaction = transaction;
            character.CommandText = """
                SELECT character.SelectedSkill0, character.SelectedSkill1,
                       character.SkillSlotExpansionExpires
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            character.Parameters.AddWithValue("$characterId", characterId);
            character.Parameters.AddWithValue("$accountId", accountId);
            character.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await character.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "技能槽会话已经失效。", 0, 0, 0, 0);
            }
            storedSkill0 = checked((uint)reader.GetInt64(0));
            storedSkill1 = checked((uint)reader.GetInt64(1));
            skillSlotExpansionExpires = checked((uint)reader.GetInt64(2));
        }

        if (skillSlotExpansionExpires == 0 && storedSkill1 != 0)
        {
            // Older launcher imports could persist an X-slot skill without the
            // matching entitlement timestamp. Repair that split ledger before
            // validating the next C401 save.
            skillSlotExpansionExpires = 2_099_123_123u;
            await using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = "UPDATE Characters SET SkillSlotExpansionExpires=$expiration WHERE Id=$characterId AND SkillSlotExpansionExpires=0";
            migrate.Parameters.AddWithValue("$expiration", skillSlotExpansionExpires);
            migrate.Parameters.AddWithValue("$characterId", characterId);
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }

        var learned = new Dictionary<uint, byte>();
        await using (var skills = connection.CreateCommand())
        {
            skills.Transaction = transaction;
            skills.CommandText = "SELECT SkillCode, Grade FROM CharacterSkills WHERE CharacterId = $characterId";
            skills.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await skills.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                learned[checked((uint)reader.GetInt64(0))] = checked((byte)reader.GetInt32(1));
        }

        static bool IsValidSlot(uint code, IReadOnlyDictionary<uint, byte> learnedSkills)
            => code == 0
                || (SkillCatalog.TryGet(code, out _)
                    && learnedSkills.GetValueOrDefault(code) is >= 1 and <= 5);

        var requestValid = IsValidSlot(requestedSkill0, learned)
            && IsValidSlot(requestedSkill1, learned)
            && (requestedSkill1 == 0
                || (SkillSlotExpansionTime.TryDecode(
                        skillSlotExpansionExpires, out var skillSlotExpiration)
                    && skillSlotExpiration > DateTime.Now))
            && (requestedSkill0 == 0 || requestedSkill0 != requestedSkill1);
        if (!requestValid)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "只能装入已经学习的技能，两个技能槽不能重复。",
                storedSkill0, learned.GetValueOrDefault(storedSkill0),
                storedSkill1, learned.GetValueOrDefault(storedSkill1));
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET SelectedSkill0 = $skill0,
                    SelectedSkill1 = $skill1,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            update.Parameters.AddWithValue("$skill0", requestedSkill0);
            update.Parameters.AddWithValue("$skill1", requestedSkill1);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$accountId", accountId);
            update.Parameters.AddWithValue("$sessionId", sessionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "技能槽保存冲突，请刷新后重试。",
                    storedSkill0, learned.GetValueOrDefault(storedSkill0),
                    storedSkill1, learned.GetValueOrDefault(storedSkill1));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty,
            requestedSkill0, learned.GetValueOrDefault(requestedSkill0),
            requestedSkill1, learned.GetValueOrDefault(requestedSkill1));
    }

    public async Task<byte?> AdvanceCardGuideStepAsync(
        long accountId,
        long characterId,
        string sessionId,
        byte currentGuideStep,
        byte requestedGuideStep,
        CancellationToken cancellationToken = default)
    {
        if (currentGuideStep > 3
            || requestedGuideStep is 0 or > 3
            || requestedGuideStep < currentGuideStep)
            return null;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Characters
            SET CardGuideStep = CASE
                    WHEN CardGuideStep < $requestedGuideStep THEN $requestedGuideStep
                    ELSE CardGuideStep
                END,
                LastSavedAt = $now
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND IsOnline = 1
              AND ActiveSessionId = $sessionId
              AND EXISTS (
                  SELECT 1
                  FROM Accounts AS account
                  WHERE account.Id = $accountId
                    AND account.IsOnline = 1
                    AND account.ActiveSessionId = $sessionId
              )
            RETURNING CardGuideStep
            """;
        command.Parameters.AddWithValue("$requestedGuideStep", requestedGuideStep);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : checked((byte)Convert.ToInt32(result));
    }

    public async Task<bool> BeginWorldSessionAsync(
        long accountId,
        long characterId,
        string sessionId,
        int channelId,
        string? remoteIp,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow.ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var characterCommand = connection.CreateCommand();
        characterCommand.Transaction = transaction;
        characterCommand.CommandText = """
            UPDATE Characters
            SET IsOnline = 1,
                ActiveSessionId = $sessionId,
                CurrentChannelId = $channelId,
                OnlineSince = CASE WHEN IsOnline = 0 THEN $now ELSE OnlineSince END,
                LastSavedAt = $now
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND IsOnline = 0
              AND ActiveSessionId IS NULL
            """;
        characterCommand.Parameters.AddWithValue("$sessionId", sessionId);
        characterCommand.Parameters.AddWithValue("$channelId", channelId);
        characterCommand.Parameters.AddWithValue("$now", now);
        characterCommand.Parameters.AddWithValue("$characterId", characterId);
        characterCommand.Parameters.AddWithValue("$accountId", accountId);
        if (await characterCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using var accountCommand = connection.CreateCommand();
        accountCommand.Transaction = transaction;
        accountCommand.CommandText = """
            UPDATE Accounts
            SET IsOnline = 1,
                ActiveSessionId = $sessionId,
                CurrentChannelId = $channelId,
                OnlineSince = CASE WHEN IsOnline = 0 THEN $now ELSE OnlineSince END,
                LastIp = COALESCE($ip, LastIp)
            WHERE Id = $accountId
              AND IsBanned = 0
              AND IsOnline = 0
              AND ActiveSessionId IS NULL
            """;
        accountCommand.Parameters.AddWithValue("$sessionId", sessionId);
        accountCommand.Parameters.AddWithValue("$channelId", channelId);
        accountCommand.Parameters.AddWithValue("$now", now);
        accountCommand.Parameters.AddWithValue("$ip", (object?)remoteIp ?? DBNull.Value);
        accountCommand.Parameters.AddWithValue("$accountId", accountId);
        if (await accountCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SaveCharacterRuntimeStateAsync(
        long accountId,
        long characterId,
        string sessionId,
        CharacterRuntimeState state,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Characters
            SET CurrentHp = MIN(MaxHp, MAX(0, $currentHp)),
                CurrentMp = MIN(MaxMp, MAX(0, $currentMp)),
                CurrentMapId = MAX(0, $mapId),
                CurrentTownPage = MIN(255, MAX(0, $townPage)),
                PositionX = $positionX,
                PositionY = $positionY,
                CurrentChannelId = $channelId,
                LastSavedAt = $now
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND IsOnline = 1
              AND ActiveSessionId = $sessionId
            """;
        AddRuntimeStateParameters(command, characterId, state);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> EndWorldSessionAsync(
        long accountId,
        long characterId,
        string sessionId,
        CharacterRuntimeState state,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow.ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var characterUpdated = false;
        await using (var characterCommand = connection.CreateCommand())
        {
            characterCommand.Transaction = transaction;
            characterCommand.CommandText = """
                UPDATE Characters
                SET CurrentHp = MIN(MaxHp, MAX(0, $currentHp)),
                    CurrentMp = MIN(MaxMp, MAX(0, $currentMp)),
                    CurrentMapId = MAX(0, $mapId),
                    CurrentTownPage = MIN(255, MAX(0, $townPage)),
                    PositionX = $positionX,
                    PositionY = $positionY,
                    IsOnline = 0,
                    ActiveSessionId = NULL,
                    CurrentChannelId = NULL,
                    OnlineSince = NULL,
                    LastOfflineAt = $now,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            AddRuntimeStateParameters(characterCommand, characterId, state);
            characterCommand.Parameters.AddWithValue("$accountId", accountId);
            characterCommand.Parameters.AddWithValue("$sessionId", sessionId);
            characterCommand.Parameters["$now"].Value = now;
            characterUpdated = await characterCommand.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        var accountUpdated = false;
        await using (var accountCommand = connection.CreateCommand())
        {
            accountCommand.Transaction = transaction;
            accountCommand.CommandText = """
                UPDATE Accounts
                SET IsOnline = 0,
                    ActiveSessionId = NULL,
                    CurrentChannelId = NULL,
                    TrialPlayedSeconds = TrialPlayedSeconds + CASE
                        WHEN OnlineSince IS NOT NULL
                        THEN MAX(0, CAST((julianday($now) - julianday(OnlineSince)) * 86400 + 0.999 AS INTEGER))
                        ELSE 0
                    END,
                    OnlineSince = NULL,
                    LastOfflineAt = $now
                WHERE Id = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            accountCommand.Parameters.AddWithValue("$now", now);
            accountCommand.Parameters.AddWithValue("$accountId", accountId);
            accountCommand.Parameters.AddWithValue("$sessionId", sessionId);
            accountUpdated = await accountCommand.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        if (!characterUpdated || !accountUpdated)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<CharacterTaskRecord>> GetCharacterTasksAsync(
        long accountId,
        long characterId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrWhiteSpace(sessionId))
            return [];

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.QuestId, t.TaskType, t.RuntimeState, t.State3,
                   t.Progress1, t.Progress2, t.Progress3, t.SlotType
            FROM CharacterTasks t
            JOIN Characters c ON c.Id = t.CharacterId
            WHERE t.CharacterId = $characterId
              AND c.AccountId = $accountId
              AND c.IsOnline = 1
              AND c.ActiveSessionId = $sessionId
            ORDER BY t.SlotType, t.CreatedAt, t.QuestId
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var result = new List<CharacterTaskRecord>(12);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CharacterTaskRecord
            {
                QuestId = checked((uint)reader.GetInt64(0)),
                TaskType = checked((byte)reader.GetInt32(1)),
                RuntimeState = checked((byte)reader.GetInt32(2)),
                State3 = checked((byte)reader.GetInt32(3)),
                Progress1 = checked((ushort)reader.GetInt32(4)),
                Progress2 = checked((ushort)reader.GetInt32(5)),
                Progress3 = checked((uint)reader.GetInt64(6)),
                SlotType = checked((byte)reader.GetInt32(7))
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<CharacterTaskRecord>> GetCharacterTasksForAdminAsync(
        long accountId,
        long characterId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0)
            return [];

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.QuestId, t.TaskType, t.RuntimeState, t.State3,
                   t.Progress1, t.Progress2, t.Progress3, t.SlotType
            FROM CharacterTasks t
            JOIN Characters c ON c.Id = t.CharacterId
            WHERE t.CharacterId = $characterId
              AND c.AccountId = $accountId
            ORDER BY t.SlotType, t.CreatedAt, t.QuestId
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        var result = new List<CharacterTaskRecord>(12);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CharacterTaskRecord
            {
                QuestId = checked((uint)reader.GetInt64(0)),
                TaskType = checked((byte)reader.GetInt32(1)),
                RuntimeState = checked((byte)reader.GetInt32(2)),
                State3 = checked((byte)reader.GetInt32(3)),
                Progress1 = checked((ushort)reader.GetInt32(4)),
                Progress2 = checked((ushort)reader.GetInt32(5)),
                Progress3 = checked((uint)reader.GetInt64(6)),
                SlotType = checked((byte)reader.GetInt32(7))
            });
        }
        return result;
    }

    public async Task<(bool Success, string Error)> GrantQuestTaskFromAdminAsync(
        long accountId,
        long characterId,
        uint scrollCode,
        CancellationToken cancellationToken = default)
    {
        if (!QuestCatalog.TryGetScroll(scrollCode, out var scroll))
            return (false, "该任务卷轴不在官方 QH 目录中。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsOfflineAdminCharacterAsync(
                connection, transaction, accountId, characterId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "账号没有角色、角色不匹配或当前仍在线。");
        }

        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText =
                "SELECT 1 FROM CharacterTasks WHERE CharacterId = $characterId AND QuestId = $questId";
            duplicate.Parameters.AddWithValue("$characterId", characterId);
            duplicate.Parameters.AddWithValue("$questId", scroll.QuestId);
            if (await duplicate.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "该角色已经持有这个任务。");
            }
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText =
                "SELECT COUNT(*) FROM CharacterTasks WHERE CharacterId = $characterId AND SlotType = 0";
            count.Parameters.AddWithValue("$characterId", characterId);
            if (Convert.ToInt32(
                    await count.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) >= 10)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "普通任务栏已经达到客户端 10 格上限。");
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CharacterTasks(
                    CharacterId, QuestId, TaskType, RuntimeState, State3,
                    Progress1, Progress2, Progress3, SlotType, CreatedAt, UpdatedAt)
                VALUES($characterId, $questId, 3, 0, 0, 0, 0, 0, 0, $now, $now)
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$questId", scroll.QuestId);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "任务写入失败。");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Error)> ActivateQuestTaskFromAdminAsync(
        long accountId,
        long characterId,
        uint questId,
        CancellationToken cancellationToken = default)
    {
        if (!QuestCatalog.TryGetQuest(questId, out _))
            return (false, "任务不在官方 QT 目录中。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsOfflineAdminCharacterAsync(
                connection, transaction, accountId, characterId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "账号没有角色、角色不匹配或当前仍在线。");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE CharacterTasks
            SET Progress2 = 1,
                UpdatedAt = $now
            WHERE CharacterId = $characterId
              AND QuestId = $questId
              AND SlotType = 0
            """;
        update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$characterId", characterId);
        update.Parameters.AddWithValue("$questId", questId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "角色没有这个普通任务。");
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Error)> SetQuestTaskProgressFromAdminAsync(
        long accountId,
        long characterId,
        uint questId,
        uint progress,
        CancellationToken cancellationToken = default)
    {
        if (!QuestCatalog.TryGetMonsterHitObjective(questId, out var objective))
            return (false, "该任务不是已核实字段的 type-26 击打任务，不能在后台猜测进度语义。");
        if (progress > objective.RequiredCount)
            return (false, $"进度不能超过官方目标数量 {objective.RequiredCount:N0}。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsOfflineAdminCharacterAsync(
                connection, transaction, accountId, characterId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "账号没有角色、角色不匹配或当前仍在线。");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE CharacterTasks
            SET Progress3 = $progress,
                UpdatedAt = $now
            WHERE CharacterId = $characterId
              AND QuestId = $questId
              AND SlotType = 0
              AND Progress2 <> 0
            """;
        update.Parameters.AddWithValue("$progress", progress);
        update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$characterId", characterId);
        update.Parameters.AddWithValue("$questId", questId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "任务不存在或尚未开启，请先执行“开启任务”。");
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Error)> DeleteQuestTaskFromAdminAsync(
        long accountId,
        long characterId,
        uint questId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsOfflineAdminCharacterAsync(
                connection, transaction, accountId, characterId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "账号没有角色、角色不匹配或当前仍在线。");
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM CharacterTasks
            WHERE CharacterId = $characterId
              AND QuestId = $questId
              AND SlotType = 0
            """;
        delete.Parameters.AddWithValue("$characterId", characterId);
        delete.Parameters.AddWithValue("$questId", questId);
        if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "角色没有这个普通任务。");
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty);
    }

    public async Task<QuestScrollPurchaseResult> PurchaseQuestScrollAsync(
        long accountId,
        long characterId,
        string sessionId,
        QuestScrollDefinition scroll,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrWhiteSpace(sessionId)
            || !QuestCatalog.TryGetScroll(scroll.ScrollCode, out var officialScroll)
            || officialScroll.QuestId != scroll.QuestId
            || officialScroll.Price != scroll.Price)
            return new QuestScrollPurchaseResult(false, QuestScrollPurchaseStatus.AlreadyExists, 0);
        scroll = officialScroll;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long hans;
        await using (var character = connection.CreateCommand())
        {
            character.Transaction = transaction;
            character.CommandText = """
                SELECT Hans
                FROM Characters
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            character.Parameters.AddWithValue("$characterId", characterId);
            character.Parameters.AddWithValue("$accountId", accountId);
            character.Parameters.AddWithValue("$sessionId", sessionId);
            var value = await character.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new QuestScrollPurchaseResult(false, QuestScrollPurchaseStatus.AlreadyExists, 0);
            }
            hans = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT 1 FROM CharacterTasks WHERE CharacterId = $characterId AND QuestId = $questId";
            duplicate.Parameters.AddWithValue("$characterId", characterId);
            duplicate.Parameters.AddWithValue("$questId", scroll.QuestId);
            if (await duplicate.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new QuestScrollPurchaseResult(true, QuestScrollPurchaseStatus.AlreadyExists, hans);
            }
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM CharacterTasks WHERE CharacterId = $characterId AND SlotType = 0";
            count.Parameters.AddWithValue("$characterId", characterId);
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= 10)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new QuestScrollPurchaseResult(true, QuestScrollPurchaseStatus.TaskListFull, hans);
            }
        }

        if (hans < scroll.Price)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new QuestScrollPurchaseResult(true, QuestScrollPurchaseStatus.InsufficientHans, hans);
        }

        var now = DateTime.UtcNow.ToString("O");
        var remainingHans = hans - scroll.Price;
        await using (var wallet = connection.CreateCommand())
        {
            wallet.Transaction = transaction;
            wallet.CommandText = """
                UPDATE Characters
                SET Hans = $hans, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND ActiveSessionId = $sessionId
                  AND Hans >= $price
                """;
            wallet.Parameters.AddWithValue("$hans", remainingHans);
            wallet.Parameters.AddWithValue("$now", now);
            wallet.Parameters.AddWithValue("$characterId", characterId);
            wallet.Parameters.AddWithValue("$accountId", accountId);
            wallet.Parameters.AddWithValue("$sessionId", sessionId);
            wallet.Parameters.AddWithValue("$price", scroll.Price);
            if (await wallet.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new QuestScrollPurchaseResult(false, QuestScrollPurchaseStatus.InsufficientHans, hans);
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CharacterTasks(
                    CharacterId, QuestId, TaskType, RuntimeState, State3,
                    Progress1, Progress2, Progress3, SlotType, CreatedAt, UpdatedAt)
                VALUES($characterId, $questId, 3, 0, 0, 0, 0, 0, 0, $now, $now)
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$questId", scroll.QuestId);
            insert.Parameters.AddWithValue("$now", now);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new QuestScrollPurchaseResult(false, QuestScrollPurchaseStatus.AlreadyExists, hans);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new QuestScrollPurchaseResult(true, QuestScrollPurchaseStatus.Success, remainingHans);
    }

    public async Task<QuestTaskMutationResult> ActivateQuestTaskAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint questId,
        byte taskType,
        byte runtimeState,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsAuthorizedCharacterAsync(connection, transaction, accountId, characterId, sessionId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new QuestTaskMutationResult(false, false, null, false, 0);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE CharacterTasks
            SET Progress2 = 1, UpdatedAt = $now
            WHERE CharacterId = $characterId
              AND QuestId = $questId
              AND TaskType = $taskType
              AND RuntimeState = $runtimeState
              AND SlotType = 0
            """;
        update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$characterId", characterId);
        update.Parameters.AddWithValue("$questId", questId);
        update.Parameters.AddWithValue("$taskType", taskType);
        update.Parameters.AddWithValue("$runtimeState", runtimeState);
        var success = await update.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (success)
            await transaction.CommitAsync(cancellationToken);
        else
            await transaction.RollbackAsync(cancellationToken);
        return new QuestTaskMutationResult(true, success, null, false, 0);
    }

    public async Task<QuestTaskMutationResult> AbandonQuestTaskAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint questId,
        byte taskType,
        ushort runtimeState,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsAuthorizedCharacterAsync(connection, transaction, accountId, characterId, sessionId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new QuestTaskMutationResult(false, false, null, false, 0);
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM CharacterTasks
            WHERE CharacterId = $characterId
              AND QuestId = $questId
              AND TaskType = $taskType
              AND RuntimeState = $runtimeState
            """;
        delete.Parameters.AddWithValue("$characterId", characterId);
        delete.Parameters.AddWithValue("$questId", questId);
        delete.Parameters.AddWithValue("$taskType", taskType);
        delete.Parameters.AddWithValue("$runtimeState", runtimeState);
        var success = await delete.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (success)
            await transaction.CommitAsync(cancellationToken);
        else
            await transaction.RollbackAsync(cancellationToken);
        return new QuestTaskMutationResult(true, success, null, false, 0);
    }

    public async Task<QuestProgressMutationResult> AdvanceQuestMonsterHitAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint targetCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsAuthorizedCharacterAsync(
                connection, transaction, accountId, characterId, sessionId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new QuestProgressMutationResult(false, false, false, []);
        }

        var activeTasks = new List<(uint QuestId, uint Progress)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT QuestId, Progress3
                FROM CharacterTasks
                WHERE CharacterId = $characterId
                  AND SlotType = 0
                  AND Progress2 <> 0
                """;
            query.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                activeTasks.Add((
                    checked((uint)reader.GetInt64(0)),
                    checked((uint)reader.GetInt64(1))));
            }
        }

        var changed = false;
        var newlyCompleted = false;
        var now = DateTime.UtcNow.ToString("O");
        foreach (var task in activeTasks)
        {
            if (!QuestCatalog.TryGetMonsterHitObjective(task.QuestId, out var objective)
                || objective.TargetCode != targetCode
                || task.Progress >= objective.RequiredCount)
                continue;

            var progress = Math.Min(objective.RequiredCount, task.Progress + 1u);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE CharacterTasks
                SET Progress3 = $progress,
                    UpdatedAt = $now
                WHERE CharacterId = $characterId
                  AND QuestId = $questId
                  AND Progress3 = $oldProgress
                  AND SlotType = 0
                  AND Progress2 <> 0
                """;
            update.Parameters.AddWithValue("$progress", progress);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$questId", task.QuestId);
            update.Parameters.AddWithValue("$oldProgress", task.Progress);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new QuestProgressMutationResult(false, false, false, []);
            }
            changed = true;
            newlyCompleted |= progress >= objective.RequiredCount;
        }

        await transaction.CommitAsync(cancellationToken);
        var tasks = await GetCharacterTasksAsync(
            accountId, characterId, sessionId, cancellationToken);
        return new QuestProgressMutationResult(true, changed, newlyCompleted, tasks);
    }

    public async Task<QuestTaskMutationResult> CompleteQuestTaskAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint questId,
        byte taskType,
        ushort runtimeState,
        CancellationToken cancellationToken = default)
    {
        if (!QuestCatalog.TryGetQuest(questId, out var definition)
            || !QuestCatalog.TryGetMonsterHitObjective(questId, out var objective)
            || definition.Rewards.Any(reward => reward.RewardType is not 1 and not 7))
            return new QuestTaskMutationResult(true, false, null, false, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        int oldLevel;
        long oldExperience;
        long oldHans;
        int vitality;
        int intelligence;
        uint progress;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT c.Level, c.Experience, c.Hans, c.Vitality, c.Intelligence,
                       t.Progress3
                FROM Characters c
                JOIN CharacterTasks t ON t.CharacterId = c.Id
                WHERE c.Id = $characterId
                  AND c.AccountId = $accountId
                  AND c.IsOnline = 1
                  AND c.ActiveSessionId = $sessionId
                  AND t.QuestId = $questId
                  AND t.TaskType = $taskType
                  AND t.RuntimeState = $runtimeState
                  AND t.Progress2 <> 0
                  AND t.SlotType = 0
                """;
            query.Parameters.AddWithValue("$characterId", characterId);
            query.Parameters.AddWithValue("$accountId", accountId);
            query.Parameters.AddWithValue("$sessionId", sessionId);
            query.Parameters.AddWithValue("$questId", questId);
            query.Parameters.AddWithValue("$taskType", taskType);
            query.Parameters.AddWithValue("$runtimeState", runtimeState);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new QuestTaskMutationResult(true, false, null, false, 0);
            }
            oldLevel = reader.GetInt32(0);
            oldExperience = reader.GetInt64(1);
            oldHans = reader.GetInt64(2);
            vitality = reader.GetInt32(3);
            intelligence = reader.GetInt32(4);
            progress = checked((uint)reader.GetInt64(5));
        }

        if (progress < objective.RequiredCount)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new QuestTaskMutationResult(true, false, null, false, 0);
        }

        var hansReward = definition.Rewards
            .Where(reward => reward.RewardType == 1)
            .Aggregate(0L, (total, reward) => checked(total + reward.Amount));
        var experienceReward = definition.Rewards
            .Where(reward => reward.RewardType == 7)
            .Aggregate(0L, (total, reward) => checked(total + reward.Amount));
        var hans = Math.Min(uint.MaxValue, oldHans + hansReward);
        var experience = Math.Min(uint.MaxValue, oldExperience + experienceReward);
        var level = Math.Max(Math.Clamp(oldLevel, 1, CharacterProgression.MaximumLevel),
            CharacterProgression.CalculateLevel(experience));
        var gainedLevels = Math.Max(0, level - oldLevel);
        var maxHp = CharacterProgression.CalculateMaxHp(level, vitality);
        var maxMp = CharacterProgression.CalculateMaxMp(level, intelligence);
        var now = DateTime.UtcNow.ToString("O");

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET Hans = $hans,
                    Experience = $experience,
                    Level = $level,
                    AttributePoints = AttributePoints + $points,
                    MaxHp = $maxHp,
                    MaxMp = $maxMp,
                    CurrentHp = CASE WHEN $points > 0 THEN $maxHp ELSE MIN(CurrentHp, $maxHp) END,
                    CurrentMp = CASE WHEN $points > 0 THEN $maxMp ELSE MIN(CurrentMp, $maxMp) END,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND ActiveSessionId = $sessionId
                """;
            update.Parameters.AddWithValue("$hans", hans);
            update.Parameters.AddWithValue("$experience", experience);
            update.Parameters.AddWithValue("$level", level);
            update.Parameters.AddWithValue("$points", gainedLevels * CharacterProgression.AttributePointsPerLevel);
            update.Parameters.AddWithValue("$maxHp", maxHp);
            update.Parameters.AddWithValue("$maxMp", maxMp);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$accountId", accountId);
            update.Parameters.AddWithValue("$sessionId", sessionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new QuestTaskMutationResult(false, false, null, false, 0);
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM CharacterTasks WHERE CharacterId = $characterId AND QuestId = $questId";
            delete.Parameters.AddWithValue("$characterId", characterId);
            delete.Parameters.AddWithValue("$questId", questId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new QuestTaskMutationResult(true, false, null, false, 0);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        var character = await GetCharacterByIdAsync(characterId, cancellationToken);
        return new QuestTaskMutationResult(true, character is not null, character, hansReward > 0, gainedLevels);
    }

    private static async Task<bool> IsAuthorizedCharacterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        long characterId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM Characters
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND IsOnline = 1
              AND ActiveSessionId = $sessionId
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> IsOfflineAdminCharacterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        long characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM Characters c
            JOIN Accounts a ON a.Id = c.AccountId
            WHERE c.Id = $characterId
              AND c.AccountId = $accountId
              AND c.IsOnline = 0
              AND a.IsOnline = 0
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<CharacterRecord?> GrantExperienceAsync(
        long characterId,
        long amount,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
            return await GetCharacterByIdAsync(characterId, cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        int oldLevel;
        long oldExperience;
        int vitality;
        int intelligence;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT Level, Experience, Vitality, Intelligence FROM Characters WHERE Id = $id";
            query.Parameters.AddWithValue("$id", characterId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            oldLevel = reader.GetInt32(0);
            oldExperience = reader.GetInt64(1);
            vitality = reader.GetInt32(2);
            intelligence = reader.GetInt32(3);
        }

        var experience = amount > long.MaxValue - oldExperience ? long.MaxValue : oldExperience + amount;
        var level = CharacterProgression.CalculateLevel(experience);
        level = Math.Max(Math.Clamp(oldLevel, 1, CharacterProgression.MaximumLevel), level);
        var gainedLevels = Math.Max(0, level - oldLevel);
        var maxHp = CharacterProgression.CalculateMaxHp(level, vitality);
        var maxMp = CharacterProgression.CalculateMaxMp(level, intelligence);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET Experience = $experience,
                    Level = $level,
                    AttributePoints = AttributePoints + $points,
                    MaxHp = $maxHp,
                    MaxMp = $maxMp,
                    CurrentHp = CASE WHEN $points > 0 THEN $maxHp ELSE MIN(CurrentHp, $maxHp) END,
                    CurrentMp = CASE WHEN $points > 0 THEN $maxMp ELSE MIN(CurrentMp, $maxMp) END,
                    LastSavedAt = $now
                WHERE Id = $id
                """;
            update.Parameters.AddWithValue("$experience", experience);
            update.Parameters.AddWithValue("$level", level);
            update.Parameters.AddWithValue("$points", gainedLevels * CharacterProgression.AttributePointsPerLevel);
            update.Parameters.AddWithValue("$maxHp", maxHp);
            update.Parameters.AddWithValue("$maxMp", maxMp);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", characterId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetCharacterByIdAsync(characterId, cancellationToken);
    }

    public async Task<CharacterRecord?> ApplyDungeonRewardAsync(
        long accountId,
        long characterId,
        string sessionId,
        byte episode,
        byte dungeon,
        byte difficulty,
        int score,
        int elapsedMinutes,
        int experienceReward,
        int petExperienceReward,
        int hansReward,
        CancellationToken cancellationToken = default,
        bool completed = true,
        bool superBoss = false,
        byte clearRating = 0)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrWhiteSpace(sessionId)
            || episode >= 20 || dungeon >= 3 || difficulty >= 3
            || (superBoss && dungeon != 2)
            || clearRating > 5
            || score < 0 || elapsedMinutes < 0 || experienceReward < 0
            || petExperienceReward < 0 || hansReward < 0)
            return null;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        int oldLevel;
        long oldExperience;
        long oldHans;
        int vitality;
        int intelligence;
        int oldMaxHp;
        int oldMaxMp;
        int petVariant;
        uint equippedPetItemCode;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT Level, Experience, Hans, Vitality, Intelligence, MaxHp, MaxMp,
                       PetVariant, EquippedPetItemCode
                FROM Characters
                WHERE Id = $characterId AND AccountId = $accountId AND ActiveSessionId = $sessionId
                """;
            query.Parameters.AddWithValue("$characterId", characterId);
            query.Parameters.AddWithValue("$accountId", accountId);
            query.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            oldLevel = reader.GetInt32(0);
            oldExperience = reader.GetInt64(1);
            oldHans = reader.GetInt64(2);
            vitality = reader.GetInt32(3);
            intelligence = reader.GetInt32(4);
            oldMaxHp = reader.GetInt32(5);
            oldMaxMp = reader.GetInt32(6);
            petVariant = reader.GetInt32(7);
            equippedPetItemCode = checked((uint)reader.GetInt64(8));
        }

        var experience = oldExperience > long.MaxValue - experienceReward
            ? long.MaxValue
            : oldExperience + experienceReward;
        var level = Math.Max(Math.Clamp(oldLevel, 1, CharacterProgression.MaximumLevel),
            CharacterProgression.CalculateLevel(experience));
        var gainedLevels = Math.Max(0, level - oldLevel);
        var maxHp = Math.Max(oldMaxHp, CharacterProgression.CalculateMaxHp(level, vitality));
        var maxMp = Math.Max(oldMaxMp, CharacterProgression.CalculateMaxMp(level, intelligence));
        var hans = oldHans > long.MaxValue - hansReward ? long.MaxValue : oldHans + hansReward;
        var now = DateTime.UtcNow.ToString("O");

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET Experience = $experience,
                    Level = $level,
                    AttributePoints = AttributePoints + $points,
                    MaxHp = $maxHp,
                    MaxMp = $maxMp,
                    CurrentHp = CASE WHEN $points > 0 THEN $maxHp ELSE MIN(CurrentHp, $maxHp) END,
                    CurrentMp = CASE WHEN $points > 0 THEN $maxMp ELSE MIN(CurrentMp, $maxMp) END,
                    Hans = $hans,
                    LastSavedAt = $now
                WHERE Id = $characterId AND AccountId = $accountId AND ActiveSessionId = $sessionId
                """;
            update.Parameters.AddWithValue("$experience", experience);
            update.Parameters.AddWithValue("$level", level);
            update.Parameters.AddWithValue("$points", gainedLevels * CharacterProgression.AttributePointsPerLevel);
            update.Parameters.AddWithValue("$maxHp", maxHp);
            update.Parameters.AddWithValue("$maxMp", maxMp);
            update.Parameters.AddWithValue("$hans", hans);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$accountId", accountId);
            update.Parameters.AddWithValue("$sessionId", sessionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        var tutorialPetItemCode = petVariant is >= 1 and <= 3
            ? 15_000_000u + (uint)petVariant
            : 0u;
        if (equippedPetItemCode == 0)
            equippedPetItemCode = tutorialPetItemCode;
        if (petExperienceReward > 0
            && equippedPetItemCode != 0
            && ShopCatalog.TryGet(15, equippedPetItemCode, out var petCatalogItem))
        {
            if (equippedPetItemCode == tutorialPetItemCode)
            {
                await using var ensureTutorialPet = connection.CreateCommand();
                ensureTutorialPet.Transaction = transaction;
                ensureTutorialPet.CommandText = """
                    INSERT INTO CharacterItems(
                        CharacterId, ItemCode, Quantity, PetCurrentStage, PetMaximumStage,
                        PetLevel, PetExperience, UpdatedAt)
                    VALUES($characterId, $itemCode, 1, $currentStage, $maximumStage, 0, 0, $now)
                    ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                        Quantity = MAX(1, CharacterItems.Quantity),
                        PetCurrentStage = CASE WHEN CharacterItems.PetCurrentStage = 0 THEN excluded.PetCurrentStage ELSE CharacterItems.PetCurrentStage END,
                        PetMaximumStage = CASE WHEN CharacterItems.PetMaximumStage = 0 THEN excluded.PetMaximumStage ELSE CharacterItems.PetMaximumStage END,
                        UpdatedAt = excluded.UpdatedAt
                    """;
                ensureTutorialPet.Parameters.AddWithValue("$characterId", characterId);
                ensureTutorialPet.Parameters.AddWithValue("$itemCode", equippedPetItemCode);
                ensureTutorialPet.Parameters.AddWithValue("$currentStage", petCatalogItem.PetModelStage);
                ensureTutorialPet.Parameters.AddWithValue("$maximumStage", petCatalogItem.PetUpgradeStage);
                ensureTutorialPet.Parameters.AddWithValue("$now", now);
                await ensureTutorialPet.ExecuteNonQueryAsync(cancellationToken);
            }

            PetState? storedPetState = null;
            await using (var readPet = connection.CreateCommand())
            {
                readPet.Transaction = transaction;
                readPet.CommandText = """
                    SELECT PetDurability, PetCurrentStage, PetMaximumStage, PetLevel, PetExperience,
                           PetAccessory0, PetAccessory1, PetAccessory2
                    FROM CharacterItems
                    WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity > 0
                    """;
                readPet.Parameters.AddWithValue("$characterId", characterId);
                readPet.Parameters.AddWithValue("$itemCode", equippedPetItemCode);
                await using var reader = await readPet.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    storedPetState = new PetState(
                        equippedPetItemCode,
                        reader.GetInt32(1) > 0 ? checked((byte)reader.GetInt32(1)) : petCatalogItem.PetModelStage,
                        reader.GetInt32(2) > 0 ? checked((byte)reader.GetInt32(2)) : petCatalogItem.PetUpgradeStage,
                        checked((uint)reader.GetInt64(3)),
                        checked((uint)reader.GetInt64(4)),
                        checked((uint)reader.GetInt64(5)),
                        checked((uint)reader.GetInt64(6)),
                        checked((uint)reader.GetInt64(7)),
                        reader.IsDBNull(0) ? petCatalogItem.PetMaxDurability : checked((short)reader.GetInt32(0)));
                }
            }

            if (storedPetState is PetState petState)
            {
                var petProgress = PetProgression.AddExperience(petState, checked((uint)petExperienceReward));
                await using var updatePet = connection.CreateCommand();
                updatePet.Transaction = transaction;
                updatePet.CommandText = """
                    UPDATE CharacterItems
                    SET PetCurrentStage = $currentStage,
                        PetMaximumStage = $maximumStage,
                        PetLevel = $level,
                        PetExperience = $experience,
                        UpdatedAt = $now
                    WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity > 0;
                    UPDATE Characters
                    SET PetLevel = $level, PetExperience = $experience
                    WHERE Id = $characterId;
                    """;
                updatePet.Parameters.AddWithValue("$currentStage", petProgress.State.CurrentStage);
                updatePet.Parameters.AddWithValue("$maximumStage", petProgress.State.MaximumStage);
                updatePet.Parameters.AddWithValue("$level", petProgress.State.Level);
                updatePet.Parameters.AddWithValue("$experience", petProgress.State.Experience);
                updatePet.Parameters.AddWithValue("$now", now);
                updatePet.Parameters.AddWithValue("$characterId", characterId);
                updatePet.Parameters.AddWithValue("$itemCode", equippedPetItemCode);
                await updatePet.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (completed)
        {
            // Retail CF88 stores only B/A/S as 1/2/3. C (rating 2) and lower
            // leave the two-bit best-rating field at zero.
            var clientBestRating = Math.Clamp(clearRating - 2, 0, 3);
            var archiveSlot = superBoss ? 3 : dungeon;
            var ratingShift = archiveSlot * 2;
            var ratingFieldMask = 0x03 << ratingShift;
            var ratingClearMask = 0xFF & ~ratingFieldMask;
            await using var progress = connection.CreateCommand();
            progress.Transaction = transaction;
            progress.CommandText = """
                INSERT INTO DungeonProgress(
                    CharacterId, Episode, Difficulty, ClearMask, BestRatings, BestScore,
                    BestElapsedMinutes, ClearedAt, UpdatedAt)
                VALUES($characterId, $episode, $difficulty, $clearMask, $bestRatings, $score, $elapsed, $now, $now)
                ON CONFLICT(CharacterId, Episode, Difficulty) DO UPDATE SET
                    ClearMask = DungeonProgress.ClearMask | excluded.ClearMask,
                    BestRatings = (DungeonProgress.BestRatings & $ratingClearMask)
                        | MAX(
                            DungeonProgress.BestRatings & $ratingFieldMask,
                            excluded.BestRatings & $ratingFieldMask),
                    BestScore = MAX(DungeonProgress.BestScore, excluded.BestScore),
                    BestElapsedMinutes = CASE
                        WHEN DungeonProgress.BestElapsedMinutes IS NULL THEN excluded.BestElapsedMinutes
                        ELSE MIN(DungeonProgress.BestElapsedMinutes, excluded.BestElapsedMinutes)
                    END,
                    UpdatedAt = excluded.UpdatedAt
                """;
            progress.Parameters.AddWithValue("$characterId", characterId);
            progress.Parameters.AddWithValue("$episode", episode);
            progress.Parameters.AddWithValue("$difficulty", difficulty);
            progress.Parameters.AddWithValue("$clearMask", 1 << archiveSlot);
            progress.Parameters.AddWithValue("$bestRatings", clientBestRating << ratingShift);
            progress.Parameters.AddWithValue("$ratingFieldMask", ratingFieldMask);
            progress.Parameters.AddWithValue("$ratingClearMask", ratingClearMask);
            progress.Parameters.AddWithValue("$score", score);
            progress.Parameters.AddWithValue("$elapsed", elapsedMinutes);
            progress.Parameters.AddWithValue("$now", now);
            await progress.ExecuteNonQueryAsync(cancellationToken);

            await using var performance = connection.CreateCommand();
            performance.Transaction = transaction;
            performance.CommandText = """
                INSERT INTO DungeonStagePerformance(
                    CharacterId, Episode, Difficulty, ArchiveSlot, BestScore,
                    BestElapsedMinutes, ClearedAt, UpdatedAt)
                VALUES($characterId, $episode, $difficulty, $archiveSlot, $score, $elapsed, $now, $now)
                ON CONFLICT(CharacterId, Episode, Difficulty, ArchiveSlot) DO UPDATE SET
                    BestScore = MAX(DungeonStagePerformance.BestScore, excluded.BestScore),
                    BestElapsedMinutes = CASE
                        WHEN DungeonStagePerformance.BestElapsedMinutes IS NULL THEN excluded.BestElapsedMinutes
                        ELSE MIN(DungeonStagePerformance.BestElapsedMinutes, excluded.BestElapsedMinutes)
                    END,
                    UpdatedAt = excluded.UpdatedAt
                """;
            performance.Parameters.AddWithValue("$characterId", characterId);
            performance.Parameters.AddWithValue("$episode", episode);
            performance.Parameters.AddWithValue("$difficulty", difficulty);
            performance.Parameters.AddWithValue("$archiveSlot", archiveSlot);
            performance.Parameters.AddWithValue("$score", score);
            performance.Parameters.AddWithValue("$elapsed", elapsedMinutes);
            performance.Parameters.AddWithValue("$now", now);
            await performance.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetCharacterByIdAsync(characterId, cancellationToken);
    }

    public async Task<ushort> GrantDungeonInventoryItemAsync(
        long characterId,
        uint itemCode,
        ushort quantity,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || itemCode == 0 || quantity == 0)
            return 0;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
            VALUES($characterId, $itemCode, $quantity, $now)
            ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                Quantity = MIN(65535, CharacterItems.Quantity + excluded.Quantity),
                UpdatedAt = excluded.UpdatedAt
            RETURNING Quantity
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$itemCode", itemCode);
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? (ushort)0 : checked((ushort)Convert.ToInt32(result));
    }

    public async Task<byte[]> GetDungeonClearMasksAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        var masks = new byte[60];
        if (characterId <= 0)
            return masks;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Episode, Difficulty, ClearMask
            FROM DungeonProgress
            WHERE CharacterId = $characterId
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var episode = reader.GetInt32(0);
            var difficulty = reader.GetInt32(1);
            var clearMask = reader.GetInt32(2);
            if (episode is < 0 or >= 20 || difficulty is < 0 or >= 3)
                continue;
            masks[episode * 3 + difficulty] = checked((byte)clearMask);
        }
        return masks;
    }

    public async Task<byte[]> GetDungeonBestRatingsAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        var ratings = new byte[60];
        if (characterId <= 0)
            return ratings;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Episode, Difficulty, BestRatings
            FROM DungeonProgress
            WHERE CharacterId = $characterId
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var episode = reader.GetInt32(0);
            var difficulty = reader.GetInt32(1);
            var bestRatings = reader.GetInt32(2);
            if (episode is < 0 or >= 20 || difficulty is < 0 or >= 3)
                continue;
            ratings[episode * 3 + difficulty] = checked((byte)bestRatings);
        }
        return ratings;
    }

    public async Task<IReadOnlyList<DungeonProgressAdminRecord>> GetDungeonProgressAdminAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        var persisted = new Dictionary<
            (byte Episode, byte Difficulty),
            (byte ClearMask, byte BestRatings)>();
        var performances = new Dictionary<
            (byte Episode, byte Difficulty, byte ArchiveSlot),
            (int BestScore, int? BestElapsedMinutes, DateTime ClearedAt, DateTime UpdatedAt)>();
        if (characterId > 0)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT Episode, Difficulty, ClearMask, BestRatings
                    FROM DungeonProgress
                    WHERE CharacterId = $characterId
                    ORDER BY Episode, Difficulty
                    """;
                command.Parameters.AddWithValue("$characterId", characterId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var episode = checked((byte)reader.GetInt32(0));
                    var difficulty = checked((byte)reader.GetInt32(1));
                    persisted[(episode, difficulty)] = (
                        checked((byte)reader.GetInt32(2)),
                        checked((byte)reader.GetInt32(3)));
                }
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT Episode, Difficulty, ArchiveSlot, BestScore,
                           BestElapsedMinutes, ClearedAt, UpdatedAt
                    FROM DungeonStagePerformance
                    WHERE CharacterId = $characterId
                    ORDER BY Episode, Difficulty, ArchiveSlot
                    """;
                command.Parameters.AddWithValue("$characterId", characterId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var episode = checked((byte)reader.GetInt32(0));
                    var difficulty = checked((byte)reader.GetInt32(1));
                    var archiveSlot = checked((byte)reader.GetInt32(2));
                    performances[(episode, difficulty, archiveSlot)] = (
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        ParseDate(reader.GetString(5)),
                        ParseDate(reader.GetString(6)));
                }
            }
        }

        var rows = new List<DungeonProgressAdminRecord>(80);
        for (byte episode = 0; episode < 20; episode++)
        {
            for (byte archiveSlot = 0; archiveSlot < 4; archiveSlot++)
            {
                byte clearMask = 0;
                byte bestRatings = 0;
                var difficultyPerformances = new DungeonDifficultyPerformanceAdminRecord[3];
                for (byte difficulty = 0; difficulty < 3; difficulty++)
                {
                    if (persisted.TryGetValue((episode, difficulty), out var progress))
                    {
                        var rating = (progress.BestRatings >> (archiveSlot * 2)) & 0x03;
                        bestRatings |= checked((byte)(rating << (difficulty * 2)));
                        if ((progress.ClearMask & (1 << archiveSlot)) != 0)
                            clearMask |= checked((byte)(1 << difficulty));
                    }

                    difficultyPerformances[difficulty] = performances.TryGetValue(
                        (episode, difficulty, archiveSlot), out var performance)
                        ? new DungeonDifficultyPerformanceAdminRecord
                        {
                            Difficulty = difficulty,
                            BestScore = performance.BestScore,
                            BestElapsedMinutes = performance.BestElapsedMinutes,
                            ClearedAt = performance.ClearedAt,
                            UpdatedAt = performance.UpdatedAt
                        }
                        : new DungeonDifficultyPerformanceAdminRecord { Difficulty = difficulty };
                }
                rows.Add(new DungeonProgressAdminRecord
                {
                    CharacterId = characterId,
                    Episode = episode,
                    Dungeon = archiveSlot == 3 ? (byte)2 : archiveSlot,
                    IsSuperBoss = archiveSlot == 3,
                    ClearMask = clearMask,
                    BestRatings = bestRatings,
                    DifficultyPerformances = difficultyPerformances
                });
            }
        }

        return rows;
    }

    public async Task<(bool Success, string Error)> UpdateDungeonProgressFromAdminAsync(
        long characterId,
        byte episode,
        byte dungeon,
        byte difficulty,
        bool cleared,
        int bestScore,
        int? bestElapsedMinutes,
        bool superBoss = false,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || episode >= 20 || dungeon >= 3 || difficulty >= 3
            || (superBoss && dungeon != 2))
            return (false, "关卡位置无效。");
        if (bestScore < 0 || bestElapsedMinutes is < 0)
            return (false, "最高分和通关时间不能为负数。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var character = connection.CreateCommand())
        {
            character.Transaction = transaction;
            character.CommandText = "SELECT COUNT(*) FROM Characters WHERE Id = $characterId";
            character.Parameters.AddWithValue("$characterId", characterId);
            if (Convert.ToInt32(await character.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "角色存档不存在。");
            }
        }

        var currentMask = 0;
        var currentBestRatings = 0;
        var currentBestScore = 0;
        int? currentBestElapsedMinutes = null;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT ClearMask, BestRatings, BestScore, BestElapsedMinutes
                FROM DungeonProgress
                WHERE CharacterId = $characterId AND Episode = $episode AND Difficulty = $difficulty
                """;
            query.Parameters.AddWithValue("$characterId", characterId);
            query.Parameters.AddWithValue("$episode", episode);
            query.Parameters.AddWithValue("$difficulty", difficulty);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                currentMask = reader.GetInt32(0);
                currentBestRatings = reader.GetInt32(1);
                currentBestScore = reader.GetInt32(2);
                currentBestElapsedMinutes = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            }
        }

        var archiveSlot = superBoss ? 3 : dungeon;
        var archiveBit = 1 << archiveSlot;
        var updatedMask = cleared ? currentMask | archiveBit : currentMask & ~archiveBit;
        var ratingFieldMask = 0x03 << (archiveSlot * 2);
        var updatedBestRatings = cleared
            ? currentBestRatings
            : currentBestRatings & ~ratingFieldMask;
        var now = DateTime.UtcNow.ToString("O");
        await using (var performance = connection.CreateCommand())
        {
            performance.Transaction = transaction;
            performance.CommandText = cleared
                ? """
                    INSERT INTO DungeonStagePerformance(
                        CharacterId, Episode, Difficulty, ArchiveSlot, BestScore,
                        BestElapsedMinutes, ClearedAt, UpdatedAt)
                    VALUES($characterId, $episode, $difficulty, $archiveSlot, $score, $elapsed, $now, $now)
                    ON CONFLICT(CharacterId, Episode, Difficulty, ArchiveSlot) DO UPDATE SET
                        BestScore = excluded.BestScore,
                        BestElapsedMinutes = excluded.BestElapsedMinutes,
                        UpdatedAt = excluded.UpdatedAt
                    """
                : """
                    DELETE FROM DungeonStagePerformance
                    WHERE CharacterId = $characterId
                      AND Episode = $episode
                      AND Difficulty = $difficulty
                      AND ArchiveSlot = $archiveSlot
                    """;
            performance.Parameters.AddWithValue("$characterId", characterId);
            performance.Parameters.AddWithValue("$episode", episode);
            performance.Parameters.AddWithValue("$difficulty", difficulty);
            performance.Parameters.AddWithValue("$archiveSlot", archiveSlot);
            if (cleared)
            {
                performance.Parameters.AddWithValue("$score", bestScore);
                performance.Parameters.AddWithValue(
                    "$elapsed",
                    (object?)bestElapsedMinutes ?? DBNull.Value);
                performance.Parameters.AddWithValue("$now", now);
            }
            await performance.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            if (updatedMask == 0)
            {
                update.CommandText = """
                    DELETE FROM DungeonProgress
                    WHERE CharacterId = $characterId AND Episode = $episode AND Difficulty = $difficulty
                    """;
            }
            else
            {
                update.CommandText = """
                    INSERT INTO DungeonProgress(
                        CharacterId, Episode, Difficulty, ClearMask, BestRatings, BestScore,
                        BestElapsedMinutes, ClearedAt, UpdatedAt)
                    VALUES($characterId, $episode, $difficulty, $clearMask, $bestRatings, $score, $elapsed, $now, $now)
                    ON CONFLICT(CharacterId, Episode, Difficulty) DO UPDATE SET
                        ClearMask = excluded.ClearMask,
                        BestRatings = excluded.BestRatings,
                        BestScore = excluded.BestScore,
                        BestElapsedMinutes = excluded.BestElapsedMinutes,
                        ClearedAt = CASE
                            WHEN DungeonProgress.ClearMask = 0 THEN excluded.ClearedAt
                            ELSE DungeonProgress.ClearedAt
                        END,
                        UpdatedAt = excluded.UpdatedAt
                    """;
                update.Parameters.AddWithValue("$clearMask", updatedMask);
                update.Parameters.AddWithValue("$bestRatings", updatedBestRatings);
                update.Parameters.AddWithValue("$score", cleared ? bestScore : currentBestScore);
                update.Parameters.AddWithValue(
                    "$elapsed",
                    (object?)(cleared ? bestElapsedMinutes : currentBestElapsedMinutes) ?? DBNull.Value);
                update.Parameters.AddWithValue("$now", now);
            }
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$episode", episode);
            update.Parameters.AddWithValue("$difficulty", difficulty);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Error, CharacterRecord? Character)> AllocateAttributeAsync(
        long characterId,
        CharacterAttribute attribute,
        int amount,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
            return (false, "分配点数必须大于零。", await GetCharacterByIdAsync(characterId, cancellationToken));

        var column = attribute switch
        {
            CharacterAttribute.Strength => "Strength",
            CharacterAttribute.Vitality => "Vitality",
            CharacterAttribute.Agility => "Agility",
            CharacterAttribute.Intelligence => "Intelligence",
            CharacterAttribute.Luck => "Luck",
            _ => throw new ArgumentOutOfRangeException(nameof(attribute))
        };

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        int points;
        int level;
        int vitality;
        int intelligence;
        int currentHp;
        int currentMp;
        int oldMaxHp;
        int oldMaxMp;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT AttributePoints, Level, Vitality, Intelligence, CurrentHp, CurrentMp, MaxHp, MaxMp FROM Characters WHERE Id = $id";
            query.Parameters.AddWithValue("$id", characterId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return (false, "角色不存在。", null);
            points = reader.GetInt32(0);
            level = reader.GetInt32(1);
            vitality = reader.GetInt32(2);
            intelligence = reader.GetInt32(3);
            currentHp = reader.GetInt32(4);
            currentMp = reader.GetInt32(5);
            oldMaxHp = reader.GetInt32(6);
            oldMaxMp = reader.GetInt32(7);
        }
        if (points < amount)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "可分配属性点不足。", await GetCharacterByIdAsync(characterId, cancellationToken));
        }

        if (attribute == CharacterAttribute.Vitality)
            vitality = checked(vitality + amount);
        if (attribute == CharacterAttribute.Intelligence)
            intelligence = checked(intelligence + amount);
        var maxHp = CharacterProgression.CalculateMaxHp(level, vitality);
        var maxMp = CharacterProgression.CalculateMaxMp(level, intelligence);
        currentHp = Math.Clamp(currentHp + Math.Max(0, maxHp - oldMaxHp), 0, maxHp);
        currentMp = Math.Clamp(currentMp + Math.Max(0, maxMp - oldMaxMp), 0, maxMp);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE Characters
                SET {column} = {column} + $amount,
                    AttributePoints = AttributePoints - $amount,
                    MaxHp = $maxHp,
                    MaxMp = $maxMp,
                    CurrentHp = $currentHp,
                    CurrentMp = $currentMp,
                    LastSavedAt = $now
                WHERE Id = $id AND AttributePoints >= $amount
                """;
            update.Parameters.AddWithValue("$amount", amount);
            update.Parameters.AddWithValue("$maxHp", maxHp);
            update.Parameters.AddWithValue("$maxMp", maxMp);
            update.Parameters.AddWithValue("$currentHp", currentHp);
            update.Parameters.AddWithValue("$currentMp", currentMp);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", characterId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "属性点更新冲突，请刷新角色后重试。", await GetCharacterByIdAsync(characterId, cancellationToken));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, await GetCharacterByIdAsync(characterId, cancellationToken));
    }

    public async Task<int> DeleteCharacterAsync(long accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Characters WHERE AccountId = $accountId AND IsOnline = 0";
        command.Parameters.AddWithValue("$accountId", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(bool Success, string Error)> UpdateCharacterFromAdminAsync(
        CharacterRecord character,
        CancellationToken cancellationToken = default)
    {
        var name = character.Name.Trim();
        if (name.Length is < 1 or > 16 || name.Any(char.IsControl))
            return (false, "角色名长度需要在 1-16 个字符之间，且不能包含控制字符。");
        if (character.Gender is < 0 or > 1)
            return (false, "性别字段只能是 0（女）或 1（男）。");
        if (character.Level is < 1 or > 99 || character.PetLevel is < 1 or > byte.MaxValue)
            return (false, "角色等级需要在 1-99，宠物等级需要在 1-255。");
        if (character.PetVariant is < 0 or > 3)
            return (false, "宠物类型只能是 0（无）或 1-3。");
        if (character.Experience is < 0 or > uint.MaxValue
            || character.PetExperience is < 0 or > uint.MaxValue)
            return (false, "角色经验和宠物经验需要在 0-4294967295 之间。");
        if (character.AttributePoints is < 0 or > ushort.MaxValue
            || character.Strength is < 0 or > ushort.MaxValue
            || character.Vitality is < 0 or > ushort.MaxValue
            || character.Agility is < 0 or > ushort.MaxValue
            || character.Intelligence is < 0 or > ushort.MaxValue
            || character.Luck is < 0 or > ushort.MaxValue)
            return (false, "属性及剩余属性点需要在 0-65535 之间。");
        if (character.MaxHp is < 1 or > ushort.MaxValue
            || character.MaxMp is < 1 or > ushort.MaxValue
            || character.CurrentHp < 0 || character.CurrentHp > character.MaxHp
            || character.CurrentMp < 0 || character.CurrentMp > character.MaxMp)
            return (false, "HP/MP 上限需要在 1-65535，当前值不能超过对应上限。");
        if (character.SpawnMapId is < 0 or > ushort.MaxValue
            || character.CurrentMapId is < 0 or > ushort.MaxValue
            || character.CurrentTownPage is < 0 or > byte.MaxValue
            || character.SpawnX is < 0 or > 1023
            || character.SpawnY is < 0 or > 1023
            || character.PositionX is < 0 or > 1023
            || character.PositionY is < 0 or > 1023)
            return (false, "地图编号需要在 0-65535，坐标需要在 0-1023 之间。");
        if (character.Hans is < 0 or > uint.MaxValue || character.Cash is < 0 or > uint.MaxValue)
            return (false, "Hans 和 Cash 需要在 0-4294967295 之间。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT COUNT(*) FROM Characters WHERE Id <> $id AND Name = $name COLLATE NOCASE";
            duplicate.Parameters.AddWithValue("$id", character.Id);
            duplicate.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "角色名已经被使用。");
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Characters
            SET Name = $name,
                Gender = $gender,
                Face = $face,
                TutorialCompleted = $tutorialCompleted,
                PetVariant = $petVariant,
                EquippedPetItemCode = $equippedPetItemCode,
                PetLevel = $petLevel,
                PetExperience = $petExperience,
                Level = $level,
                Experience = $experience,
                AttributePoints = $attributePoints,
                Strength = $strength,
                Vitality = $vitality,
                Agility = $agility,
                Intelligence = $intelligence,
                Luck = $luck,
                MaxHp = $maxHp,
                MaxMp = $maxMp,
                CurrentHp = $currentHp,
                CurrentMp = $currentMp,
                SpawnMapId = $spawnMapId,
                SpawnX = $spawnX,
                SpawnY = $spawnY,
                CurrentMapId = $currentMapId,
                CurrentTownPage = $currentTownPage,
                PositionX = $positionX,
                PositionY = $positionY,
                Hans = $hans,
                Cash = $cash,
                LastSavedAt = $now
            WHERE Id = $id
              AND AccountId = $accountId
              AND IsOnline = 0
              AND ActiveSessionId IS NULL
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$gender", character.Gender);
        command.Parameters.AddWithValue("$face", character.Face);
        command.Parameters.AddWithValue("$tutorialCompleted", character.TutorialCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$petVariant", character.PetVariant);
        command.Parameters.AddWithValue("$equippedPetItemCode", character.EquippedPetItemCode);
        command.Parameters.AddWithValue("$petLevel", character.PetLevel);
        command.Parameters.AddWithValue("$petExperience", character.PetExperience);
        command.Parameters.AddWithValue("$level", character.Level);
        command.Parameters.AddWithValue("$experience", character.Experience);
        command.Parameters.AddWithValue("$attributePoints", character.AttributePoints);
        command.Parameters.AddWithValue("$strength", character.Strength);
        command.Parameters.AddWithValue("$vitality", character.Vitality);
        command.Parameters.AddWithValue("$agility", character.Agility);
        command.Parameters.AddWithValue("$intelligence", character.Intelligence);
        command.Parameters.AddWithValue("$luck", character.Luck);
        command.Parameters.AddWithValue("$maxHp", character.MaxHp);
        command.Parameters.AddWithValue("$maxMp", character.MaxMp);
        command.Parameters.AddWithValue("$currentHp", character.CurrentHp);
        command.Parameters.AddWithValue("$currentMp", character.CurrentMp);
        command.Parameters.AddWithValue("$spawnMapId", character.SpawnMapId);
        command.Parameters.AddWithValue("$spawnX", character.SpawnX);
        command.Parameters.AddWithValue("$spawnY", character.SpawnY);
        command.Parameters.AddWithValue("$currentMapId", character.CurrentMapId);
        command.Parameters.AddWithValue("$currentTownPage", character.CurrentTownPage);
        command.Parameters.AddWithValue("$positionX", character.PositionX);
        command.Parameters.AddWithValue("$positionY", character.PositionY);
        command.Parameters.AddWithValue("$hans", character.Hans);
        command.Parameters.AddWithValue("$cash", character.Cash);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", character.Id);
        command.Parameters.AddWithValue("$accountId", character.AccountId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "角色正在游戏中、会话尚未释放，或存档已经不存在。");
        }

        if (character.EquippedPetItemCode != 0)
        {
            await using var updatePetState = connection.CreateCommand();
            updatePetState.Transaction = transaction;
            updatePetState.CommandText = """
                UPDATE CharacterItems
                SET PetLevel = $petLevel,
                    PetExperience = $petExperience,
                    UpdatedAt = $now
                WHERE CharacterId = $characterId
                  AND ItemCode = $itemCode
                  AND Quantity > 0
                """;
            updatePetState.Parameters.AddWithValue("$petLevel", character.PetLevel);
            updatePetState.Parameters.AddWithValue("$petExperience", character.PetExperience);
            updatePetState.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            updatePetState.Parameters.AddWithValue("$characterId", character.Id);
            updatePetState.Parameters.AddWithValue("$itemCode", character.EquippedPetItemCode);
            await updatePetState.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty);
    }

    public async Task ResetAllOnlineStatesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow.ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Accounts
            SET LastOfflineAt = CASE WHEN IsOnline = 1 THEN $now ELSE LastOfflineAt END,
                IsOnline = 0, ActiveSessionId = NULL, CurrentChannelId = NULL, OnlineSince = NULL;
            UPDATE Characters
            SET LastOfflineAt = CASE WHEN IsOnline = 1 THEN $now ELSE LastOfflineAt END,
                IsOnline = 0, ActiveSessionId = NULL, CurrentChannelId = NULL, OnlineSince = NULL;
            """;
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetOnlineStatesForSessionsAsync(
        IEnumerable<string> sessionIds,
        CancellationToken cancellationToken = default)
    {
        var ownedSessionIds = sessionIds
            .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ownedSessionIds.Length == 0)
            return;

        var sessionParameters = string.Join(
            ", ",
            ownedSessionIds.Select((_, index) => $"$session{index}"));
        var now = DateTime.UtcNow.ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE Accounts
            SET TrialPlayedSeconds = TrialPlayedSeconds + CASE
                    WHEN IsOnline = 1 AND OnlineSince IS NOT NULL
                    THEN MAX(0, CAST((julianday($now) - julianday(OnlineSince)) * 86400 + 0.999 AS INTEGER))
                    ELSE 0
                END,
                LastOfflineAt = CASE WHEN IsOnline = 1 THEN $now ELSE LastOfflineAt END,
                IsOnline = 0,
                ActiveSessionId = NULL,
                CurrentChannelId = NULL,
                OnlineSince = NULL
            WHERE ActiveSessionId IN ({sessionParameters});
            UPDATE Characters
            SET LastOfflineAt = CASE WHEN IsOnline = 1 THEN $now ELSE LastOfflineAt END,
                IsOnline = 0,
                ActiveSessionId = NULL,
                CurrentChannelId = NULL,
                OnlineSince = NULL
            WHERE ActiveSessionId IN ({sessionParameters});
            """;
        command.Parameters.AddWithValue("$now", now);
        for (var index = 0; index < ownedSessionIds.Length; index++)
            command.Parameters.AddWithValue($"$session{index}", ownedSessionIds[index]);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(int AccountsDeleted, int CharactersDeleted)> ClearAllPlayerDataAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        static async Task<int> CountRowsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT COUNT(*) FROM [{table}]";
            return Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        var accountCount = await CountRowsAsync(
            connection, transaction, "Accounts", cancellationToken);
        var characterCount = await CountRowsAsync(
            connection, transaction, "Characters", cancellationToken);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                DELETE FROM IpBans;
                DELETE FROM Accounts;
                DELETE FROM sqlite_sequence
                WHERE name IN ('Accounts', 'Characters', 'CharacterShopWishlist', 'MentorInteractions');
                """;
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (accountCount, characterCount);
    }

    public async Task SetMentorAdvertisingAsync(
        long characterId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CharacterMentorAdvertisements (CharacterId, IsAdvertising, UpdatedAt)
            VALUES ($characterId, $enabled, $now)
            ON CONFLICT(CharacterId) DO UPDATE SET
                IsAdvertising = excluded.IsAdvertising,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeactivateAllMentorAdvertisementsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CharacterMentorAdvertisements
            SET IsAdvertising = 0, UpdatedAt = $now
            WHERE IsAdvertising <> 0;
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CoupleRelationRecord?> GetActiveCoupleRelationAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relation.Id,
                   relation.Character1Id, first.Name,
                   relation.Character2Id, second.Name,
                   relation.RingItemCode, relation.EstablishedAt
            FROM CoupleRelations AS relation
            INNER JOIN Characters AS first ON first.Id = relation.Character1Id
            INNER JOIN Characters AS second ON second.Id = relation.Character2Id
            WHERE relation.EndedAt IS NULL
              AND (relation.Character1Id = $characterId OR relation.Character2Id = $characterId)
            ORDER BY relation.Id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCoupleRelation(reader) : null;
    }

    public async Task<(bool Success, string Error, ushort RemainingQuantity, CoupleRelationRecord? Relation)>
        CreateCoupleRelationAsync(
            long accountId,
            long requesterCharacterId,
            string sessionId,
            long targetCharacterId,
            uint ringItemCode,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || requesterCharacterId <= 0
            || targetCharacterId <= 0
            || requesterCharacterId == targetCharacterId
            || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(ringItemCode, out var ring)
            || ring.Category != 43
            || !string.Equals(ring.Source, "CI._D28/COUPLERING", StringComparison.Ordinal))
            return (false, "Invalid couple-ring request.", 0, null);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long quantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT item.Quantity
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                INNER JOIN CharacterItems AS item
                    ON item.CharacterId = character.Id
                   AND item.ItemCode = $itemCode
                   AND item.Quantity > 0
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                  AND EXISTS (SELECT 1 FROM Characters WHERE Id = $targetCharacterId)
                """;
            current.Parameters.AddWithValue("$itemCode", ringItemCode);
            current.Parameters.AddWithValue("$characterId", requesterCharacterId);
            current.Parameters.AddWithValue("$targetCharacterId", targetCharacterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            quantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (quantity <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The requester does not own this couple ring.", 0, null);
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT COUNT(*) FROM CoupleRelations
                WHERE EndedAt IS NULL
                  AND (Character1Id IN ($requester, $target)
                       OR Character2Id IN ($requester, $target))
                """;
            existing.Parameters.AddWithValue("$requester", requesterCharacterId);
            existing.Parameters.AddWithValue("$target", targetCharacterId);
            if (Convert.ToInt32(await existing.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "One of the characters already has a couple relation.", checked((ushort)quantity), null);
            }
        }

        var remaining = quantity - 1;
        if (!await ConsumeCharacterItemAsync(
                connection, transaction, requesterCharacterId, ringItemCode,
                quantity, remaining, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The couple-ring quantity changed.", checked((ushort)quantity), null);
        }

        var firstId = Math.Min(requesterCharacterId, targetCharacterId);
        var secondId = Math.Max(requesterCharacterId, targetCharacterId);
        var establishedAt = DateTime.UtcNow;
        long relationId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CoupleRelations(
                    Character1Id, Character2Id, RingItemCode, EstablishedAt, EndedAt, EndItemCode)
                VALUES($firstId, $secondId, $ringItemCode, $establishedAt, NULL, NULL);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$firstId", firstId);
            insert.Parameters.AddWithValue("$secondId", secondId);
            insert.Parameters.AddWithValue("$ringItemCode", ringItemCode);
            insert.Parameters.AddWithValue("$establishedAt", establishedAt.ToString("O"));
            relationId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
        }

        string firstName;
        string secondName;
        await using (var names = connection.CreateCommand())
        {
            names.Transaction = transaction;
            names.CommandText = """
                SELECT first.Name, second.Name
                FROM Characters AS first, Characters AS second
                WHERE first.Id = $firstId AND second.Id = $secondId
                """;
            names.Parameters.AddWithValue("$firstId", firstId);
            names.Parameters.AddWithValue("$secondId", secondId);
            await using var reader = await names.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The couple characters changed.", checked((ushort)quantity), null);
            }
            firstName = reader.GetString(0);
            secondName = reader.GetString(1);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, checked((ushort)remaining), new CoupleRelationRecord
        {
            Id = relationId,
            Character1Id = firstId,
            Character1Name = firstName,
            Character2Id = secondId,
            Character2Name = secondName,
            RingItemCode = ringItemCode,
            EstablishedAt = establishedAt
        });
    }

    public async Task<(bool Success, string Error, ushort RemainingQuantity)>
        EndCoupleRelationAsync(
            long accountId,
            long requesterCharacterId,
            string sessionId,
            long targetCharacterId,
            uint couponItemCode,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || requesterCharacterId <= 0
            || targetCharacterId <= 0
            || requesterCharacterId == targetCharacterId
            || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(couponItemCode, out var coupon)
            || coupon.Category != 43
            || !string.Equals(coupon.Source, "CI._D28/COUPLECANCEL", StringComparison.Ordinal))
            return (false, "Invalid couple-separation request.", 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long quantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT item.Quantity
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                INNER JOIN CharacterItems AS item
                    ON item.CharacterId = character.Id
                   AND item.ItemCode = $itemCode
                   AND item.Quantity > 0
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$itemCode", couponItemCode);
            current.Parameters.AddWithValue("$characterId", requesterCharacterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            quantity = Convert.ToInt64(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (quantity <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The requester does not own this separation coupon.", 0);
        }

        long relationId;
        await using (var relation = connection.CreateCommand())
        {
            relation.Transaction = transaction;
            relation.CommandText = """
                SELECT Id FROM CoupleRelations
                WHERE EndedAt IS NULL
                  AND ((Character1Id = $requester AND Character2Id = $target)
                       OR (Character1Id = $target AND Character2Id = $requester))
                LIMIT 1
                """;
            relation.Parameters.AddWithValue("$requester", requesterCharacterId);
            relation.Parameters.AddWithValue("$target", targetCharacterId);
            relationId = Convert.ToInt64(await relation.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (relationId <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The requested active couple relation does not exist.", checked((ushort)quantity));
        }

        var remaining = quantity - 1;
        if (!await ConsumeCharacterItemAsync(
                connection, transaction, requesterCharacterId, couponItemCode,
                quantity, remaining, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The separation-coupon quantity changed.", checked((ushort)quantity));
        }

        await using (var close = connection.CreateCommand())
        {
            close.Transaction = transaction;
            close.CommandText = """
                UPDATE CoupleRelations
                SET EndedAt = $endedAt, EndItemCode = $itemCode
                WHERE Id = $relationId AND EndedAt IS NULL
                """;
            close.Parameters.AddWithValue("$endedAt", DateTime.UtcNow.ToString("O"));
            close.Parameters.AddWithValue("$itemCode", couponItemCode);
            close.Parameters.AddWithValue("$relationId", relationId);
            if (await close.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The couple relation changed.", checked((ushort)quantity));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, checked((ushort)remaining));
    }

    private static async Task<bool> ConsumeCharacterItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        uint itemCode,
        long quantity,
        long remaining,
        CancellationToken cancellationToken)
    {
        await using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = remaining == 0
            ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity"
            : "UPDATE CharacterItems SET Quantity = $remaining, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity";
        consume.Parameters.AddWithValue("$characterId", characterId);
        consume.Parameters.AddWithValue("$itemCode", itemCode);
        consume.Parameters.AddWithValue("$quantity", quantity);
        if (remaining != 0)
        {
            consume.Parameters.AddWithValue("$remaining", remaining);
            consume.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        }
        return await consume.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static CoupleRelationRecord ReadCoupleRelation(SqliteDataReader reader)
        => new()
        {
            Id = reader.GetInt64(0),
            Character1Id = reader.GetInt64(1),
            Character1Name = reader.GetString(2),
            Character2Id = reader.GetInt64(3),
            Character2Name = reader.GetString(4),
            RingItemCode = checked((uint)reader.GetInt64(5)),
            EstablishedAt = ParseDate(reader.GetString(6))
        };

    public async Task<long> RecordMentorInteractionAsync(
        ushort requestOpcode,
        long requesterCharacterId,
        long targetCharacterId,
        uint lessonCode,
        byte targetUid,
        CancellationToken cancellationToken = default)
    {
        if (requestOpcode is not (0xC583 or 0xC585))
            throw new ArgumentOutOfRangeException(nameof(requestOpcode));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MentorInteractions (
                RequestOpcode, RequesterCharacterId, TargetCharacterId,
                LessonCode, TargetUid, Status, CreatedAt, UpdatedAt)
            VALUES ($opcode, $requester, $target, $lessonCode, $targetUid, 0, $now, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$opcode", requestOpcode);
        command.Parameters.AddWithValue("$requester", requesterCharacterId);
        command.Parameters.AddWithValue("$target", targetCharacterId);
        command.Parameters.AddWithValue("$lessonCode", (long)lessonCode);
        command.Parameters.AddWithValue("$targetUid", targetUid);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Mentor interaction insert returned no identity."));
    }

    public async Task CompleteMentorInteractionAsync(
        long interactionId,
        ushort status,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE MentorInteractions
            SET Status = $status, UpdatedAt = $now
            WHERE Id = $id AND Status = 0;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", interactionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MentorAdvertisementAdminRecord>> GetMentorAdvertisementsForAdminAsync(
        CancellationToken cancellationToken = default)
    {
        var records = new List<MentorAdvertisementAdminRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT C.Id, C.AccountId, A.Username, C.Name,
                   COALESCE(M.IsAdvertising, 0), C.IsOnline, C.CurrentChannelId,
                   COALESCE(M.UpdatedAt, C.CreatedAt)
            FROM Characters C
            JOIN Accounts A ON A.Id = C.AccountId
            LEFT JOIN CharacterMentorAdvertisements M ON M.CharacterId = C.Id
            ORDER BY COALESCE(M.IsAdvertising, 0) DESC, C.IsOnline DESC, C.Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new MentorAdvertisementAdminRecord
            {
                CharacterId = reader.GetInt64(0),
                AccountId = reader.GetInt64(1),
                Username = reader.GetString(2),
                CharacterName = reader.GetString(3),
                IsAdvertising = reader.GetInt64(4) != 0,
                IsOnline = reader.GetInt64(5) != 0,
                ChannelId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                UpdatedAtUtc = ParseDate(reader.GetString(7))
            });
        }
        return records;
    }

    public async Task<IReadOnlyList<MentorInteractionAdminRecord>> GetMentorInteractionsForAdminAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var records = new List<MentorInteractionAdminRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT M.Id, M.RequestOpcode, Requester.Name, Target.Name,
                   M.LessonCode, M.Status, M.CreatedAt, M.UpdatedAt
            FROM MentorInteractions M
            JOIN Characters Requester ON Requester.Id = M.RequesterCharacterId
            JOIN Characters Target ON Target.Id = M.TargetCharacterId
            ORDER BY M.Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new MentorInteractionAdminRecord
            {
                Id = reader.GetInt64(0),
                RequestOpcode = checked((ushort)reader.GetInt32(1)),
                RequesterName = reader.GetString(2),
                TargetName = reader.GetString(3),
                LessonCode = checked((uint)reader.GetInt64(4)),
                Status = checked((ushort)reader.GetInt32(5)),
                CreatedAtUtc = ParseDate(reader.GetString(6)),
                UpdatedAtUtc = ParseDate(reader.GetString(7))
            });
        }
        return records;
    }

    private static async Task RecordLoginAsync(
        SqliteConnection connection,
        long accountId,
        string? ip,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE Accounts SET LastLoginAt = $last, LastIp = $ip WHERE Id = $id";
        update.Parameters.AddWithValue("$last", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", accountId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddRuntimeStateParameters(SqliteCommand command, long characterId, CharacterRuntimeState state)
    {
        var position = TownPositionPolicy.Normalize(state.PositionX, state.PositionY);
        command.Parameters.AddWithValue("$currentHp", state.CurrentHp);
        command.Parameters.AddWithValue("$currentMp", state.CurrentMp);
        command.Parameters.AddWithValue("$mapId", state.CurrentMapId);
        command.Parameters.AddWithValue("$townPage", state.CurrentTownPage);
        command.Parameters.AddWithValue("$positionX", position.X);
        command.Parameters.AddWithValue("$positionY", position.Y);
        command.Parameters.AddWithValue("$channelId", (object?)state.CurrentChannelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$characterId", characterId);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=15000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task MigrateDungeonProgressToOfficialLayoutAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var progressColumns = await GetTableColumnsAsync(
            connection,
            "DungeonProgress",
            cancellationToken);
        var legacyLayout = progressColumns.Contains("Dungeon");
        var officialLayout = progressColumns.Contains("Difficulty");
        if (!legacyLayout && !officialLayout)
            throw new InvalidDataException("DungeonProgress has neither the legacy Dungeon key nor the official Difficulty key.");

        var hasSuperBossTable = await TableExistsAsync(
            connection,
            "DungeonSuperBossProgress",
            cancellationToken);
        if (legacyLayout && !progressColumns.Contains("BestRatings"))
            await EnsureColumnAsync(connection, "DungeonProgress", "BestRatings", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        if (hasSuperBossTable)
        {
            var superBossColumns = await GetTableColumnsAsync(
                connection,
                "DungeonSuperBossProgress",
                cancellationToken);
            if (!superBossColumns.Contains("BestRatings"))
                await EnsureColumnAsync(connection, "DungeonSuperBossProgress", "BestRatings", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
        if (legacyLayout)
        {
            await using var transpose = connection.CreateCommand();
            transpose.Transaction = transaction;
            transpose.CommandText = """
                ALTER TABLE DungeonProgress RENAME TO DungeonProgressLegacyV3;
                CREATE TABLE DungeonProgress (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    Episode INTEGER NOT NULL CHECK (Episode BETWEEN 0 AND 19),
                    Difficulty INTEGER NOT NULL CHECK (Difficulty BETWEEN 0 AND 2),
                    ClearMask INTEGER NOT NULL DEFAULT 0 CHECK (ClearMask BETWEEN 0 AND 255),
                    BestRatings INTEGER NOT NULL DEFAULT 0 CHECK (BestRatings BETWEEN 0 AND 255),
                    BestScore INTEGER NOT NULL DEFAULT 0,
                    BestElapsedMinutes INTEGER NULL,
                    ClearedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, Episode, Difficulty)
                );
                WITH Difficulties(Difficulty) AS (VALUES(0), (1), (2))
                INSERT INTO DungeonProgress(
                    CharacterId, Episode, Difficulty, ClearMask, BestRatings,
                    BestScore, BestElapsedMinutes, ClearedAt, UpdatedAt)
                SELECT legacy.CharacterId,
                       legacy.Episode,
                       difficulties.Difficulty,
                       SUM(CASE
                           WHEN (legacy.ClearMask & (1 << difficulties.Difficulty)) != 0
                           THEN 1 << legacy.Dungeon
                           ELSE 0
                       END),
                       SUM(((legacy.BestRatings >> (difficulties.Difficulty * 2)) & 3)
                           << (legacy.Dungeon * 2)),
                       MAX(legacy.BestScore),
                       MIN(legacy.BestElapsedMinutes),
                       MIN(legacy.ClearedAt),
                       MAX(legacy.UpdatedAt)
                FROM DungeonProgressLegacyV3 AS legacy
                CROSS JOIN Difficulties AS difficulties
                GROUP BY legacy.CharacterId, legacy.Episode, difficulties.Difficulty;
                DROP TABLE DungeonProgressLegacyV3;
                """;
            await transpose.ExecuteNonQueryAsync(cancellationToken);
        }

        if (hasSuperBossTable)
        {
            await using var mergeSuperBoss = connection.CreateCommand();
            mergeSuperBoss.Transaction = transaction;
            mergeSuperBoss.CommandText = """
                WITH Difficulties(Difficulty) AS (VALUES(0), (1), (2))
                INSERT INTO DungeonProgress(
                    CharacterId, Episode, Difficulty, ClearMask, BestRatings,
                    BestScore, BestElapsedMinutes, ClearedAt, UpdatedAt)
                SELECT legacy.CharacterId,
                       legacy.Episode,
                       difficulties.Difficulty,
                       CASE
                           WHEN (legacy.ClearMask & (1 << difficulties.Difficulty)) != 0
                           THEN 8
                           ELSE 0
                       END,
                       ((legacy.BestRatings >> (difficulties.Difficulty * 2)) & 3) << 6,
                       legacy.BestScore,
                       legacy.BestElapsedMinutes,
                       legacy.ClearedAt,
                       legacy.UpdatedAt
                FROM DungeonSuperBossProgress AS legacy
                CROSS JOIN Difficulties AS difficulties
                WHERE (legacy.ClearMask & (1 << difficulties.Difficulty)) != 0
                   OR ((legacy.BestRatings >> (difficulties.Difficulty * 2)) & 3) != 0
                ON CONFLICT(CharacterId, Episode, Difficulty) DO UPDATE SET
                    ClearMask = DungeonProgress.ClearMask | excluded.ClearMask,
                    BestRatings = (DungeonProgress.BestRatings & 63)
                        | MAX(DungeonProgress.BestRatings & 192, excluded.BestRatings & 192),
                    BestScore = MAX(DungeonProgress.BestScore, excluded.BestScore),
                    BestElapsedMinutes = CASE
                        WHEN DungeonProgress.BestElapsedMinutes IS NULL THEN excluded.BestElapsedMinutes
                        WHEN excluded.BestElapsedMinutes IS NULL THEN DungeonProgress.BestElapsedMinutes
                        ELSE MIN(DungeonProgress.BestElapsedMinutes, excluded.BestElapsedMinutes)
                    END,
                    ClearedAt = MIN(DungeonProgress.ClearedAt, excluded.ClearedAt),
                    UpdatedAt = MAX(DungeonProgress.UpdatedAt, excluded.UpdatedAt);
                DROP TABLE DungeonSuperBossProgress;
                """;
            await mergeSuperBoss.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = """
                INSERT OR IGNORE INTO SchemaMigrations(Name, AppliedAt)
                VALUES('official-dungeon-progress-layout-v3', $now)
                """;
            marker.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await marker.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateDungeonStagePerformanceAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText = """
            CREATE TABLE IF NOT EXISTS DungeonStagePerformance (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                Episode INTEGER NOT NULL CHECK (Episode BETWEEN 0 AND 19),
                Difficulty INTEGER NOT NULL CHECK (Difficulty BETWEEN 0 AND 2),
                ArchiveSlot INTEGER NOT NULL CHECK (ArchiveSlot BETWEEN 0 AND 3),
                BestScore INTEGER NOT NULL DEFAULT 0 CHECK (BestScore >= 0),
                BestElapsedMinutes INTEGER NULL CHECK (BestElapsedMinutes IS NULL OR BestElapsedMinutes >= 0),
                ClearedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (CharacterId, Episode, Difficulty, ArchiveSlot)
            );
            INSERT OR IGNORE INTO DungeonStagePerformance(
                CharacterId, Episode, Difficulty, ArchiveSlot, BestScore,
                BestElapsedMinutes, ClearedAt, UpdatedAt)
            SELECT CharacterId,
                   Episode,
                   Difficulty,
                   CASE
                       WHEN (ClearMask & 8) != 0 THEN 3
                       WHEN (ClearMask & 4) != 0 THEN 2
                       WHEN (ClearMask & 2) != 0 THEN 1
                       ELSE 0
                   END,
                   BestScore,
                   BestElapsedMinutes,
                   ClearedAt,
                   UpdatedAt
            FROM DungeonProgress
            WHERE ClearMask != 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM SchemaMigrations
                  WHERE Name = 'dungeon-stage-performance-v1'
              );
            INSERT OR IGNORE INTO SchemaMigrations(Name, AppliedAt)
            VALUES('dungeon-stage-performance-v1', $now);
            """;
        migration.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await migration.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateLegacyDungeonLowDifficultySelectorAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var applied = connection.CreateCommand())
        {
            applied.CommandText = """
                SELECT 1
                FROM SchemaMigrations
                WHERE Name = 'official-dungeon-normal-low-selector-v1'
                """;
            if (await applied.ExecuteScalarAsync(cancellationToken) is not null)
                return;
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var migrate = connection.CreateCommand())
        {
            migrate.Transaction = transaction;
            migrate.CommandText = """
                DROP TABLE IF EXISTS temp.LegacyDungeonNormalLowSlots;
                CREATE TEMP TABLE LegacyDungeonNormalLowSlots AS
                SELECT performance.CharacterId,
                       performance.Episode,
                       performance.ArchiveSlot,
                       performance.BestScore,
                       performance.BestElapsedMinutes,
                       performance.ClearedAt,
                       performance.UpdatedAt,
                       (progress.BestRatings >> (performance.ArchiveSlot * 2)) & 3 AS BestRating
                FROM DungeonStagePerformance AS performance
                INNER JOIN DungeonProgress AS progress
                    ON progress.CharacterId = performance.CharacterId
                   AND progress.Episode = performance.Episode
                   AND progress.Difficulty = performance.Difficulty
                WHERE performance.Difficulty = 2
                  AND performance.ArchiveSlot BETWEEN 0 AND 2
                  AND performance.BestScore > 0;

                INSERT INTO DungeonProgress(
                    CharacterId, Episode, Difficulty, ClearMask, BestRatings,
                    BestScore, BestElapsedMinutes, ClearedAt, UpdatedAt)
                SELECT CharacterId,
                       Episode,
                       0,
                       SUM(1 << ArchiveSlot),
                       SUM(BestRating << (ArchiveSlot * 2)),
                       MAX(BestScore),
                       MIN(BestElapsedMinutes),
                       MIN(ClearedAt),
                       MAX(UpdatedAt)
                FROM LegacyDungeonNormalLowSlots
                WHERE 1 = 1
                GROUP BY CharacterId, Episode
                ON CONFLICT(CharacterId, Episode, Difficulty) DO UPDATE SET
                    ClearMask = DungeonProgress.ClearMask | excluded.ClearMask,
                    BestRatings = MAX(DungeonProgress.BestRatings & 3, excluded.BestRatings & 3)
                        | MAX(DungeonProgress.BestRatings & 12, excluded.BestRatings & 12)
                        | MAX(DungeonProgress.BestRatings & 48, excluded.BestRatings & 48)
                        | (DungeonProgress.BestRatings & 192),
                    BestScore = MAX(DungeonProgress.BestScore, excluded.BestScore),
                    BestElapsedMinutes = CASE
                        WHEN DungeonProgress.BestElapsedMinutes IS NULL THEN excluded.BestElapsedMinutes
                        WHEN excluded.BestElapsedMinutes IS NULL THEN DungeonProgress.BestElapsedMinutes
                        ELSE MIN(DungeonProgress.BestElapsedMinutes, excluded.BestElapsedMinutes)
                    END,
                    ClearedAt = MIN(DungeonProgress.ClearedAt, excluded.ClearedAt),
                    UpdatedAt = MAX(DungeonProgress.UpdatedAt, excluded.UpdatedAt);

                INSERT INTO DungeonStagePerformance(
                    CharacterId, Episode, Difficulty, ArchiveSlot, BestScore,
                    BestElapsedMinutes, ClearedAt, UpdatedAt)
                SELECT CharacterId,
                       Episode,
                       0,
                       ArchiveSlot,
                       BestScore,
                       BestElapsedMinutes,
                       ClearedAt,
                       UpdatedAt
                FROM LegacyDungeonNormalLowSlots
                WHERE 1 = 1
                ON CONFLICT(CharacterId, Episode, Difficulty, ArchiveSlot) DO UPDATE SET
                    BestScore = MAX(DungeonStagePerformance.BestScore, excluded.BestScore),
                    BestElapsedMinutes = CASE
                        WHEN DungeonStagePerformance.BestElapsedMinutes IS NULL THEN excluded.BestElapsedMinutes
                        WHEN excluded.BestElapsedMinutes IS NULL THEN DungeonStagePerformance.BestElapsedMinutes
                        ELSE MIN(DungeonStagePerformance.BestElapsedMinutes, excluded.BestElapsedMinutes)
                    END,
                    ClearedAt = MIN(DungeonStagePerformance.ClearedAt, excluded.ClearedAt),
                    UpdatedAt = MAX(DungeonStagePerformance.UpdatedAt, excluded.UpdatedAt);

                UPDATE DungeonProgress
                SET ClearMask = ClearMask & ~COALESCE((
                        SELECT SUM(1 << moved.ArchiveSlot)
                        FROM LegacyDungeonNormalLowSlots AS moved
                        WHERE moved.CharacterId = DungeonProgress.CharacterId
                          AND moved.Episode = DungeonProgress.Episode
                    ), 0),
                    BestRatings = BestRatings & ~COALESCE((
                        SELECT SUM(3 << (moved.ArchiveSlot * 2))
                        FROM LegacyDungeonNormalLowSlots AS moved
                        WHERE moved.CharacterId = DungeonProgress.CharacterId
                          AND moved.Episode = DungeonProgress.Episode
                    ), 0)
                WHERE Difficulty = 2
                  AND EXISTS (
                      SELECT 1
                      FROM LegacyDungeonNormalLowSlots AS moved
                      WHERE moved.CharacterId = DungeonProgress.CharacterId
                        AND moved.Episode = DungeonProgress.Episode
                  );

                DELETE FROM DungeonStagePerformance
                WHERE Difficulty = 2
                  AND EXISTS (
                      SELECT 1
                      FROM LegacyDungeonNormalLowSlots AS moved
                      WHERE moved.CharacterId = DungeonStagePerformance.CharacterId
                        AND moved.Episode = DungeonStagePerformance.Episode
                        AND moved.ArchiveSlot = DungeonStagePerformance.ArchiveSlot
                  );

                UPDATE DungeonProgress
                SET BestScore = COALESCE((
                        SELECT MAX(performance.BestScore)
                        FROM DungeonStagePerformance AS performance
                        WHERE performance.CharacterId = DungeonProgress.CharacterId
                          AND performance.Episode = DungeonProgress.Episode
                          AND performance.Difficulty = 2
                    ), 0),
                    BestElapsedMinutes = (
                        SELECT MIN(performance.BestElapsedMinutes)
                        FROM DungeonStagePerformance AS performance
                        WHERE performance.CharacterId = DungeonProgress.CharacterId
                          AND performance.Episode = DungeonProgress.Episode
                          AND performance.Difficulty = 2
                    ),
                    ClearedAt = COALESCE((
                        SELECT MIN(performance.ClearedAt)
                        FROM DungeonStagePerformance AS performance
                        WHERE performance.CharacterId = DungeonProgress.CharacterId
                          AND performance.Episode = DungeonProgress.Episode
                          AND performance.Difficulty = 2
                    ), ClearedAt),
                    UpdatedAt = COALESCE((
                        SELECT MAX(performance.UpdatedAt)
                        FROM DungeonStagePerformance AS performance
                        WHERE performance.CharacterId = DungeonProgress.CharacterId
                          AND performance.Episode = DungeonProgress.Episode
                          AND performance.Difficulty = 2
                    ), UpdatedAt)
                WHERE Difficulty = 2
                  AND EXISTS (
                      SELECT 1
                      FROM LegacyDungeonNormalLowSlots AS moved
                      WHERE moved.CharacterId = DungeonProgress.CharacterId
                        AND moved.Episode = DungeonProgress.Episode
                  );

                DELETE FROM DungeonProgress
                WHERE Difficulty = 2
                  AND ClearMask = 0
                  AND EXISTS (
                      SELECT 1
                      FROM LegacyDungeonNormalLowSlots AS moved
                      WHERE moved.CharacterId = DungeonProgress.CharacterId
                        AND moved.Episode = DungeonProgress.Episode
                  );
                DROP TABLE temp.LegacyDungeonNormalLowSlots;
                """;
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = """
                INSERT INTO SchemaMigrations(Name, AppliedAt)
                VALUES('official-dungeon-normal-low-selector-v1', $now)
                """;
            marker.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await marker.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateLegacyAdapterSettingsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // Preserve existing local state while exposing the current adapter-facing name.
        const string legacyTable = "Ser" + "verSettings";
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1";
        exists.Parameters.AddWithValue("$table", legacyTable);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            return;

        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = $"""
            INSERT OR REPLACE INTO AdapterSettings(Key, Value, UpdatedAt)
            SELECT Key, Value, UpdatedAt FROM [{legacyTable}];
            DROP TABLE [{legacyTable}];
            """;
        await migrate.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateQuickSlotIdentitySchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var uniqueIndexes = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "PRAGMA index_list([CharacterQuickSlots])";
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetInt32(2) != 0) uniqueIndexes.Add(reader.GetString(1));
        }

        var uniqueColumnSets = new List<string[]>();
        foreach (var index in uniqueIndexes)
        {
            var escaped = index.Replace("]", "]]", StringComparison.Ordinal);
            await using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info([{escaped}])";
            var columns = new List<(int Sequence, string Name)>();
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add((reader.GetInt32(0), reader.GetString(2)));
            uniqueColumnSets.Add(columns.OrderBy(column => column.Sequence).Select(column => column.Name).ToArray());
        }

        static bool Matches(string[] columns, string second) =>
            columns.Length == 2
            && string.Equals(columns[0], "CharacterId", StringComparison.OrdinalIgnoreCase)
            && string.Equals(columns[1], second, StringComparison.OrdinalIgnoreCase);

        var hasInventoryIdentity = uniqueColumnSets.Any(columns => Matches(columns, "InventoryIndex"));
        var hasItemCodeIdentity = uniqueColumnSets.Any(columns => Matches(columns, "ItemCode"));
        if (hasInventoryIdentity && !hasItemCodeIdentity) return;

        await using var transaction = connection.BeginTransaction();
        await using (var migrate = connection.CreateCommand())
        {
            migrate.Transaction = transaction;
            migrate.CommandText = """
                DROP TABLE IF EXISTS CharacterQuickSlotsIdentityV2;
                CREATE TABLE CharacterQuickSlotsIdentityV2 (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    Slot INTEGER NOT NULL CHECK (Slot BETWEEN 0 AND 5),
                    ItemCode INTEGER NOT NULL CHECK (ItemCode BETWEEN 1 AND 4294967295),
                    InventoryIndex INTEGER NOT NULL CHECK (InventoryIndex BETWEEN 0 AND 83),
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, Slot),
                    UNIQUE (CharacterId, InventoryIndex)
                );
                INSERT OR IGNORE INTO CharacterQuickSlotsIdentityV2(
                    CharacterId, Slot, ItemCode, InventoryIndex, UpdatedAt)
                SELECT CharacterId, Slot, ItemCode, InventoryIndex, UpdatedAt
                FROM CharacterQuickSlots
                WHERE Slot BETWEEN 0 AND 5
                  AND ItemCode BETWEEN 1 AND 4294967295
                  AND InventoryIndex BETWEEN 0 AND 83
                ORDER BY UpdatedAt DESC, Slot ASC;
                DROP TABLE CharacterQuickSlots;
                ALTER TABLE CharacterQuickSlotsIdentityV2 RENAME TO CharacterQuickSlots;
                INSERT OR REPLACE INTO SchemaMigrations(Name, AppliedAt)
                VALUES('quick-slot-inventory-identity-v2', $now);
                """;
            migrate.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{table}])";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table";
        command.Parameters.AddWithValue("$table", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"PRAGMA table_info([{table}])";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        await reader.CloseAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE [{table}] ADD COLUMN [{column}] {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CharacterRecord ReadCharacter(SqliteDataReader reader)
    {
        return new CharacterRecord
        {
            Id = reader.GetInt64(0),
            AccountId = reader.GetInt64(1),
            Name = reader.GetString(2),
            Gender = reader.GetInt32(3),
            Face = reader.GetInt32(4),
            Appearance = ((byte[])reader[5]).Concat(new byte[36]).Take(36).ToArray(),
            TutorialCompleted = reader.GetInt64(6) != 0,
            PetVariant = reader.GetInt32(7),
            EquippedPetItemCode = checked((uint)reader.GetInt64(8)),
            PetLevel = reader.GetInt32(9),
            PetExperience = reader.GetInt64(10),
            Level = reader.GetInt32(11),
            Experience = reader.GetInt64(12),
            AttributePoints = reader.GetInt32(13),
            Strength = reader.GetInt32(14),
            Vitality = reader.GetInt32(15),
            Agility = reader.GetInt32(16),
            Intelligence = reader.GetInt32(17),
            Luck = reader.GetInt32(18),
            MaxHp = reader.GetInt32(19),
            MaxMp = reader.GetInt32(20),
            CurrentHp = reader.GetInt32(21),
            CurrentMp = reader.GetInt32(22),
            SpawnMapId = reader.GetInt32(23),
            SpawnX = reader.GetInt32(24),
            SpawnY = reader.GetInt32(25),
            CurrentMapId = reader.GetInt32(26),
            CurrentTownPage = reader.GetInt32(27),
            PositionX = reader.GetInt32(28),
            PositionY = reader.GetInt32(29),
            CurrentChannelId = reader.IsDBNull(30) ? null : reader.GetInt32(30),
            IsOnline = reader.GetInt64(31) != 0,
            OnlineSince = ReadNullableDate(reader, 32),
            LastOfflineAt = ReadNullableDate(reader, 33),
            LastSavedAt = ReadNullableDate(reader, 34),
            CreatedAt = ParseDate(reader.GetString(35)),
            Hans = reader.GetInt64(36),
            Cash = reader.GetInt64(37),
            CardGuideStep = checked((byte)reader.GetInt32(38)),
            CardSummonCount = checked((byte)reader.GetInt32(39)),
            CardMysteryKeyCount = checked((byte)reader.GetInt32(40)),
            CardGoldenKeyCount = checked((byte)reader.GetInt32(41)),
            SkillPoints = checked((ushort)reader.GetInt32(42)),
            SelectedSkill0 = checked((uint)reader.GetInt64(43)),
            SelectedSkill1 = checked((uint)reader.GetInt64(44)),
            SkillSlotExpansionExpires = checked((uint)reader.GetInt64(45)),
            MikeChannelUseCount = checked((byte)reader.GetInt32(46)),
            MikeGlobalUseCount = checked((byte)reader.GetInt32(47)),
            RevivalUseCount = checked((byte)reader.GetInt32(48)),
            AvatarInventoryExpansionExpires = checked((uint)reader.GetInt64(49)),
            PetInventoryExpansionExpires = checked((uint)reader.GetInt64(50)),
            GameInventoryExpansionExpires = checked((uint)reader.GetInt64(51)),
            InteriorInventoryExpansionExpires = checked((uint)reader.GetInt64(52)),
            QuickSlotExpansionExpires = checked((uint)reader.GetInt64(53)),
            FreeMagicExpansionExpires = checked((uint)reader.GetInt64(54)),
            AttackModifier = checked((uint)reader.GetInt64(55)),
            DefenseFlat = checked((ushort)reader.GetInt32(56)),
            InitialAttackMode = checked((byte)reader.GetInt32(57))
        };
    }

    private static async Task<List<CharacterItemRecord>> GetCharacterItemsAsync(
        SqliteConnection connection,
        long characterId,
        CancellationToken cancellationToken)
    {
        var result = new List<CharacterItemRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ItemCode, Quantity, PetDurability,
                   PetCurrentStage, PetMaximumStage, PetLevel, PetExperience,
                   PetAccessory0, PetAccessory1, PetAccessory2
            FROM CharacterItems
            WHERE CharacterId = $characterId
            ORDER BY ItemCode
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CharacterItemRecord
            {
                ItemCode = checked((uint)reader.GetInt64(0)),
                Quantity = checked((ushort)reader.GetInt32(1)),
                PetDurability = reader.IsDBNull(2) ? null : checked((short)reader.GetInt32(2)),
                PetCurrentStage = checked((byte)reader.GetInt32(3)),
                PetMaximumStage = checked((byte)reader.GetInt32(4)),
                PetLevel = checked((uint)reader.GetInt64(5)),
                PetExperience = checked((uint)reader.GetInt64(6)),
                PetAccessory0 = checked((uint)reader.GetInt64(7)),
                PetAccessory1 = checked((uint)reader.GetInt64(8)),
                PetAccessory2 = checked((uint)reader.GetInt64(9))
            });
        }
        return result;
    }

    private static async Task<List<CharacterQuickSlotRecord>> GetCharacterQuickSlotsAsync(
        SqliteConnection connection,
        long characterId,
        CancellationToken cancellationToken)
    {
        var result = new List<CharacterQuickSlotRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Slot, ItemCode, InventoryIndex
            FROM CharacterQuickSlots
            WHERE CharacterId = $characterId
            ORDER BY Slot
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CharacterQuickSlotRecord
            {
                Slot = checked((byte)reader.GetInt32(0)),
                ItemCode = checked((uint)reader.GetInt64(1)),
                InventoryIndex = checked((byte)reader.GetInt32(2))
            });
        }
        return result;
    }

    private static async Task<List<CharacterItemRecord>> GetCharacterCashInboxItemsAsync(
        SqliteConnection connection,
        long characterId,
        CancellationToken cancellationToken)
    {
        var result = new List<CharacterItemRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ItemCode, Quantity FROM CharacterCashInboxItems WHERE CharacterId = $characterId ORDER BY ItemCode";
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CharacterItemRecord
            {
                ItemCode = checked((uint)reader.GetInt64(0)),
                Quantity = checked((ushort)reader.GetInt32(1))
            });
        }
        return result;
    }

    private static DateTime ParseDate(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTime? ReadNullableDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));
}
