// ============================================================
// TosuJsonParser.cs —— 从 tosu 推来的一包 JSON 里抠出我们关心的字段
//
// 为什么单独成一个文件：
//   TosuClient 是 WebSocket 客户端 —— 要连服务器、要等消息、要处理断线重连，
//   **这种东西没法在控制台里测**。而"从一包 JSON 里抠字段"是纯逻辑：
//   给一段字符串、返回几个值，没有副作用 —— 拆出来就能用测试把行为钉死。
//   （和当初把 TriggerResolver 从 OverlayWindow 里搬出来是同一个手法：
//     能用测试测的逻辑，别跟"要连外部世界"的代码混在一起。）
//
// 实测确认的数据路径：
//   menu.state              —— 界面（0 主菜单 / 5 选歌 / 2 打歌 / 7 结算 / 13 多人大厅…）
//   gameplay.gameMode       —— **这是哪个游玩模式**：0=osu 1=taiko 2=catch 3=mania。
//                               ★ 它**不能**用来判断"进没进游戏"（2026-09-20 纠正，见下面的 Hp）
//   gameplay.hp             —— 血条。加载中 = 0，歌曲一开始就是满的 → **判断进没进游戏靠它**
//   gameplay.combo.current  —— 当前连击
//   gameplay.combo.current  —— 当前连击
//   gameplay.combo.max      —— 本局最大连击
// ============================================================
using System.Text.Json;

namespace OsuLive2dOverlay;

/// <summary>
/// 从一包 tosu JSON 里抠出来的字段。
///
/// MenuState / GameMode / Hp 用可空类型，是为了区分**「读到 0」和「没这个字段」** ——
/// 前者是"osu 在主菜单"，后者是"这一段数据里压根没有"，处理方式完全不同。
///
/// **★ Hp 是 2026-09-20 加的**：判断"进了游戏没有"要靠它，不能靠 GameMode。
/// 原判据是 `gameMode == 3`，那其实是**误读了一局 mania**：
/// mania 恰好是第四个模式（=3），于是"加载 0 → 打歌 3"看起来像个布尔值。
/// **换成 std/taiko/catch，这个值从头到尾就是 0/1/2，永远不会变成 3** ——
/// 结果就是打歌时角色不显示。详见 `GameStateTracker` 的说明。
///
/// GameMode 留着（不删）：它仍然是"这是哪个模式"的事实，只是**不再用来判断进没进游戏**。
/// </summary>
public readonly record struct TosuSnapshot(
    int? MenuState,
    int? GameMode,
    double? Hp,
    int Combo,
    int MaxCombo,
    double? Accuracy);      // ★ 2026-09-26：结算准确率（0~100）。非结算时 tosu 报 0，**用的时候必须限定 state==7**

public static class TosuJsonParser
{
    /// <summary>
    /// 解析一包 tosu JSON。
    ///
    /// 返回 <c>null</c> 表示**这一包没法用**（不是 JSON、空串、心跳包）—— 调用方应当整包跳过。
    /// 返回 <see cref="TosuSnapshot"/> 表示**这一包能用**，某个字段是 null / 0 只是"没读到"。
    ///
    /// 这个区分很要紧：tosu 的心跳包是持续在推的，如果把"没法用"也当成"缺字段的快照"，
    /// 调用方会拿空字段去更新状态，表现成角色一下一下地闪。
    /// </summary>
    public static TosuSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;                        // 心跳包 / 半截包
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;   // 数组之类一律当没法用

            var menuState = root.TryGetProperty("menu", out var menuEl) ? ReadInt(menuEl, "state") : null;

            int? gameMode = null;
            double? hp = null;
            var combo = 0;
            var maxCombo = 0;

            if (root.TryGetProperty("gameplay", out var gameplayEl))
            {
                gameMode = ReadInt(gameplayEl, "gameMode");
                hp = ReadHp(gameplayEl);

                if (gameplayEl.ValueKind == JsonValueKind.Object
                    && gameplayEl.TryGetProperty("combo", out var comboEl))
                {
                    combo = ReadInt(comboEl, "current") ?? 0;
                    maxCombo = ReadInt(comboEl, "max") ?? 0;
                }
            }

