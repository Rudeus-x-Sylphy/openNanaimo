using System.Globalization;
using System.IO;

namespace OpenNanaimo.Adapter.Services;

public sealed record QuestScrollDefinition(
    uint ScrollCode,
    string Name,
    uint Price,
    uint QuestId);

public sealed record QuestObjectiveCondition(byte Tag, uint Value, uint Comparison = 1)
{
    public QuestObjectiveCondition(byte tag, uint value) : this(tag, value, 1) { }
    public void Deconstruct(out byte tag, out uint value) { tag = Tag; value = Value; }
}

public sealed record QuestObjectiveDefinition(
    uint ObjectiveId,
    string Name,
    byte ObjectiveType,
    uint TargetCode,
    uint RequiredCount,
    IReadOnlyList<QuestObjectiveCondition> Conditions);

public sealed record QuestRewardDefinition(
    byte RewardType,
    uint RewardCode,
    uint Amount);

public sealed record QuestDefinition(
    uint QuestId,
    string Name,
    IReadOnlyList<QuestObjectiveDefinition> Objectives,
    uint RewardTextId,
    IReadOnlyList<QuestRewardDefinition> Rewards);

public static class QuestCatalog
{
    private const string QuestShopResourceName = "OpenNanaimo.Adapter.ClientData.QH._D33";
    private const string QuestResourceName = "OpenNanaimo.Adapter.ClientData.QT._D30";
    private const int QuestShopGroupCount = 7;
    private const int QuestShopRecordFieldCount = 9;
    private const int OfficialQuestShopRecordCount = 204;
    private const int ObjectiveFieldCount = 27;
    private const int OfficialObjectiveCount = 357;
    private const int OfficialQuestCount = 217;
    private const int QuestFixedFieldCount = 10;
    private const int QuestConditionTripleCount = 4;

    private static readonly Lazy<CatalogData> Data = new(Load);
    private static readonly Lazy<IReadOnlyList<uint>> MainLine = new(() => ComputeMainLine(Data.Value));

    public static IReadOnlyCollection<QuestScrollDefinition> Scrolls => Data.Value.Scrolls.Values.ToArray();
    public static IReadOnlyCollection<QuestDefinition> Quests => Data.Value.Quests.Values.ToArray();
    public static IReadOnlyList<uint> MainLineQuestIds => MainLine.Value;

    private static IReadOnlyList<uint> ComputeMainLine(CatalogData data)
    {
        var sold = data.Scrolls.Values.Select(item => item.QuestId).ToHashSet();
        var story = data.Quests.Values.Where(item => !sold.Contains(item.QuestId)).ToArray();
        var storyIds = story.Select(item => item.QuestId).ToHashSet();
        var next = new Dictionary<uint, uint>();
        var referenced = new HashSet<uint>();
        foreach (var quest in story)
            foreach (var reward in quest.Rewards)
                if (reward.RewardType == 3 && reward.RewardCode == 1 && storyIds.Contains(reward.Amount))
                {
                    next[quest.QuestId] = reward.Amount;
                    referenced.Add(reward.Amount);
                }
        var result = new List<uint>(story.Length);
        var seen = new HashSet<uint>();
        var current = story.Select(item => item.QuestId).Where(id => !referenced.Contains(id)).OrderBy(id => id).FirstOrDefault();
        while (current != 0 && seen.Add(current))
        {
            result.Add(current);
            current = next.GetValueOrDefault(current);
        }
        foreach (var quest in story.OrderBy(item => item.QuestId))
            if (seen.Add(quest.QuestId))
                result.Add(quest.QuestId);
        return result;
    }

    public static bool TryGetScroll(uint scrollCode, out QuestScrollDefinition definition)
        => Data.Value.Scrolls.TryGetValue(scrollCode, out definition!);

    public static bool TryGetQuest(uint questId, out QuestDefinition definition)
        => Data.Value.Quests.TryGetValue(questId, out definition!);

    public static bool TryGetMonsterHitObjective(uint questId, out QuestObjectiveDefinition objective)
    {
        objective = null!;
        if (!TryGetQuest(questId, out var quest)
            || quest.Objectives.Count != 1
            || quest.Objectives[0].ObjectiveType != 26
            || quest.Objectives[0].RequiredCount == 0)
            return false;
        objective = quest.Objectives[0];
        return true;
    }

