using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    private readonly SemaphoreSlim _mentorshipSchemaGate = new(1, 1);
    private bool _mentorshipSchemaReady;

    public async Task InitializeMentorshipAsync(CancellationToken token = default)
    {
        await _mentorshipSchemaGate.WaitAsync(token);
        try
        {
            if (_mentorshipSchemaReady) return;
            await using var connection = await OpenConnectionAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS MentorshipRelations (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TeacherCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    StudentCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    State INTEGER NOT NULL DEFAULT 0 CHECK(State BETWEEN 0 AND 2),
                    CreatedAt TEXT NOT NULL,
                    EndedAt TEXT NULL,
                    CHECK(TeacherCharacterId <> StudentCharacterId),
                    CHECK((State = 0 AND EndedAt IS NULL) OR (State <> 0 AND EndedAt IS NOT NULL))
                );
                CREATE UNIQUE INDEX IF NOT EXISTS UX_MentorshipRelations_ActiveStudent
                    ON MentorshipRelations(StudentCharacterId) WHERE State = 0;
                CREATE INDEX IF NOT EXISTS IX_MentorshipRelations_Teacher
                    ON MentorshipRelations(TeacherCharacterId, State);
                CREATE TABLE IF NOT EXISTS MentorshipRequests (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Direction INTEGER NOT NULL CHECK(Direction IN (0, 1)),
                    RequesterAccountId INTEGER NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                    RequesterCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    RequesterSessionId TEXT NOT NULL,
                    TargetAccountId INTEGER NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                    TargetCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    TargetSessionId TEXT NOT NULL,
                    ChannelId INTEGER NOT NULL,
                    TeacherCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    StudentCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    State INTEGER NOT NULL DEFAULT 0 CHECK(State BETWEEN 0 AND 5),
                    CreatedAt TEXT NOT NULL,
                    ExpiresAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    RelationId INTEGER NULL REFERENCES MentorshipRelations(Id) ON DELETE SET NULL,
                    CHECK(RequesterCharacterId <> TargetCharacterId),
                    CHECK(RequesterAccountId <> TargetAccountId),
                    CHECK(TeacherCharacterId <> StudentCharacterId),
                    CHECK((Direction = 0 AND TeacherCharacterId = TargetCharacterId AND StudentCharacterId = RequesterCharacterId)
                       OR (Direction = 1 AND TeacherCharacterId = RequesterCharacterId AND StudentCharacterId = TargetCharacterId))
                );
                CREATE UNIQUE INDEX IF NOT EXISTS UX_MentorshipRequests_PendingStudent
                    ON MentorshipRequests(StudentCharacterId) WHERE State = 0;
                CREATE INDEX IF NOT EXISTS IX_MentorshipRequests_Teacher
                    ON MentorshipRequests(TeacherCharacterId, State);
                CREATE TABLE IF NOT EXISTS MentorshipLessons (
                    RelationId INTEGER NOT NULL REFERENCES MentorshipRelations(Id) ON DELETE CASCADE,
                    LessonCode INTEGER NOT NULL CHECK(LessonCode BETWEEN 1 AND 4294967295),
                    CompletionKey TEXT NOT NULL,
                    CompletedAt TEXT NOT NULL,
                    PRIMARY KEY(RelationId, LessonCode), UNIQUE(RelationId, CompletionKey)
                );
                CREATE TABLE IF NOT EXISTS MentorshipSettlements (
                    SettlementKey TEXT PRIMARY KEY, Episode INTEGER NOT NULL, DungeonBit INTEGER NOT NULL,
                    TeamIdentity TEXT NOT NULL, CreatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS MentorshipSettlementRelations (
                    SettlementKey TEXT NOT NULL REFERENCES MentorshipSettlements(SettlementKey) ON DELETE CASCADE,
                    RelationId INTEGER NOT NULL REFERENCES MentorshipRelations(Id) ON DELETE CASCADE,
                    PRIMARY KEY(SettlementKey, RelationId));
                CREATE TABLE IF NOT EXISTS MentorshipClearReceipts (
                    SettlementKey TEXT NOT NULL REFERENCES MentorshipSettlements(SettlementKey) ON DELETE CASCADE,
                    RelationId INTEGER NOT NULL REFERENCES MentorshipRelations(Id) ON DELETE CASCADE,
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    SessionId TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                    PRIMARY KEY(SettlementKey, RelationId, CharacterId));
                CREATE TABLE IF NOT EXISTS MentorshipGraduationAwards (
                    RelationId INTEGER PRIMARY KEY REFERENCES MentorshipRelations(Id) ON DELETE CASCADE,
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    ItemCode INTEGER NOT NULL, Quantity INTEGER NOT NULL, ResourceQuestId INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS MentorshipGraduations (
                    RelationId INTEGER PRIMARY KEY REFERENCES MentorshipRelations(Id) ON DELETE CASCADE,
                    CompletedAt TEXT NOT NULL,
                    RewardDisposition TEXT NOT NULL CHECK(RewardDisposition IN ('not-configured', 'granted'))
                );
                """;
            await command.ExecuteNonQueryAsync(token);
            using var definition = connection.CreateCommand();
            definition.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'MentorshipGraduations'";
            var ddl = (string?)await definition.ExecuteScalarAsync(token);
            if (ddl?.Contains("RewardDisposition = 'not-configured'", StringComparison.Ordinal) == true)
            {
                using var migration = connection.CreateCommand();
                migration.CommandText = """
                    BEGIN IMMEDIATE;
                    ALTER TABLE MentorshipGraduations RENAME TO MentorshipGraduationsPrevious;
                    CREATE TABLE MentorshipGraduations (
                        RelationId INTEGER PRIMARY KEY REFERENCES MentorshipRelations(Id) ON DELETE CASCADE,
                        CompletedAt TEXT NOT NULL,
                        RewardDisposition TEXT NOT NULL CHECK(RewardDisposition IN ('not-configured', 'granted')));
                    INSERT INTO MentorshipGraduations SELECT * FROM MentorshipGraduationsPrevious;
                    DROP TABLE MentorshipGraduationsPrevious;
                    COMMIT;
                    """;
                await migration.ExecuteNonQueryAsync(token);
            }
            _mentorshipSchemaReady = true;
        }
        finally { _mentorshipSchemaGate.Release(); }
    }

    private sealed record MentorshipCharacter(long Id, long AccountId, string Name, int Level, int Gender);

    private static SqliteCommand MentorshipCommand(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command;
    }

    private static async Task<long> MentorshipScalarAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken token, params (string Name, object? Value)[] values)
    {
        using var command = MentorshipCommand(connection, transaction, sql, values);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token) ?? 0L, CultureInfo.InvariantCulture);
    }

    private static async Task<MentorshipCharacter?> ReadMentorshipActorAsync(SqliteConnection connection,
        SqliteTransaction transaction, MentorshipActor actor, CancellationToken token)
    {
        if (actor.AccountId <= 0 || actor.CharacterId <= 0 || string.IsNullOrWhiteSpace(actor.SessionId)
            || actor.ChannelId <= 0) return null;
        using var command = MentorshipCommand(connection, transaction, """
            SELECT c.Id, c.AccountId, c.Name, c.Level, c.Gender FROM Characters c
            JOIN Accounts a ON a.Id = c.AccountId
            WHERE c.Id = $character AND c.AccountId = $account
              AND c.ActiveSessionId = $session AND a.ActiveSessionId = $session
              AND c.IsOnline = 1 AND a.IsOnline = 1 AND a.IsBanned = 0
              AND c.CurrentChannelId = $channel AND a.CurrentChannelId = $channel
            """, ("$character", actor.CharacterId), ("$account", actor.AccountId),
            ("$session", actor.SessionId), ("$channel", actor.ChannelId));
        using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)) : null;
    }

    private static async Task<int> ExpireMentorshipRequestsAsync(SqliteConnection connection,
        SqliteTransaction transaction, DateTime now, CancellationToken token)
    {
        using var command = MentorshipCommand(connection, transaction, """
            UPDATE MentorshipRequests SET State = 3, UpdatedAt = $now WHERE State = 0
              AND (ExpiresAt <= $now OR NOT EXISTS (
                SELECT 1 FROM Characters c JOIN Accounts a ON a.Id = c.AccountId
                WHERE c.Id = RequesterCharacterId AND a.Id = RequesterAccountId
                  AND c.ActiveSessionId = RequesterSessionId AND a.ActiveSessionId = RequesterSessionId
                  AND c.IsOnline = 1 AND a.IsOnline = 1 AND a.IsBanned = 0
                  AND c.CurrentChannelId = ChannelId AND a.CurrentChannelId = ChannelId)
              OR NOT EXISTS (
                SELECT 1 FROM Characters c JOIN Accounts a ON a.Id = c.AccountId
                WHERE c.Id = TargetCharacterId AND a.Id = TargetAccountId
                  AND c.ActiveSessionId = TargetSessionId AND a.ActiveSessionId = TargetSessionId
                  AND c.IsOnline = 1 AND a.IsOnline = 1 AND a.IsBanned = 0
                  AND c.CurrentChannelId = ChannelId AND a.CurrentChannelId = ChannelId))
            """, ("$now", now.ToString("O")));
        return await command.ExecuteNonQueryAsync(token);
    }

    public async Task<int> ExpireMentorshipRequestsAsync(CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        var count = await ExpireMentorshipRequestsAsync(connection, transaction, DateTime.UtcNow, token);
        transaction.Commit();
        return count;
    }

    private static async Task<bool> CanEstablishMentorshipAsync(SqliteConnection connection,
        SqliteTransaction transaction, MentorshipCharacter teacher, MentorshipCharacter student,
        MentorshipPolicy policy, long excludedRequestId, CancellationToken token)
    {
        if (teacher.Id == student.Id || teacher.AccountId == student.AccountId
            || (policy.GraduationMinimumLevel is int graduation && student.Level >= graduation)
            || teacher.Level < policy.MinimumTeacherLevel
            || (long)teacher.Level - student.Level < policy.MinimumLevelGap) return false;
        var roleConflicts = await MentorshipScalarAsync(connection, transaction, """
            SELECT (SELECT COUNT(*) FROM MentorshipRelations WHERE State = 0
                      AND (StudentCharacterId = $teacher OR StudentCharacterId = $student OR TeacherCharacterId = $student))
                 + (SELECT COUNT(*) FROM MentorshipRequests WHERE State = 0 AND Id <> $excluded
                      AND (StudentCharacterId = $teacher OR StudentCharacterId = $student OR TeacherCharacterId = $student))
            """, token, ("$teacher", teacher.Id), ("$student", student.Id), ("$excluded", excludedRequestId));
        if (roleConflicts != 0) return false;
        var students = await MentorshipScalarAsync(connection, transaction, """
            SELECT (SELECT COUNT(*) FROM MentorshipRelations WHERE State = 0 AND TeacherCharacterId = $teacher)
                 + (SELECT COUNT(*) FROM MentorshipRequests WHERE State = 0 AND TeacherCharacterId = $teacher AND Id <> $excluded)
            """, token, ("$teacher", teacher.Id), ("$excluded", excludedRequestId));
        return students < policy.MaximumStudents;
    }

    private static async Task<MentorshipQualification> ReadMentorshipQualificationAsync(
        SqliteConnection connection, SqliteTransaction transaction, MentorshipActor actor,
        MentorshipPolicy policy, CancellationToken token)
    {
        var character = await ReadMentorshipActorAsync(connection, transaction, actor, token);
        if (character is null) return new(false, false, false, false, false, 0, 0);
        var students = checked((int)await MentorshipScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM MentorshipRelations WHERE TeacherCharacterId = $id AND State = 0", token, ("$id", actor.CharacterId)));
        var reservations = await MentorshipScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM MentorshipRequests WHERE TeacherCharacterId = $id AND State = 0", token, ("$id", actor.CharacterId));
        var isStudent = await MentorshipScalarAsync(connection, transaction, """
            SELECT (SELECT COUNT(*) FROM MentorshipRelations WHERE StudentCharacterId = $id AND State = 0)
                 + (SELECT COUNT(*) FROM MentorshipRequests WHERE StudentCharacterId = $id AND State = 0)
            """, token, ("$id", actor.CharacterId)) > 0;
        var canTeach = !isStudent && character.Level >= policy.MinimumTeacherLevel && students + reservations < policy.MaximumStudents;
        var canStudy = !isStudent && students + reservations == 0
            && (policy.GraduationMinimumLevel is not int graduation || character.Level < graduation);
        var canGraduate = false;
        using (var query = MentorshipCommand(connection, transaction,
            "SELECT Id FROM MentorshipRelations WHERE StudentCharacterId = $id AND State = 0", ("$id", actor.CharacterId)))
        {
            var id = await query.ExecuteScalarAsync(token);
            if (id is long relationId)
                canGraduate = await CanGraduateMentorshipAsync(connection, transaction, relationId, character.Level, policy, token);
        }
        return new(true, canTeach, canTeach, canStudy, canGraduate, character.Level, students);
    }

    public async Task<MentorshipQualification> GetMentorshipQualificationAsync(MentorshipActor actor,
        MentorshipPolicy policy, CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        await ExpireMentorshipRequestsAsync(connection, transaction, DateTime.UtcNow, token);
        var result = await ReadMentorshipQualificationAsync(connection, transaction, actor, policy, token);
        transaction.Commit();
        return result;
    }

    public async Task<MentorshipResultCode> SetMentorshipAdvertisingAsync(MentorshipActor actor, bool enabled,
        MentorshipPolicy policy, CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        await ExpireMentorshipRequestsAsync(connection, transaction, DateTime.UtcNow, token);
        var qualification = await ReadMentorshipQualificationAsync(connection, transaction, actor, policy, token);
        if (!qualification.SessionOwned) return MentorshipResultCode.Unauthorized;
        if (enabled && !qualification.CanAdvertise) return MentorshipResultCode.Ineligible;
        var advertising = await MentorshipScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM CharacterMentorAdvertisements WHERE CharacterId = $id AND IsAdvertising = 1",
            token, ("$id", actor.CharacterId)) != 0;
        if (enabled && !advertising)
        {
            using var debit = MentorshipCommand(connection, transaction,
                "UPDATE Characters SET Hans = Hans - 100 WHERE Id = $id AND Hans >= 100",
                ("$id", actor.CharacterId));
            if (await debit.ExecuteNonQueryAsync(token) != 1) return MentorshipResultCode.Ineligible;
        }
        using var command = MentorshipCommand(connection, transaction, """
            INSERT INTO CharacterMentorAdvertisements(CharacterId, IsAdvertising, UpdatedAt)
            VALUES($id, $enabled, $now) ON CONFLICT(CharacterId) DO UPDATE SET
                IsAdvertising = excluded.IsAdvertising, UpdatedAt = excluded.UpdatedAt
            """, ("$id", actor.CharacterId), ("$enabled", enabled ? 1 : 0), ("$now", DateTime.UtcNow.ToString("O")));
        await command.ExecuteNonQueryAsync(token);
        transaction.Commit();
        return MentorshipResultCode.Success;
    }

    public async Task<IReadOnlyList<CharacterRecord>> GetMentorshipAdvertisingCharactersAsync(MentorshipActor actor,
        MentorshipPolicy policy, CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        if (await ReadMentorshipActorAsync(connection, transaction, actor, token) is null) return [];
        await ExpireMentorshipRequestsAsync(connection, transaction, DateTime.UtcNow, token);
        var candidates = new List<(MentorshipActor Actor, CharacterRecord Character)>();
        using (var query = MentorshipCommand(connection, transaction, """
            SELECT c.Id, c.AccountId, c.ActiveSessionId, c.Name, c.Level, c.Gender
            FROM Characters c JOIN Accounts a ON a.Id = c.AccountId
            JOIN CharacterMentorAdvertisements m ON m.CharacterId = c.Id
            WHERE m.IsAdvertising = 1 AND c.IsOnline = 1 AND a.IsOnline = 1 AND a.IsBanned = 0
              AND c.ActiveSessionId = a.ActiveSessionId AND c.CurrentChannelId = $channel
              AND a.CurrentChannelId = $channel ORDER BY c.Id
            """, ("$channel", actor.ChannelId)))
        {
            using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                candidates.Add((new(reader.GetInt64(1), reader.GetInt64(0), reader.GetString(2), actor.ChannelId),
                    new CharacterRecord { Id = reader.GetInt64(0), AccountId = reader.GetInt64(1),
                        Name = reader.GetString(3), Level = reader.GetInt32(4), Gender = reader.GetInt32(5) }));
        }
        var result = new List<CharacterRecord>();
        foreach (var candidate in candidates)
            if ((await ReadMentorshipQualificationAsync(connection, transaction, candidate.Actor, policy, token)).CanAdvertise)
                result.Add(candidate.Character);
        transaction.Commit();
        foreach (var character in result)
            character.DungeonGrade = await LoadDungeonGradeAsync(connection, character.Id, token);
        return result;
    }

    public async Task<MentorshipResult> CreateMentorshipRequestAsync(MentorshipActor requester, MentorshipActor target,
        MentorshipDirection direction, MentorshipPolicy policy, CancellationToken token = default)
    {
        if (direction is not (MentorshipDirection.StudentApplication or MentorshipDirection.TeacherInvitation))
            return new(MentorshipResultCode.Unsupported);
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow;
        await ExpireMentorshipRequestsAsync(connection, transaction, now, token);
        var source = await ReadMentorshipActorAsync(connection, transaction, requester, token);
        var destination = await ReadMentorshipActorAsync(connection, transaction, target, token);
        if (source is null || destination is null) return new(MentorshipResultCode.Unauthorized);
        if (requester.ChannelId != target.ChannelId) return new(MentorshipResultCode.Ineligible);
        var teacher = direction == MentorshipDirection.StudentApplication ? destination : source;
        var student = direction == MentorshipDirection.StudentApplication ? source : destination;
        if (!await CanEstablishMentorshipAsync(connection, transaction, teacher, student, policy, 0, token))
            return new(MentorshipResultCode.Ineligible);
        if (direction == MentorshipDirection.StudentApplication && await MentorshipScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM CharacterMentorAdvertisements WHERE CharacterId = $id AND IsAdvertising = 1",
            token, ("$id", teacher.Id)) == 0) return new(MentorshipResultCode.Ineligible);
        using var insert = MentorshipCommand(connection, transaction, """
            INSERT INTO MentorshipRequests(Direction, RequesterAccountId, RequesterCharacterId, RequesterSessionId,
                TargetAccountId, TargetCharacterId, TargetSessionId, ChannelId, TeacherCharacterId, StudentCharacterId,
                State, CreatedAt, ExpiresAt, UpdatedAt)
            VALUES($direction, $requesterAccount, $requester, $requesterSession, $targetAccount, $target,
                $targetSession, $channel, $teacher, $student, 0, $now, $expires, $now);
            SELECT last_insert_rowid();
            """, ("$direction", (int)direction), ("$requesterAccount", requester.AccountId),
            ("$requester", requester.CharacterId), ("$requesterSession", requester.SessionId),
            ("$targetAccount", target.AccountId), ("$target", target.CharacterId), ("$targetSession", target.SessionId),
            ("$channel", requester.ChannelId), ("$teacher", teacher.Id), ("$student", student.Id),
            ("$now", now.ToString("O")), ("$expires", (now + policy.RequestLifetime).ToString("O")));
        var id = (long)(await insert.ExecuteScalarAsync(token))!;
        var request = await ReadMentorshipRequestAsync(connection, transaction, id, token);
        transaction.Commit();
        return new(MentorshipResultCode.Success, request);
    }

    private static async Task<MentorshipRequestRecord?> ReadMentorshipRequestAsync(SqliteConnection connection,
        SqliteTransaction transaction, long requestId, CancellationToken token)
    {
        using var query = MentorshipCommand(connection, transaction, """
            SELECT Id, Direction, RequesterAccountId, RequesterCharacterId, RequesterSessionId,
                TargetAccountId, TargetCharacterId, TargetSessionId, ChannelId, TeacherCharacterId,
                StudentCharacterId, State, CreatedAt, ExpiresAt, RelationId FROM MentorshipRequests WHERE Id = $id
            """, ("$id", requestId));
        using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(reader.GetInt64(0), (MentorshipDirection)reader.GetInt32(1),
            new(reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetInt32(8)),
            new(reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt32(8)),
            reader.GetInt64(9), reader.GetInt64(10), (MentorshipRequestState)reader.GetInt32(11),
            ParseMentorshipTime(reader.GetString(12)), ParseMentorshipTime(reader.GetString(13)),
            reader.IsDBNull(14) ? null : reader.GetInt64(14));
    }

    private static DateTime ParseMentorshipTime(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static async Task<MentorshipRelationRecord?> ReadMentorshipRelationAsync(SqliteConnection connection,
        SqliteTransaction transaction, long relationId, CancellationToken token)
    {
        using var query = MentorshipCommand(connection, transaction, """
            SELECT r.Id, r.TeacherCharacterId, r.StudentCharacterId, r.State, r.CreatedAt, r.EndedAt,
              (SELECT COUNT(*) FROM MentorshipLessons l WHERE l.RelationId = r.Id),
              EXISTS(SELECT 1 FROM MentorshipGraduationAwards a WHERE a.RelationId = r.Id)
            FROM MentorshipRelations r WHERE r.Id = $id
            """, ("$id", relationId));
        using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), (MentorshipRelationState)reader.GetInt32(3),
                ParseMentorshipTime(reader.GetString(4)), reader.IsDBNull(5) ? null : ParseMentorshipTime(reader.GetString(5)),
                reader.GetInt32(6), reader.GetInt32(7) != 0) : null;
    }

    public async Task<MentorshipResult> RespondMentorshipRequestAsync(MentorshipActor responder, long requestId,
        bool accept, MentorshipPolicy policy, CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow;
        await ExpireMentorshipRequestsAsync(connection, transaction, now, token);
        var request = await ReadMentorshipRequestAsync(connection, transaction, requestId, token);
        if (request is null) return new(MentorshipResultCode.NotFound);
        if (request.Target != responder || await ReadMentorshipActorAsync(connection, transaction, responder, token) is null)
            return new(MentorshipResultCode.Unauthorized);
        if (request.State != MentorshipRequestState.Pending)
        {
            var existing = request.RelationId is long known ? await ReadMentorshipRelationAsync(connection, transaction, known, token) : null;
            transaction.Commit();
            return new(request.State == MentorshipRequestState.Expired ? MentorshipResultCode.Expired
                : MentorshipResultCode.AlreadyCompleted, request, existing);
        }
        var state = accept ? MentorshipRequestState.Accepted : MentorshipRequestState.Refused;
        long? relationId = null;
        var code = MentorshipResultCode.Success;
        if (accept)
        {
            var source = await ReadMentorshipActorAsync(connection, transaction, request.Requester, token);
            var target = await ReadMentorshipActorAsync(connection, transaction, request.Target, token);
            var teacher = request.Direction == MentorshipDirection.StudentApplication ? target : source;
            var student = request.Direction == MentorshipDirection.StudentApplication ? source : target;
            var advertised = request.Direction != MentorshipDirection.StudentApplication
                || await MentorshipScalarAsync(connection, transaction,
                    "SELECT COUNT(*) FROM CharacterMentorAdvertisements WHERE CharacterId = $id AND IsAdvertising = 1",
                    token, ("$id", request.TeacherCharacterId)) > 0;
            if (teacher is null || student is null || !advertised
                || !await CanEstablishMentorshipAsync(connection, transaction, teacher, student, policy, request.Id, token))
            {
                state = MentorshipRequestState.Ineligible;
                code = MentorshipResultCode.Ineligible;
            }
            else
            {
                using var insert = MentorshipCommand(connection, transaction, """
                    INSERT INTO MentorshipRelations(TeacherCharacterId, StudentCharacterId, State, CreatedAt)
                    VALUES($teacher, $student, 0, $now); SELECT last_insert_rowid();
                    """, ("$teacher", teacher.Id), ("$student", student.Id), ("$now", now.ToString("O")));
                relationId = (long)(await insert.ExecuteScalarAsync(token))!;
                using var stop = MentorshipCommand(connection, transaction, """
                    UPDATE CharacterMentorAdvertisements SET IsAdvertising = 0, UpdatedAt = $now
                    WHERE CharacterId = $student OR (CharacterId = $teacher AND
                        (SELECT COUNT(*) FROM MentorshipRelations WHERE TeacherCharacterId = $teacher AND State = 0) >= $capacity)
                    """, ("$now", now.ToString("O")), ("$student", student.Id), ("$teacher", teacher.Id), ("$capacity", policy.MaximumStudents));
                await stop.ExecuteNonQueryAsync(token);
            }
        }
        using (var update = MentorshipCommand(connection, transaction, """
            UPDATE MentorshipRequests SET State = $state, RelationId = $relation, UpdatedAt = $now WHERE Id = $id AND State = 0
            """, ("$state", (int)state), ("$relation", relationId), ("$now", now.ToString("O")), ("$id", request.Id)))
            await update.ExecuteNonQueryAsync(token);
        var relation = relationId is long id ? await ReadMentorshipRelationAsync(connection, transaction, id, token) : null;
        request = await ReadMentorshipRequestAsync(connection, transaction, request.Id, token);
        transaction.Commit();
        return new(code, request, relation);
    }

    public async Task<MentorshipResult> CancelMentorshipRequestAsync(MentorshipActor requester, long requestId,
        CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        await ExpireMentorshipRequestsAsync(connection, transaction, DateTime.UtcNow, token);
        var request = await ReadMentorshipRequestAsync(connection, transaction, requestId, token);
        if (request is null) return new(MentorshipResultCode.NotFound);
        if (request.Requester != requester || await ReadMentorshipActorAsync(connection, transaction, requester, token) is null)
            return new(MentorshipResultCode.Unauthorized);
        if (request.State != MentorshipRequestState.Pending)
        {
            transaction.Commit();
            return new(MentorshipResultCode.AlreadyCompleted, request);
        }
        using var update = MentorshipCommand(connection, transaction,
            "UPDATE MentorshipRequests SET State = 4, UpdatedAt = $now WHERE Id = $id AND State = 0",
            ("$now", DateTime.UtcNow.ToString("O")), ("$id", requestId));
        await update.ExecuteNonQueryAsync(token);
        request = await ReadMentorshipRequestAsync(connection, transaction, requestId, token);
        transaction.Commit();
        return new(MentorshipResultCode.Success, request);
    }

    public async Task<IReadOnlyList<MentorshipRelationRecord>> GetMentorshipRelationsAsync(MentorshipActor actor,
        bool includeEnded = false, CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        if (await ReadMentorshipActorAsync(connection, transaction, actor, token) is null) return [];
        var ids = new List<long>();
        using (var command = MentorshipCommand(connection, transaction, """
            SELECT Id FROM MentorshipRelations WHERE (TeacherCharacterId = $id OR StudentCharacterId = $id)
              AND ($ended = 1 OR State = 0) ORDER BY Id
            """, ("$id", actor.CharacterId), ("$ended", includeEnded ? 1 : 0)))
        {
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetInt64(0));
        }
        var records = new List<MentorshipRelationRecord>();
        foreach (var id in ids)
            if (await ReadMentorshipRelationAsync(connection, transaction, id, token) is { } record) records.Add(record);
        return records;
    }

    public async Task<MentorshipResult> ReleaseMentorshipAsync(MentorshipActor actor, long relationId,
        CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        if (await ReadMentorshipActorAsync(connection, transaction, actor, token) is null)
            return new(MentorshipResultCode.Unauthorized);
        var relation = await ReadMentorshipRelationAsync(connection, transaction, relationId, token);
        if (relation is null) return new(MentorshipResultCode.NotFound);
        if (actor.CharacterId != relation.TeacherCharacterId && actor.CharacterId != relation.StudentCharacterId)
            return new(MentorshipResultCode.Unauthorized);
        if (relation.State != MentorshipRelationState.Active)
            return new(MentorshipResultCode.AlreadyCompleted, Relation: relation);
        using var update = MentorshipCommand(connection, transaction,
            "UPDATE MentorshipRelations SET State = 2, EndedAt = $now WHERE Id = $id AND State = 0",
            ("$now", DateTime.UtcNow.ToString("O")), ("$id", relationId));
        await update.ExecuteNonQueryAsync(token);
        relation = await ReadMentorshipRelationAsync(connection, transaction, relationId, token);
        transaction.Commit();
        return new(MentorshipResultCode.Success, Relation: relation);
    }

    public Task<MentorshipResult> CompleteMentorshipLessonAsync(MentorshipActor teacher, MentorshipActor student,
        long relationId, uint lessonCode, string completionKey, MentorshipPolicy policy, CancellationToken token = default)
        => Task.FromResult(new MentorshipResult(MentorshipResultCode.Unsupported));

    private static async Task<bool> CanGraduateMentorshipAsync(SqliteConnection connection,
        SqliteTransaction transaction, long relationId, int studentLevel, MentorshipPolicy policy, CancellationToken token)
    {
        if (policy.GraduationMinimumLevel is not int minimum || studentLevel < minimum || policy.LessonCodes.Count == 0) return false;
        foreach (var lessonCode in policy.LessonCodes)
            if (await MentorshipScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM MentorshipLessons WHERE RelationId = $id AND LessonCode = $lesson",
                token, ("$id", relationId), ("$lesson", (long)lessonCode)) == 0) return false;
        return true;
    }

    public async Task<MentorshipResult> GraduateMentorshipAsync(MentorshipActor teacher, long relationId,
        MentorshipPolicy policy, CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        if (await ReadMentorshipActorAsync(connection, transaction, teacher, token) is null)
            return new(MentorshipResultCode.Unauthorized);
        var relation = await ReadMentorshipRelationAsync(connection, transaction, relationId, token);
        if (relation is null) return new(MentorshipResultCode.NotFound);
        if (teacher.CharacterId != relation.TeacherCharacterId) return new(MentorshipResultCode.Unauthorized);
        if (relation.State != MentorshipRelationState.Active)
            return new(MentorshipResultCode.AlreadyCompleted, Relation: relation);
        if (policy.GraduationMinimumLevel is null || policy.LessonCodes.Count == 0)
            return new(MentorshipResultCode.Unsupported, Relation: relation);
        var level = checked((int)await MentorshipScalarAsync(connection, transaction,
            "SELECT Level FROM Characters WHERE Id = $id", token, ("$id", relation.StudentCharacterId)));
        if (!await CanGraduateMentorshipAsync(connection, transaction, relationId, level, policy, token))
            return new(MentorshipResultCode.Ineligible, Relation: relation);
        var now = DateTime.UtcNow.ToString("O");
        var reward = policy.GraduationReward;
        if (reward is not null)
        {
            if (!QuestCatalog.TryGetQuest(reward.ResourceQuestId, out var definition)
                || !definition.Rewards.Any(item => item.RewardType == 2 && item.RewardCode == reward.ItemCode
                    && item.Amount == reward.Quantity) || !ShopCatalog.TryGet(reward.ItemCode, out _))
                return new(MentorshipResultCode.Unsupported, Relation: relation);
            using var grant = MentorshipCommand(connection, transaction, """
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                VALUES($student, $item, $quantity, $now)
                ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET Quantity = CharacterItems.Quantity + excluded.Quantity,
                    UpdatedAt = excluded.UpdatedAt WHERE CharacterItems.Quantity <= 65535 - excluded.Quantity
                """, ("$student", relation.StudentCharacterId), ("$item", (long)reward.ItemCode),
                ("$quantity", reward.Quantity), ("$now", now));
            if (await grant.ExecuteNonQueryAsync(token) != 1) return new(MentorshipResultCode.Conflict, Relation: relation);
            using var award = MentorshipCommand(connection, transaction, """
                INSERT INTO MentorshipGraduationAwards(RelationId, CharacterId, ItemCode, Quantity, ResourceQuestId, CreatedAt)
                VALUES($relation, $student, $item, $quantity, $quest, $now)
                """, ("$relation", relationId), ("$student", relation.StudentCharacterId), ("$item", (long)reward.ItemCode),
                ("$quantity", reward.Quantity), ("$quest", (long)reward.ResourceQuestId), ("$now", now));
            await award.ExecuteNonQueryAsync(token);
        }
        using var update = MentorshipCommand(connection, transaction, """
            UPDATE MentorshipRelations SET State = 1, EndedAt = $now WHERE Id = $id AND State = 0;
            INSERT INTO MentorshipGraduations(RelationId, CompletedAt, RewardDisposition) VALUES($id, $now, $disposition);
            """, ("$id", relationId), ("$now", now), ("$disposition", reward is null ? "not-configured" : "granted"));
        await update.ExecuteNonQueryAsync(token);
        relation = await ReadMentorshipRelationAsync(connection, transaction, relationId, token);
        transaction.Commit();
        return new(MentorshipResultCode.Success, Relation: relation);
    }

    public async Task<IReadOnlyList<MentorshipRequestRecord>> CleanupMentorshipSessionAsync(
        MentorshipActor actor, CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        var ids = new List<long>();
        using (var query = MentorshipCommand(connection, transaction, """
            SELECT Id FROM MentorshipRequests WHERE State = 0
              AND ((RequesterCharacterId = $id AND RequesterAccountId = $account AND RequesterSessionId = $session)
                OR (TargetCharacterId = $id AND TargetAccountId = $account AND TargetSessionId = $session))
            """, ("$id", actor.CharacterId), ("$account", actor.AccountId), ("$session", actor.SessionId)))
        {
            using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetInt64(0));
        }
        using var command = MentorshipCommand(connection, transaction, """
            UPDATE MentorshipRequests SET State = 3, UpdatedAt = $now WHERE State = 0
              AND ((RequesterCharacterId = $id AND RequesterAccountId = $account AND RequesterSessionId = $session)
                OR (TargetCharacterId = $id AND TargetAccountId = $account AND TargetSessionId = $session));
            UPDATE CharacterMentorAdvertisements SET IsAdvertising = 0, UpdatedAt = $now WHERE CharacterId = $id
              AND EXISTS(SELECT 1 FROM Characters WHERE Id = $id AND AccountId = $account)
              AND NOT EXISTS(SELECT 1 FROM Characters c JOIN Accounts a ON a.Id = c.AccountId
                WHERE c.Id = $id AND c.IsOnline = 1 AND a.IsOnline = 1
                  AND c.ActiveSessionId = a.ActiveSessionId AND c.ActiveSessionId <> $session);
            """, ("$id", actor.CharacterId), ("$account", actor.AccountId),
            ("$session", actor.SessionId), ("$now", DateTime.UtcNow.ToString("O")));
        await command.ExecuteNonQueryAsync(token);
        var records = new List<MentorshipRequestRecord>();
        foreach (var id in ids)
            if (await ReadMentorshipRequestAsync(connection, transaction, id, token) is { } request) records.Add(request);
        transaction.Commit();
        return records;
    }

    internal async Task<MentorshipSettlementResult> RecordMentorshipClearAsync(MentorshipActor actor,
        string settlementKey, int episode, int dungeonBit, IReadOnlyList<MentorshipActor> participants,
        MentorshipPolicy policy, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(settlementKey) || settlementKey.Length > 160 || settlementKey.Any(char.IsControl)
            || participants.Count is < 2 or > 4 || participants.Distinct().Count() != participants.Count
            || participants.Select(item => item.CharacterId).Distinct().Count() != participants.Count
            || !participants.Contains(actor) || participants.Any(item => item.ChannelId != actor.ChannelId))
            return new(MentorshipResultCode.Ineligible, []);
        var courses = policy.Courses.Where(item => item.Episode == episode && item.DungeonBit == dungeonBit).ToArray();
        if (courses.Length == 0) return new(MentorshipResultCode.Unsupported, []);
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
        foreach (var participant in participants)
            if (await ReadMentorshipActorAsync(connection, transaction, participant, token) is null)
                return new(MentorshipResultCode.Unauthorized, []);
        var identity = string.Join("|", participants.OrderBy(item => item.CharacterId)
            .Select(item => $"{item.AccountId}:{item.CharacterId}:{item.SessionId}:{item.ChannelId}"));
        var now = DateTime.UtcNow.ToString("O");
        using var create = MentorshipCommand(connection, transaction, """
            INSERT INTO MentorshipSettlements(SettlementKey, Episode, DungeonBit, TeamIdentity, CreatedAt)
            VALUES($key, $episode, $dungeon, $team, $now) ON CONFLICT DO NOTHING
            """, ("$key", settlementKey), ("$episode", episode), ("$dungeon", dungeonBit), ("$team", identity), ("$now", now));
        var created = await create.ExecuteNonQueryAsync(token) == 1;
        if (await MentorshipScalarAsync(connection, transaction, """
            SELECT COUNT(*) FROM MentorshipSettlements WHERE SettlementKey = $key AND Episode = $episode
                AND DungeonBit = $dungeon AND TeamIdentity = $team
            """, token, ("$key", settlementKey), ("$episode", episode), ("$dungeon", dungeonBit), ("$team", identity)) != 1)
            return new(MentorshipResultCode.Conflict, []);
        if (created)
        {
            var participantIds = participants.Select(item => item.CharacterId).ToHashSet();
            var candidates = new List<(long Id, long Teacher, long Student)>();
            using (var query = MentorshipCommand(connection, transaction,
                "SELECT Id, TeacherCharacterId, StudentCharacterId FROM MentorshipRelations WHERE State = 0"))
            {
                using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) candidates.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
            foreach (var relation in candidates.Where(item => participantIds.Contains(item.Teacher) && participantIds.Contains(item.Student)))
            {
                using var bind = MentorshipCommand(connection, transaction,
                    "INSERT INTO MentorshipSettlementRelations(SettlementKey, RelationId) VALUES($key, $relation)",
                    ("$key", settlementKey), ("$relation", relation.Id));
                await bind.ExecuteNonQueryAsync(token);
            }
        }
        var relationIds = new List<long>();
        using (var query = MentorshipCommand(connection, transaction, """
            SELECT r.Id FROM MentorshipRelations r JOIN MentorshipSettlementRelations b ON b.RelationId = r.Id
            WHERE b.SettlementKey = $key AND r.State = 0 AND (r.TeacherCharacterId = $actor OR r.StudentCharacterId = $actor)
            """, ("$key", settlementKey), ("$actor", actor.CharacterId)))
        {
            using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) relationIds.Add(reader.GetInt64(0));
        }
        var completed = new List<MentorshipRelationRecord>();
        var receiptsAdded = 0;
        foreach (var relationId in relationIds)
        {
            using var receipt = MentorshipCommand(connection, transaction, """
                INSERT INTO MentorshipClearReceipts(SettlementKey, RelationId, CharacterId, SessionId, CreatedAt)
                VALUES($key, $relation, $actor, $session, $now) ON CONFLICT DO NOTHING
                """, ("$key", settlementKey), ("$relation", relationId), ("$actor", actor.CharacterId),
                ("$session", actor.SessionId), ("$now", now));
            receiptsAdded += await receipt.ExecuteNonQueryAsync(token);
            if (await MentorshipScalarAsync(connection, transaction, """
                SELECT COUNT(DISTINCT CharacterId) FROM MentorshipClearReceipts
                WHERE SettlementKey = $key AND RelationId = $relation
                """, token, ("$key", settlementKey), ("$relation", relationId)) != 2) continue;
            foreach (var course in courses)
            {
                using var lesson = MentorshipCommand(connection, transaction, """
                    INSERT INTO MentorshipLessons(RelationId, LessonCode, CompletionKey, CompletedAt)
                    VALUES($relation, $lesson, $key, $now) ON CONFLICT DO NOTHING
                    """, ("$relation", relationId), ("$lesson", (long)course.LessonCode), ("$key", settlementKey), ("$now", now));
                if (await lesson.ExecuteNonQueryAsync(token) == 1)
                {
                    completed.Add((await ReadMentorshipRelationAsync(connection, transaction, relationId, token))!);
                    break;
                }
            }
        }
        transaction.Commit();
        return new(receiptsAdded == 0 && completed.Count == 0 ? MentorshipResultCode.AlreadyCompleted : MentorshipResultCode.Success, completed);
    }

    private static Task<long> CountMentorshipStudentsAsync(SqliteConnection connection, SqliteTransaction transaction,
        long teacherCharacterId, CancellationToken token)
        => MentorshipScalarAsync(connection, transaction, """
            SELECT COUNT(DISTINCT StudentCharacterId) FROM MentorshipRelations WHERE TeacherCharacterId = $id AND State IN (0, 1)
            """, token, ("$id", teacherCharacterId));
}
