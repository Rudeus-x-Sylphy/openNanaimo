using OpenNanaimo.Adapter.Services;

namespace OpenNanaimo.Adapter.Models;

public sealed class QuestCatalogAdminRecord
{
    public required uint ScrollCode { get; init; }
    public required uint QuestId { get; init; }
    public required string Name { get; init; }
    public required uint Price { get; init; }
    public required string ObjectiveSummary { get; init; }
    public required string TargetSummary { get; init; }
    public required string RequiredSummary { get; init; }
    public required string RewardSummary { get; init; }
    public required bool SupportsProgressManagement { get; init; }

    public string ManagementStatus => SupportsProgressManagement ? "击打进度" : "仅目录展示";

    public static QuestCatalogAdminRecord Create(
        QuestScrollDefinition scroll,
        QuestDefinition quest)
    {
        var supportsProgress = QuestCatalog.TryGetMonsterHitObjective(
            quest.QuestId, out var objective);
        return new QuestCatalogAdminRecord
        {
            ScrollCode = scroll.ScrollCode,
            QuestId = quest.QuestId,
            Name = quest.Name,
            Price = scroll.Price,
            ObjectiveSummary = quest.Objectives.Count == 0
                ? "无目标"
                : string.Join("；", quest.Objectives.Select(item => item.Name)),
            TargetSummary = supportsProgress
                ? objective.TargetCode.ToString()
                : string.Join(",", quest.Objectives.Select(item => item.TargetCode).Distinct()),
            RequiredSummary = supportsProgress
                ? objective.RequiredCount.ToString()
                : string.Join(",", quest.Objectives.Select(item => item.RequiredCount)),
            RewardSummary = FormatRewards(quest.Rewards),
            SupportsProgressManagement = supportsProgress
        };
    }

    internal static string FormatRewards(IReadOnlyList<QuestRewardDefinition> rewards)
        => string.Join("；", rewards.Select(reward => reward.RewardType switch
        {
            1 => $"Hans {reward.Amount:N0}",
            7 => $"经验 {reward.Amount:N0}",
            _ => $"类型{reward.RewardType} / {reward.RewardCode} x{reward.Amount}"
        }));
}

public sealed class CharacterTaskAdminRecord
{
    public required uint QuestId { get; init; }
    public required string Name { get; init; }
    public required byte TaskType { get; init; }
    public required byte RuntimeState { get; init; }
    public required ushort Progress2 { get; init; }
    public required uint Progress3 { get; init; }
    public required uint RequiredCount { get; init; }
    public required string ObjectiveSummary { get; init; }
    public required string RewardSummary { get; init; }
    public required bool SupportsProgressManagement { get; init; }

    public string State => Progress2 == 0
        ? "未开启"
        : SupportsProgressManagement && Progress3 >= RequiredCount
            ? "已达成"
            : "进行中";

    public string ProgressStatus => SupportsProgressManagement
        ? $"{Math.Min(Progress3, RequiredCount):N0} / {RequiredCount:N0}"
        : Progress3.ToString("N0");

    public static CharacterTaskAdminRecord Create(CharacterTaskRecord task)
    {
        if (!QuestCatalog.TryGetQuest(task.QuestId, out var quest))
        {
            return new CharacterTaskAdminRecord
            {
                QuestId = task.QuestId,
                Name = "未知任务",
                TaskType = task.TaskType,
                RuntimeState = task.RuntimeState,
                Progress2 = task.Progress2,
                Progress3 = task.Progress3,
                RequiredCount = 0,
                ObjectiveSummary = "客户端目录中不存在",
                RewardSummary = string.Empty,
                SupportsProgressManagement = false
            };
        }

        var supportsProgress = QuestCatalog.TryGetMonsterHitObjective(
            task.QuestId, out var objective);
        return new CharacterTaskAdminRecord
        {
            QuestId = task.QuestId,
            Name = quest.Name,
            TaskType = task.TaskType,
            RuntimeState = task.RuntimeState,
            Progress2 = task.Progress2,
            Progress3 = task.Progress3,
            RequiredCount = supportsProgress ? objective.RequiredCount : 0,
            ObjectiveSummary = quest.Objectives.Count == 0
                ? "无目标"
                : string.Join("；", quest.Objectives.Select(item => item.Name)),
            RewardSummary = QuestCatalogAdminRecord.FormatRewards(quest.Rewards),
            SupportsProgressManagement = supportsProgress
        };
    }
}
