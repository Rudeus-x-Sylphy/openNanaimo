namespace OpenNanaimo.Adapter.Models;

public sealed class DungeonRoomManagementRecord
{
    public int RoomId { get; init; }
    public int ChannelId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string OwnerName { get; init; } = string.Empty;
    public string DungeonStatus { get; init; } = string.Empty;
    public string StateText { get; init; } = string.Empty;
    public int MemberCount { get; init; }
    public uint BossEnergy { get; init; }
}

public sealed class DungeonMemberManagementRecord
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
    public string HpMpText { get; init; } = string.Empty;
    public int Score { get; init; }
    public string P2PEndpoint { get; init; } = string.Empty;
}
