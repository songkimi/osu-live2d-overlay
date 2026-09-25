// ============================================================
// GeneralViewModel.cs —— 「应用 → 通用」页的 ViewModel

//   那"改配置 / 看代码 / 看界面三处同一个词"怎么办？靠**每层都有明确映射**：
//
//     config.json  「外观」  ↔  GeneralConfig.Appearance  ↔  本类的 Appearance  ↔  {Binding Appearance}
//        用户自查                 [JsonPropertyName] 映射           （给编译器）        （给绑定）
//
//   → **属性名与配置类的 C# 属性名保持一致**，所以"界面上的值是配置里的哪个字段"
//     永远是同名对应，不需要额外的对照表。
//
// 【它不认识任何控件】（《项目结构约定》§四 第 2 条）
//   不引 MessageBox / Window / Toast，要提示就发 <see cref="Notify"/> 事件让 View 去显示。
// ============================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

public partial class GeneralViewModel : ObservableObject
{
    private readonly ConfigStore _store;

    /// <summary>正在从配置批量读值——这期间不要标脏（构造和"放弃"都会走这条路）</summary>
    private bool _loading;

    // ------------------------------------------------------------
    // 八个配置字段。
    // **名字与 GeneralConfig 的属性名一一对应**（配置类那边再用 [JsonPropertyName] 映射中文键名）。
    // ------------------------------------------------------------

    [ObservableProperty] private string _appearance = "跟随系统";
    [ObservableProperty] private string _language = "跟随系统";
    [ObservableProperty] private string _font = "跟随系统";
    [ObservableProperty] private double _fontSizePoints = 10.5;
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private bool _trayIcon = true;
    [ObservableProperty] private bool _minimizeToTrayOnClose = true;
    [ObservableProperty] private bool _autoSavePlacement;

    public GeneralViewModel(ConfigStore store)
    {
        _store = store;
        ReloadFromConfig();

        // 未保存标记一变就通知界面。
        _store.DirtyChanged += () =>
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(SaveStateText));

