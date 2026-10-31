namespace OpenNanaimo.Adapter.Services;

public static class QuestMonsterCatalog
{
    private static readonly IReadOnlySet<uint> TargetCodes = QuestCatalog.Quests
        .SelectMany(quest => quest.Objectives)
        .Where(objective => objective.ObjectiveType == 26)
        .Select(objective => objective.TargetCode)
        .ToHashSet();

    // The resolved combat template carries the stage/slot-specific MMO identity.
    internal static bool TryResolveTarget(DungeonCombatTemplate template, out uint targetCode)
    {
        targetCode = template.ResourceCode > 0 ? (uint)template.ResourceCode : 0;
        if (TargetCodes.Contains(targetCode)) return true;
        targetCode = 0;
        return false;
    }
}
