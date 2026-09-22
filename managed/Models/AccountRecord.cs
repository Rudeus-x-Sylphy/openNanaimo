using System.ComponentModel;

namespace OpenNanaimo.Adapter.Models;

public enum IpBanMode
{
    None = 0,
    IpOnly = 1,
    Double = 2
}

public sealed class AccountRecord : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? PlaintextPassword { get; set; }
    public bool IsBanned { get; set; }
    public bool IsWebAdmin { get; set; }
    public bool IsGm { get; set; }
    public bool IsOnline { get; set; }
    public long? CharacterId { get; set; }
    public string? CharacterName { get; set; }
    public int? CharacterLevel { get; set; }
    public bool? TutorialCompleted { get; set; }
    public long Hans { get; set; }
    public long Cash { get; set; }
    public ushort SkillPoints { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? LastIp { get; set; }
    public string? RegistrationIp { get; set; }
    public bool IsLoginAuthorized { get; set; }
    public bool IsRestrictionExempt { get; set; }
    public long TrialPlayedSeconds { get; set; }
    public int? TrialLimitMinutes { get; set; }
    public IpBanMode IpBanMode { get; set; }
    public int? CurrentChannelId { get; set; }
    public DateTime? OnlineSince { get; set; }
    public DateTime? LastOfflineAt { get; set; }

    public string OnlineStatus => IsOnline ? "在线" : "离线";
    public string WebAdminStatus => IsWebAdmin ? "是" : "否";
    public string GmStatus => IsGm ? "GM" : "普通";
    public string CharacterStatus => CharacterId.HasValue ? CharacterName ?? "角色" : "未创建";
    public string TutorialStatus => TutorialCompleted switch
    {
        true => "已完成",
        false => "未完成",
        null => "-"
    };
    public string ChannelStatus => CurrentChannelId?.ToString() ?? "-";
    public string BalanceStatus => CharacterId.HasValue ? $"{Hans:N0} / {Cash:N0}" : "-";
    public string PasswordDisplay => string.IsNullOrEmpty(PlaintextPassword)
        ? "等待正确登录同步"
        : PlaintextPassword;
    public string LastLoginLocalText => LastLoginAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    public string IpBanStatus => IpBanMode switch
    {
        IpBanMode.IpOnly => "封IP",
        IpBanMode.Double => "双封",
        _ => "未封"
    };
    public bool IsDoubleBanned => IsBanned && IpBanMode == IpBanMode.Double;
    public string LoginAuthorizationStatus => IsLoginAuthorized ? "已授权" : "未授权";
    public string RestrictionWhitelistStatus => IsRestrictionExempt ? "白名单" : "普通";
    public string TrialPlayedStatus
    {
        get
        {
            var seconds = Math.Max(0, TrialPlayedSeconds);
            if (IsOnline && OnlineSince is DateTime onlineSince)
            {
                var currentSessionSeconds = (long)Math.Floor(
                    Math.Max(0, (DateTime.UtcNow - onlineSince.ToUniversalTime()).TotalSeconds));
                seconds = checked(seconds + currentSessionSeconds);
            }
            return $"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}";
        }
    }
    public string TrialLimitStatus => TrialLimitMinutes is int minutes ? $"{minutes} 分钟" : "未设置";

    public void RefreshLiveTrialPlayedStatus()
    {
        if (IsOnline && OnlineSince.HasValue)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrialPlayedStatus)));
    }
}
