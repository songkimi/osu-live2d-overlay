// ============================================================
// TouchReaction.cs —— 纯逻辑：被摸到的时候，从列表里挑一条来播
//
// 【为什么单独一个文件】
//   和 ComboTracker / GameStateTracker / UpdateVersion 一个道理：
//   进一个列表 + 一个数，出一条反应 —— 不碰 WPF、不碰网络、不碰文件，
//   所以能进控制台测（《项目结构约定》§二）。
//
// 【★ "随机"为什么要拆成两半】
//   随机本身**没法测**（跑两次结果不一样，测试会忽绿忽红）。
//   所以这里只做"给定第几下，取哪一条"—— 那是纯函数，能穷举；
//   至于"第几下"是多少，由调用方用 Random 摇出来：
//
//     调用方：  TouchReaction.Pick(config.Touch, Random.Shared.Next())
//     测试：    TouchReaction.Pick(list, 0 / 1 / 2 / 100 / -1 / int.MinValue)
//
//   这也是那堆 XxxTracker 的老套路：**把不确定性挤到边界外面**。
//
// 【数大了、数是负的，都不该崩】
//   调用方将来可能传任何东西进来（换个随机源、或者别处算错了）。
//   取模要"绕回来"，而不是"假定它一定在合法范围内"。
//
//   ⚠ **别用 Math.Abs(roll) % count** —— `Math.Abs(int.MinValue)` 会抛
//   OverflowException（int 装不下它的绝对值）。这是个很老但很真的坑，
//   而随机源恰好就是能摇出 int.MinValue 的那种东西。
// ============================================================
using System;
using System.Collections.Generic;

namespace OsuLive2dOverlay;

public static class TouchReaction
{
    /// <summary>
    /// 从触摸反应列表里挑一条。
    ///
    /// 列表是 null 或空的 → 返回 null（调用方什么都不做，别硬造一个空反应出来）。
    /// </summary>
    /// <param name="reactions">配置里那一段 `触摸`</param>
    /// <param name="roll">摇出来的数。**任何 int 都行** —— 大了绕回来，负的也绕回来</param>
    public static ReactionConfig? Pick(IReadOnlyList<ReactionConfig>? reactions, int roll)
    {
        // TODO：你来写。两步：
        //   ① 空列表 / null → return null
        //   ② 取 reactions[把 roll 绕进 0..Count-1 的那个下标]
        if (reactions == null || reactions.Count == 0) return null;
        int count = reactions.Count;
        int index = (roll % count + count) % count;
        //   先想清楚这三个应该分别取到哪一条（测试里就是这么断言的）：
        //     count = 3 时： roll = 3  → ?      （想想"绕一圈"）
        //                   roll = -1 → ?      （负数也不能崩，得绕到尾巴上）
        //                   roll = 100 → ?
        return reactions[index];
    }
}
