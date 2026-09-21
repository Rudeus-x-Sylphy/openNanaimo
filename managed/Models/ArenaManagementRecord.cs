namespace OpenNanaimo.Adapter.Models;

public sealed class ArenaRoomManagementRecord
{
    public int RoomId { get; init; }
    public int ChannelId { get; init; }
    public byte GameType { get; init; }
    public string GameTypeText { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string OwnerName { get; init; } = string.Empty;
    public int MemberCount { get; init; }
    public uint Property { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string CreatedAtText => CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
}

public sealed class ArenaMemberManagementRecord
{
    public int RoomId { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public long AccountId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;
    public string RoleText { get; init; } = string.Empty;
    public byte SlotIndex { get; init; }
    public string ReadyText { get; init; } = string.Empty;
    public byte TeamCode { get; init; }
    public int Level { get; init; }
    public string HpMpText { get; init; } = string.Empty;
    public string P2PEndpoint { get; init; } = string.Empty;
}
