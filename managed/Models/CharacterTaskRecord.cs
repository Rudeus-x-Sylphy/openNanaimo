namespace OpenNanaimo.Adapter.Models;

public sealed class CharacterTaskRecord
{
    public uint QuestId { get; init; }
    public byte TaskType { get; init; }
    public byte RuntimeState { get; init; }
    public byte State3 { get; init; }
    public ushort Progress1 { get; init; }
    public ushort Progress2 { get; init; }
    public uint Progress3 { get; init; }
    public byte SlotType { get; init; }
}

public enum QuestScrollPurchaseStatus : uint
{
    Success = 0,
    TaskListFull = 2,
    InsufficientHans = 3,
    AlreadyExists = 5
}

public readonly record struct QuestScrollPurchaseResult(
    bool Authorized,
    QuestScrollPurchaseStatus Status,
    long Hans);

public readonly record struct QuestTaskMutationResult(
    bool Authorized,
    bool Success,
    CharacterRecord? Character,
    bool HansChanged,
    int GainedLevels,
    bool InventoryChanged = false);

public readonly record struct QuestRunRestrictions(
    bool ItemUsed, bool Charged, bool Revived,
    bool HasClear = false, int Episode = -1, int Difficulty = -1, int DungeonBit = -1, uint Score = 0)
{
    public uint? BattlePetCode { get; init; }
    public bool BossDefeated { get; init; }
    public QuestRunRestrictions(bool itemUsed, bool charged, bool revived, bool hasClear,
        int episode, int difficulty, int dungeonBit)
        : this(itemUsed, charged, revived, hasClear, episode, difficulty, dungeonBit, 0) { }
    public void Deconstruct(out bool itemUsed, out bool charged, out bool revived, out bool hasClear,
        out int episode, out int difficulty, out int dungeonBit)
    {
        itemUsed = ItemUsed; charged = Charged; revived = Revived; hasClear = HasClear;
        episode = Episode; difficulty = Difficulty; dungeonBit = DungeonBit;
    }
    public static QuestRunRestrictions None { get; } = new(false, false, false);
}

public readonly record struct QuestProgressMutationResult(
    bool Authorized,
    bool Changed,
    bool NewlyCompleted,
    IReadOnlyList<CharacterTaskRecord> Tasks);

public readonly record struct NativeQuestSettlement(int Episode, int Difficulty, int DungeonBit, uint Score)
{
    public bool Authoritative { get; init; }
    public uint PetCode { get; init; }
    public int Participants { get; init; }
    public bool BossDefeated { get; init; }
    public uint BattleEpoch { get; init; }
}