    public static bool TryGetClearTarget(
        QuestObjectiveDefinition objective,
        out int episode,
        out int difficulty,
        out int dungeonBit,
        out uint requiredPetCode)
    {
        // Kind 14 is not a proven difficulty selector. Retain the API but use
        // -1 for any recorded difficulty, per the original-alignment handoff.
        difficulty = -1;
        return TryGetDungeonClearCondition(objective, out episode, out dungeonBit, out requiredPetCode);
    }

    public static bool TryGetDungeonClearCondition(
        QuestObjectiveDefinition objective, out int episode, out int dungeonBit, out uint requiredPetCode)
    {
        episode = dungeonBit = -1;
        requiredPetCode = 0;
        foreach (var condition in objective.Conditions)
            switch (condition.Tag)
            {
                case 12: episode = checked((int)condition.Value) - 1; break;
                case 13: dungeonBit = checked((int)condition.Value) - 1; break;
                case 24: requiredPetCode = condition.Value; break;
            }
        // These four QT/QS mainline objectives explicitly name the super BOSS.
        // Their kind-13 value is the physical dungeon (3), not its clear-mask
        // slot (4). Do not reinterpret the unverified kind 38 globally.
        if (objective.ObjectiveType == 20 && dungeonBit == 2
            && objective.ObjectiveId is 75000139 or 75000143 or 75000148 or 75000151)
            dungeonBit = 3;
        // Types 21/22/23 are zero-count restriction challenges. They are safe
        // only when evaluated against a live settlement carrying charge/item/
        // revival evidence; a passive task-list refresh has HasClear=false.
        var hasEvaluableCount = objective.RequiredCount != 0
            || objective.ObjectiveType is 21 or 22 or 23;
        return hasEvaluableCount && episode is >= 0 and < 16
            && dungeonBit is >= 0 and < 4;
    }

    public static bool TryGetScoreTarget(QuestObjectiveDefinition objective, out int episode, out uint score)
    {
        episode = -1; score = 0;
        foreach (var condition in objective.Conditions)
        {
            if (condition.Tag == 12) episode = checked((int)condition.Value) - 1;
            if (condition.Tag == 16 && condition.Comparison == 0) score = condition.Value;
        }
        return objective.RequiredCount != 0 && episode is >= 0 and < 16 && score > 0;
    }

    public static bool TryGetPetBossTarget(QuestObjectiveDefinition objective, out int episode, out uint pet)
    {
        episode = -1; pet = 0;
        // Only the 48 catalogued "use purple/blue/lemon cockroach against BOSS"
        // objectives. No guessed interpretation of condition kinds 15 or 38.
        if (objective.ObjectiveType != 25 || objective.RequiredCount != 1
            || objective.Conditions.Any(c => c.Tag == 13)) return false;
        foreach (var c in objective.Conditions)
        {
            if (c.Tag == 12) episode = (int)c.Value - 1;
            if (c.Tag == 24) pet = c.Value;
        }
        return episode is >= 0 and < 16 && pet is 15000001 or 15000002 or 15000003;
    }

    public static bool MatchesSettlement(QuestObjectiveDefinition objective, int episode, int dungeonBit,
        uint pet, uint score, bool solo, bool bossDefeated)
    {
        if (TryGetPetBossTarget(objective, out var bossEpisode, out var bossPet))
            return bossDefeated && episode == bossEpisode && pet == bossPet;
        if (TryGetScoreTarget(objective, out var scoreEpisode, out var threshold))
            return episode == scoreEpisode && score >= threshold;
        if (!TryGetDungeonClearCondition(objective, out var targetEpisode, out var targetBit, out var targetPet))
            return false;
        return episode == targetEpisode && dungeonBit == targetBit
            && MatchesSettlementPet(objective, targetPet, pet)
            && (objective.ObjectiveType != 9 || !solo)
            && (objective.ObjectiveType != 25 || solo);
    }

