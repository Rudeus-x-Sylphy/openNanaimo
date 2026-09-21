namespace OpenNanaimo.Adapter.Models;

public sealed class PartyManagementRecord
{
    public int PartyId { get; init; }
    public int ChannelId { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public int MemberCount { get; init; }
    public string StateText { get; init; } = string.Empty;
}

public sealed class PartyMemberManagementRecord
{
    public int PartyId { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public long AccountId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;
    public string RoleText { get; init; } = string.Empty;
    public int Level { get; init; }
    public long JoinOrder { get; init; }
    public string SceneText { get; init; } = string.Empty;
    public string OnlineText { get; init; } = string.Empty;
}

public sealed class TradeManagementRecord
{
    public int RoomId { get; init; }
    public int ChannelId { get; init; }
    public string InviterName { get; init; } = string.Empty;
    public string InviteeName { get; init; } = string.Empty;
    public int JoinedCount { get; init; }
    public int ReadyCount { get; init; }
    public int FinalCount { get; init; }
    public string StateText { get; init; } = string.Empty;
}

public sealed class TradeMemberManagementRecord
{
    public int RoomId { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public long AccountId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;
    public string RoleText { get; init; } = string.Empty;
    public string JoinedText { get; init; } = string.Empty;
    public string ReadyText { get; init; } = string.Empty;
    public string FinalText { get; init; } = string.Empty;
    public ulong Hans { get; init; }
    public string CardsText { get; init; } = string.Empty;
}
