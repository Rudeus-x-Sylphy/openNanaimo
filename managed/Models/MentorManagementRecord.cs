namespace OpenNanaimo.Adapter.Models;

public sealed class MentorAdvertisementAdminRecord
{
    public long CharacterId { get; init; }
    public long AccountId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;
    public bool IsAdvertising { get; init; }
    public bool IsOnline { get; init; }
    public int? ChannelId { get; init; }
    public DateTime UpdatedAtUtc { get; init; }

    public string AdvertisingStatus => IsAdvertising ? "发布中" : "已停止";
    public string OnlineStatus => IsOnline ? "在线" : "离线";
    public string ChannelStatus => ChannelId?.ToString() ?? "-";
    public string UpdatedAtText => UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class MentorInteractionAdminRecord
{
    public long Id { get; init; }
    public ushort RequestOpcode { get; init; }
    public string RequesterName { get; init; } = string.Empty;
    public string TargetName { get; init; } = string.Empty;
    public uint LessonCode { get; init; }
    public ushort Status { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }

    public string Direction => "历史交互";
    public string Protocol => $"0x{RequestOpcode:X4}";
    public string StatusText => Status switch
    {
        0 => "等待答复",
        2 => "超时",
        10 => "已接受",
        20 => "已拒绝",
        7 => "已取消",
        _ => $"状态 {Status}"
    };
    public string CreatedAtText => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string UpdatedAtText => UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}


public readonly record struct MentorshipActor(long AccountId, long CharacterId, string SessionId, int ChannelId);

public enum MentorshipDirection { StudentApplication, TeacherInvitation }
public enum MentorshipRequestState { Pending, Accepted, Refused, Expired, Cancelled, Ineligible }
public enum MentorshipRelationState { Active, Graduated, Released }
public enum MentorshipResultCode { Success, Unauthorized, NotFound, Ineligible, Conflict, Expired, AlreadyCompleted, Unsupported }

public sealed record MentorshipRequestRecord(
    long Id, MentorshipDirection Direction, MentorshipActor Requester, MentorshipActor Target,
    long TeacherCharacterId, long StudentCharacterId, MentorshipRequestState State,
    DateTime CreatedAtUtc, DateTime ExpiresAtUtc, long? RelationId);

public sealed record MentorshipRelationRecord(
    long Id, long TeacherCharacterId, long StudentCharacterId, MentorshipRelationState State,
    DateTime CreatedAtUtc, DateTime? EndedAtUtc, int CompletedLessonCount,
    bool GraduationRewardGranted);

public sealed record MentorshipResult(
    MentorshipResultCode Code, MentorshipRequestRecord? Request = null, MentorshipRelationRecord? Relation = null)
{
    public bool Success => Code == MentorshipResultCode.Success;
}

public sealed record MentorshipQualification(
    bool SessionOwned, bool CanAdvertise, bool CanTeach, bool CanStudy,
    bool CanGraduate, int Level, int ActiveStudentCount);

public sealed record MentorshipCourseDefinition(uint LessonCode, int Episode, int DungeonBit);
public sealed record MentorshipGraduationReward(uint ItemCode, ushort Quantity, uint ResourceQuestId);
public sealed record MentorshipSettlementResult(MentorshipResultCode Code, IReadOnlyList<MentorshipRelationRecord> CompletedRelations);

public sealed record MentorshipDelivery(
    MentorshipActor Recipient, string Operation, MentorshipResult Result);

public sealed class MentorshipPolicy
{
    public int MinimumTeacherLevel { get; }
    public int MinimumLevelGap { get; }
    public int MaximumStudents { get; }
    public TimeSpan RequestLifetime { get; }
    public int? GraduationMinimumLevel { get; }
    private readonly HashSet<uint> _lessonCodes;
    private readonly MentorshipCourseDefinition[] _courses;
    public IReadOnlyList<MentorshipCourseDefinition> Courses => _courses.ToArray();
    public MentorshipGraduationReward? GraduationReward { get; }
    private static readonly Lazy<MentorshipPolicy> ProductionRules = new(
        OpenNanaimo.Adapter.Services.MentorProtocol.CreateProductionPolicy);
    public static MentorshipPolicy Production => ProductionRules.Value;
    public IReadOnlyCollection<uint> LessonCodes => _lessonCodes.ToArray();

    public static MentorshipPolicy Conservative { get; } = new(2, 1, 1, TimeSpan.FromMinutes(2));

    public MentorshipPolicy(int minimumTeacherLevel, int minimumLevelGap, int maximumStudents,
        TimeSpan requestLifetime, int? graduationMinimumLevel = null, IEnumerable<uint>? lessonCodes = null,
        IEnumerable<MentorshipCourseDefinition>? courses = null, MentorshipGraduationReward? graduationReward = null)
    {
        if (minimumTeacherLevel < 1 || minimumLevelGap < 1 || maximumStudents < 1
            || requestLifetime <= TimeSpan.Zero || requestLifetime > TimeSpan.FromDays(1)
            || graduationMinimumLevel is < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumTeacherLevel), "Invalid mentorship policy.");
        MinimumTeacherLevel = minimumTeacherLevel;
        MinimumLevelGap = minimumLevelGap;
        MaximumStudents = maximumStudents;
        RequestLifetime = requestLifetime;
        GraduationMinimumLevel = graduationMinimumLevel;
        _lessonCodes = lessonCodes?.ToHashSet() ?? [];
        if (_lessonCodes.Contains(0)) throw new ArgumentOutOfRangeException(nameof(lessonCodes));
        _courses = courses?.ToArray() ?? [];
        if (_courses.Any(course => !_lessonCodes.Contains(course.LessonCode) || course.Episode is < 0 or >= 16
            || course.DungeonBit is < 0 or >= 4) || _courses.Select(c => c.LessonCode).Distinct().Count() != _courses.Length)
            throw new ArgumentOutOfRangeException(nameof(courses));
        GraduationReward = graduationReward;
        if (graduationReward is { Quantity: 0 } or { ItemCode: 0 })
            throw new ArgumentOutOfRangeException(nameof(graduationReward));
    }

    public bool SupportsLesson(uint lessonCode) => _lessonCodes.Contains(lessonCode);
}
