// ============================================================
// GameStateTracker.cs —— 把 tosu 报的原始状态翻译成"角色现在在哪个界面"
//
// 为什么单独一个类：
//   和 ComboTracker / TriggerResolver 一个道理 —— 这段"查表、判状态"的逻辑
//   不依赖 WPF 也不依赖网络，搬出来之后**可以用控制台测试**，
//   而且**外部的易变值被挡在这一层里面**：出了这个类，程序里只有
//   MainMenu / SongSelect / Playing / Result 这些自己定义的概念。
//
// 实测字段形状（抓包 `osu插件探索\加载界面判断` 的 0057 → 0058 两包，逐字段对比过）：
//   加载中：state=2, gameMode=0, hp={normal:0,  smooth:0},      SR=0
//   打歌中：state=2, gameMode=3, hp={normal:200,smooth:200},    SR=2.99
//   （那几包里同时变化的还有 accuracy / leaderboard.isVisible / settings.showInterface，
//     但 hp 是最直接的那个：它就是"玩家还有没有血"。）
//
//   **★ hp 是个对象，不是数字** —— 这一点当初猜错了，直接导致了加载期误判，见 TosuJsonParser.ReadHp。
//
// 【★ 原来那个判据是错的（2026-09-20 用户实测发现）】
//   老代码用 `gameMode == 3` 判断"进了游戏" —— 依据就是上面那次采样里 gameMode 从 0 变成了 3。
//   可是 **gameMode 是"哪个游玩模式"**：0=osu、1=taiko、2=catch、3=mania。
//   那次采样**恰好打的是 mania**，而 mania 正好是第四个模式（=3），
//   于是"加载 0 → 打歌 3"看起来像个布尔值 —— **纯属巧合，被当成了事实。**
//
//   换成 std / taiko / catch，这个值从加载到打歌一直是 0 / 1 / 2，**永远不会等于 3** ——
//   结果是这三种模式打歌时角色根本不显示。用户还测到有些模式（自定义 ruleset）
//   的 gameMode 始终是 0，同样中招。
//
//   现在改成看 **hp**：一首歌开始的时候血条是满的，加载期间是 0。
//   这个判据**与模式无关** —— 四个官方模式和自定义 ruleset 都成立。
//
//   > 教训：**一个字段"在这个场景下恰好变了"，不等于"它是为这件事变的"。**
//   > 采样只测了一种模式，却得出了对所有模式生效的结论。
//   > 写判据前要先问"这个字段的定义是什么"，而不是"它这次变没变"。
//
// 所以「加载」和「打歌」是靠 menu.state + gameplay.hp 两个字段切开的，
// **不靠等时间**：加载时长随机器和谱面变，定时器只能猜，而血条是事实。
//
// 顺带一个结论：暂停落在"打歌"这一档里 —— 而这也正是想要的，
// 玩家按 Esc 时角色不该消失。暂停期间要不要隐藏角色属于配置，不归这个类管。
// ============================================================
namespace OsuLive2dOverlay;

/// <summary>角色现在处于哪个界面</summary>
public enum GameScene
{
    /// <summary>拿不到状态：osu 没开，或者 tosu 连不上</summary>
    Unknown,

    /// <summary>主菜单（osu 封面）—— tosu 报 0</summary>
    MainMenu,

    /// <summary>选歌界面 —— tosu 报 5</summary>
    SongSelect,

    /// <summary>打歌中（含暂停）—— tosu 报 2，且血条不为 0</summary>
    Playing,

    /// <summary>结算画面 —— tosu 报 7</summary>
    Result
}

public sealed class GameStateTracker
{
    private GameScene _confirmedScene = GameScene.Unknown;

    /// <summary>当前**已确认**的状态（不是 tosu 报的原始值）</summary>
    public GameScene Current => _confirmedScene;

