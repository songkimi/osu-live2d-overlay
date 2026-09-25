// ============================================================
// OverlaySettingsViewModel.cs —— 「联动与窗口 → 悬浮窗」页
//
// 【这一页的结构】（定稿 §2④-1）
//   页内按**作用范围**分：全局区（一份） + 按界面区（四份，四个 Tab）。
//   一级按"这东西是什么"分（窗口 vs 窗口里的角色/内容），两个维度各管一件事。
//
// 【★ 全局区这一版**没有做**，而且是刻意的】
//   动这一页之前逐条查证过：全局区那六个字段**一个都没人读** ——
//   `OverlayWindow` 里吸附距离是写死的 const、位置锁定没人读、
//   三个热键字段压根没被读过（真实注册的是写死的一批，其中"临时交互"
//   代码里是 `Ctrl+Alt+T`，而配置写着 `Ctrl+Alt+E`）。
//   照文档把控件摆出来 = 一次性造六个"改了没反应"的东西，其中一个是
//   "照界面提示按键、按不出来"。所以先只做**按界面区**，全局区写明未做。
//   要接上得先做：热键字符串解析器 → 注册改读配置 → 吸附距离读配置 →
//   位置锁定 → 显示/隐藏热键的实现。详见《设置项总表》§3.1。
//
// 【预览看哪一份站位】
//   「打歌」有"按模式分站位"（决策 50）。如果预览永远只显示"场景站位"，
//   用户调 mania 那一份时预览纹丝不动 —— 那就成了"预览和实际不一致"。
//   所以给一个选择器：场景站位 / osu / taiko / catch / mania。**默认场景站位**。
// ============================================================
using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

public partial class OverlaySettingsViewModel : ObservableObject
{
    private readonly ConfigStore _store;

    /// <summary>四个界面，顺序固定：主菜单 → 选歌 → 打歌 → 结算</summary>
    private static readonly GameScene[] AllScenes =
        { GameScene.MainMenu, GameScene.SongSelect, GameScene.Playing, GameScene.Result };

