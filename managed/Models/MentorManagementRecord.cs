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

    public string Direction => RequestOpcode == 0xC583 ? "学生请求家教" : "老师邀请授课";
    public string Protocol => $"0x{RequestOpcode:X4}";
    public string StatusText => Status switch
    {
        0 => "等待答复",
        2 => "超时",
        10 => "已接受",
        20 => "已拒绝",
        7 => "已取消",
        _ => $"官方状态 {Status}"
    };
    public string CreatedAtText => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string UpdatedAtText => UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
