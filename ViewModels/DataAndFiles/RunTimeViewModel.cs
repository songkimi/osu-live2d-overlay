// ============================================================
// RunTimeViewModel.cs —— 「数据与档案 → 运行期调整」页
//
// 【这一页管什么】
//   你在游戏里把悬浮窗拖到别处，那些改动先攒在内存里（不立刻写档案）。
//   这一页是那些改动的**唯一落脚点**：看得见、能存下来、也能丢掉。
//
//   没有它的话，唯一的保存机会就是"关掉悬浮窗时弹的那个询问框" ——
//   用户手一快点了「否」，这次调整就永远没了，而且没有任何补救的地方。
//
// 【两块内容，性质完全不同】
//   · 待保存的调整 —— **可操作**：保存 / 放弃。这是这一页存在的原因
//   · 已保存的位置 —— **只读参考**：告诉你"这个界面在档案里记的是哪儿"，
//     好判断"要不要把刚才拖的存下来"。
//     它不带编辑按钮是刻意的：那个字段的真正编辑入口在
//     「联动与窗口 → 界面感知」那一页，**一个字段只有一个编辑入口**
//     （两处都能改，就是又一次"改了 A 不生效"）。
//
// 【待保存层是全程序共用一份】（`App.WindowAdjustments`）
//   所以这一页和悬浮窗看见的是同一份数据。保存只要 `TakeAll` 再写档案就完了，
//   不需要去"通知"悬浮窗 —— 它俩本来就是同一份。
// ============================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

/// <summary>待保存的一条：某个界面被拖成了什么样</summary>
public sealed class PendingChangeRow
{
    public PendingChangeRow(GameScene scene, WindowBounds bounds)
    {
        SceneName = SceneProfiles.KeyOf(scene) ?? "未知界面";
        PositionText = $"左 {bounds.Left:0}，上 {bounds.Top:0}";
        SizeText = $"宽 {bounds.Width:0}，高 {bounds.Height:0}";
    }

    public string SceneName { get; }
    public string PositionText { get; }
    public string SizeText { get; }
}

/// <summary>
/// 已保存的一条：某个界面在档案里记的位置。
///
/// **逐字段显示**，不拼成一句"(x,y) w×h" —— 因为读取时就是逐字段的
/// （见 `WindowPlacementResolver`：X/Y/宽/高 各自独立，缺一个只补那一个）。
/// 拼成一整句的话，"只设了左和高"这种状态就显示不出来了，
/// 而那种状态恰恰是用户最容易困惑的那种。
/// </summary>
public sealed class SavedPlacementRow
{
    public SavedPlacementRow(GameScene scene, WindowPlacement placement)
    {
        SceneName = SceneProfiles.KeyOf(scene) ?? "未知界面";

        var set = new List<string>();
        if (placement.X is not null) set.Add($"左 {placement.X:0}");
        if (placement.Y is not null) set.Add($"上 {placement.Y:0}");
        if (placement.Width is > 0) set.Add($"宽 {placement.Width:0}");
        if (placement.Height is > 0) set.Add($"高 {placement.Height:0}");

        PlacementText = set.Count == 0 ? "未设 —— 用默认摆放" : string.Join("，", set);
    }

    public string SceneName { get; }
    public string PlacementText { get; }
}

public partial class RunTimeViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private readonly PendingWindowAdjustments _pending;

    /// <summary>界面的固定顺序（与 `PendingWindowAdjustments` 里那份一致：主菜单 → 选歌 → 打歌 → 结算）</summary>
    private static readonly GameScene[] AllScenes =
        { GameScene.MainMenu, GameScene.SongSelect, GameScene.Playing, GameScene.Result };

    public RunTimeViewModel(ConfigStore store, PendingWindowAdjustments pending)
    {
        _store = store;
        _pending = pending;

        Refresh();

        // 换了档案 = 每个界面"已保存的位置"整份变了，这一页得跟着重读
        _store.ConfigChanged += Refresh;
    }

    public ObservableCollection<PendingChangeRow> PendingRows { get; } = new();
    public ObservableCollection<SavedPlacementRow> SavedRows { get; } = new();

    /// <summary>有没有待保存的调整（底部两个按钮靠它启用/置灰）</summary>
    [ObservableProperty] private bool _hasPending;

    /// <summary>悬浮窗现在有没有在跑 —— 决定保存/放弃的后果，所以要如实显示</summary>
    [ObservableProperty] private bool _overlayRunning;

    /// <summary>待保存那一块顶上的一句话（说清楚"这些是从哪来的"）</summary>
    [ObservableProperty] private string _pendingSummary = "";

    public event Action<string>? Notify;

    // ------------------------------------------------------------

    /// <summary>
    /// 重新读一遍内存里的东西。
    /// **每次这一页露出来都要调**（见 RunTimeView 的 IsVisibleChanged）——
    /// 页面是随 Tab 容器一次性造好的，而"用户拖悬浮窗"随时可能发生，
    /// 不重读就会停在造页面那一刻的快照上。
    /// </summary>
    public void Refresh()
    {
        PendingRows.Clear();
        foreach (var scene in _pending.Scenes)
        {
            var bounds = _pending.Get(scene);
            if (bounds is not null) PendingRows.Add(new PendingChangeRow(scene, bounds.Value));
        }

        SavedRows.Clear();
        var scenes = _store.Config.Scenes;
        if (scenes is not null)
        {
            foreach (var scene in AllScenes)
            {
                var profile = scenes.Get(scene);
                if (profile is not null) SavedRows.Add(new SavedPlacementRow(scene, profile.Window));
            }
        }

        HasPending = PendingRows.Count > 0;
        OverlayRunning = App.Overlay is not null;
        PendingSummary = BuildPendingSummary();
    }

    private string BuildPendingSummary()
    {
        if (!HasPending)
        {
            return OverlayRunning
                ? "没有待保存的调整。现在去拖一下悬浮窗，这里就会出现。"
                : "没有待保存的调整。";
        }

        return OverlayRunning
            ? "下面这些是刚拖出来的，还没写进档案。"
            : "悬浮窗已经关了，这些是上一轮留下的、还没保存的改动。";
    }

    partial void OnHasPendingChanged(bool value)
    {
        SavePendingCommand.NotifyCanExecuteChanged();
        DiscardPendingCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasPending))]
    private void SavePending()
    {
        // 先取走再写：写失败时要能**放回去**，否则用户的拖动就白费了
        var changes = _pending.TakeAll();

        try
        {
            var written = ConfigFileWriter.WriteWindowBounds(_store.ConfigPath, changes);

            Refresh();
            Notify?.Invoke($"已保存 {written} 个界面的窗口位置");
        }
        catch (Exception ex)
        {
            foreach (var (scene, bounds) in changes)
                _pending.Record(scene, bounds.Left, bounds.Top, bounds.Width, bounds.Height);

            Refresh();
            Notify?.Invoke($"保存失败：{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPending))]
    private void DiscardPending()
    {
        // 悬浮窗在跑 → 让它当场把窗口摆回已保存的位置。
        // 少了这一步，用户点了「放弃」却看见窗口还待在刚拖的地方 —— 那看起来就是没生效。
        if (App.Overlay is { } overlay) overlay.DiscardPendingWindowChanges();
        else _pending.TakeAll();

        Refresh();
        Notify?.Invoke("已放弃这些调整");
    }
}
