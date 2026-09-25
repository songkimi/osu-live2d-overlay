// ============================================================
// CharacterOpacity.cs —— "角色现在该多不透明"
//
// 【为什么单独一个类，而不是在窗口里写一句 `alpha = 0`】
//   因为这句话迟早会变复杂。现在只有两档（看不见 / 完全看得见），
//   但已经能预见的需求是"**角色透明度跟着血条走**"（血少了角色变淡）。
//   到那时候，"什么时候该多透明"会变成一段有分支、可能要插值、要能单独测的逻辑 ——
//   那种东西不该长在 `OverlayWindow` 那个一千多行的文件里。
//
//   所以现在就把这个决定**收到一个地方**：将来只改 `Resolve`，
//   窗口那边一行都不用动（它只管把算出来的值发出去）。
//
// 【它和 `show` 是两回事 —— 这一条最容易混】
//   · `show = false` → 这个界面**压根不显示角色**（没启用 / 认不出的界面）
//   · `opacity = 0`  → 角色**还在**，只是暂时看不见（加载期间）
//   后者不动位置、不卸载模型，歌一开始把透明度恢复成 1 就完了。
//   这正是我们要的：加载时人物既不乱跳，也不是"消失了像是坏了"。
//
// 【它不引 WPF、不碰文件】纯计算 → 能进控制台测（《项目结构约定》§二）。
// ============================================================
namespace OsuLive2dOverlay;

public static class CharacterOpacity
{
    /// <summary>完全看得见</summary>
    public const double Opaque = 1.0;

    /// <summary>完全看不见（仍占着位置、仍挂着模型，只是透明）</summary>
    public const double Hidden = 0.0;

    /// <summary>
    /// 角色现在该多不透明：`0` = 看不见，`1` = 完全看得见。
    ///
    /// **将来要加"跟着血条变淡"，就在这个方法里加** ——
    /// 例如多收一个血条比例参数，低血量时返回 `0.4`；
    /// 调用方（`OverlayWindow`）不需要跟着改。
    /// </summary>
    /// <param name="sceneUsable">
    /// 当前界面是不是"要显示角色"。`false` = 界面没启用 / 认不出 ——
    /// 那时角色**根本不该出现**。它和"暂时藏起来"是两回事，但结果都是 0。
    /// </param>
    /// <param name="loading">
    /// 谱面还在加载（见 <see cref="GameStateTracker.IsLoading"/>）。
    /// </param>
    public static double Resolve(bool sceneUsable, bool loading)
    {
        if (!sceneUsable) return Hidden;   // 这个界面不显示角色
        if (loading) return Hidden;        // 加载中：藏起来，但**位置和模型都不动**

        return Opaque;
    }
}