            // CanExecute 不会因为属性变化而自动重新求值 —— 必须手动喊一声，
            // 否则按钮的灰/亮状态一直停在最初那一次的结果上。
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            ResetToDefaultCommand.NotifyCanExecuteChanged();
        };
    }


    public IReadOnlyList<string> AppearanceOptions { get; } = new[] { "跟随系统", "浅色", "深色" };

    public IReadOnlyList<string> LanguageOptions { get; } = new[] { "跟随系统", "中文", "English" };

    /// <summary>
    /// 字体选项 = 「跟随系统」+ 本机所有字体族。
    ///
    /// 列表偏长（几百个）是正常的，ComboBox 会虚拟化。
    /// **注意别让用户选到没有中文字形的字体**（`Segoe UI` / `Tahoma` / `Arial` 都没有）——
    /// 目前靠"默认是跟随系统 + 中文兜底"来兜住；真要拦，等做字体页时再加检查
    /// </summary>
    public IReadOnlyList<string> FontOptions { get; } = BuildFontChoices();

    public double MinFontSizePoints => FontScale.MinBodyPoints;
    public double MaxFontSizePoints => FontScale.MaxBodyPoints;

    /// <summary>有未保存的改动（底部保存栏用它决定"保存/放弃"能不能点）</summary>
    public bool HasUnsavedChanges => _store.IsDirty;

    /// <summary>
    /// 给界面直接显示的一句话。
    /// 做成字符串而不是 bool + 值转换器 —— 少一个转换器、少一处能写错的地方。
    /// </summary>
    public string SaveStateText => _store.IsDirty ? "● 未保存" : "没有未保存的改动";

    /// <summary>
    /// 配置文件在哪
    /// </summary>
    public string ConfigFilePath => _store.SettingsPath;

    /// <summary>一句话提示（成功/失败）。View 收到后弹 toast —— VM 自己不弹。</summary>
    public event Action<string>? Notify;

    // ------------------------------------------------------------
    // 改动追踪：任何**配置字段**变了就标脏
    //
    // 用重写 OnPropertyChanged 而不是给八个属性各写一个 partial 方法 ——
    // 少写七处、也就少七处"以后加了字段忘了标脏"的机会。
    //
    // 但派生属性必须排除掉，否则会形成**自我标脏的死循环**：
    //     点保存 → Save() 清脏 → 触发 DirtyChanged → 这里通知 SaveStateText 变了
    //     → OnPropertyChanged 又跑一遍 → 又 MarkSettingsDirty() → 又变脏
    // ------------------------------------------------------------
    private static readonly string[] DerivedProperties =
    [
        nameof(HasUnsavedChanges),
        nameof(SaveStateText),
        nameof(ConfigFilePath),
        nameof(MinFontSizePoints),
        nameof(MaxFontSizePoints),
        nameof(AppearanceOptions),
        nameof(LanguageOptions),
        nameof(FontOptions),
        nameof(IsTrayIconOff),
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_loading) return;
        if (e.PropertyName is { } name && DerivedProperties.Contains(name)) return;

        _store.MarkSettingsDirty();
    }

    // ------------------------------------------------------------
    // 三个"即时生效"的项：改完立刻应用到资源字典（定稿 §1.1 的"即时"类）
    //
    // 只有它们需要额外动作，其余字段只是"存起来等对应功能来读"。
    // 每一项都传齐三个参数 —— ThemeApplier 是一次性应用整组的。
    // ------------------------------------------------------------
    partial void OnAppearanceChanged(string value)
    {
        if (_loading) return;
        ThemeApplier.Apply(value, Font, FontSizePoints);
    }

    partial void OnFontChanged(string value)
    {
        if (_loading) return;
        ThemeApplier.Apply(Appearance, value, FontSizePoints);
    }

    partial void OnFontSizePointsChanged(double value)
    {
        if (_loading) return;
        ThemeApplier.Apply(Appearance, Font, value);
    }

    // ------------------------------------------------------------
    // 「启动与托盘」
    // ------------------------------------------------------------

    /// <summary>
    /// 托盘图标的显示状态要变了 —— 由 View 转发给主窗口去落实。
    ///
    /// 为什么用事件而不是在 VM 里直接调：托盘是**窗口层**的东西
    /// （HandyControl 的 `NotifyIcon`），而 VM 不许认识控件（《项目结构约定》§四 第 2 条）。
    /// </summary>
    public event Action<bool>? TrayIconVisibilityChanged;

    /// <summary>
    /// 托盘图标 + 与「关闭时最小化到托盘」的**强制联动**。
    ///
    /// 【为什么必须联动】托盘图标关着、"关闭时最小化到托盘"开着的话：
    /// 点 X 会把窗口藏起来，而**没有任何入口能把它叫回来** ——
    /// 托盘里没有图标、任务栏里没有窗口、进程还在后台跑。
    /// 用户唯一的出路是任务管理器。所以关掉托盘图标时，"最小化到托盘"必须跟着关。
    /// </summary>
    partial void OnTrayIconChanged(bool value)
    {
        if (!value && MinimizeToTrayOnClose) MinimizeToTrayOnClose = false;

        // 那一行"先打开托盘图标"的小字跟着显示/隐藏
        OnPropertyChanged(nameof(IsTrayIconOff));

        // 不受 _loading 影响：**"放弃"要靠它把图标恢复成配置里的样子**
        TrayIconVisibilityChanged?.Invoke(value);
    }

    /// <summary>
    /// 托盘图标是不是关着。
    /// 只给界面用（决定"需要先打开上面的托盘图标"那句提示显不显示）——
    /// 做成一个属性而不是写个取反转换器：少一个类、少一处能写错的地方。
    /// </summary>
    public bool IsTrayIconOff => !TrayIcon;

    /// <summary>
    /// 反过来：想勾"关闭时最小化到托盘"就顺手把托盘图标打开 ——
    /// 否则那个开关根本没法工作（上面那条联动会立刻把它按回去）。
    /// 两个方向都有，用户在界面上就不会撞进"勾了又跳回去"的死角。
    /// </summary>
    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        if (value && !TrayIcon) TrayIcon = true;
    }

    /// <summary>开机自启：勾上/取消都**立刻写注册表**（写是即时的，只是下次登录才体现）</summary>
    partial void OnAutoStartChanged(bool value)
    {
        if (_loading) return;      // 读配置那一路不要去动系统状态（由 SyncAutoStart 统一对齐）
        ApplyAutoStart(value);
    }

    /// <summary>
    /// 把开机自启登记写进/移出注册表，并把结果说清楚。
    /// **失败必须报**：被安全软件或组策略拦住时，用户只会看到"勾了，但下次开机没反应"。
    /// </summary>
    private void ApplyAutoStart(bool enabled)
    {
        var ok = enabled
            ? AutoStartRegistration.Enable(AutoStartRegistration.CurrentExePath)
            : AutoStartRegistration.Disable();

        DebugLog.Write(enabled
            ? (ok ? "开机自启：已登记" : $"开机自启：登记失败 —— {AutoStartRegistration.LastError}")
            : (ok ? "开机自启：已取消登记" : $"开机自启：取消登记失败 —— {AutoStartRegistration.LastError}"));

        if (!ok)
            Notify?.Invoke(enabled ? "登记开机自启失败" : "取消开机自启失败");
    }

    /// <summary>
    /// 把**系统里的登记**对齐到配置值（不一致才动手，一致时一个字都不改）。
    ///
    /// 两个调用时机，都是为了同一个坑：
    ///   ① 页面构造 / 用户点「放弃」→ 界面读回配置之后，系统状态也得跟着回退。
    ///      不同步的话会出现"界面上没勾、注册表里却还登记着"——下次开机它照样自己起来。
    ///   ② 程序换了目录 → 登记里那条命令指着旧路径，自启会**悄悄失效**。
    /// </summary>
    private void SyncAutoStart()
    {
        var want = AutoStart;

        // 不只是"有没有登记"，还要"登记的是不是这个 exe"
        var alreadyAligned = AutoStartRegistration.IsEnabledFor(AutoStartRegistration.CurrentExePath);

        if (want == alreadyAligned) return;

        ApplyAutoStart(want);
    }

    // ------------------------------------------------------------
    // 命令
    //
    // 三个都挂 CanExecute = HasUnsavedChanges —— 没有改动时按钮自己就是灰的，
    // 不用在 XAML 里再绑一次 IsEnabled
    // ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Save()
    {
        var general = _store.Settings.General;

        general.Appearance = Appearance;
        general.Language = Language;
        general.Font = Font;
        general.FontSizePoints = FontSizePoints;
        general.AutoStart = AutoStart;
        general.TrayIcon = TrayIcon;
        general.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        general.AutoSavePlacement = AutoSavePlacement;

        try
        {
            _store.SaveSettings();
            Notify?.Invoke("已保存");
        }
        catch (Exception ex)
        {
            // 写盘失败必须**明确报错**，不能静默（定稿 §3.2）：
            // 目录不存在、文件被占用、磁盘只读都走这里。
            // 注意此时**不改**脏标记 —— 用户的东西还没落地，标记不能清。
            Notify?.Invoke($"保存失败：{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Discard()
    {
        _store.ReloadSettings();
        ReloadFromConfig();
        Notify?.Invoke("已放弃改动");
    }

    /// <summary>
    /// 恢复默认：把这一页的字段还原成 <see cref="GeneralConfig"/> 的出厂值。
    /// **注意它只是"改成默认值"，不是立刻落盘** —— 用户还要点保存才算数（与"放弃"区别在这）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void ResetToDefault()
    {
        var defaults = new GeneralConfig();

        Appearance = defaults.Appearance;
        Language = defaults.Language;
        Font = defaults.Font;
        FontSizePoints = defaults.FontSizePoints;
        AutoStart = defaults.AutoStart;
        TrayIcon = defaults.TrayIcon;
        MinimizeToTrayOnClose = defaults.MinimizeToTrayOnClose;
        AutoSavePlacement = defaults.AutoSavePlacement;

        Notify?.Invoke("已恢复默认（还要点保存才会生效）");
    }

    /// <summary>
    /// 打开配置文件所在的文件夹。
    ///
    /// 用户找不到配置文件是很常见的事（2026-09-20：他跑去**项目根目录**找了，
    /// 而程序读写的其实是 **exe 旁边那一份**）。与其解释路径，不如给个按钮直接送他过去。
    ///
    /// 这里出现 <c>Process</c> 是唯一一处"VM 碰外部世界"的地方 ——
    /// 它不算控件类型（约定只禁止 MessageBox / Window / WebView2），
    /// 而且"打开文件夹"本来就是要委托给操作系统的事。
    /// 将来若要做成完全可测的，就把它抽成一个注入的服务。
    /// </summary>
    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(_store.SettingsPath);
            if (string.IsNullOrEmpty(dir)) return;

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"打不开文件夹：{ex.Message}");
        }
    }

    // ------------------------------------------------------------
    private void ReloadFromConfig()
    {
        _loading = true;
        try
        {
            var general = _store.Settings.General;
            Appearance = general.Appearance;
            Language = general.Language;
            Font = general.Font;
            FontSizePoints = general.FontSizePoints;
            AutoStart = general.AutoStart;
            TrayIcon = general.TrayIcon;
            MinimizeToTrayOnClose = general.MinimizeToTrayOnClose;
            AutoSavePlacement = general.AutoSavePlacement;
        }
        finally
        {
            _loading = false;
        }

        // 三个即时项**统一应用一次**：不能靠上面的 setter 各自应用 ——
        // 那样"改外观"时会用到还没读进来的字体与字号（顺序不对，会闪一下旧值）。
        ThemeApplier.Apply(Appearance, Font, FontSizePoints);

        // 开机自启的登记项也对齐一次（读配置这一路跳过了 OnAutoStartChanged）——
        // "放弃"就是靠这一句把注册表退回配置里的样子。见 SyncAutoStart 的说明。
        SyncAutoStart();
    }

    private static IReadOnlyList<string> BuildFontChoices()
    {
        var list = new List<string> { "跟随系统" };

        try
        {
            list.AddRange(Fonts.SystemFontFamilies
                               .Select(f => f.Source)
                               .Where(s => !string.IsNullOrWhiteSpace(s))
                               .Distinct()
                               .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            // 枚举系统字体失败不该让页面打不开 —— 至少「跟随系统」还在
        }

        return list;
    }
}
