// ============================================================
// GameMode.cs —— osu 的**游玩模式**
//
// 【为什么要独立成文件】
//   它现在有三边要用：`TosuJsonParser`（把 tosu 报的数字翻成它）、
//   `SceneProfiles`（按模式取站位）、悬浮窗（按模式决定发哪份站位）。
//   挂在任何一边都会让另外两边多一个依赖 —— 和 `RangeCheckMode` 同一个道理
//   （那次是"被两边共用 → 独立成文件"，已经踩过两次）。
//
// 【★ 数值就是 tosu 报的 `gameplay.gameMode`，别改这个对应关系】
//   0 = osu!standard、1 = taiko、2 = catch、3 = mania —— 这是 osu 自己的枚举顺序。
//
//   **一个撞号的坑（用户实测）**：自定义 ruleset（比如"戳泡泡"）报的也是 **0**，
//   和 osu!standard 撞在一起。所以：
//     · 不能把"模式 == Standard"当成"一定是 osu!standard"
//     · 界面上也不能这么承诺，否则又是一个名不副实的字段
//   影响面可接受：这类模式会套用 osu 那套站位 —— 至少有个合理的位置。
//
// 【它不引 WPF、不碰文件】纯数据 → 能进控制台测（《项目结构约定》§二）。
// ============================================================
using System;

namespace OsuLive2dOverlay;

/// <summary>游玩模式。数值 = tosu 的 `gameplay.gameMode`。</summary>
public enum GameMode
{
    /// <summary>osu!standard（tosu 报 0）。**自定义 ruleset 也报 0**，和它撞号。</summary>
    Standard = 0,

    /// <summary>osu!taiko（tosu 报 1）</summary>
    Taiko = 1,

    /// <summary>osu!catch（tosu 报 2）</summary>
    Catch = 2,

    /// <summary>osu!mania（tosu 报 3）</summary>
    Mania = 3
}

public static class GameModes
{
    /// <summary>
    /// tosu 报的数字 → 枚举。**认不出的（null、负数、大于 3）一律给 null** ——
    /// 白名单，和 `GameStateTracker` 对 `menu.state` 的做法一致：
    /// 认不出就不按它挑站位，退回场景自己那份，而不是瞎猜一个。
    /// </summary>
    public static GameMode? FromTosu(int? raw) => raw switch
    {
        0 => GameMode.Standard,
        1 => GameMode.Taiko,
        2 => GameMode.Catch,
        3 => GameMode.Mania,
        _ => null
    };

    /// <summary>
    /// 枚举 → 它在配置里的键名。
    /// **用名字不用数字**：`"mania"` 谁都看得懂，`"3"` 得回去翻代码。
    /// </summary>
    public static string KeyOf(GameMode mode) => mode switch
    {
        GameMode.Standard => "osu",
        GameMode.Taiko => "taiko",
        GameMode.Catch => "catch",
        GameMode.Mania => "mania",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "认不出的游玩模式")
    };

    /// <summary>四个模式（配置里那一组的顺序，界面照着它排）</summary>
    public static readonly GameMode[] All =
        { GameMode.Standard, GameMode.Taiko, GameMode.Catch, GameMode.Mania };
}
