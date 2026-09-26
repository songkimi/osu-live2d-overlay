// ============================================================
// TriggerResolver.cs —— 把"状态机报的事件"翻译成"要发给页面的动作"
//
// 为什么单独一个类：这段"查配置、决定演什么说什么"的逻辑，
//   ① 原本散在 OverlayWindow 里（那个文件 700 多行，越来越难找东西）
//   ② 它根本不依赖 WPF / WebView2 —— 搬出来之后**可以用控制台测试**
//   ③ 热键要"造个假事件走一遍同样的处理"也变得只有几行
//
// 职责边界：它只说"该发什么"，不负责"怎么发出去"。
//   发消息、写日志、刷状态栏是 OverlayWindow.HandleEvent 的事。
// ============================================================
using System.Windows.Input;

namespace OsuLive2dOverlay;

/// <summary>要发给页面的动作类型（和页面的消息 type 一一对应）</summary>
public enum TriggerActionKind
{
    /// <summary>跨过阈值 → 页面消息 type = "combo"</summary>
    Combo,

    /// <summary>断连击 → 页面消息 type = "miss"</summary>
    Miss,

    /// <summary>区间变化 → 页面消息 type = "steady"（常态表情）</summary>
    Steady,
    /// <summary>被触摸</summary>
    Touched,
    Result
}

/// <summary>一次要发给页面的动作</summary>
public sealed record TriggerAction(
    TriggerActionKind Kind,
    string Expression,     // 瞬时表情 / 常态表情的标识（可能是空串）
    string Action,         // 动作标识（可能是空串）
    string Emotion,        // 语音情绪（可能是空串）
    string Note);          // 给人看的说明，会写进日志和状态栏

public static class TriggerResolver
{
    /// <summary>
    /// 把一个事件翻译成"要发给页面的动作"。
    /// 不需要做任何事时返回**空列表**（不是 null）。
    /// </summary>
    public static List<TriggerAction> Resolve(ComboEvent evt, PluginConfig config)
    {
        var actions = new List<TriggerAction>();

        switch (evt.Kind)
        {
            case ComboEventKind.Step:
            {
                var trigger = config.ComboTriggers.FirstOrDefault(t => t.Threshold == evt.Threshold);
                var action = Build(TriggerActionKind.Combo,
                    trigger?.Reaction?.Expression ?? "",
                    trigger?.Reaction?.Action ?? "",
                    trigger?.Reaction?.VoiceEmotion ?? "",
                    $"{evt.Combo} 连击（跨过 {evt.Threshold}）");
                if (action is not null) actions.Add(action);
                break;
            }

            case ComboEventKind.Break:
            {
                var (expression, action, emotion, isBig) = ResolveMiss(evt.PreviousCombo, config);
                var built = Build(TriggerActionKind.Miss, expression, action, emotion,
                    isBig ? "断连击（大额）" : "断连击（小额）");
                if (built is not null) actions.Add(built);
                break;
            }

            case ComboEventKind.RangeChanged:
            {
                // 索引越界 = 配置和状态机不同步（本该不可能发生）→ 静默跳过。
                // 别产出一个"三个字段全空"的动作：那会变成一条空消息发给页面。
                if (evt.RangeIndex < 0 || evt.RangeIndex >= config.Ranges.Count) break;

                var range = config.Ranges[evt.RangeIndex];
                if (string.IsNullOrWhiteSpace(range.Expression)) break;

                actions.Add(new TriggerAction(
                    Kind: TriggerActionKind.Steady,
                    Expression: range.Expression,
                    Action: "",
                    Emotion: "",
                    Note: $"进入区间 {evt.RangeIndex} → 常态表情 {range.Expression}"));
                break;
            }
        }

        return actions;
    }
    /// <summary>
    /// 触摸时发送的动作
    /// </summary>
    /// <param name="reaction"></param>
    /// <param name="note"></param>
    /// <returns></returns>
    public static TriggerAction? ResolveTouch(ReactionConfig? reaction, string note)
    {
        if(reaction == null) return null;
        return Build(TriggerActionKind.Touched, reaction.Expression, reaction.Action, reaction.VoiceEmotion, note);
    }
    /// <summary>
    /// 结算界面的动作
    /// </summary>
    /// <param name="reaction"></param>
    /// <param name="note"></param>
    /// <returns></returns>
    public static TriggerAction? ResolveResult(ReactionConfig? reaction, string note)
    => reaction is null ? null
       : Build(TriggerActionKind.Result, reaction.Expression, reaction.Action, reaction.VoiceEmotion, note);

    /// <summary>
    /// 统一收口：三个字段全空就不产出 —— 别给页面发一条什么都没说的消息。
    /// </summary>
    private static TriggerAction? Build(TriggerActionKind kind, string expression, string action, string emotion, string note)
    {
        if (string.IsNullOrWhiteSpace(expression) && string.IsNullOrWhiteSpace(action) && string.IsNullOrWhiteSpace(emotion))
            return null;

        return new TriggerAction(
            Kind: kind,
            Expression: expression ?? string.Empty,
            Action: action ?? string.Empty,
            Emotion: emotion ?? string.Empty,
            Note: note ?? string.Empty);
    }

    /// <summary>
    /// 断连击前手里有 previousCombo 连击 → 该用哪一档。
    /// 两档门槛都不到就返回三个空值（什么都不做，这是产品规则：连击没起来就不理）。
    /// </summary>
    private static (string Expression, string Action, string Emotion, bool IsBig) ResolveMiss(int previousCombo, PluginConfig config)
    {
        if (previousCombo >= config.Miss.BigThreshold)
            return (config.Miss.BigReaction?.Expression ?? "",
                    config.Miss.BigReaction?.Action ?? "",
                    config.Miss.BigReaction?.VoiceEmotion ?? "",
                    true);

        if (previousCombo >= config.Miss.SmallThreshold)
            return (config.Miss.SmallReaction?.Expression ?? "",
                    config.Miss.SmallReaction?.Action ?? "",
                    config.Miss.SmallReaction?.VoiceEmotion ?? "",
                    false);

        return ("", "", "", false);
    }
}
