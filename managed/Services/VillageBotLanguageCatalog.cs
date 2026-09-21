using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal static class VillageBotLanguageCatalog
{
    private static readonly string[] IdlePhrases =
    [
        "今天村庄里挺热闹的。", "先在这里休息一会儿。", "有人准备去地宫吗？", "刚才看见一只很可爱的宠物。",
        "慢慢逛，总能发现新东西。", "今天也要记得完成任务。", "我的宠物又升了一级。", "商城里好像上了新衣服。",
        "有人一起刷卡片吗？", "地宫组队的话会轻松很多。", "站一会儿再出发。", "这个村庄的风景真不错。",
        "刚整理完宠物箱。", "卡片册还差好几张呢。", "准备去看看下一扇门后面有什么。", "升级技能也需要不少SP。",
        "有人需要组队就喊我。", "今天的运气应该不错。", "先逛逛再去打怪。", "换套衣服，心情也会变好。",
        "宠物跟在身边很有安全感。", "听说高分结算奖励更好。", "别忘了及时保存公寓装修。", "村庄里到处走走也挺有意思。"
    ];

    public static IReadOnlyList<VillageBotPhraseRecord> Entries { get; } =
    [
        new() { Category = "主动发言", Trigger = "随机", Text = "从村庄闲聊语句中随机选择，按配置间隔发言。" },
        new() { Category = "问候", Trigger = "你好 / 嗨 / hello", Text = "你好呀，今天准备去哪里玩？" },
        new() { Category = "在线", Trigger = "在吗 / 有人吗", Text = "在呢，我正在村庄里逛逛。" },
        new() { Category = "身份", Trigger = "名字 / 你是谁", Text = "我是村庄里的飞行员。" },
        new() { Category = "宠物", Trigger = "宠物", Text = "宠物可以在宠物箱里更换，记得带它一起冒险。" },
        new() { Category = "地宫", Trigger = "地宫 / 组队 / BOSS", Text = "组队进入地宫会更稳，打完记得看结算奖励。" },
        new() { Category = "卡片", Trigger = "卡片 / 卡片册", Text = "卡片可以通过地宫战斗和结算获得。" },
        new() { Category = "商城", Trigger = "商城 / 买东西", Text = "购买后可以去对应的衣物箱、宠物箱或道具栏查看。" },
        new() { Category = "技能", Trigger = "技能 / SP", Text = "SP可以用来升级技能，装好技能再进入战斗。" },
        new() { Category = "等级", Trigger = "等级 / 升级", Text = "完成任务和地宫战斗都能积累经验。" },
        new() { Category = "致谢", Trigger = "谢谢 / 多谢", Text = "不客气，祝你玩得开心。" },
        new() { Category = "告别", Trigger = "再见 / 拜拜", Text = "再见，路上小心。" },
        new() { Category = "兜底回答", Trigger = "直接叫机器人名字或一般提问", Text = "这个我也在研究，要不要一起去村庄里看看？" }
    ];

    public static string CreateIdlePhrase(IReadOnlyCollection<string>? recentMessages = null)
        => Pick(string.Empty, recentMessages, IdlePhrases);

    public static string CreateReply(
        string playerName,
        string message,
        IReadOnlyCollection<string>? recentMessages = null)
    {
        var prefix = string.IsNullOrWhiteSpace(playerName) ? string.Empty : $"{playerName}，";
        if (ContainsAny(message, "你好", "嗨", "哈喽", "hello", "hi"))
            return Pick(prefix, recentMessages,
                "你好呀，今天准备去哪里玩？",
                "嗨，刚好在村庄碰见你。",
                "你好，今天也一起四处逛逛吧。",
                "哈喽，我正准备在村庄转一圈。");
        if (ContainsAny(message, "在吗", "有人吗"))
            return Pick(prefix, recentMessages,
                "在呢，我正在村庄里逛逛。",
                "我在，刚停下来休息一会儿。",
                "在呀，你想去哪里？",
                "在这里呢，正好没走远。");
        if (ContainsAny(message, "名字", "你是谁"))
            return Pick(prefix, recentMessages,
                "我是村庄里的飞行员。",
                "我也是来这里冒险的飞行员。",
                "在村庄里经常能看见我。",
                "叫我的名字就能找到我啦。");
        if (message.Contains("宠物", StringComparison.OrdinalIgnoreCase))
            return Pick(prefix, recentMessages,
                "宠物可以在宠物箱里更换，记得带它一起冒险。",
                "换好宠物再出发，战斗时会轻松一些。",
                "我也常去宠物箱看看有没有合适的伙伴。",
                "带上喜欢的宠物，在村庄里也能看见它跟随。");
        if (ContainsAny(message, "地宫", "组队", "boss"))
            return Pick(prefix, recentMessages,
                "组队进地宫会更稳，打完记得看结算奖励。",
                "地宫最好找几个人一起去，路上能互相照应。",
                "打BOSS前先准备好宠物和技能吧。",
                "想组队的话可以先开房间等其他人加入。");
        if (ContainsAny(message, "卡片", "卡册"))
            return Pick(prefix, recentMessages,
                "卡片可以通过地宫战斗和结算获得。",
                "我也在慢慢收集卡片册里缺少的卡片。",
                "通关结算时记得看看拿到了什么卡片。",
                "有些卡片要多刷几次地宫才容易遇到。");
        if (ContainsAny(message, "商城", "商场", "买东西"))
            return Pick(prefix, recentMessages,
                "购买后可以去对应的物品栏查看。",
                "买完记得去衣物箱、宠物箱或道具栏找找。",
                "商城里的东西会放进对应分类的背包。",
                "先看看物品类型，买完就知道该去哪个箱子找了。");
        if (ContainsAny(message, "技能", "sp"))
            return Pick(prefix, recentMessages,
                "SP可以升级技能，装好技能再进入战斗。",
                "技能升级以后，打地宫会顺手不少。",
                "别忘了把技能放进技能栏再出发。",
                "有SP的话可以先看看哪些技能值得升级。");
        if (ContainsAny(message, "等级", "升级", "经验"))
            return Pick(prefix, recentMessages,
                "完成任务和地宫战斗都能积累经验。",
                "多做任务，等级会升得更稳定。",
                "地宫通关也能拿到不少经验。",
                "慢慢玩就会升级，不用一直赶进度。");
        if (ContainsAny(message, "谢谢", "多谢"))
            return Pick(prefix, recentMessages,
                "不客气，祝你玩得开心。",
                "没事，能帮上忙就好。",
                "不用谢，路上小心。",
                "客气啦，下次村庄见。");
        if (ContainsAny(message, "再见", "拜拜", "下次见"))
            return Pick(prefix, recentMessages,
                "再见，路上小心。",
                "拜拜，下次村庄再见。",
                "回头见，我再逛一会儿。",
                "下次见，祝你冒险顺利。");
        return Pick(prefix, recentMessages,
            "这个我也在研究，要不要一起去村庄里看看？",
            "我还不太确定，等我再四处看看。",
            "这个问题挺有意思，我也想弄清楚。",
            "要不先去附近转转，也许能找到答案。");
    }

    private static string Pick(
        string prefix,
        IReadOnlyCollection<string>? recentMessages,
        params string[] candidates)
    {
        var available = recentMessages is null || recentMessages.Count == 0
            ? candidates
            : candidates.Where(candidate => !recentMessages.Any(message =>
                message.EndsWith(candidate, StringComparison.Ordinal))).ToArray();
        var pool = available.Length > 0 ? available : candidates;
        return prefix + pool[Random.Shared.Next(pool.Length)];
    }

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
