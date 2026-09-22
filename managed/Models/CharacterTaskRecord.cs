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
    int GainedLevels);

public readonly record struct QuestProgressMutationResult(
    bool Authorized,
    bool Changed,
    bool NewlyCompleted,
    IReadOnlyList<CharacterTaskRecord> Tasks);
