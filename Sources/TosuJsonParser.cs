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
//   gameplay.gameMode       —— 0 = 还没进游戏，3 = 进了（含暂停）
//   gameplay.combo.current  —— 当前连击
//   gameplay.combo.max      —— 本局最大连击
// ============================================================
using System.Text.Json;

namespace OsuLive2dOverlay;

/// <summary>
/// 从一包 tosu JSON 里抠出来的字段。
///
/// MenuState / GameMode 用可空类型，是为了区分**「读到 0」和「没这个字段」** ——
/// 前者是"osu 在主菜单"，后者是"这一段数据里压根没有"，处理方式完全不同。
/// </summary>
public readonly record struct TosuSnapshot(
    int? MenuState,
    int? GameMode,
    int Combo,
    int MaxCombo);

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
            var combo = 0;
            var maxCombo = 0;

            if (root.TryGetProperty("gameplay", out var gameplayEl))
            {
                gameMode = ReadInt(gameplayEl, "gameMode");

                if (gameplayEl.ValueKind == JsonValueKind.Object
                    && gameplayEl.TryGetProperty("combo", out var comboEl))
                {
                    combo = ReadInt(comboEl, "current") ?? 0;
                    maxCombo = ReadInt(comboEl, "max") ?? 0;
                }
            }

            return new TosuSnapshot(menuState, gameMode, combo, maxCombo);
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
}
