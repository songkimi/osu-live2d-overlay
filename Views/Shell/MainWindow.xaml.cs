// ============================================================
// MainWindow.xaml.cs —— 配置界面的壳：导航 + 悬浮窗的生死
//
// 【本轮它只做三件事】
//   1. 左导航切换内容区（六个一级板块目前都是占位页）
//   2. 启动 / 停止悬浮窗，并维护顶部状态条
//   3. 一个临时的风格验证页入口
//
// 【"启动悬浮窗"现在是临时实现】
//   直接 new OverlayWindow() —— 因为它自己会读配置、扫模型、连 tosu。
//   等 Services/ 那一层（服务生命周期门面）做好之后，这里要改成调它，
//   那时才有"启动失败提示""单实例""进程管理"这些能力。
//   见《项目结构约定》§四 与 定稿 §6 的实施顺序（第 5 步）。

// ============================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OsuLive2dOverlay;

public partial class MainWindow : Window
{
    /// <summary>
    /// 一个导航项：标题 + 图标字形 + "怎么造出这一页"。
    ///
    /// 图标是 **Segoe MDL2 Assets 的字形码**（Windows 自带那套图标字体），
    /// 不是 HandyControl 的 PackIcon —— 实测 HC 3.5.1 里根本没有 PackIcon
    /// （见 Resources/Styles/Styles.Basic.xaml 的说明）。
    ///
    /// ⚠ 这些字形码必须在真机上核对一遍（风格验证页里有一整排候选）：
    ///   码位写错不会报错，只会显示成方块或另一个图标。
    /// </summary>
    /// <param name="CreatePage">用委托而不是实例 —— 点了才造，没点的页不占内存、不跑构造</param>
    private sealed record NavEntry(string Title, string Icon, Func<FrameworkElement> CreatePage);

    private OverlayWindow? _overlay;

    public MainWindow()
    {
        InitializeComponent();

        NavList.ItemsSource = BuildNav();
        NavList.SelectedIndex = 0;      // 默认落在「概览」

        // 顶部那个"● 未保存"跟着配置门面走。
        // 它是**全局**的（不分页面）—— 用户切到别的页也能看见"有东西没存"。
        App.Config.DirtyChanged += UpdateDirtyIndicator;
        UpdateDirtyIndicator();

        // 顶部的"档案：××"也要跟着走：在「配置档案」页换了档案，
        // 状态条上那个名字得当场改掉 —— 否则用户会以为切换没生效。
        App.Config.ConfigChanged += UpdateProfileIndicator;
        UpdateProfileIndicator();
    }

    private void UpdateProfileIndicator()
    {
        ProfileText.Text = $"档案：{App.Config.ProfileName}";
    }

    private void UpdateDirtyIndicator()
    {
        DirtyText.Visibility = App.Config.IsDirty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 六个一级板块。口诀：**首页 → 形象 → 行为 → 联动与窗口 → 我的数据 → 软件本身**。
    /// </summary>
    private static NavEntry[] BuildNav() =>
    [
        new("概览", "\uE80F",                       // Home
            () => new OverviewView(new OverviewViewModel(App.Config))),

        new("形象", "\uE77B",                       // Contact（人物）
            () => new ModelView(new ModelViewModel(App.Config))),

        new("行为", "\uE768",                       // Play
            () => new BehaviorView(new BehaviorViewModel(App.Config))),

        new("联动与窗口", "\uE71B",                 // Link
            () => new OverlaySettingsView(new OverlaySettingsViewModel(App.Config))),

        new("数据与档案", "\uE8B7",                 // Folder
            () => new DataAndFilesView(App.Config)),

        new("应用", "\uE713",                       // Settings（齿轮）
            () => new AppView(App.Config)),
    ];

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedItem 拿不到就什么都不做（正常流程里不会发生 —— 导航永远是六个之一）
        if (NavList.SelectedItem is NavEntry entry)
            PageHost.Content = entry.CreatePage();
    }

