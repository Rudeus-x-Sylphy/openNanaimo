namespace OpenNanaimo.Adapter.Models;

public sealed class FriendRelationAdminRecord
{
    public long Id { get; init; }
    public long FirstCharacterId { get; init; }
    public string FirstCharacterName { get; init; } = string.Empty;
    public long SecondCharacterId { get; init; }
    public string SecondCharacterName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

public sealed class FriendRequestAdminRecord
{
    public long SerialNo { get; init; }
    public long RequesterCharacterId { get; init; }
    public string RequesterCharacterName { get; init; } = string.Empty;
    public long RequesteeCharacterId { get; init; }
    public string RequesteeCharacterName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int Status { get; init; }
    public string StatusName => Status switch
    {
        0 => "待确认",
        1 => "已接受",
        2 => "已拒绝",
        _ => "未知"
    };
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class FriendCategoryAdminRecord
{
    public long OwnerCharacterId { get; init; }
    public string OwnerCharacterName { get; init; } = string.Empty;
    public uint CategoryCode { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public byte Property { get; init; }
    public byte AllowType { get; init; }
    public int MemberCount { get; init; }
}

internal sealed class FriendListRecord
{
    public long CharacterId { get; init; }
    public long AccountId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;
    public int Level { get; init; }
    public string Memo { get; init; } = string.Empty;
    public bool IsBlocked { get; init; }
    public bool IsWaitingConfirmation { get; init; }
    public uint? CategoryCode { get; init; }
}

internal sealed class FriendCategoryRecord
{
    public uint CategoryCode { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public byte Property { get; init; }
    public byte AllowType { get; init; }
    public List<FriendListRecord> Friends { get; init; } = [];
}

internal sealed class FriendListSnapshot
{
    public List<FriendCategoryRecord> Categories { get; init; } = [];
    public List<FriendListRecord> Unrelated { get; init; } = [];
}

internal sealed record FriendRequestCreationResult(
    bool Success,
    string Error,
    uint SerialNo,
    long RequesteeCharacterId,
    string RequesteeUsername,
    string RequesteeCharacterName);

internal sealed record FriendRequestNotification(
    uint SerialNo,
    string RequestId,
    string RequesterUsername,
    string RequesterCharacterName,
    uint RequesterVirtualId,
    uint InsertCategoryCode,
    string Message,
    bool AddToNxFriend);
