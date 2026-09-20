// ============================================================
// PendingWindowAdjustments.cs —— 攒着"运行期拖出来的窗口状态"，等用户确认才写盘
//
// 为什么要有这一层（定稿 §3.6.2）：
//   悬浮窗被拖到新位置之后**不能立刻写配置文件**。两个原因：
//     ① 用户常常只是"临时挪开一下"，自动保存会把配置弄脏；
//     ② 拖动是连续动作，每帧写盘既抖磁盘、又可能写坏文件。
//   所以先在内存里攒着，等退出程序时问一次"要不要保存"。
//
// 为什么单独一个类：
//   它和 ComboTracker / GameStateTracker 一个道理 —— 这段逻辑不依赖 WPF、不依赖文件，
//   所以能搬出来用控制台测试，顺便给 OverlayWindow 那个上千行的文件瘦身。
//
// 职责边界：它只管"攒"（谁改了、改成什么、有没有待保存的），
//   **不碰文件、也不知道配置文件长什么样** —— 那是 ConfigFileWriter 的事。
// ============================================================
namespace OsuLive2dOverlay;

/// <summary>窗口在屏幕上的位置与尺寸（运行期拖出来的那一份）</summary>
/// <param name="Left">屏幕 X（DIP）</param>
/// <param name="Top">屏幕 Y（DIP）</param>
/// <param name="Width">窗口宽</param>
/// <param name="Height">窗口高</param>
public readonly record struct WindowBounds(double Left, double Top, double Width, double Height);

public sealed class PendingWindowAdjustments
{
    /// <summary>
    /// 提示文案里的界面顺序固定成"主菜单 → 选歌 → 打歌 → 结算"。
    /// 不能拿字典的遍历顺序去拼文案 —— 那玩意儿不保证是插入顺序，
    /// 同一件事的提示语会一会儿一个样。
    /// </summary>
    private static readonly GameScene[] DisplayOrder =
        { GameScene.MainMenu, GameScene.SongSelect, GameScene.Playing, GameScene.Result };

    private readonly Dictionary<GameScene, WindowBounds> _changes = new();

    /// <summary>有没有待保存的调整</summary>
    public bool Any => _changes.Count > 0;

    /// <summary>
    /// 某个界面这一轮拖出来的窗口状态（没拖过就是 null）。
    ///
    /// 它是"这个界面现在该摆在哪"的**第一层**来源（见 WindowPlacementResolver）：
    /// 用户刚拖完、还没落盘，这一轮里窗口就得待在他拖的地方 —— 哪怕中途切去别的界面再切回来。
    /// 少了这一层，切回来会跳回配置文件里的旧位置，用户会以为"我拖的那下没生效"。
    /// </summary>
    public WindowBounds? Get(GameScene scene)
        => _changes.TryGetValue(scene, out var bounds) ? bounds : null;

    /// <summary>涉及哪些界面（顺序固定：主菜单 → 选歌 → 打歌 → 结算）</summary>
    public IReadOnlyList<GameScene> Scenes =>
        DisplayOrder.Where(_changes.ContainsKey).ToList();

    /// <summary>记下某个界面拖出来的窗口状态（同一界面重复记 → 覆盖成最新的那次）</summary>
    public void Record(GameScene scene, double left, double top, double width, double height)
    {
        // 用 KeyOf 判断而不是 Enum.IsDefined：前者是"这个场景在配置里有没有位置"的唯一出处。
        // 两者现在结果一样（四档刚好一一对应），但将来要是加了 GameScene.Lobby 这种
        // "枚举里有、配置里没有"的档位，IsDefined 会放它过去、KeyOf 会挡住。
        if (SceneProfiles.KeyOf(scene) is null) return;

        _changes[scene] = new WindowBounds(left, top, width, height);
    }

    /// <summary>
    /// 取走全部调整并清空。返回的是副本 —— "取走"的语义就是"我这份清空、你拿走去用"，
    /// 交内部那个字典本身出去的话，外面一改就串味了。
    /// </summary>
    public IReadOnlyDictionary<GameScene, WindowBounds> TakeAll()
    {
        var snapshot = new Dictionary<GameScene, WindowBounds>(_changes);
        _changes.Clear();
        return snapshot;
    }
}