            // 结算准确率（★ 2026-09-26）—— **只从 resultsScreen 段读**。
            //
            // 为什么不用 gameplay.accuracy：抓包实测（2026-09-26，四个界面的包都看过）——
            // 非结算时 `resultsScreen.accuracy` 是 **0**、而 `gameplay.accuracy` 是 **100**，
            // 两个都是假值。这里只负责"老实读出来"，**什么时候能用是调用方的事**
            // （靠 `menu.state == 7` 挡，见 Logic/ResultReaction.cs 的文件头）。
            //
            // **读不到给 null，不给 0** —— "这个字段不在"和"值是 0"是两回事。
            double? accuracy = null;
            if (root.TryGetProperty("resultsScreen", out var resultsEl))
                accuracy = ReadDouble(resultsEl, "accuracy");

            return new TosuSnapshot(menuState, gameMode, hp, combo, maxCombo, accuracy);
        }
    }

    /// <summary>
    /// 从一个对象里读一个整数：缺字段、不是对象、值不是数字 → 都给 null。
    ///
    /// 先看 ValueKind 是必须的：`TryGetInt32` 只在「值是数字、但超出 int 范围」时返回 false，
    /// 值**根本不是数字**时（比如 "state": "2"）它会直接抛 InvalidOperationException ——
    /// **名字里带 Try 的方法不代表它不会抛**，判据永远在文档的 Exceptions 那一节。
    /// </summary>
    private static int? ReadInt(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number) return null;
        return el.TryGetInt32(out var value) ? value : null;
    }

    /// <summary>
    /// 读血条。**它不是一个数字，是一个对象**：
    /// <code>"hp": { "normal": 200, "smooth": 200 }</code>
    ///
    /// ★ 2026-09-20 实测纠错 —— 这里原来按"数字"解析（还在注释里专门担心过 `200.0`
    /// 这种带小数点的写法），于是 `ValueKind != Number` 直接返回 null。
    /// 后果不是"读不到就算了"：当时还配了一句"缺 hp 就乐观当作在游戏里"的兜底，
    /// 两错叠加 → **加载期间角色就跳到打歌的位置**（用户实测发现，抓包在
    /// `osu插件探索\加载界面判断`）。
    ///
    /// > 教训：**字段的形状要去抓包里看，不能靠猜。** 当时 `TosuPacketLogger` 的抓包
    /// > 就在手边，我担心的是"它会不会是 200.0"，真正的问题是"它根本不是数字"。
    ///
    /// 取 `normal`（真实血量）；`smooth` 是给界面用的平滑值，只在没有 `normal` 时退而求其次。
    /// 另外仍然接受"直接就是个数字"的写法 —— 万一某个 tosu 版本或 lazer 那样报。
    /// </summary>
    private static double? ReadHp(JsonElement gameplayEl)
    {
        if (gameplayEl.ValueKind != JsonValueKind.Object) return null;
        if (!gameplayEl.TryGetProperty("hp", out var hpEl)) return null;

        if (hpEl.ValueKind == JsonValueKind.Object)
            return ReadDouble(hpEl, "normal") ?? ReadDouble(hpEl, "smooth");

        return hpEl.ValueKind == JsonValueKind.Number && hpEl.TryGetDouble(out var value) ? value : null;
    }

    /// <summary>
    /// 从一个对象里读一个浮点数。
    ///
    /// **hp 的 `normal` / `smooth` 这类字段必须用这个读，不能用 <see cref="ReadInt"/>**：
    /// 它们完全可能报成 `200.0` 或 `0.85` 这种带小数点的形式，
    /// 而 `TryGetInt32` 对 `200.0` 会返回 **false**（它按整数文本解析，小数点就过不了）——
    /// 于是被读成 null，判据永远不成立，**打歌时角色又不显示了**。
    /// </summary>
    private static double? ReadDouble(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number) return null;
        return el.TryGetDouble(out var value) ? value : null;
    }
}