    /// <summary>
    /// 这一包是不是"谱面还在加载"—— **osu 的 state 已经进了 2，但歌还没真正开始**。
    ///
    /// 【它为什么不是一种 <see cref="GameScene"/>】
    ///   加载期间**窗口不该动**：屏幕上那个窗口还停在上一个界面（通常是选歌）的位置，
    ///   而"加载"没有自己的站位可以套 —— 它不是一个界面，只是打歌前后的一段过渡。
    ///   所以场景照旧是选歌，另外用这个标志告诉界面"角色先藏起来"。
    ///
    ///   **这也正是当初那个 bug 的样子**：把加载也当成"打歌"，用户一进加载
    ///   就看见人物跳到打歌的位置去了。修法不是给加载编一套站位，而是**别动**。
    ///
    /// 【拿它做什么】
    ///   把角色透明度调成 0（见 <see cref="CharacterOpacity"/>）——
    ///   角色还在、位置没变、模型没卸载，歌一开始恢复成 1 就完了。
    /// </summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// 收到一包 tosu 状态就调一次。
    /// </summary>
    /// <param name="rawState">tosu 报的 menu.state；null = 没读到（osu 没开 / 连不上）</param>
    /// <param name="hp">tosu 报的 gameplay.hp（取里面的 normal）；null = 没读到</param>
    /// <returns>
    ///   **确认的场景真的变了** → 返回新场景
    ///   没变（含"还在加载"）→ 返回 null
    /// </returns>
    /// <remarks>
    /// ⚠ **"还在加载"期间这里返回 null，但 <see cref="IsLoading"/> 会翻面。**
    ///   所以调用方不能只看返回值：场景没变 ≠ 没事可做，
    ///   透明度还得跟着 IsLoading 走（加载→藏起来，歌开始→显回来）。
    /// </remarks>
    public GameScene? Update(int? rawState, double? hp)
    {
        // 先把"在不在加载"这个事实定下来：state 进了 2、但血条还没起来。
        // 它和"在哪个界面"是**两个独立的事实**，所以分开记 ——
        // 加载期间的场景是"上一个界面"，不是"加载界面"。
        IsLoading = rawState == 2 && !IsInGame(hp);

        // 认得出的界面 → 目标场景；认不出的（含 13 多人大厅）→ Unknown。
        // 白名单：只有明确认识的界面才让角色出现，宁可少显示也不乱显示。
        //
        // 注意 `2` 那一支返回的可能是 **null**（还在加载）—— 那是"保持现状"，
        // 不是"跳到某个场景"。它和下面那句"没变化返回 null"用的是同一个 null，
        // 但含义不同，所以那里的注释分开写了。
        var target = rawState switch
        {
            null => GameScene.Unknown,
            0 => GameScene.MainMenu,
            5 => GameScene.SongSelect,
            7 => GameScene.Result,
            2 => ResolvePlaying(hp),
            _ => GameScene.Unknown
        };

        if (target is null) return null;                       // 还在加载：场景一动不动
        if (_confirmedScene == target.Value) return null;      // 真的没变化：不重复通知

        _confirmedScene = target.Value;
        return target;
    }

    /// <summary>
    /// `state == 2` 里面再分两段 —— **这一支是整个类里唯一"有可能不切场景"的地方**：
    ///
    /// | 血条 | 含义 | 结果 |
    /// |---|---|---|
    /// | `0` | 谱面还在加载（实测约 2.5 秒） | **null** —— 场景保持不动（通常还是选歌） |
    /// | `> 0` | 歌真的开始了（暂停时它也是 > 0） | **Playing** |
    ///
    /// **为什么"加载"不做成第五个场景**：那它就得有一份自己的站位/窗口配置
    /// （四档变五档），而"加载"根本没有自己的位置概念。
    /// 用户要的是"这时候别动、把角色藏起来"，所以它是一个独立标志
    /// <see cref="IsLoading"/>，不是新场景。站位配置也就仍然是四份。
    ///
    /// 判据是**血条**，不是 `gameMode`：`gameMode` 是"哪个游玩模式"。
    /// 拿它当布尔值是当初误读了一局 mania（mania 恰好 = 3），
    /// 换 std / taiko / catch 就永远不成立。详见文件头那两段教训。
    /// </summary>
    private static GameScene? ResolvePlaying(double? hp)
        => IsInGame(hp) ? GameScene.Playing : null;

    /// <summary>
    /// 进了游戏没有 —— **血条不为 0 就算进了**（歌曲一开始血条就是满的，加载期间是 0）。
    ///
    /// ★ 拿不到 hp 时**不当作在游戏里** —— 这与本类的白名单原则一致：
    /// **宁可少显示，也不乱显示**（见下面 switch 表那条注释）。
    ///
    /// 这里原来是**反的**（缺 hp 就乐观当作在游戏里），理由是"宁可早 2.5 秒显示，
    /// 也不要整局不显示"。那个权衡本身没错，但它和一条更基本的原则冲突：
    /// **拿不准的时候不要假装知道**。代价实测过了 ——
    /// `hp` 的形状当时被猜错（它是个对象，不是数字），解析一路返回 null，
    /// 于是那句乐观兜底让**加载期间就判定成打歌**，角色提前跳到打歌的位置。
    /// 症状看起来像"判据还行"，其实信号根本没读到 —— 最难查的那一类。
    ///
    /// 真遇到"打歌时角色不显示"，第一件事是看 tosu 的 `hp` 形状是不是又变了
    /// （抓包工具在 `osu插件探索\tools\TosuPacketLogger`）。
    /// </summary>
    private static bool IsInGame(double? hp) => hp is > 0;
}