    /// <summary>
    /// 窗口加载完补一次主题应用。
    ///
    /// 为什么需要补：`App.OnStartup` 里已经调过一次 `ThemeApplier.Apply`，
    /// 但那时**主窗口还没被创建**（`Application.MainWindow` 是 null），
    /// 而 HandyControl 的换肤 `Theme.SetSkin` 必须挂在某个元素上 ——
    /// 所以那一次只应用了我们自己的 Token，HC 控件没跟上。
    /// 不做这一步的表现是：**深色设置要等用户手动切一次外观才生效**。
    /// </summary>
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var general = App.Config.Settings.General;
        ThemeApplier.Apply(general.Appearance, general.Font, general.FontSizePoints);

        // 托盘图标的初始状态也要**在这里**才定：它是按配置显示/隐藏的，
        // 放在 Loaded 而不是构造函数，是因为它要在窗口就绪之后才谈得上"显示"。
        InitTray();
        ApplyTrayVisibility();

        StartGameExitWatch();
    }

    // ============================================================
    // 「osu 退出时退出本程序」（★ 2026-09-23 用户改的方向）
    //
    // 每隔 2 秒问一次"osu 还在不在跑"。**定时器一直开着、由开关决定要不要问** ——
    // 这样用户在设置页里把那个开关打开的那一刻就生效，不需要重启程序。
    //
    // 这段代码里最要紧的一行是 `_gameExitWatcher` 那句注释里说的：
    // **"没在跑"不等于"刚退出"**，程序刚启动时 osu 本来就没开。
    // 判断全在 `Logic/GameExitWatcher.cs` 里（纯逻辑、有测试），这里只负责"每 2 秒问一次"。
    // ============================================================

    private readonly GameExitWatcher _gameExitWatcher = new();
    private DispatcherTimer? _gameExitTimer;

    private void StartGameExitWatch()
    {
        if (_gameExitTimer is not null) return;

        _gameExitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameExitTimer.Tick += OnGameExitTick;
        _gameExitTimer.Start();
    }

    private void OnGameExitTick(object? sender, EventArgs e)
    {
        var dataSource = App.Config.Settings.DataSource;

        if (!dataSource.ExitWhenOsuExits)
        {
            // 开关关着：什么都不做，而且**把"见过 osu"的痕迹清掉**。
            // 不清的话，"关掉开关 → 过一会儿再打开"会把"现在没在跑"
            // 当成一次退出，程序当场自己关掉 —— 那正是"开关看起来抽风"。
            _gameExitWatcher.Reset();
            return;
        }

        // 配了 osu 路径就按那个程序的进程名查，没配就用 osu!（见 ProcessLauncher）
        if (!_gameExitWatcher.Observe(App.Processes.IsOsuRunning(dataSource.OsuPath))) return;

        DebugLog.Write("osu 已退出，而且开着「osu 退出时退出本程序」→ 准备收摊");
        _gameExitTimer?.Stop();
        ExitBecauseGameClosed();
    }

    /// <summary>
    /// osu 退出之后按设置收摊。
    ///
    /// 【顺序是有讲究的】
    ///   ① 有未保存的改动 → **先问一次**（不能让用户刚调好的东西静静丢掉）
    ///   ② 关悬浮窗 —— 走它正常的关闭流程，那里还会问一次"窗口位置要不要保存"
    ///   ③ 退出程序（`App.OnExit` 里再按设置关掉自己启动的 tosu）
    ///
    /// 【为什么先关悬浮窗，而不是直接 Shutdown】
    ///   `Application.Shutdown()` 会把窗口直接收掉，**悬浮窗那两个询问会被整段跳过** ——
    ///   用户刚拖好的窗口位置就白拖了。
    /// </summary>
    private void ExitBecauseGameClosed()
    {
        var config = App.Config;

        if (config.IsDirty)
        {
            // 悬浮窗在跑的时候主窗口是被最小化的 —— 不还原的话，
            // 这个弹窗会挂在任务栏里没人看见，用户只会觉得"软件卡住了"
            WindowState = WindowState.Normal;
            Activate();

            var answer = MessageBox.Show(this,
                "osu 已经退出，按设置要一起退出本程序。\n\n" +
                "配置里还有没保存的改动 —— 要保存吗？\n" +
                "（选「取消」就不退出，继续留着这个窗口）",
                "osu-live2d-overlay", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                _gameExitTimer?.Start();
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    if (config.IsConfigDirty) config.SaveConfig();
                    if (config.IsSettingsDirty) config.SaveSettings();
                }
                catch (Exception ex)
                {
                    // 存不下就**别退**：退了这些改动就永远没了，
                    // 而用户刚刚才明确说了"要保存"
                    MessageBox.Show(this, $"保存失败：{ex.Message}\n\n先不退出，你手动处理一下。",
                                    "osu-live2d-overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _gameExitTimer?.Start();
                    return;
                }
            }
        }

        var overlay = _overlay;

        if (overlay is not null)
        {
            overlay.Close();        // 走正常关闭流程（含"窗口位置要不要保存"的询问）

            if (overlay.IsVisible)
            {
                // 关窗被取消了（用户在询问里选了「取消」）—— 那就是"别关"，
                // 尊重它：不退出，接着盯
                DebugLog.Write("悬浮窗没有被关掉（用户取消）→ 取消这次退出");
                _gameExitTimer?.Start();
                return;
            }
        }

        DebugLog.Write("按「osu 退出时退出本程序」退出程序");
        ExitApplication();
    }

    // ============================================================
    // 点 X 的行为（定稿决策 26，★ 2026-09-24 接上）
    // ============================================================

    /// <summary>
    /// 程序是不是正在退出（托盘菜单的"退出"、osu 退出时收摊、以后别的明确要退的路径
    /// 都会先把它打上）。
    ///
    /// 【为什么非要这个标记】`Application.Shutdown()` 同样会走窗口的 `Closing` ——
    /// 而那里一旦看到"关闭时最小化到托盘"勾着就会 `e.Cancel = true; Hide()`，
    /// 于是托盘菜单里那个"退出"会变成**"窗口不见了、程序还在后台跑"**。
    /// 这类"退出退不掉"是最让人恼火的一种 bug，所以用一个显式标记把两条路分开。
    /// </summary>
    private bool _exiting;

    /// <summary>
    /// 点右上角 X 时干什么 —— 由「关闭时最小化到托盘」决定：
    ///
    /// | 这一项 | 点 X 的结果 |
    /// |---|---|
    /// | 勾上（默认） | 缩到托盘，**悬浮窗继续跑** |
    /// | 取消 | 直接退出程序 |
    ///
    /// 【"缩到托盘"有一个前提：托盘图标真的可用】
    ///   托盘图标关掉之后还"缩到托盘"，得到的是一个**没有入口的窗口**：
    ///   界面上看不见、托盘里也没有菜单能把它叫回来，用户只能去任务管理器结束进程。
    ///   所以这里连托盘开关一起判（设置页那边还有一层联动，见 GeneralViewModel）。
    ///
    /// 【真正退出时，悬浮窗在跑要追加一次确认】（决策 26）
    ///   误关会让打歌中的角色当场消失 —— 悬浮窗是被这个窗口一路管着的。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        // 程序自己在退出 → 一个字都不拦
        if (_exiting)
        {
            base.OnClosing(e);
            return;
        }

        var general = App.Config.Settings.General;

        if (general.MinimizeToTrayOnClose && general.TrayIcon)
        {
            e.Cancel = true;
            Hide();
            DebugLog.Write("点 X → 缩到托盘（悬浮窗继续跑；托盘菜单里能叫回来）");
            return;
        }

        if (_overlay is not null && !ConfirmExitWhileOverlayRunning())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>悬浮窗还在跑的时候要退出程序 → 追加一次确认（决策 26）</summary>
    private bool ConfirmExitWhileOverlayRunning()
    {
        var answer = MessageBox.Show(this,
            "悬浮窗还在运行，退出程序会一起结束它。\n\n确定退出？",
            "osu-live2d-overlay", MessageBoxButton.YesNo, MessageBoxImage.Question);

        return answer == MessageBoxResult.Yes;
    }

    /// <summary>
    /// 窗口真的关掉了 —— 顺手把托盘图标摘掉。
    /// 不摘的话它会一直留在通知区域里，直到用户把鼠标划过它才由系统清掉。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        DisposeTray();
        base.OnClosed(e);
    }

    // ============================================================
    // 悬浮窗的启动与停止
    // ============================================================

    private void OnToggleOverlayClick(object sender, RoutedEventArgs e)
    {
        if (_overlay is null) StartOverlay();
        else StopOverlay();
    }

    private void StartOverlay()
    {
        // ★ 2026-09-23：**先把该拉起来的程序拉起来，再开悬浮窗**。
        // 顺序不能反 —— 悬浮窗一起来就开始连 tosu；这时 tosu 还没起来的话，
        // 用户会先看到一轮"连接失败"再连上（TosuClient 每 3 秒重试，能自愈，但日志会很脏）。
        var startedProcesses = AutoStartProcesses();

        var overlay = new OverlayWindow();
        overlay.Closed += OnOverlayClosed;
        _overlay = overlay;

        // 让设置界面知道"现在有悬浮窗在跑"（运行期调整页要用：
        // 点了「放弃」得让它当场摆回去，还得如实告诉用户到底有没有在跑）
        App.Overlay = overlay;

        overlay.Show();

        // 只在**我们真启动了东西**的时候才等：用户自己开着的 tosu 不需要这个提示
        if (startedProcesses) WatchForFirstTosuData(overlay);

        // 决策 26：启动悬浮窗的同时，配置界面自动最小化（不是隐藏到托盘，
        // 这样用户还能从任务栏点回来 —— 而且"手动打开被自动最小化的配置页"
        // 正是他设计的第二条停止悬浮窗的路径）。
        WindowState = WindowState.Minimized;

        UpdateOverlayState();
    }

    // ============================================================
    // 自动启动 tosu / osu（★ 2026-09-23，"数据源"那四个开关里的前两个）
    // ============================================================

    /// <summary>
    /// 按设置启动悬浮窗前要拉起的那两个程序。
    /// 返回**是否真启动了新的进程**（"已经在跑"不算 —— 那种情况不需要等待提示）。
    ///
    /// 【失败必须说出来】这台机器上"点了开关没反应"是最难查的一类问题：
    /// 路径可能被挪走了、可能填的是个快捷方式、也可能被权限挡住。
    /// 所以四种"没启动"的结论都会进日志，**看得出人话的那几种还会弹一次提示**。
    /// </summary>
    private bool AutoStartProcesses()
    {
        var dataSource = App.Config.Settings.DataSource;
        var startedSomething = false;
        var problems = new List<string>();

        if (dataSource.AutoStartTosu)
            startedSomething |= Record("tosu", App.Processes.Launch(dataSource.TosuPath), problems);

        if (dataSource.AutoStartOsu)
            startedSomething |= Record("osu", App.Processes.Launch(dataSource.OsuPath), problems);

        if (problems.Count > 0)
        {
            MessageBox.Show(this,
                string.Join("\n", problems) +
                "\n\n可以在「数据与档案 → 程序与数据源」里检查那两个路径，" +
                "或者关掉对应的自动启动开关。",
                "osu-live2d-overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return startedSomething;
    }

    /// <summary>把一次启动结果写进日志；失败时收集一句话给弹窗。返回值 = 是不是"新启动了"</summary>
    private static bool Record(string what, LaunchResult result, List<string> problems)
    {
        DebugLog.Write($"自动启动 {what}：{result.Message}");

        if (result.Ok) return result.Outcome == LaunchOutcome.Started;

        problems.Add($"没能自动启动 {what} —— {result.Message}");
        return false;
    }

    /// <summary>
    /// 「启动等待秒数」的用处：自动启动之后，等这么久还没收到 tosu 数据就写一条日志。
    ///
    /// 【为什么只写日志、不弹窗】它不是错误，是一种"大概率哪里不对"的信号：
    /// tosu 慢（机械硬盘、开着杀软）也会超时，而连接本身会一直重试、最后多半能连上。
    /// 一超时就弹窗，会让"机器慢"看起来像"软件坏了"。
    /// 而日志里那句话是给**排查**用的：它把该查的三件事一次说完（地址端口 / tosu 有没有起来 / 把秒数调大）。
    /// </summary>
    private void WatchForFirstTosuData(OverlayWindow overlay)
    {
        var seconds = App.Config.Settings.DataSource.StartupWaitSeconds;
        var received = false;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (received) return;

            DebugLog.Write($"自动启动之后等了 {seconds} 秒还没收到 tosu 数据 —— 检查三件事：" +
                           "①「程序与数据源」页的地址与端口（那儿有「测试连接」）" +
                           "②tosu 是不是真的起来了 ③把「启动等待秒数」调大一点");
        };

        overlay.FirstUsableDataReceived += () =>
        {
            received = true;
            timer.Stop();
        };

        timer.Start();
    }

    private void StopOverlay()
    {
        var overlay = _overlay;
        _overlay = null;            // 先清空，免得 Closed 回调再走一遍
        App.Overlay = null;
        overlay?.Close();

        WindowState = WindowState.Normal;   // 决策 26：停悬浮窗后配置界面恢复原大小
        Activate();

        UpdateOverlayState();
    }

    /// <summary>
    /// 悬浮窗**不是**由这里关掉的时候走到这儿 ——
    /// 比如用户按了 `Ctrl+Alt+Q`（结束悬浮窗热键），或者悬浮窗自己出了错退掉。
    /// 这时配置界面同样要恢复，否则用户会以为"点了热键软件没了"。
    /// </summary>
    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (_overlay is null) return;       // 已经由 StopOverlay 处理过了

        _overlay = null;
        App.Overlay = null;
        WindowState = WindowState.Normal;
        UpdateOverlayState();
    }

    /// <summary>把顶部状态条刷成当前事实。状态灯的颜色也用 Token，切主题时跟着变。</summary>
    private void UpdateOverlayState()
    {
        var running = _overlay is not null;

        StatusText.Text = running ? "悬浮窗运行中" : "悬浮窗未启动";
        StatusDot.Fill = (Brush)FindResource(running ? "Brush.Success" : "Brush.TextDisabled");
        OverlayButton.Content = running ? "停止悬浮窗" : "启动悬浮窗";

        // 托盘菜单里那一项也跟着切（★ 2026-09-24）。
        // **不靠"菜单打开时再刷新"**：那会"先显示旧文案再变"，闪一下。
        // 状态一变就改，菜单永远是对的，而且一处都不多。
        if (_trayOverlayItem is not null)
            _trayOverlayItem.Text = running ? "停止悬浮窗" : "启动悬浮窗";
    }

    // ============================================================
    // 占位页
    //
    // 用代码造而不是 XAML：它只是脚手架，接一个真页面就少一个。
    // 真要给它写个 XAML 文件，会凭空多出一堆迟早要删的东西。
    // ============================================================

    private static FrameworkElement Placeholder(string title, string note)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };

        var titleBlock = new TextBlock { Text = title };
        titleBlock.SetResourceReference(StyleProperty, "Text.PageTitle");
        panel.Children.Add(titleBlock);

        var noteBlock = new TextBlock { Text = note, Margin = new Thickness(0, 10, 0, 0) };
        noteBlock.SetResourceReference(StyleProperty, "Text.Caption");
        panel.Children.Add(noteBlock);

        var hint = new TextBlock
        {
            Text = "（这一页还没实现 —— 按定稿 §6 的实施顺序逐页接上来）",
            Margin = new Thickness(0, 18, 0, 0)
        };
        hint.SetResourceReference(StyleProperty, "Text.Label");
        panel.Children.Add(hint);

        return panel;
    }
}
