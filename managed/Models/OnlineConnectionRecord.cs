namespace OpenNanaimo.Adapter.Models;

public sealed class OnlineConnectionRecord
{
    public string SessionId { get; init; } = string.Empty;
    public long AccountId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;
    public string RemoteIp { get; init; } = string.Empty;
    public int ChannelId { get; init; }
    public DateTime OnlineSinceUtc { get; init; }
    public DateTime LastHeartbeatUtc { get; init; }
    public long HeartbeatCount { get; init; }
    public DateTime SnapshotUtc { get; init; }

    public string OnlineSinceText => OnlineSinceUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string OnlineDurationText => FormatDuration(SnapshotUtc - OnlineSinceUtc);
    public string HeartbeatText
    {
        get
        {
            var elapsed = SnapshotUtc - LastHeartbeatUtc;
            var age = elapsed.TotalSeconds < 2 ? "刚刚" : $"{FormatDuration(elapsed)}前";
            return $"{LastHeartbeatUtc.ToLocalTime():HH:mm:ss}（{age}）";
        }
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}天 {value.Hours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
        return $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
    }
}