    private static bool MatchesSettlementPet(QuestObjectiveDefinition objective, uint requiredPet, uint battlePet)
    {
        if (requiredPet == 0 || requiredPet == battlePet) return true;
        // QT names the story-reward Blue Fairy (15005009). The ordinary
        // Blue Fairy (15000010) has the same client name, family 6 and BOO
        // model, but a different socket count. Accept both for this story
        // objective; do not alias inventory IDs or unrelated pet missions.
        return objective.ObjectiveId == 75000139 && objective.ObjectiveType == 20
            && requiredPet == 15005009 && battlePet == 15000010;
    }

    private static CatalogData Load()
    {
        var quests = LoadQuests();
        var scrolls = LoadScrolls(quests);
        return new CatalogData(scrolls, quests);
    }

    private static IReadOnlyDictionary<uint, QuestScrollDefinition> LoadScrolls(
        IReadOnlyDictionary<uint, QuestDefinition> quests)
    {
        var fields = CardCatalog.DecryptFields(QuestShopResourceName);
        if (fields.Length < 4
            || fields[0] != "QUESTSHOP"
            || !ParseInt(fields[2], out var declaredCount)
            || declaredCount != OfficialQuestShopRecordCount)
            throw new InvalidDataException("The official QH quest-shop catalog header is invalid.");

        var result = new Dictionary<uint, QuestScrollDefinition>(declaredCount);
        var offset = 3;
        for (var group = 0; group < QuestShopGroupCount; group++)
        {
            var groupCount = ReadInt(fields, ref offset, "QH group count");
            if (groupCount < 0 || offset + groupCount * QuestShopRecordFieldCount > fields.Length)
                throw new InvalidDataException($"The official QH group {group} exceeds the catalog bounds.");

            for (var index = 0; index < groupCount; index++)
            {
                var record = offset + index * QuestShopRecordFieldCount;
                var scrollCode = ReadUInt(fields[record], "QH scroll code");
                var price = ReadUInt(fields[record + 4], "QH Hans price");
                var questId = ReadUInt(fields[record + 7], "QH quest id");
                if (!quests.ContainsKey(questId))
                    throw new InvalidDataException($"QH scroll {scrollCode} references missing QT quest {questId}.");
                if (!result.TryAdd(scrollCode, new QuestScrollDefinition(
                        scrollCode, fields[record + 1], price, questId)))
                    throw new InvalidDataException($"The official QH catalog repeats scroll {scrollCode}.");
            }
            offset += groupCount * QuestShopRecordFieldCount;
        }

        if (result.Count != declaredCount)
            throw new InvalidDataException($"QH declares {declaredCount} scrolls but contains {result.Count}.");
        return result;
    }

