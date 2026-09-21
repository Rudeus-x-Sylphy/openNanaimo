namespace OpenNanaimo.Adapter.Models;

public sealed class VillageBotSettings
{
    public bool Enabled { get; set; }
    public int Count { get; set; } = 5;
    public bool SpeechEnabled { get; set; } = true;
    public bool ConversationEnabled { get; set; } = true;
    public bool EmotionEnabled { get; set; } = true;
    public bool MovementEnabled { get; set; } = true;
    public bool PauseEnabled { get; set; } = true;
    public bool PortalTravelEnabled { get; set; } = true;
    public int MinimumSpeechSeconds { get; set; } = 45;
    public int MaximumSpeechSeconds { get; set; } = 120;
}

public sealed class VillageBotRuntimeStatus
{
    public bool Enabled { get; set; }
    public bool ServiceRunning { get; set; }
    public int ConfiguredCount { get; set; }
    public int ActiveCount { get; set; }
    public bool SpeechEnabled { get; set; }
    public bool ConversationEnabled { get; set; }
    public bool EmotionEnabled { get; set; }
    public bool MovementEnabled { get; set; }
    public bool PauseEnabled { get; set; }
    public bool PortalTravelEnabled { get; set; }
    public List<VillageBotRuntimeRecord> Bots { get; set; } = [];
}

public sealed class VillageBotPhraseRecord
{
    public string Category { get; init; } = string.Empty;
    public string Trigger { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
}

public sealed class VillageBotRuntimeRecord
{
    public string Name { get; set; } = string.Empty;
    public ushort EntityId { get; set; }
    public int ChannelId { get; set; }
    public byte TownId { get; set; }
    public byte TownPage { get; set; }
    public ushort PositionX { get; set; }
    public ushort PositionY { get; set; }
    public string State { get; set; } = string.Empty;
    public string GenderName { get; set; } = string.Empty;
    public int Level { get; set; }
    public uint PetItemCode { get; set; }
    public string PetName { get; set; } = string.Empty;
    public string OutfitName { get; set; } = string.Empty;
    public string AppearanceHex { get; set; } = string.Empty;
}
