namespace OpenNanaimo.Adapter.Models;

public sealed class CoupleRelationRecord
{
    public long Id { get; init; }
    public long Character1Id { get; init; }
    public string Character1Name { get; init; } = string.Empty;
    public long Character2Id { get; init; }
    public string Character2Name { get; init; } = string.Empty;
    public uint RingItemCode { get; init; }
    public DateTime EstablishedAt { get; init; }

    public long GetPartnerId(long characterId)
        => Character1Id == characterId ? Character2Id : Character1Id;

    public string GetPartnerName(long characterId)
        => Character1Id == characterId ? Character2Name : Character1Name;
}
