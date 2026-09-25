// ============================================================
// SceneSettingsViewModel.cs —— 「悬浮窗 → 按界面区」里**一个界面**的设置
//
// 【四份为什么是同一个类】
//   主菜单 / 选歌 / 打歌 / 结算 的设置项结构完全一样。
//   在 XAML 里把同样的九个控件抄四遍，下场是"改一个界面漏三个" ——
//   所以做成一类 × 四实例，XAML 里只有一份模板。
//
// 【只有「打歌」多一块】按游玩模式分别设置站位（定稿决策 50）。
//   主菜单 / 选歌 / 结算的布局不随模式变，给它们也露出来只会让人以为它们也要设。
//
// 【尺寸上限】（§8.8 第 4 条"超大的悬浮窗要有拦阻"）
//   数字框的上限 = 显示器工作区。这里从 `SystemParameters` 取 ——
//   ViewModel 在 WPF 工程里，用 WPF 的 API 是允许的（只有 `Logic/` `Config/`
//   那一层不许引 WPF，见《项目结构约定》§二）。
//   "超没超"这个**判断**仍然交给 `PreviewScale`（那一份能进控制台测）。
// ============================================================
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OsuLive2dOverlay;

/// <summary>「模式站位」里的一行：某个游玩模式要不要单独设一套站位</summary>
public partial class ModePlacementRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _loading;

    public ModePlacementRowViewModel(GameMode mode, Action onChanged)
    {
        Mode = mode;
        _onChanged = onChanged;
        Title = GameModes.KeyOf(mode);
    }

    public GameMode Mode { get; }

    /// <summary>配置里的键名（osu / taiko / catch / mania）—— 界面上直接显示它</summary>
    public string Title { get; }

    /// <summary>勾上 = 这个模式用自己那一套；不勾 = 用上面那份（场景站位）</summary>
    [ObservableProperty] private bool _enabled;

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private double _angle;

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        _onChanged();
    }

    partial void OnXChanged(double value) => Changed();
    partial void OnYChanged(double value) => Changed();
    partial void OnScaleChanged(double value) => Changed();
    partial void OnAngleChanged(double value) => Changed();

    private void Changed()
    {
        if (_loading) return;
        _onChanged();
    }

    /// <summary>从配置读这一行（没有那一份 = 没单独设过 → 勾选框空着）</summary>
    public void Load(ModePlacements? byMode)
    {
        _loading = true;
        try
        {
            var slot = byMode?.Get(Mode);
            Enabled = slot is not null;

            // 勾选框取消过之后，数值用场景那一份做初值 —— 勾上时不至于从 0 开始
            X = slot?.X ?? 0;
            Y = slot?.Y ?? 0;
            Scale = slot?.Scale ?? 1.0;
            Angle = slot?.AngleDegrees ?? 0;
        }
        finally { _loading = false; }
    }

    /// <summary>把这一行写回配置：勾上 → 建一份并填值；不勾 → 清掉（回到场景那份）</summary>
    public void WriteTo(ModePlacements target)
    {
        if (!Enabled)
        {
            target.Clear(Mode);
            return;
        }

        var slot = target.Ensure(Mode);
        slot.X = X;
        slot.Y = Y;
        slot.Scale = Scale;
        slot.AngleDegrees = Angle;
    }
}

