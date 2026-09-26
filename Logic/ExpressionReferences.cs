// ============================================================
// ExpressionReferences.cs —— 配置里"哪些标识被引用了、被引了几处"
//
// 【为什么要有它，而不是在界面里数一遍】
//   同一件事本来已经有实现：`ModelHost.CollectUsedIds` —— 它决定**往模型里补注册哪些标识**。
//   界面要显示"已被引用 N 处"，如果再写一份计数逻辑，那就是**两份真相**：
//   补丁按 A 收、界面按 B 数，用户在界面上看到"已引用 2 处"，实际却有 3 处没补上。
//   这个项目一路在删的就是这种东西（见定稿决策 49 那三次）。
//
//   所以把它提出来，两边都用这一份：
//     · `ModelHost` 补注册 → `UsedIds(config)`（去重后的两个集合）
//     · 界面显示引用数   → `Collect(config)`（**逐个列出引用处**，不去重）
//   `UsedIds` 就是 `Collect` 去重得来的 —— 只有一个地方知道"什么算引用"。
//
// 【哪些算引用】搬自 ModelHost，一处没改：
//   常驻组件[] / 常态区间[].表情 / 连击触发[].反应 / 失误触发的小额·大额反应 / 待机动作
//   注意**常驻组件算表情引用**（ModelHost 就是这么把它加进 Expressions 的）。
//
// 【它不碰文件、不碰 WPF】纯数据整理 → 能进控制台测（《项目结构约定》§二）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace OsuLive2dOverlay;

/// <summary>被引用的是表情还是动作</summary>
public enum ReferenceKind
{
    Expression,
    Motion
}

/// <summary>
/// 一处引用：哪个标识、被配置里的哪一项引用。
/// <paramref name="Where"/> 是给人看的位置说明（"常态区间[2]（50~99）"），
/// 界面上点开引用列表时直接显示它。
/// </summary>
public sealed record ReferenceSite(ReferenceKind Kind, string Id, string Where);

public static class ExpressionReferences
{
    /// <summary>
    /// 把所有引用处**逐个**列出来（同一个标识被引两次就出现两次）。
    ///
    /// 顺序固定（常驻组件 → 常态区间 → 连击触发 → 失误 → 待机动作），
    /// 不跟着字典/集合的遍历顺序变 —— 同一份配置每次数出来要一模一样，
    /// 否则界面上的"引用 3 处"会一会儿一个样。
    /// </summary>
    public static IReadOnlyList<ReferenceSite> Collect(PluginConfig config)
    {
        var sites = new List<ReferenceSite>();

        void Take(ReferenceKind kind, string? id, string where)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            sites.Add(new ReferenceSite(kind, id.Trim(), where));
        }

        void TakeReaction(ReactionConfig? reaction, string where)
        {
            if (reaction is null) return;
            Take(ReferenceKind.Expression, reaction.Expression, where);
            Take(ReferenceKind.Motion, reaction.Action, where);
        }

        // ① 常驻组件：算**表情**引用（与 ModelHost 一致）
        for (var i = 0; i < config.PersistentParts.Count; i++)
            Take(ReferenceKind.Expression, config.PersistentParts[i], $"常驻组件[{i + 1}]");

        // ② 常态区间
        for (var i = 0; i < config.Ranges.Count; i++)
        {
            var range = config.Ranges[i];
            var span = range.Max is null ? $"{range.Min} 以上" : $"{range.Min}~{range.Max}";
            Take(ReferenceKind.Expression, range.Expression, $"常态区间[{i + 1}]（{span}）");
        }

        // ③ 连击触发
        for (var i = 0; i < config.ComboTriggers.Count; i++)
        {
            var trigger = config.ComboTriggers[i];
            TakeReaction(trigger.Reaction, $"连击触发[{i + 1}]（{trigger.Threshold} 连击）");
        }

        // ④ 失误反应的两档
        TakeReaction(config.Miss?.SmallReaction, "失误反应（小额）");
        TakeReaction(config.Miss?.BigReaction, "失误反应（大额）");

        // ⑤ 待机动作：动作**组名**
        Take(ReferenceKind.Motion, config.Model.IdleGroup, "待机动作");

        // ⑥ 触摸反应
        for (var i = 0; i < config.Touch.Count; i++)
            TakeReaction(config.Touch[i], $"触摸反应[{i + 1}]");

        // ⑦ 结算反应
        for (var i = 0; i < config.ResultRanges.Count; i++)
        {
            var range = config.ResultRanges[i];
            var span = range.Max is null
                ? $"{range.Min:0.##} 以上"
                : $"{range.Min:0.##}~{range.Max.Value:0.##}";

            TakeReaction(range.Reaction, $"结算反应[{i + 1}]（{span}）");
        }

        return sites;
    }

    /// <summary>
    /// 标识 → 被引用几处。键不分大小写（和 `ModelHost` 里的集合一致）——
    /// 否则 `Idle` 和 `idle` 会被数成两个标识。
    /// </summary>
    public static IReadOnlyDictionary<string, int> CountBy(PluginConfig config)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var site in Collect(config))
            counts[site.Id] = counts.TryGetValue(site.Id, out var n) ? n + 1 : 1;

        return counts;
    }

    /// <summary>某个标识的全部引用处（界面上"点开看看它被谁用了"）</summary>
    public static IReadOnlyList<ReferenceSite> SitesOf(PluginConfig config, ReferenceKind kind, string id)
        => Collect(config)
            .Where(s => s.Kind == kind && string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// 被引用到的标识（去重）。**`ModelHost` 补注册用的就是它** ——
    /// 模型里可能有好几百个资源，只有被引用的才需要补进 model3.json。
    /// </summary>
    public static (HashSet<string> Expressions, HashSet<string> Motions) UsedIds(PluginConfig config)
    {
        var expressions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var motions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var site in Collect(config))
        {
            if (site.Kind == ReferenceKind.Expression) expressions.Add(site.Id);
            else motions.Add(site.Id);
        }

        return (expressions, motions);
    }
}
