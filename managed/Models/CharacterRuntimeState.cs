namespace OpenNanaimo.Adapter.Models;

public sealed record CharacterRuntimeState(
    int CurrentHp,
    int CurrentMp,
    int CurrentMapId,
    int CurrentTownPage,
    int PositionX,
    int PositionY,
    int? CurrentChannelId);