    private static IReadOnlyDictionary<uint, QuestDefinition> LoadQuests()
    {
        var fields = CardCatalog.DecryptFields(QuestResourceName);
        if (fields.Length < 4
            || fields[0] != "QUEST"
            || !ParseInt(fields[2], out var objectiveCount)
            || objectiveCount != OfficialObjectiveCount)
            throw new InvalidDataException("The official QT quest catalog header is invalid.");

        var offset = 3;
        if (offset + objectiveCount * ObjectiveFieldCount >= fields.Length)
            throw new InvalidDataException("The official QT objective table exceeds the catalog bounds.");
        var objectives = new Dictionary<uint, QuestObjectiveDefinition>(objectiveCount);
        for (var index = 0; index < objectiveCount; index++)
        {
            var record = offset + index * ObjectiveFieldCount;
            var objectiveId = ReadUInt(fields[record], "QT objective id");
            var objectiveType = checked((byte)ReadUInt(fields[record + 2], "QT objective type"));
            var targetCode = ReadUInt(fields[record + 3], "QT objective target");
            var requiredCount = ReadUInt(fields[record + 4], "QT objective count");
            var conditions = new List<QuestObjectiveCondition>(4);
            for (var conditionIndex = 0; conditionIndex < 4; conditionIndex++)
            {
                var condition = record + 5 + conditionIndex * 3;
                var tag = ReadUInt(fields[condition], "QT objective condition tag");
                var value = ReadUInt(fields[condition + 2], "QT objective condition value");
                if (tag != 0)
                    conditions.Add(new QuestObjectiveCondition(checked((byte)tag), value,
                        ReadUInt(fields[condition + 1], "QT objective comparison or skill code")));
            }
            if (!objectives.TryAdd(objectiveId, new QuestObjectiveDefinition(
                    objectiveId, fields[record + 1], objectiveType, targetCode, requiredCount, conditions)))
                throw new InvalidDataException($"The official QT catalog repeats objective {objectiveId}.");
        }
        offset += objectiveCount * ObjectiveFieldCount;

        var questCount = ReadInt(fields, ref offset, "QT quest count");
        if (questCount != OfficialQuestCount)
            throw new InvalidDataException($"The official QT quest count is {questCount}, expected {OfficialQuestCount}.");
        var quests = new Dictionary<uint, QuestDefinition>(questCount);
        for (var index = 0; index < questCount; index++)
        {
            EnsureAvailable(fields, offset, QuestFixedFieldCount + QuestConditionTripleCount * 3 + 1, "QT quest header");
            var questId = ReadUInt(fields[offset], "QT quest id");
            var name = fields[offset + 4];
            offset += QuestFixedFieldCount + QuestConditionTripleCount * 3;

            var linkedObjectiveCount = ReadInt(fields, ref offset, "QT linked objective count");
            if (linkedObjectiveCount < 0)
                throw new InvalidDataException($"QT quest {questId} has a negative objective count.");
            EnsureAvailable(fields, offset, linkedObjectiveCount * 2 + 2, "QT linked objectives");
            var linkedObjectives = new List<QuestObjectiveDefinition>(linkedObjectiveCount);
            for (var objectiveIndex = 0; objectiveIndex < linkedObjectiveCount; objectiveIndex++)
            {
                var objectiveId = ReadUInt(fields[offset + objectiveIndex], "QT linked objective id");
                if (!objectives.TryGetValue(objectiveId, out var objective))
                    throw new InvalidDataException($"QT quest {questId} references missing objective {objectiveId}.");
                linkedObjectives.Add(objective);
            }
            offset += linkedObjectiveCount;
            offset += linkedObjectiveCount; // Native loader stores this parallel signed-value array separately.

            var rewardTextId = ReadUInt(fields[offset++], "QT reward text id");
            var rewardCount = ReadInt(fields, ref offset, "QT reward count");
            if (rewardCount < 0)
                throw new InvalidDataException($"QT quest {questId} has a negative reward count.");
            EnsureAvailable(fields, offset, rewardCount * 3, "QT rewards");
            var rewards = new List<QuestRewardDefinition>(rewardCount);
            for (var rewardIndex = 0; rewardIndex < rewardCount; rewardIndex++)
            {
                var record = offset + rewardIndex * 3;
                rewards.Add(new QuestRewardDefinition(
                    checked((byte)ReadUInt(fields[record], "QT reward type")),
                    ReadUInt(fields[record + 1], "QT reward code"),
                    ReadUInt(fields[record + 2], "QT reward amount")));
            }
            offset += rewardCount * 3;
            if (!quests.TryAdd(questId, new QuestDefinition(
                    questId, name, linkedObjectives, rewardTextId, rewards)))
                throw new InvalidDataException($"The official QT catalog repeats quest {questId}.");
        }

        if (quests.Count != questCount)
            throw new InvalidDataException($"QT declares {questCount} quests but contains {quests.Count}.");
        return quests;
    }

    private static void EnsureAvailable(string[] fields, int offset, int count, string label)
    {
        if (offset < 0 || count < 0 || offset > fields.Length - count)
            throw new InvalidDataException($"{label} exceeds the official catalog bounds.");
    }

    private static int ReadInt(string[] fields, ref int offset, string label)
    {
        EnsureAvailable(fields, offset, 1, label);
        if (!ParseInt(fields[offset], out var value))
            throw new InvalidDataException($"{label} is not an integer: {fields[offset]}");
        offset++;
        return value;
    }

    private static uint ReadUInt(string value, string label)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException($"{label} is not an unsigned integer: {value}");
        return result;
    }

    private static bool ParseInt(string value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private sealed record CatalogData(
        IReadOnlyDictionary<uint, QuestScrollDefinition> Scrolls,
        IReadOnlyDictionary<uint, QuestDefinition> Quests);
}