/// <summary>一个界面的设置（主菜单 / 选歌 / 打歌 / 结算各一份实例）</summary>
public partial class SceneSettingsViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private bool _loading;

    public SceneSettingsViewModel(ConfigStore store, GameScene scene)
    {
        _store = store;
        Scene = scene;

        Title = SceneProfiles.KeyOf(scene) ?? scene.ToString();
        IsPlaying = scene == GameScene.Playing;

        if (IsPlaying)
        {
            foreach (var mode in GameModes.All)
                Modes.Add(new ModePlacementRowViewModel(mode, () => OnModeRowChanged(mode)));
        }

        // 尺寸上限 = 显示器工作区（扣掉任务栏）。拿不到就退回一个保守值，
        // 免得 Maximum 变成 0 让数字框完全不能动。
        var area = SystemParameters.WorkArea;
        MaxWindowWidth = area.Width > 100 ? Math.Floor(area.Width) : 1920;
        MaxWindowHeight = area.Height > 100 ? Math.Floor(area.Height) : 1080;

        Load();
    }

    public GameScene Scene { get; }

    /// <summary>配置里的键名，也就是 Tab 标题（主菜单 / 选歌 / 打歌 / 结算）</summary>
    public string Title { get; }

    /// <summary>是不是「打歌」—— 只有它多一块"按模式分站位"</summary>
    public bool IsPlaying { get; }

    public double MaxWindowWidth { get; }
    public double MaxWindowHeight { get; }

    // ---- 窗口 ----

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double _windowWidth;
    [ObservableProperty] private double _windowHeight;
    [ObservableProperty] private bool _clickThrough;
    [ObservableProperty] private bool _snapToEdges;

    // ---- 角色站位 ----

    [ObservableProperty] private double _characterX;
    [ObservableProperty] private double _characterY;
    [ObservableProperty] private double _characterScale = 1.0;
    [ObservableProperty] private double _characterAngle;

    /// <summary>允许触摸（P2 留位）—— 界面**置灰**，只显示不编辑</summary>
    public bool TouchEnabled => Loaded().TouchEnabled;

    /// <summary>打歌那一档的"按模式分站位"四行；别的界面是空的</summary>
    public ObservableCollection<ModePlacementRowViewModel> Modes { get; } = new();

    /// <summary>
    /// 某一行的模式站位被动了（勾选框或数值）。
    /// 页面据此**把预览切到那个模式** —— 否则用户改 mania 的数字、
    /// 预览却还显示"场景站位"，那就还是"改了看不到"。
    /// </summary>
    public event Action<GameMode>? ModeEdited;

    private void OnModeRowChanged(GameMode mode)
    {
        MarkDirty();
        ModeEdited?.Invoke(mode);
    }

    /// <summary>尺寸超过工作区时的提示（§8.8 第 4 条）；空 = 没问题</summary>
    public string SizeWarning =>
        PreviewScale.Exceeds(WindowWidth, WindowHeight, MaxWindowWidth, MaxWindowHeight)
            ? $"这个尺寸超过了显示器工作区（{MaxWindowWidth:0}×{MaxWindowHeight:0}）。" +
              "想让人物更大，改「角色站位 → 缩放」比拉窗口有效。"
            : "";

    public bool HasSizeWarning => SizeWarning.Length > 0;

    /// <summary>
    /// 站位那一块的标题。
    ///
    /// **「打歌」那一档有两层站位**（默认 + 四个模式的覆盖），所以要把上面那份
    /// 明确标成"默认"
    /// </summary>
    public string PlacementTitle => IsPlaying ? "站位（默认）" : "角色站位";

    /// <summary>站位那一块的说明（打歌那一档要多交代一句"默认"是相对谁而言）</summary>
    public string PlacementHint => IsPlaying
        ? "左右/上下是相对画布的比例 （0 = 居中 / 底部贴齐）" +
          "没在下面单独设过的模式，用的就是这一份。"
        : "左右/上下是相对画布的比例（0 = 居中 / 底部贴齐）";

    // ------------------------------------------------------------

    private SceneProfile Loaded()
    {
        var scenes = _store.Config.Scenes;
        var profile = scenes?.Get(Scene);
        return profile ?? new SceneProfile();
    }

    /// <summary>从配置读一遍（页面的"放弃"与初次加载都走它）</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var profile = Loaded();

            Enabled = profile.Enabled;
            WindowWidth = profile.Window.Width ?? 0;      // null = 没设过 → 界面上显示 0
            WindowHeight = profile.Window.Height ?? 0;
            ClickThrough = profile.Window.ClickThrough;
            SnapToEdges = profile.Window.SnapToEdges;

            CharacterX = profile.Character.X;
            CharacterY = profile.Character.Y;
            CharacterScale = profile.Character.Scale;
            CharacterAngle = profile.Character.AngleDegrees;

            foreach (var row in Modes) row.Load(profile.ByMode);
        }
        finally { _loading = false; }

        // IsPlaying 那一档的初值是从配置里现读的，"允许触摸"要跟着刷新
        OnPropertyChanged(nameof(TouchEnabled));
        OnPropertyChanged(nameof(SizeWarning));
        OnPropertyChanged(nameof(HasSizeWarning));
    }

    /// <summary>把界面上的值写回配置对象（**不落盘** —— 落盘由页面统一做）</summary>
    public void WriteTo()
    {
        var profile = Loaded();

        profile.Enabled = Enabled;

        // 0 = "没设过" → 存 null（读取时才会退回"程序默认摆放"）。
        // 存 0 的话窗口会变成 0 宽，那是另一回事（看都看不见）。
        profile.Window.Width = WindowWidth > 0 ? WindowWidth : null;
        profile.Window.Height = WindowHeight > 0 ? WindowHeight : null;
        profile.Window.ClickThrough = ClickThrough;
        profile.Window.SnapToEdges = SnapToEdges;

        profile.Character.X = CharacterX;
        profile.Character.Y = CharacterY;
        profile.Character.Scale = CharacterScale;
        profile.Character.AngleDegrees = CharacterAngle;

        if (IsPlaying)
        {
            // 真要写才有 ByMode，全都没勾时**整个清掉** ——
            // 留一个"四项都是 null"的空对象进配置，只会让文件里多一段看不懂的东西
            var any = Modes.Any(m => m.Enabled);
            if (any)
            {
                profile.ByMode ??= new ModePlacements();
                foreach (var row in Modes) row.WriteTo(profile.ByMode);
            }
            else
            {
                profile.ByMode = null;
            }
        }
    }

    /// <summary>把这一档的内容填给还没配置的界面（文档里那个"一键同步"按钮）</summary>
    public void ApplyToUnconfigured()
        => _store.Config.Scenes?.ApplyToUnconfigured(Scene);

    partial void OnEnabledChanged(bool value) => MarkDirty();

    partial void OnWindowWidthChanged(double value) => SizeChanged();
    partial void OnWindowHeightChanged(double value) => SizeChanged();

    partial void OnClickThroughChanged(bool value) => MarkDirty();
    partial void OnSnapToEdgesChanged(bool value) => MarkDirty();
    partial void OnCharacterXChanged(double value) => MarkDirty();
    partial void OnCharacterYChanged(double value) => MarkDirty();
    partial void OnCharacterScaleChanged(double value) => MarkDirty();
    partial void OnCharacterAngleChanged(double value) => MarkDirty();

    private void SizeChanged()
    {
        OnPropertyChanged(nameof(SizeWarning));
        OnPropertyChanged(nameof(HasSizeWarning));
        MarkDirty();
    }

    /// <summary>
    /// 界面上的值变了（`_loading` 期间不算 —— 那是"从配置灌值"）。
    ///
    /// **★ 这里要顺手把值写回配置对象**（只写内存，落盘仍然靠"保存"）。
    /// 为什么不像别的页那样"改了只存 VM、保存时才写回"：
    ///   这一页右边有个**预览区**，而预览取站位走的是 `SceneProfile.PlacementFor`
    ///   —— 和悬浮窗**同一个方法**，读的是**配置对象**。
    ///   只改 VM 的话，拖滑块时配置没动 → 预览纹丝不动
    ///   （用户 2026-09-20 实测到的"人物没有跟着动"就是它）。
    ///   边改边写还保住了"取值规则只有一份"：预览不另算一套从 VM 取站位的逻辑
    ///   （那迟早和实际跑到的那份对不上）。
    /// </summary>
    private void MarkDirty()
    {
        if (_loading) return;

        WriteTo();
        _store.MarkConfigDirty();
    }
}
