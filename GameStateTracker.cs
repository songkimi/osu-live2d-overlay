// ============================================================
// GameStateTracker.cs —— 把 tosu 报的原始状态翻译成"角色现在在哪个界面"
//
// 为什么单独一个类：
//   和 ComboTracker / TriggerResolver 一个道理 —— 这段"查表、判状态"的逻辑
//   不依赖 WPF 也不依赖网络，搬出来之后**可以用控制台测试**，
//   而且**外部的易变值被挡在这一层里面**：出了这个类，程序里只有
//   MainMenu / SongSelect / Playing / Result 这些自己定义的概念。
//
// 实测依据（.build-verify 连续采样一整局得到的）：
//   按下开始 → state=2, gameMode=0, hp=0     ← 加载中，持续约 2.5 秒
//   2.5 秒后 → state=2, gameMode=3, hp=200   ← 游戏真的开始了
//   暂停中   → state=2, gameMode=3, bm 停住  ← gameMode 仍是 3
//
// 所以「加载」和「打歌」是靠 menu.state + gameplay.gameMode 两个字段切开的，
// **不靠等时间**：加载时长随机器和谱面变，定时器只能猜，而 gameMode 是事实。
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

    /// <summary>打歌中（含暂停）—— tosu 报 2 且 gameplay.gameMode 为 3</summary>
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
    /// 收到一包 tosu 状态就调一次。
    /// </summary>
    /// <param name="rawState">tosu 报的 menu.state；null = 没读到（osu 没开 / 连不上）</param>
    /// <param name="inGame">歌曲是不是真的开始了 —— 由调用方传 gameplay.gameMode == 3</param>
    /// <returns>
    ///   **确认的状态真的变了** → 返回新状态
    ///   没变（含"还在加载"）→ 返回 null，调用方什么都不用做
    /// </returns>
    public GameScene? Update(int? rawState, bool inGame)
    {
        // 认得出的界面 → 目标状态；认不出的（含 13 多人大厅）→ Unknown。
        // 白名单：只有明确认识的界面才让角色出现，宁可少显示也不乱显示。
        //
        // 2 要分两种：inGame=false 是还在加载（什么都不做），
        //             inGame=true  才是真的在打。
        var target = rawState switch
        {
            null => GameScene.Unknown,
            0 => GameScene.MainMenu,
            5 => GameScene.SongSelect,
            7 => GameScene.Result,
            2 => inGame ? GameScene.Playing : (GameScene?)null,
            _ => GameScene.Unknown
        };

        if (target is null) return null;                       // 加载中：保持现状，不通知
        if (_confirmedScene == target.Value) return null;      // 没变化：不重复通知

        _confirmedScene = target.Value;
        return target;
    }
}