    public OverlaySettingsViewModel(ConfigStore store)
    {
        _store = store;

        foreach (var scene in AllScenes)
        {
            var vm = new SceneSettingsViewModel(store, scene);

            // 动了某个模式的站位 → **把预览切到那个模式**。
            // 不切的话，用户改 mania 的数字、预览还显示"场景站位"，
            // 那就还是"改了看不到"，和"要保存才看得到"是同一种难受。
            vm.ModeEdited += mode => PreviewModeIndex = Array.IndexOf(GameModes.All, mode) + 1;

            Scenes.Add(vm);
        }

        _selected = Scenes[0];

        _store.DirtyChanged += () =>
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            ResetToDefaultCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<SceneSettingsViewModel> Scenes { get; } = new();

    /// <summary>当前在看的那个界面（Tab 选中项）</summary>
    [ObservableProperty] private SceneSettingsViewModel? _selected;

    /// <summary>
    /// 预览里看哪一份站位：`0` = 场景站位，`1..4` = 四个模式。
    /// 只有「打歌」那一档有这个选择器（别的界面没有模式站位）。
    /// </summary>
    [ObservableProperty] private int _previewModeIndex;

    public bool HasUnsavedChanges => _store.IsDirty;

    /// <summary>
    /// 现在要显示的配置 —— **预览区起页面时用它**。
    /// 换档案时 `ConfigStore.Config` 会换成新对象，预览区靠这一次引用比较
    /// 就知道"该重建了"（它自己不认识 ConfigStore，见 PreviewPane 的说明）。
    /// </summary>
    public PluginConfig Config => _store.Config;

    /// <summary>调试模式（预览区 init 报文里那个 debug 开关）</summary>
    public bool DebugMode => _store.Settings.DebugMode;

    /// <summary>打歌那一档（"填给还没配置的界面"要问它；也用来决定要不要露模式选择器）</summary>
    public SceneSettingsViewModel? PlayingScene => Scenes.FirstOrDefault(s => s.IsPlaying);

    /// <summary>模式选择器的选项（第一项是"场景站位"）</summary>
    public IReadOnlyList<string> PreviewModeOptions { get; } =
        new[] { "场景站位" }.Concat(GameModes.All.Select(GameModes.KeyOf)).ToList();

    public event Action<string>? Notify;

    // ------------------------------------------------------------

    partial void OnSelectedChanged(SceneSettingsViewModel? value)
    {
        // 换界面 = 换一份站位，预览里那个"看哪一份模式"的选择器要回到"场景站位"
        // （不然从打歌切到选歌时，选择器还停在一个这一档根本不存在的模式上）
        PreviewModeIndex = 0;

        OnPropertyChanged(nameof(PlayingScene));
    }

    /// <summary>
    /// 预览现在该显示的那份站位。
    /// 选择器指向某个模式、而那一档真有那份站位时才用它；否则用场景站位 ——
    /// **不会显示一份实际不生效的东西**。
    /// </summary>
    public CharacterPlacement PreviewPlacement
    {
        get
        {
            var scene = Selected;
            if (scene is null) return new CharacterPlacement();

            var mode = PreviewModeIndex >= 1 && PreviewModeIndex <= GameModes.All.Length
                ? GameModes.All[PreviewModeIndex - 1]
                : (GameMode?)null;

            // 走的是**和悬浮窗同一个**取值方法（SceneProfile.PlacementFor）——
            // 预览要是自己另算一套，"看到的"和"跑到"的迟早对不上
            var profile = _store.Config.Scenes?.Get(scene.Scene);
            return profile?.PlacementFor(mode) ?? new CharacterPlacement();
        }
    }

    // ------------------------------------------------------------
    // 全局区（★ 2026-09-23 补上界面）
    //
    // 这几项存在 **settings.json**（软件设置）里，**不随档案走** ——
    // 所以它们和上面四个档位不是一回事：档位是"这个界面怎么显示"（随角色），
    // 这里是"悬浮窗这个程序本身怎么行为"（全局）。
    //
    // 【热键只读、不给改】（用户 2026-09-23："不要让用户去改，实现起来坑有点多"）
    //   要真做"录制"，得处理：冲突检测、被占用时的提示、录制期间的热键屏蔽、
    //   非法组合的拒绝……坑一个接一个。现在的做法是**如实显示当前注册的是什么**，
    //   想换就在 settings.json 里改（这属于高级用户兜底，README 会写）。
    // ------------------------------------------------------------

    /// <summary>边缘吸附距离（DIP）。0 或负数 = 不吸附。</summary>
    public double SnapDistance
    {
        get => _store.Settings.Overlay.SnapDistance;
        set
        {
            if (Math.Abs(value - SnapDistance) < 0.01) return;
            _store.Settings.Overlay.SnapDistance = Math.Max(0, Math.Round(value, 0));
            MarkSettings();
        }
    }

    /// <summary>位置锁定：锁上之后拖不动窗口（拖动那一处会忽略）</summary>
    public bool LockPosition
    {
        get => _store.Settings.Overlay.LockPosition;
        set
        {
            if (value == LockPosition) return;
            _store.Settings.Overlay.LockPosition = value;
            MarkSettings();
        }
    }

    /// <summary>结束悬浮窗的热键（**只读展示**，实际注册时读的就是它）</summary>
    public string StopHotkey => _store.Settings.Overlay.StopHotkey;

    /// <summary>临时允许交互的热键（同上）</summary>
    public string TempInteractiveHotkey => _store.Settings.Overlay.TempInteractiveHotkey;

    /// <summary>把全局区从设置里重新读一遍（放弃改动时用）</summary>
    private void LoadGlobal()
    {
        OnPropertyChanged(nameof(SnapDistance));
        OnPropertyChanged(nameof(LockPosition));
        OnPropertyChanged(nameof(StopHotkey));
        OnPropertyChanged(nameof(TempInteractiveHotkey));
    }

    /// <summary>全局区改动共用的收尾（这几项在设置里，所以标的是"设置脏"）</summary>
    private void MarkSettings()
    {
        _store.MarkSettingsDirty();
        LoadGlobal();
    }
    // ------------------------------------------------------------
    // 保存 / 放弃 / 恢复默认
    // ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Save()
    {
        foreach (var scene in Scenes) scene.WriteTo();

        try
        {
            _store.SaveConfig();

            // ★ 2026-09-23：全局区那几项存在 **settings.json**（软件设置）里，
            //   不是档案里 —— 所以这里必须再存一次设置。
            //   少这一句的表现是"全局区改了、按保存、重开又变回去"，而且日志里什么都没有。
            _store.SaveSettings();

            Notify?.Invoke("已保存（窗口尺寸 / 穿透 / 站位要重启悬浮窗才生效）");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"保存失败：{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Discard()
    {
        _store.ReloadConfig();
        _store.ReloadSettings();
        foreach (var scene in Scenes) scene.Load();
        LoadGlobal();

        Notify?.Invoke("已放弃改动");
    }

    /// <summary>
    /// 恢复默认 = 四档都回到"没配过"：不启用、尺寸留空（用程序默认摆放）、
    /// 站位回到 0/0/1/0、模式站位清掉 —— 与 `new PluginConfig()` 一致。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void ResetToDefault()
    {
        var scenes = _store.Config.Scenes;
        if (scenes is null) return;

        var defaults = new SceneProfiles();

        foreach (var scene in AllScenes)
        {
            var target = scenes.Get(scene);
            var source = defaults.Get(scene);
            if (target is null || source is null) continue;

            target.CopyFrom(source);
            target.ByMode = null;
        }

        foreach (var scene in Scenes) scene.Load();
        _store.MarkConfigDirty();

        Notify?.Invoke("已恢复默认（还要点保存才会生效）");
    }

    // ------------------------------------------------------------

    /// <summary>把当前这一档填给还没配置的界面（那个"一键同步"按钮）</summary>
    [RelayCommand]
    private void ApplyToOthers()
    {
        var scene = Selected;
        if (scene is null) return;

        scene.ApplyToUnconfigured();

        // 别人被改了，界面上那几份要重读
        foreach (var other in Scenes) other.Load();

        _store.MarkConfigDirty();
        Notify?.Invoke($"已把「{scene.Title}」的设置填给还没配置的界面");
    }
}
