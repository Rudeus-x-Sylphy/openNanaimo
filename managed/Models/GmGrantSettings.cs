namespace OpenNanaimo.Adapter.Models;

public sealed class GmGrantItemSettings
{
    public uint ItemCode { get; init; }
    public ushort Quantity { get; init; }
}

public sealed class GmGrantSettings
{
    public long Hans { get; set; }
    public long Cash { get; set; }
    public int SkillPoints { get; set; }
    public IReadOnlyList<GmGrantItemSettings> Items { get; set; } = [];
}
