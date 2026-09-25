// ============================================================
// OverlayWindow.xaml.cs —— 把三块拼起来
//
//   数据层  TosuClient   (WebSocket 读 osu! 连击)
//        ↓
//   逻辑层  ComboTracker (跨阈值 / 断连击，阈值来自配置)
//        ↓
//   表现层  WebView2 → web/index.html (Live2D 播表情)
//
// 另外负责"不干扰游戏"的那几件事：穿透、不抢焦点、调整模式、全局热键。
// ============================================================
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography.Xml;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace OsuLive2dOverlay;

public partial class OverlayWindow : Window
{
    // 热键编号：这是**应用自己的概念**（不是 Win32 的），所以留在窗口类里。
    // 修饰键、虚拟键码、窗口消息、扩展样式位、P/Invoke 全在 Infrastructure/Win32.cs。
    //
    // ★ 2026-09-23 瘦身：删掉了 9003~9008 六个热键 ——
    //   `Ctrl+Alt+R`（重载页面）与 `Ctrl+Alt+1/2/3/4/V` 五个开发期测试键
    //   （模拟连击 / 断连击 / 区间 / 试听语音）。用户明确说不再用它们。
    //   剩下这两个**是功能**，而且都能在设置界面里改（只读展示，见定稿决策 57）：
    private const int HOTKEY_TOGGLE_MODE = 9001;    // 临时允许交互（拖动窗口用）
    private const int HOTKEY_QUIT = 9002;           // 结束悬浮窗

    /// <summary>
    /// 宿主窗口的**默认尺寸**（XAML 里的 `Width`/`Height` 就是引的这两个）。
    ///
    /// 单独拎成常量，是因为**设置界面的预览区也要用它**：
    /// 某一档没设过窗口尺寸时，预览要按默认尺寸画那个边框 ——
    /// 否则它只能显示一个"用户其实没设过"的数字，那是在撒谎。
    /// </summary>
    public const double DefaultWidth = 430;
    public const double DefaultHeight = 620;

    /// <summary>窗口右下角用来缩放的"抓取区"边长（应用自己的约定，不是系统的）</summary>
    private const double ResizeGripSize = 18;

    private readonly PluginConfig _config;

    /// <summary>
    /// 当前档案文件的完整路径（`profiles\&lt;档案名&gt;.json`）。
    ///
    /// 存下来是因为**保存窗口位置时要写回同一个文件**。
    /// 以前那个地方自己拼了一遍 `Path.Combine(exeDir, "config.json")` ——
    /// 于是"读哪份"和"写哪份"是两份独立的真相，加了多档案之后必然对不上。
    /// </summary>
    private readonly string _configPath;

    /// <summary>
    /// 软件设置（settings.json）—— 悬浮窗也要读它。
    ///
    /// 因为**「调试模式」搬到了那边**（2026-09-20 拆档案/设置）。
    /// 它描述的是"软件怎么运行"（要不要在画面上显示开发期信息），不是"角色怎么演"，
    /// 所以它跟着 settings.json 走、**不随档案切换**。
    /// </summary>
    private readonly AppSettings _settings;

    /// <summary>
    /// 数据源客户端。地址从设置里来
    /// </summary>
    private readonly TosuClient _client;
    private readonly ComboTracker _tracker;

    /// <summary>
    /// 界面档位：四个界面各一份（显示什么、显示在哪、能不能点）。
    /// 服务运行时只用它一个：`Get(当前场景)` → `IsUsable` 决定显不显示。
    /// </summary>
    private readonly SceneProfiles _scenes;

    private readonly CancellationTokenSource _cts = new();
    private readonly string _webDir;

    /// <summary>
    /// 表情清单：标识 → 这个表情要改哪些参数。
    /// 启动时从 exp3 文件读一次，之后每次触发只是查字典 —— 页面收消息时直接带上参数表，
    /// 页面就不用去碰文件系统了（它本来也不该知道文件在哪）。
    /// </summary>
    private ExpressionCatalog _catalog = ExpressionCatalog.Empty;

    /// <summary>
    /// 界面状态机：把 tosu 报的 menu.state 翻译成"现在在哪个界面"。
    /// 它只负责**知道**，不负责**表现** —— 每个界面显示什么、在哪、能不能拖，
    /// 都是配置的事，这个类一概不管。
    /// </summary>
    private readonly GameStateTracker _sceneTracker = new();

    /// <summary>
    /// 上一次发给页面的"在不在加载"。
    ///
    /// 为什么要单独记：加载**不改变场景**（场景仍是上一个界面，窗口不该动），
    /// 所以 `GameStateTracker.Update` 那时返回 null。只盯着返回值的话，
    /// "加载开始 / 加载结束"这两件事就永远发现不了，透明度也就永远不刷新。
    /// </summary>
    private bool _loadingSent;

    /// <summary>
    /// 上一次收到 / 发出去的**游玩模式**（`gameplay.gameMode` 翻译过来的）。
    ///
    /// 它决定「打歌」那一档用哪份站位（决策 50）。和 `_loadingSent` 一样，
    /// 它**不改变场景**却改变"该发什么"，所以要单独记着才发现得了变化。
    ///
    /// null = 认不出的模式（比如 tosu 报了个 7）—— 那时不按模式挑，用场景自己那份站位。
    /// </summary>
    private GameMode? _currentMode;

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isRunningMode = true;

    /// <summary>
    /// 上一次真正应用到窗口上的穿透状态。用来判断"这次的穿透状态是不是变了" ——
    /// 只有真变了才需要强制重合成一次（见 NudgeRepaint）。
    /// 初值 true 与启动时"还不知道在哪个界面"的默认选择一致，免得启动就抖一下。
    /// </summary>
    private bool _clickThroughApplied = true;

    /// <summary>
    /// 收到过第一包**能用**的 tosu 数据没有
    ///
    /// 它只为一件事存在：「启动悬浮窗时自动启动 tosu/osu」之后，
    /// 按「启动等待秒数」等一段时间还没数据就**说一声** ——
    /// 那是自动启动唯一可能静默失败的地方（tosu 起来了但端口不对、或者根本没起来）。
    /// </summary>
    private bool _firstDataAnnounced;

    /// <summary>
    /// 第一包能用的数据到了 —— **整条链通了**的唯一可靠信号。
    ///
    /// 为什么要单独抛一个事件，而不是让外面去看 <see cref="TosuSnapshot"/>：
    /// `OnSnapshotReceived` 里有一句"什么都没变就提前 return"（场景、加载、模式都没变），
    /// 把通知挂在里面，**第一包数据有极大概率正好落在那句 return 上**，
    /// 于是"等到了"这件事永远不会被报告 —— 又是一种"看起来接上了、其实没有"。
    /// </summary>
    public event Action? FirstUsableDataReceived;

    /// <summary>
    /// 影子窗口：专门负责接鼠标。本窗口里放透明元素是接不到鼠标的 ——
    /// WebView2 是 HwndHost，会阻断它所在区域的 WPF 命中测试（见 InteractionWindow 的说明）。
    /// </summary>
    private InteractionWindow? _interaction;
    private bool _pageReady;
    private System.Windows.Threading.DispatcherTimer? _cursorTimer;
    private Point _lastCursorSent = new(double.NaN, double.NaN);

    // ---- 语音 ----
    private string? _voiceDir;                             // 解析后的语音目录（没有就不映射）
    private object? _voicePayload;                         // 发给页面的"情绪 → 地址"表

    /// <summary>
    /// XAML 里的默认窗口尺寸（构造时取一次）。
    /// 它是"某个界面没设过宽/高"以及"窗口第一次出现"时的兜底尺寸 ——
    /// 刻意**不**读当前 Width/Height：那会把上一个界面的尺寸借给下一个界面。
    /// </summary>
    private readonly (double Width, double Height) _defaultSize;

    /// <summary>默认摆放距离屏幕右边 / 上边的留白（DIP）</summary>
    private const double DefaultRightMargin = 40;
    private const double DefaultTopMargin = 120;

    public OverlayWindow()
    {
        InitializeComponent();

        // 记下 XAML 里的默认尺寸：它是"某个界面没设过宽/高"时的兜底。
        // **必须在任何 ApplyWindowPlacement 之前取** —— 那之后 Width/Height 就是"窗口当前大小"了，
        // 拿它当默认值，等于又把上一个界面的尺寸借给了下一个界面（那正是要修的 bug）。
        _defaultSize = (Width, Height);

        // 先读软件设置（"当前是哪个档案"记在里面），再由它定出该读哪个档案文件 ——
        // 顺序和 App.OnStartup 一致，路径规则也共用 ProfileLocator 那一份
        var exeDir = AppContext.BaseDirectory;
        var settingsPath = Path.Combine(exeDir, "settings.json");
        var legacyConfig = Path.Combine(exeDir, ProfileLocator.LegacyFileName);

        _settings = AppSettings.Load(settingsPath, legacyConfig);
        _configPath = ProfileLocator.EnsureProfile(exeDir, _settings.Profile.Current, legacyConfig);
        _config = PluginConfig.Load(_configPath);

        _webDir = Path.Combine(exeDir, "web");
        // 常态区间来自配置：省略「最大」表示"以上"，补成 int.MaxValue 交给状态机（它只认闭区间）
        _tracker = new ComboTracker(
            _config.ComboTriggers.Select(t => t.Threshold),
            _config.Ranges.Select(r => new ComboRange(r.Min, r.Max ?? int.MaxValue)));

        // 界面档位：配置加载时已经保证非空（老配置会被迁移成四档），这里只是兜底
        _scenes = _config.Scenes ?? new SceneProfiles();

        // 日志已经在 App.OnStartup 里统一初始化过（整个程序只调一次）。
        // 这里**不能**再 Start —— 那会把当天已经写下的那份日志文件重置掉。
        DebugLog.Write("悬浮窗已启动");

        // 配置体检：门槛配反了不会崩，但"小额"那一档就永远轮不到 —— 在日志里说一声，
        // 免得改数字的人（包括我自己）对着配置猜半天。
        if (_config.Miss.BigThreshold > 0 && _config.Miss.BigThreshold <= _config.Miss.SmallThreshold)
            DebugLog.Write($"配置提醒：大额门槛({_config.Miss.BigThreshold}) " +
                           $"应大于小额门槛({_config.Miss.SmallThreshold})，否则小额那一档用不到");

        // 语音是"配置 + 文件系统"的事，和 WebView2 无关 —— 所以在构造时就解析好。
        // 这样即使浏览器那一层出问题，也能立刻知道是"语音配错了"还是"页面没起来"。
        _voicePayload = BuildVoicePayload();

        // 数据源地址**从设置里来**（★ 2026-09-23）。
        // 写进日志是刻意的：连不上时第一个要回答的问题就是"它到底在连哪个地址"，
        // 而配置里的 IP / 端口有两处（设置界面、JSON），靠翻文件猜太慢。
        _client = new TosuClient(_settings.DataSource.WebSocketUrl);
        DebugLog.Write($"tosu 地址：{_client.Url}（改了要重启悬浮窗才生效）");

        _client.ComboUpdated += OnComboUpdated;

        // 第一包数据单独记一次（见 FirstUsableDataReceived 的说明）。
        // 必须在调用 OnSnapshotReceived **之前**判定 —— 那里面第一句就可能提前 return。
        _client.SnapshotReceived += snapshot =>
        {
            if (!_firstDataAnnounced)
            {
                _firstDataAnnounced = true;
                DebugLog.Write("收到第一包 tosu 数据（连接已真正打通）");
                FirstUsableDataReceived?.Invoke();
            }

            OnSnapshotReceived(snapshot);
        };

        _client.SnapshotReceived += OnSnapshotReceived;

        // tosu 断了（osu 退出 / tosu 自己关了）→ 界面状态回到"不知道"。
        // 不这么做的话，角色会停在上一个界面不动 —— "关掉游戏、角色还挂在屏幕上"就是这个原因：
        // 断开之后没有任何数据再推过来，状态机自己永远不会变。
        _client.Disconnected += () =>
        {
            _sceneTracker.Update(null, hp: null);   // state 为 null → Unknown，hp 传什么都一样
            ApplyScene();
        };
        // tosu 自己在报状态（连上了 / 断了 / 找不到 tosu 进程）。**只进日志**：
        // 悬浮窗画面上不写任何字 —— 理由见 `ShowStatus` 撤掉那一段。
        _client.StatusChanged += text => DebugLog.Write("tosu：" + text);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // 默认停在屏幕右侧（正是"游戏右边"）。
        // 这里**故意**用工作区（扣掉任务栏），跟拖动/吸附用的 ActiveArea() 不一样：
        // 第一次露面时窗口该是完整可见的，不该被任务栏压住一条。
        // 同一份默认摆放还有第二个用处："某个界面没设过位置"时用它兜底（见 ApplyWindowPlacement），
        // 所以摆放在 DefaultWindowBounds() 里，别在这里写死第二遍。
        var start = DefaultWindowBounds();
        Left = start.Left;
        Top = start.Top;

        // 影子窗口：和悬浮窗同位置同尺寸，专门负责接鼠标、把它变成"拖动窗口"。
        // 为什么要单独一个窗口：本窗口里嵌着 WebView2（HwndHost），它会阻断所在区域的
        // WPF 命中测试 —— 在本窗口里放什么透明元素都接不到鼠标。
        _interaction = new InteractionWindow();
        _interaction.Dragged += OnInteractionDragged;
        _interaction.DragFinished += () =>
        {
            SnapToEdge();          // 先判定要不要吸到边上
            RecordWindowChange();  // 再把最终位置记进"待保存"（吸完的位置才是用户看到的位置）
        };
        // 窗口挪了位置就让影子窗口跟上（影子窗口必须和悬浮窗严丝合缝地重叠）。
        // 注意这里**不做吸附** —— 吸附只发生在"松手那一刻"，见 SnapToEdge。
        LocationChanged += (_, _) => SyncInteractionWindow();
        SizeChanged += (_, _) => SyncInteractionWindow();
        Closed += (_, _) =>
        {
            try { _interaction?.Close(); } catch { /* 关窗时的异常不值得管 */ }
        };

        ApplyMode();

        // ------------------------------------------------------------
        // 热键注册（★ 2026-09-23：**改成读配置**）
        //
        // 原来这里是写死的：`Hotkey(HOTKEY_QUIT, VK_Q)`、`Hotkey(HOTKEY_TOGGLE_MODE, VK_T)`。
        // 于是配置里那三个热键字段一个都没被读过，而且埋着一个直接骗用户的矛盾：
        // 文档与配置默认值写的是"临时交互 = Ctrl+Alt+E"，代码里注册的却是 **T** ——
        // 差一个字母，用户照着界面提示按键**按不出来**。
        // 现在走 `HotkeyParser`：人写的 `"Ctrl+Alt+E"` → `MOD_CONTROL|MOD_ALT` + `VK_E`。
        //
        // **注册失败绝不静默**（定稿 §3.1）：被别的软件占用时用户会以为软件坏了，
        // 所以失败要写日志、而且日志里说清是哪一个热键。
        // ------------------------------------------------------------
        void Hotkey(int id, string what, string? configured, uint fallbackModifiers, uint fallbackKey)
        {
            var parsed = HotkeyParser.Parse(configured);

            var modifiers = parsed?.Modifiers ?? fallbackModifiers;
            var key = parsed?.VirtualKey ?? fallbackKey;

            if (parsed is null)
            {
                DebugLog.Write($"热键「{what}」配置里写的是「{configured}」，认不出来 → " +
                               $"先用默认的 {HotkeyParser.Normalize(fallbackModifiers, fallbackKey)}");
            }

            if (!Win32.RegisterHotkey(_hwnd, id, modifiers, key))
                DebugLog.Write($"热键注册失败（多半被别的软件占了）：{what} = " +
                               HotkeyParser.Normalize(modifiers, key));
        }

        var hotkeys = _settings.Overlay;

        // 这两个**从配置来**（默认值就在 WindowSettings 里）
        // 注意 `VK_E` 没在 `Win32` 里定义（那边只挑了用得着的几个）——
        // 字母的虚拟码就是它大写的 ASCII 码，所以直接写 `'E'`，不必要为它再加常量。
        Hotkey(HOTKEY_TOGGLE_MODE, "临时允许交互", hotkeys.TempInteractiveHotkey,
               Win32.MOD_CONTROL | Win32.MOD_ALT, 'E');
        Hotkey(HOTKEY_QUIT, "结束悬浮窗", hotkeys.StopHotkey,
               Win32.MOD_CONTROL | Win32.MOD_ALT, Win32.VK_Q);

        // ★ 2026-09-23：原来这里还注册了 `Ctrl+Alt+R`（重载页面）与五个测试热键
        //   （`1/2/3/4/V`）。它们已经删掉 —— 见上面 `测试触发` 那一段的说明。
        //   于是**整个悬浮窗现在只有两个热键**，而且两个都能在设置界面里看到/改。

        if (HwndSource.FromHwnd(_hwnd) is { } source)
            source.AddHook(WndProc);

        _ = InitializeWebViewAsync();

        // 后台开始收连击数据
        _ = _client.RunAsync(_cts.Token);
    }

    // ---------------- WebView2 初始化 ----------------

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // 透明背景必须在导航之前设置
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);

            DebugLog.Write("WebView2：开始创建环境");

            // Live2D 必须有 WebGL 才能渲染。加一个"允许软件渲染"的兜底参数：
            // 如果这台机器上 WebView2 的 GPU 路径不可用（透明窗口下有可能发生），
            // 没有它就会创建不出 WebGL 上下文 → 模型加载成功但什么都画不出来。
            //
            // 第二个参数是语音的关键：WebView2 默认沿用 Chromium 的策略 —— 不允许
            //   "没有用户手势"的媒体自动播放。而这个窗口是点击穿透的，WebView2 永远等不到
            //   一次点击或按键，不加它语音会一句都放不出来（play() 直接被拒绝）。
            // 后两个参数是 2026-09-18 排查"角色看不见"时加的，**它们不是最终定案的解法**
            //   （真正成因是：主窗口不穿透时 WebView2 的原生子窗口样式被清除 → WebGL 停止上屏；
            //     现在由"主窗口运行期恒穿透 + 独立的影子窗口接鼠标"解决，见 InteractionWindow）。
            // 留着是因为这两个都在防"窗口被别的窗口盖住时渲染被挂起"这一类事 ——
            //   osu 全屏时本窗口经常处于被覆盖状态，两种保护都无害，也可能省掉一次偶发故障。
            var options = new CoreWebView2EnvironmentOptions
            {
                // 参数原文搬去了 PageHost —— 它是"把页面喂起来"这件事的一部分，
                // 而设置界面的预览区要用**同一套**（少一个参数就可能没有 WebGL）。
                AdditionalBrowserArguments = PageHost.BrowserArguments
            };

            // 用户数据目录用 WebView2 的默认位置（exe 同级的 osu-live2d-overlay.exe.WebView2）
            var environment = await CoreWebView2Environment.CreateAsync(null, null, options);
            DebugLog.Write("WebView2：环境已创建，开始 EnsureCoreWebView2Async");
            await Web.EnsureCoreWebView2Async(environment);
            DebugLog.Write("WebView2：控件已就绪");

            // WebView2 的子窗口到这一刻才存在 → 补一次样式设置，让它们也被设成穿透。
            // （不然在 osu 没开、场景一直不变的情况下就没人去碰它们，鼠标会被 WebView2 吃掉。）
            ApplyMode();
            var core = Web.CoreWebView2;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;      // 调试用；正式发布可改 false

            if (!Directory.Exists(_config.Model.Directory))
                DebugLog.Write("找不到模型目录：" + _config.Model.Directory);

            // 语音目录：解析成"情绪 → 地址"的表，随 init 一起发给页面。
            // 已经**在构造时**算好了 —— 那句"N 类情绪"的日志也在那边写

            // 先把模型扫一遍 —— 配置里引用的是"标识"，补丁要靠这份扫描结果把标识翻回文件路径
            var scan = ModelScanner.Scan(_config.Model.Directory, _config.Model.Entry);
            foreach (var problem in scan.Problems) DebugLog.Write("扫描：" + problem);

            // 表情清单：把每个 exp3 文件里"要改哪些参数"读进来（页面合成时要用），
            // 顺带拿 cdi3 里的名字反射的注册名（"3.exp3" → "星星眼"）
            _catalog = ExpressionCatalog.Build(scan, _config.Model.Directory);
            foreach (var problem in _catalog.Problems) DebugLog.Write("表情：" + problem);
            if (scan.Expressions.Count > 0)
            {
                DebugLog.Write($"表情清单 {scan.Expressions.Count} 个，例如 " +
                               string.Join("、", scan.Expressions.Take(5)
                                   .Select(r => $"{r.Id} → {_catalog.DisplayName(r.Id)}")));
            }

            // 生成"补过资源"的模型定义到临时目录，再把四个目录映射成虚拟主机。
            // 目录名带 "overlay"：设置界面的预览区有**它自己的**一份
            // （它读的可能是还没保存的配置，共用目录会互相覆盖，见 PageHost.PatchDirectoryFor）。
            var patchDir = PageHost.PatchDirectoryFor("overlay");
            try
            {
                var patch = ModelHost.WritePatchedModel(_config, scan, patchDir);

                // 配置里写了模型里没有的标识（多半是打错字）—— 这是"不修就没法用"，必须说出来
                foreach (var problem in patch.Problems) DebugLog.Write("补丁：" + problem);

                DebugLog.Write($"模型已补丁：{scan.Expressions.Count} 个表情 / " +
                               $"{scan.Motions.Count} 个动作 → {Path.GetFileName(patch.Path)}");
            }
            catch (Exception ex)
            {
                DebugLog.Write("生成模型补丁失败：" + ex.Message);
            }

            ModelHost.SetupVirtualHosts(_webDir, _config.Model.Directory, patchDir, _voiceDir,
                (host, dir) => core.SetVirtualHostNameToFolderMapping(host, dir, CoreWebView2HostResourceAccessKind.Allow));

            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, args) =>
            {
                // 把"最后落在哪个地址"也记下来：落在浏览器自己的错误页上时，
                // 那个地址是 `chrome-error://chromewebdata/` 而不是我们的页面 ——
                // 这是"悬浮窗白屏但日志里什么都没有"唯一能抓住的线索（同预览区那边的说明）。
                DebugLog.Write("页面导航完成：成功=" + args.IsSuccess + " " + args.WebErrorStatus +
                               " 实际地址=" + core.Source);

                if (args.IsSuccess) SendInit();
            };

            var startUrl = PageHost.EntryUrl;
            DebugLog.Write("开始导航：" + startUrl);
            core.Navigate(startUrl);
        }
        catch (Exception ex)
        {
            DebugLog.Write("WebView2 初始化异常：" + ex);
        }
    }

    /// <summary>
    /// 把模型地址、待机动作、视图参数、表情持续时间、语音表告诉页面。
    ///
    /// 2026-09-20：报文的**字段形状搬去了 `PageHost.BuildInitPayload`** ——
    /// 设置界面的预览区要用同一个页面、同一包协议。
    /// 两边各写一份的话，改一处漏一处 = 页面上"某个功能莫名其妙不生效"，
    /// 而且**两边表现还不一样**，那是最难查的一类。
    /// </summary>
    private void SendInit()
        => SendJson(PageHost.BuildInitPayload(_config, _settings.DebugMode, _voicePayload, _catalog));

    /// <summary>
    /// 解析语音配置 → 发给页面的表：{ enabled, volume, minIntervalMs, emotions:[{name,urls}], problem }
    /// 解析不出来时也不报错崩溃，而是把原因放进 problem，由页面在诊断条里说明（正常玩的时候看不见）。
    /// </summary>
    private object? BuildVoicePayload()
    {
        var voice = _config.Voice;
        if (!voice.Enabled) return new { enabled = false, problem = "" };

        if (string.IsNullOrWhiteSpace(voice.Directory))
            return new { enabled = false, problem = "「语音.启用」是 true，但没配「目录」" };

        string voiceRoot;
        try
        {
            voiceRoot = Path.GetFullPath(voice.Directory);
        }
        catch (Exception ex)
        {
            return new { enabled = false, problem = "语音目录路径不合法：" + ex.Message };
        }

        if (!Directory.Exists(voiceRoot))
        {
            _voiceDir = null;      // 目录不存在 → 不映射（映射不存在的目录会抛异常）
            return new { enabled = false, problem = "找不到语音目录：" + voice.Directory };
        }

        _voiceDir = voiceRoot;

        var (emotions, problems) = VoiceLibrary.Resolve(voice, voiceRoot);
        foreach (var p in problems) DebugLog.Write("语音：" + p);

        if (emotions.Count == 0)
            return new { enabled = false, problem = problems.FirstOrDefault() ?? "语音目录里没有可用的音频文件" };

        DebugLog.Write($"语音目录已映射：{voiceRoot}（{emotions.Count} 类情绪）");

        return new
        {
            enabled = true,
            volume = Math.Clamp(voice.Volume, 0.0, 1.0),
            minIntervalMs = Math.Max(0, voice.MinIntervalMs),
            emotions = emotions.Select(e => new { name = e.Name, urls = e.Urls }).ToList(),
            problem = problems.Count > 0 ? string.Join("；", problems) : ""
        };
    }

    /// <summary>
    /// 给页面发消息。
    /// 注意：WebView2 的 API 只能在 UI 线程调用（连击事件是从 WebSocket 接收线程来的），
    ///   所以这里统一做一次线程调度 —— 否则消息发不出去，而且异常会被 WS 循环吞掉，表现成"什么都没发生"。
    /// </summary>
    private void SendJson(object payload)
    {
        if (Web.CoreWebView2 is null) return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SendJson(payload));
            return;
        }

        try
        {
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            DebugLog.Write("向页面发消息失败：" + ex.Message);
        }
    }

    // ---------------- 测试触发 ----------------
    //
    // ★ 2026-09-23 整段删掉了：四个测试方法（模拟跨阈值 / 模拟三种断连击 /
    //   轮流试听情绪 / 依次推过各区间）与它们的热键 `Ctrl+Alt+1/2/3/4/V`，
    //   以及 `RangeTestCombos` 那组假连击值和三个计数器字段。
    //   用户明确说"不会再使用任何测试时的热键和方法了"。
    //
    //   它们当初的用途是"不开游戏也能验证 消息→表情/语音 这条链"，
    //   现在这条链已经由**真实打歌**反复验证过，测试键反而成了负担：
    //   它要额外维护五个热键的注册/注销，而且 `SendTestRange` 还得刻意绕过
    //   生产路径上那道场景判断 —— 那种"测试专用旁路"最容易和生产代码走岔。
    //
    //   要再验证这条链，走真路：`Logic/ComboTracker` 有控制台穷举测试
    //   （`.build-verify/L03check` 等），端到端就开一局游戏。


    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var text = root.TryGetProperty("text", out var tx) ? tx.GetString() : "";

            switch (type)
            {
                case "ready":
                    _pageReady = true;
                    DebugLog.Write("页面就绪（模型已加载）" +
                                   (DebugLog.CurrentFile.Length > 0 ? $"；日志：{DebugLog.CurrentFile}" : ""));
                    SendCurrentSteady();     // 页面刚起来，把当前的常态表情补给它
                    ApplyScene();            // 页面刚起来，把当前的界面与常态表情补给它
                    StartCursorTracking();
                    break;

                case "log":
                    DebugLog.Write("页面：" + text);
                    break;

                case "error":
                    DebugLog.Write("页面错误：" + text);
                    break;

                // ★ 2026-09-23 新增：页面**画不出东西**了（模型没加载出来、缺 Cubism Core）。
                //   与 error 的区别是"后果"：error 是某一次没做成，fatal 是这一屏永远是空的。
                //   悬浮窗**没有地方显示它**（画面上不写任何字，见 ShowStatus 撤掉那一段），
                //   所以这里只记日志；设置界面的预览区会把它显示在自己那个提示行上。
                case "fatal":
                    DebugLog.Write("页面无法渲染：" + text);
                    break;

                // ★ 2026-09-21：`case "snapshot"` 撤掉了 —— 页面不再导出画面快照，
                //   这一侧也就不需要接（原来这里把 base64 存成 PNG）。
                //   理由见下面 `Text(double?)` 后面那段说明。
            }
        }
        catch
        {
            // 页面消息格式意外时忽略，不影响主流程
        }
    }

    /// <summary>
    /// 页面就绪时把"当前处于哪个区间"补发一次。
    /// 区间事件在程序启动那一刻就报过了，而那时页面还在加载、消息直接被丢掉 ——
    /// 不补这一下，角色会一直没有常态表情（要等玩家真跨过下一个区间才出现）。
    /// </summary>
    /// <summary>
    /// 每收到一包**能用的** tosu 数据就走这里。
    ///
    /// 注意「界面状态」和「连击」是两条独立的线：连击走 OnComboUpdated，
    /// 这里只管界面切换，两者互不影响 —— 和"表情 / 语音 / 动作"三条线解耦是同一个思路。
    /// </summary>
    private void OnSnapshotReceived(TosuSnapshot snapshot)
    {
        // "进了游戏没有"看**血条**（加载期间它是 0，歌曲一开始就是满的）
        var scene = _sceneTracker.Update(snapshot.MenuState, snapshot.Hp);
        var loading = _sceneTracker.IsLoading;
        var mode = GameModes.FromTosu(snapshot.GameMode);

        // ★ 这里**不能**只判断 scene 是否为 null。
        //   有两件事**都不改变场景**，却都改变"该发什么"：
        //     · 加载开始/结束 → 角色透明度
        //     · 游玩模式变了   → 打歌那一档按模式挑站位（决策 50）
        //   只看 scene 的话这两件事永远等不到刷新（场景始终没变）。所以三个都看。
        if (scene is null && loading == _loadingSent && mode == _currentMode) return;

        var modeChanged = mode != _currentMode;
        _loadingSent = loading;
        _currentMode = mode;

        if (scene is not null)
        {
            DebugLog.Write($"界面切换：{scene.Value}（menu.state={Text(snapshot.MenuState)}，" +
                           $"hp={Text(snapshot.Hp)}，gameMode={Text(snapshot.GameMode)}）");

            // ★ 2026-09-23 修正：连击记忆在**离开**打歌时作废，而不是进入时。
            //
            //   原来这里是"进入打歌 → Reset()"。那有个藏得很深的坑：
            //   进入打歌的那一包数据里 combo 未必是 0 —— 场景刚从加载切过来、
            //   或者被一包残留数据误判成 Playing 时，同一包的 combo 可能还是上一局的高值。
            //   记忆刚清成 0 就吃到"1000"，会被当成连击刚刚涨上去 →
            //   凭空报一次 RangeChanged + Step（角色把打歌时的常态表情与最高阈值表情重放一遍）。
            //   用户报的"从结算退回选歌会再次触发一次连击状态机"正是这个。
            //
            //   改成离开时清：进入打歌时记忆本来就是干净的，第一包报什么都不算"重放"。
            if (scene.Value != GameScene.Playing)
            {
                if (_tracker.LastCombo > 0)
                    DebugLog.Write($"[连击] 离开打歌（{scene.Value}）→ 记忆作废（上一局到过 {_tracker.LastCombo}）");

                _tracker.Reset();
            }
        }
        else if (modeChanged)
        {
            DebugLog.Write("游玩模式变了 → " + (mode?.ToString() ?? "认不出（不按模式挑站位）"));
        }
        else
        {
            DebugLog.Write(loading
                ? "谱面加载中：角色临时隐藏（窗口与站位都不动）"
                : "加载结束：角色恢复显示");
        }

        ApplyScene();
    }

    /// <summary>把可空的 tosu 字段打成能读的日志文字（"缺失" = 这一包里根本没读到）</summary>
    private static string Text(int? value) => value?.ToString() ?? "缺失";

    /// <summary>血条是浮点（`200` 和 `200.0` 都可能出现），日志里按原样打</summary>
    private static string Text(double? value) => value?.ToString("0.###") ?? "缺失";

   

    /// <summary>
    /// 把"当前界面该长什么样"落到页面和窗口上。
    /// 两个入口都走这里：场景真的变了（OnSnapshotReceived），以及页面刚就绪要补发（OnWebMessage 的 ready 分支）。
    ///
    /// 用的是 Get + IsUsable 这**两个**判断，它们回答的是不同的问题：
    ///   Get(场景) == null   → 这个场景根本没有档位（osu 没开 / 认不出的界面）→ 不显示
    ///   IsUsable == false   → 有档位，但用户没启用或没配模型          → 不显示
    /// </summary>
    private void ApplyScene()
    {
        // **线程**：这个方法是从 tosu 的接收线程调进来的（OnSnapshotReceived 走 WebSocket 回调），
        // 而窗口位置、窗口可见性这些 WPF 属性**只有 UI 线程能碰** —— 跨线程访问会抛
        // InvalidOperationException，那个异常又会被 WS 循环的 catch 吞掉（只剩一句"连接失败"），
        // 于是窗口行为看起来时对时错、还查不出原因。
        // 所以在入口统一调度一次，方法体里就都能按"我在 UI 线程"来写。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplyScene());
            return;
        }

        var scene = _sceneTracker.Current;
        var profile = _scenes.Get(scene);
        var usable = profile is { IsUsable: true };

        // 角色该多不透明 —— **这一个决定收在 CharacterOpacity 里**，不散在这儿。
        // 现在只有"看得见 / 看不见"，将来要做"透明度跟着血条走"时只改那个类。
        var opacity = CharacterOpacity.Resolve(usable, _sceneTracker.IsLoading);

        // 站位要**按当前游玩模式**挑（决策 50）：mania 是下落式、std 是拟太鼓，
        // 角色该站哪、多大跟着不一样。没单独设过的模式自动用场景自己那份。
        var placement = profile?.PlacementFor(_currentMode);

        DebugLog.Write($"界面表现：{scene} → " + (usable
            ? $"显示角色（穿透 {profile!.Window.ClickThrough}）"
            : "不显示角色") +
            $"，透明度 {opacity:0.##}（加载中={_sceneTracker.IsLoading}，页面就绪={_pageReady}）" +
            (usable ? $"，模式={_currentMode?.ToString() ?? "认不出"}" : ""));

        if (_pageReady)
        {
            SendJson(new
            {
                type = "scene",
                scene = scene.ToString(),
                show = usable,

                // opacity 和 show 是两件事：
                //   show=false    → 这个界面压根不显示角色（没启用 / 认不出）
                //   opacity=0     → 角色还在、位置也没动，只是暂时看不见（加载期间）
                // 后者是刻意选的：不动窗口、不卸载模型，歌一开始恢复成 1 就完了。
                opacity = opacity,

                // 站位只在"要显示"的时候才有意义。不显示时发 null，页面按"维持原样"处理 ——
                // 免得隐藏期间位置被清成 0，下次显示时角色先闪一下再跳回去。
                // （加载期间 show 仍是 true —— 场景还是选歌，站位照发，
                //   这样它藏在哪儿、待会儿就从哪儿显出来，不会跳。）
                character = usable && placement is not null ? new
                {
                    x = placement.X,
                    y = placement.Y,
                    scale = placement.Scale,
                    angle = placement.AngleDegrees
                } : null
            });
        }

        if (profile is not null) ApplyWindowPlacement(scene, profile);

        ApplyMode();      // 穿透/交互跟着模式与当前档位走（见 ShouldClickThrough）
    }

    /// <summary>
    /// 把"这个界面该把窗口摆在哪"应用到窗口上。
    ///
    /// **这里曾经有个 bug（2026-09-19 修）**：原来档位里 X/Y/宽/高 为 null 时解释成
    /// "保持窗口现在待的地方"，而屏幕上只有一个窗口 —— 它现在待的地方就是**上一个界面**的位置。
    /// 结果只给主菜单设过位置时，选歌/打歌/结算全显示在主菜单的位置上；用户在选歌里随手拖一下，
    /// 这个借来的位置就被记成选歌自己的位置，从此固化。
    /// 现在"没设过"= 用**程序默认摆放**，三个界面的位置互不串味。
    ///
    /// 三个来源的优先级在 WindowPlacementResolver 里（纯逻辑、可在控制台测）。
    /// </summary>
    private void ApplyWindowPlacement(GameScene scene, SceneProfile profile)
    {
        var bounds = WindowPlacementResolver.Resolve(
            profile.Window,
            _pendingAdjustments.Get(scene),      // 这一轮刚拖出来的（还没落盘）
            DefaultWindowBounds());

        if (Math.Abs(Width - bounds.Width) > 0.5) Width = bounds.Width;
        if (Math.Abs(Height - bounds.Height) > 0.5) Height = bounds.Height;
        if (Math.Abs(Left - bounds.Left) > 0.5) Left = bounds.Left;
        if (Math.Abs(Top - bounds.Top) > 0.5) Top = bounds.Top;
    }

    /// <summary>
    /// 程序自己的默认摆放：屏幕右侧偏下一点。
    /// 两个地方用它 —— 窗口第一次出现、以及某个界面还没设过位置。
    ///
    /// 用工作区（扣掉任务栏）而不是拖动/吸附用的整个屏幕（见 ActiveArea）：
    /// 默认摆放必须保证窗口**完整可见**，不能被任务栏压住一条。
    /// 用户自己拖到哪儿、吸到哪儿，那是他自己的选择，按整个屏幕算。
    /// </summary>
    private WindowBounds DefaultWindowBounds() => new(
        SystemParameters.WorkArea.Right - _defaultSize.Width - DefaultRightMargin,
        SystemParameters.WorkArea.Top + DefaultTopMargin,
        _defaultSize.Width,
        _defaultSize.Height);

    /// <summary>
    /// 页面就绪时把"当前的常态表情"补发一次。
    /// 理由：程序启动那一刻页面还在加载，那会儿发出的消息直接被丢掉 ——
    /// 不补这一下，角色会一直没有常态表情。
    ///
    /// ★ 2026-09-23：原来它上面还有一个 `SendCurrentScene() => ApplyScene()` 的纯转发方法，
    ///   一并删掉了 —— 调用处直接写 `ApplyScene()` 更少一层跳转。
    /// </summary>
    private void SendCurrentSteady()
    {
        var evt = new ComboEvent(
            Kind: ComboEventKind.RangeChanged,
            Combo: 0,
            PreviousCombo: 0,
            Level: 0,
            Threshold: 0,
            MaxCombo: 0,
            RangeIndex: _tracker.CurrentRangeIndex);

        HandleEvent(evt);
    }

    // ---------------- 连击事件 → 页面 ----------------

    /// <summary>
    /// 上一次在**非打歌**场景收到的原始连击（只给日志去重用）。
    /// 结算/选歌界面里 tosu 会一包一包重复报同一个残留值，不去重就会刷屏。
    /// </summary>
    private int _lastOutsideCombo = -1;

    private void OnComboUpdated(int combo, int maxCombo)
    {
        // ★ 2026-09-23 重写。原来这里是一道"非打歌就整段 return"的门 ——
        //   它确实挡住了"结算→选歌时凭空断连击"，但**连"常态表情该跟着区间走"也一起挡住了**：
        //   角色的常态表情会一直挂在打完那一局的最高档上，退回选歌也不回来。
        //
        //   现在改成两道更准的规矩：
        //     ① 非打歌时**按 combo=0 喂** —— tosu 在这里报的是上一局的残留值
        //        （实测 2026-09-22 抓包 0299 包：state 已经是 5「选歌」，
        //         而 gameplay 段里 combo=85 / score=712897 还挂着，下一包才整体归零）。
        //        那个值对数据本身没错，对界面却没有意义：界面上根本没有"连击"这回事，
        //        按 0 算才能让角色回到"0 连击那一档"的常态表情。
        //     ② 断连击与跨阈值**只在打歌里报**（`inGame` 参数）—— 那两件事
        //        报出来就是"凭空做一次反应"。
        //
        //   顺带：这道门原来还得靠"TosuClient 先抛 Snapshot 再抛 Combo"这个顺序
        //   才能读到同一包的场景。现在仍然依赖它（`_sceneTracker.Current` 是刚更新的），
        //   但即使慢一拍也不会造假事件了 —— 最坏只是区间晚半秒切。
        var scene = _sceneTracker.Current;
        var inGame = scene == GameScene.Playing;

        if (!inGame && combo != _lastOutsideCombo)
        {
            _lastOutsideCombo = combo;
            DebugLog.Write($"[连击] 非打歌（{scene}）收到 combo={combo} → 按 0 处理（只更新常态区间）");
        }
        else if (inGame)
        {
            _lastOutsideCombo = -1;
        }

        var events = _tracker.Update(inGame ? combo : 0, maxCombo, inGame);
        if (events.Count == 0) return;

        if (!_pageReady)
        {
            // 页面还没就绪就丢消息。**记一条日志**：不然表现成"完全没反应"，
            // 而画面上已经不再提示任何东西了（见 ShowStatus 撤掉那一段）。
            DebugLog.Write("页面未就绪，忽略这次触发");
            return;
        }

        foreach (var evt in events) HandleEvent(evt);
    }

    /// <summary>
    /// 把一个事件交给 TriggerResolver 翻译成"该发什么"，再把动作发给页面。
    /// 正常路径和测试热键都走这里 —— 保证"测的"和"真跑的"是同一条路。
    /// 这里只负责"怎么发"（SendJson / 日志），"发什么"由 TriggerResolver 决定。
    /// </summary>
    private void HandleEvent(ComboEvent evt)
    {
        foreach (var action in TriggerResolver.Resolve(evt, _config))
        {
            if (!string.IsNullOrWhiteSpace(action.Expression))
            {
                SendJson(new
                {
                    type = action.Kind switch
                    {
                        TriggerActionKind.Combo => "combo",
                        TriggerActionKind.Miss  => "miss",
                        _                       => "steady"      // 区间变化 → 常态表情
                    },
                    expression = action.Expression,
                    // 这个表情要改哪些参数 —— 页面拿到就能直接往模型上写，不用自己去读文件。
                    // （@ 只是把 C# 关键字 params 当普通标识符用，序列化出来是 "params"）
                    @params = _catalog.ParametersOf(action.Expression),
                    combo = evt.Combo,
                    step = evt.Level,
                    threshold = evt.Threshold
                });
            }

            // 动作（motion）单独一条消息：页面收到就播一次（`case "motion"`）。
            // 和表情完全解耦 —— 表情没播出来，动作照样做
            if (!string.IsNullOrWhiteSpace(action.Action))
                SendJson(new { type = "motion", action = action.Action, reason = action.Note });

            // 语音也是独立的一条：表情没播出来语音照样说（这就是"解耦"落在代码上的样子）
            if (!string.IsNullOrWhiteSpace(action.Emotion))
                SendJson(new { type = "voice", emotion = action.Emotion, reason = action.Note, combo = evt.Combo });

            DebugLog.Write(action.Note +
                           $" → 表情「{action.Expression}」动作「{action.Action}」语音「{action.Emotion}」");
        }
    }

    // ---------------- 运行期拖动窗口（由影子窗口转发过来） ----------------

    /// <summary>
    /// 影子窗口报告"鼠标拖了多远" → 把真正的悬浮窗挪过去，并让影子窗口跟上。
    ///
    /// **拖动过程中不做任何吸附**：窗口要自由跟手，吸不吸等松手时再判（见 SnapToEdge）。
    ///
    /// 只有"不穿透"的界面（选歌 / 结算）才会走到这里：打歌时影子窗口是隐藏的，
    /// 鼠标本来就该留给 osu（想拖就按 Ctrl+Alt+T 进调整模式）。
    ///
    /// 拖动结果目前只在内存里：真正写回配置要配合"停服务时询问是否保存"那套
    /// 待保存调整机制（定稿 §3.6.2），属于下一步。
    /// </summary>
    /// <summary>
    /// 悬浮窗的"活动区域"：**整个屏幕**，而不是工作区（工作区会扣掉任务栏）。
    ///
    /// 为什么不用工作区（用户 2026-09-19 指出，很关键）：
    ///   玩 osu 时游戏是**全屏**的，**任务栏那块地方显示的就是游戏画面** ——
    ///   这时窗口如果只贴到工作区底边，离屏幕底边还差一条任务栏的高度，
    ///   玩家看到的就是"没吸到底"，会当成 bug。
    ///   悬浮窗本来就是给玩游戏时用的，所以按屏幕边缘算才符合实际观感。
    ///
    /// 代价：回到桌面（任务栏露出来）时，窗口拖到最底下会被任务栏压住一条 ——
    ///   但那是"用户确实拖到了屏幕最底"的结果，比"游戏里吸不到底"划算得多。
    ///
    /// 用 VirtualScreen* 而不是 PrimaryScreen*：前者是**所有显示器的并集**
    /// （多屏时窗口跨屏拖动也不用改这段；单屏时它就等于屏幕尺寸）。
    /// </summary>
    private static Rect ActiveArea() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    private void OnInteractionDragged(double dx, double dy)
    {
        // ★ 2026-09-23：接上「位置锁定」。这个字段从 2026-09-20 就存在，
        //   但一直没有任何代码读它 —— 界面上打开它等于没打开。
        //   锁住的是**拖动**：窗口位置不许被拖走；调整模式（Ctrl+Alt+T）里的原生拖动
        //   同理走这里，所以一处就够。
        if (_settings.Overlay.LockPosition)
        {
            DebugLog.Write("位置锁定开着 → 忽略这次拖动");
            return;
        }
        // **拖动过程中把窗口留在屏幕里**（不是吸附，是别让它飘出屏幕找不回来）。
        // 不在边界附近时这段完全不起作用，窗口就是老老实实跟手。
        //
        // 顺带解决了"下边吸不住"：窗口底部不会再越过屏幕下边缘，
        // 于是松手时"离下边多远"是个正常的小数字，贴齐判定才成立
        // （原来鼠标按在窗口偏上位置时，拖到底就已经把窗口**拖出屏幕**了）。
        var area = ActiveArea();
        Left = Math.Clamp(Left + dx, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top + dy, area.Top, Math.Max(area.Top, area.Bottom - Height));

        SyncInteractionWindow();
    }

    /// <summary>
    /// 松手时判定要不要"吸"到屏幕边缘 —— 只有这一次机会判定，拖动过程中不吸。
    ///
    /// 为什么必须是松手时（用户 2026-09-19 反馈）：
    ///   拖动中实时吸的话，窗口会在靠近边缘时被"粘"在边上、和鼠标脱节，
    ///   用户想停在边缘附近却停不住 —— 那是很别扭的手感。放到松手这一刻判定，
    ///   拖动过程就完全是"窗口乖乖跟着手"，落点才做一次纠正。
    ///
    /// 两个来源分开放（定稿 §3.6.5）：
    ///   · **吸不吸** —— 每界面一份（档位里的"自动吸附"），因为可能打歌界面想让它老实待在角落
    ///   · **吸多远** —— 全局手感设置，这里先用 16 DIP（用户要求"稍微小一点，太大像被粘住"）
    ///
    /// 调用点有两个（对应两条拖动路径）：
    ///   · 运行期拖影子窗口 → InteractionWindow 的 DragFinished 事件
    ///   · 调整模式由系统原生拖动 → WM_EXITSIZEMOVE 消息
    ///
    /// 现在只按**主屏幕工作区**算（不含任务栏）。多显示器时应该取"窗口所在那块屏幕"的工作区 ——
    /// 用户目前是单屏，等真有多屏需求再改（不提前写没人用的分支）。
    /// </summary>
    private void SnapToEdge()
    {
        // ★ 2026-09-23：吸附距离改成**读配置**（原来是写死的 `const double SnapDistance = 16`）。
        //   配置里那个字段从 2026-09-20 就存在（`悬浮窗.吸附距离`），但一直没人读 ——
        //   界面上改它等于没改。现在这里接上了，那个字段才算真的"能用"。
        const double FallbackSnapDistance = 16;          // 配置读不出来时的兜底（= 原来的写死值）
        var configured = _settings.Overlay.SnapDistance;
        var snapDistance = configured > 0 ? configured : FallbackSnapDistance;

        var area = ActiveArea();
        var maxLeft = Math.Max(area.Left, area.Right - Width);
        var maxTop = Math.Max(area.Top, area.Bottom - Height);

        // 先在"吸附之前"把窗口收回屏幕内。
        // 这条对**调整模式的系统原生拖动**尤其重要：那条路径不受上面那个 Clamp 管，
        // 用户可以把它拖出屏幕，松手时得先捞回来。
        Left = Math.Clamp(Left, area.Left, maxLeft);
        Top = Math.Clamp(Top, area.Top, maxTop);

        if (_scenes.Get(_sceneTracker.Current)?.Window.SnapToEdges != true) return;

        if (Math.Abs(Left - area.Left) < snapDistance) Left = area.Left;
        else if (Math.Abs(maxLeft - Left) < snapDistance) Left = maxLeft;

        if (Math.Abs(Top - area.Top) < snapDistance) Top = area.Top;
        else if (Math.Abs(maxTop - Top) < snapDistance) Top = maxTop;
    }

    // ---------------- 状态提示：**已撤掉** ----------------
    //
    // ★ 2026-09-23（用户要求："现在有日志了，可以取消掉悬浮窗的弹幕提示了"）
    //
    //   原来这里有个 `ShowStatus(text, autoHide)`，把一句话同时送到两处：
    //     ① WPF 的状态条 —— 其实**基本看不见**（WebView2 是原生窗口，会盖住 WPF 元素，
    //        只有调整模式把页面藏起来时它才露出来）；
    //     ② 发给页面，由页面在**底部弹一条半透明的横条** —— 运行时真正看得见的其实是这一条。
    //   现在两个都不发了，**只留下日志**：所有原来走 ShowStatus 的话都改成
    //   `DebugLog.Write(...)`，内容一条没丢。
    //
    //   为什么撤：① 悬浮窗盖在游戏上，任何文字都是挡视线；
    //             ② 日志（设置界面「日志」页 + 调试模式的日志文件）信息更全，而且**能回溯**；
    //             ③ 出了问题该看的地方是「日志」和「配置体检」，不是让角色旁边飘一行字。
    //   唯一"屏幕上本来什么都没有"的情况（模型没加载出来）由**页面**报 `fatal`，
    //   设置界面的预览区会把它显示在自己那个提示行上 —— 那是用户正看着的窗口。

    // ---------------- 运行模式 / 调整模式 ----------------

    private void ApplyMode()
    {
        // **悬浮窗本身永远穿透**（运行模式下）—— 这是 WebGL 能上屏的前提：
        //   WebView2 内部的原生子窗口一旦被清除 WS_EX_TRANSPARENT，它的 WebGL 层就停止上屏
        //   （症状是"页面文字看得见、角色看不见"，而页面自导快照完好）。
        //   所以运行模式下这个样式**一次都不去动**。
        //   调整模式例外：那时 WebView2 是隐藏的，窗口要能接鼠标才拖得动虚线框。
        var clickThrough = ShouldClickThrough();
        var modeChanged = clickThrough != _clickThroughApplied;
        _clickThroughApplied = clickThrough;

        ApplyExStyleToWindowTree(_hwnd, clickThrough);

        DebugLog.Write($"窗口：模式={(_isRunningMode ? "运行" : "调整")} " +
                       $"穿透={clickThrough} 页面={Web.Visibility} " +
                       $"位置=({Left:0},{Top:0}) 尺寸={Width:0}x{Height:0}" +
                       (modeChanged ? "（穿透状态刚变过）" : ""));

        // WebView2 是原生窗口，WPF 元素盖不到它上面 → 调整时先把它藏起来，露出虚线框
        if (_isRunningMode)
        {
            AdjustLayer.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
        }
        else
        {
            // 提示里读的是**当前界面那一档**的站位：界面感知接管之后，全局那套视图参数
            // 只是老配置的迁移来源，不再是运行时的权威值 —— 还显示它会把改配置的人带偏。
            var character = _scenes.Get(_sceneTracker.Current)?.Character;
            AdjustHint.Text = character is null
                ? "拖动移动 · 右下角缩放"
                : $"拖动移动 · 右下角缩放\n当前界面站位：缩放 {character.Scale:0.00}，左右 {character.X:0.00}，上下 {character.Y:0.00}";
            Web.Visibility = Visibility.Hidden;
            AdjustLayer.Visibility = Visibility.Visible;
        }

        // "能不能点"由影子窗口负责：它不含原生子窗口，WPF 命中测试在它身上才有效。
        // 调整模式下它必须让位 —— 那时是虚线框（本窗口）在接鼠标。
        SyncInteractionWindow(show: _isRunningMode && ShouldInteract());
    }

    /// <summary>
    /// 现在这个界面**该不该能交互**（决定影子窗口显示不显示）。
    ///
    /// 注意这和"悬浮窗穿不穿透"是两件事：悬浮窗永远穿透（否则 WebGL 不上屏），
    /// 而"能不能点"由影子窗口决定。定稿 §四 的交互模式（打歌＝穿透，选歌 / 结算＝可交互）
    /// 落到的就是这里。
    /// </summary>
    private bool ShouldInteract()
        => _scenes.Get(_sceneTracker.Current)?.Window.ClickThrough == false;

    /// <summary>让影子窗口跟着悬浮窗走：位置、尺寸、以及该不该显示</summary>
    private void SyncInteractionWindow(bool? show = null)
    {
        if (_interaction is null) return;

        try
        {
            _interaction.Left = Left;
            _interaction.Top = Top;
            _interaction.Width = Width;
            _interaction.Height = Height;

            if (show is { } want)
            {
                if (want && !_interaction.IsVisible)
                {
                    _interaction.Show();
                    DebugLog.Write($"影子窗口：显示（位置=({Left:0},{Top:0}) 尺寸={Width:0}x{Height:0}）");
                }
                else if (!want && _interaction.IsVisible)
                {
                    _interaction.Hide();
                    DebugLog.Write("影子窗口：隐藏");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("影子窗口同步失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 强制这个分层窗口重新合成一次。
    ///
    /// 为什么需要（2026-09-18 真机踩到的）：
    ///   主窗口是 AllowsTransparency 的**分层窗口**，而 WebView2 内部有两套合成路径 ——
    ///   HTML（DOM）一套、WebGL 画布另一套。**切换穿透状态之后，WebGL 那一层有时不会被
    ///   重新合成**，症状非常迷惑：
    ///     · 页面里的文字看得见（DOM 正常），角色却不见了（WebGL 没合成）
    ///     · 可页面自己导出的画面快照里角色完好无损（渲染是好的，只是没上屏）
    ///   而"打歌界面能看到角色"只是因为刚切过穿透，合成被顺带刷新了。
    ///
    /// 做法：把窗口尺寸碰一下再改回来 —— Windows 会因此重算并重新合成这个窗口，
    /// 同时也会触发页面的 ResizeObserver 重排一次，两套合成路径一起被叫醒。
    /// </summary>
    private void NudgeRepaint()
    {
        try
        {
            // ① 先摇一下 WebView2 控件自己的可见性 —— 这一下直接打在它的上屏通道上。
            //    （只碰窗口尺寸是不够的：那只能让 DWM 重算外层，WebView2 内部那一层
            //      照样可以是"渲染了但不上屏"的状态。）
            if (Web.Visibility == Visibility.Visible)
            {
                Web.Visibility = Visibility.Hidden;
                Web.Visibility = Visibility.Visible;
            }

            // ② 再碰一下窗口尺寸：让页面的 ResizeObserver 重排一次、DWM 重算这个分层窗口
            var w = Width;
            Width = w + 1;
            Width = w;

            DebugLog.Write("穿透状态变了 → 已强制窗口重合成一次");
        }
        catch (Exception ex)
        {
            DebugLog.Write("强制重合成失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 这次该不该点击穿透？三个来源按优先级排：
    ///   · 调整模式（热键解除锁定）→ 一律**不穿透**，否则鼠标都收不到，虚线框没法拖
    ///   · 运行模式 → 穿透。**悬浮窗本身永远穿透**是这个方案的核心：WebView2 内部的
    ///     原生子窗口一旦被清除 WS_EX_TRANSPARENT，WebGL 那层就停止上屏。
    ///   · 于是"能不能交互"完全交给影子窗口（见 ShouldInteract / InteractionWindow）
    /// </summary>
    private bool ShouldClickThrough()
    {
        if (!_isRunningMode) return false;
        return true;
    }

    /// <summary>
    /// 设置点击穿透。
    ///
    /// **顶层窗口按需要切换，子窗口（WebView2 内部那些原生窗口）永远保持穿透** ——
    /// 这条是 2026-09-18 真机踩出来的，别改回去：
    ///   子窗口身上的 WS_EX_TRANSPARENT 一旦被**清除**，WebView2 的 WebGL 那层就不再上屏。
    ///   症状极具迷惑性 —— 页面里的文字（DOM）看得见、角色（WebGL）看不见，
    ///   而页面自己导出的画面快照完好无损（离屏渲染照跑，只是没画到屏幕上）。
    ///   于是表现成"打歌（穿透）正常，选歌 / 结算（不穿透）丢角色"。
    ///
    /// 为什么子窗口保持穿透也够用：鼠标先经过 WebView2 的子窗口（穿透）到达顶层窗口，
    ///   · 打歌：顶层也穿透 → 鼠标一路交给 osu
    ///   · 选歌 / 结算：顶层不穿透 → 鼠标停在顶层窗口上，用户能拖动窗口（"可交互"要的就是这个）
    /// 代价只有一个：选歌时点不到插件窗口盖住的那一片 osu 界面 ——
    ///   窗口只有 430×620，挪开就行，"允许交互"本来就有这个取舍。
    /// </summary>
    private void ApplyExStyleToWindowTree(IntPtr root, bool clickThrough)
    {
        // 顶层：按界面切换穿透；子窗口：**永远保持穿透，别清除**（原因见上面的注释）。
        // 遍历与样式位操作都在 Win32 里，"子窗口必须保持穿透"这条知识留在这里。
        Win32.ForEachInWindowTree(root, h => Win32.SetClickThrough(h, h == root ? clickThrough : true));
    }

    // ---------------- 目光跟随鼠标 ----------------

    /// <summary>
    /// 每 50ms 读一次全局光标位置，换算成窗口内坐标后发给页面（页面调 model.focus 让角色看向鼠标）。
    /// 为什么要绕这一圈：窗口是点击穿透的（这是"不干扰游戏"的前提），WebView2 收不到任何鼠标事件。
    /// </summary>
    private void StartCursorTracking()
    {
        if (!_config.FollowCursor || _cursorTimer is not null) return;

        _cursorTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)      // 20 次/秒，眼睛跟随足够顺滑
        };
        _cursorTimer.Tick += (_, _) =>
        {
            if (!_pageReady || !Win32.TryGetCursorPos(out var p)) return;

            // 屏幕设备像素 → WPF 坐标（自动处理 DPI）→ 窗口内坐标
            var local = PointFromScreen(new Point(p.X, p.Y));

            if (!double.IsNaN(_lastCursorSent.X) &&
                Math.Abs(local.X - _lastCursorSent.X) < 1.5 &&
                Math.Abs(local.Y - _lastCursorSent.Y) < 1.5) return;   // 没移动就不发，省开销

            _lastCursorSent = local;
            SendJson(new { type = "cursor", x = Math.Round(local.X), y = Math.Round(local.Y) });
        };
        _cursorTimer.Start();
    }

    // ---------------- 热键与拖动 ----------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY:
                switch (wParam.ToInt32())
                {
                    case HOTKEY_TOGGLE_MODE:
                        _isRunningMode = !_isRunningMode;
                        ApplyMode();
                        // 退出调整模式后，WebView2 的子窗口可能已重建，重新应用一次穿透
                        if (_isRunningMode) Dispatcher.BeginInvoke(() => ApplyExStyleToWindowTree(_hwnd, true));
                        handled = true;
                        break;

                    case HOTKEY_QUIT:
                        Close();
                        handled = true;
                        break;
                }
                break;

            case Win32.WM_EXITSIZEMOVE:
                // 系统原生拖动 / 缩放结束了（= 用户松开鼠标）→ 这才是判定"要不要吸到边上"的时刻。
                // 调整模式走的是系统原生拖动（WM_NCHITTEST 返回 HTCAPTION），
                // 所以这条消息是那条路径上唯一的"松手"信号。
                SnapToEdge();
                RecordWindowChange();      // 吸完之后的最终位置才是要保存的
                break;

            case Win32.WM_NCHITTEST:
                if (_isRunningMode) break;      // 运行模式下窗口是穿透的，系统不会问到这里

                int screenX = (short)(lParam.ToInt32() & 0xFFFF);
                int screenY = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
                Point local = PointFromScreen(new Point(screenX, screenY));
                bool inCorner = local.X >= ActualWidth - ResizeGripSize
                             && local.Y >= ActualHeight - ResizeGripSize;

                handled = true;
                return new IntPtr(inCorner ? Win32.HTBOTTOMRIGHT : Win32.HTCAPTION);
        }

        return IntPtr.Zero;
    }

    // ---------------- 运行期调整落盘（窗口位置/尺寸） ----------------

    /// <summary>
    /// 待保存的窗口调整。
    ///
    /// 运行期拖出来的窗口状态先攒在内存里**不立刻写盘**（定稿 §3.6.2）：
    /// 用户常常只是"临时挪开一下"，自动保存会把配置弄脏；而且拖动是连续动作，
    /// 每帧写盘既抖磁盘、又可能写坏文件。
    ///
    /// 现在有**三个**时机问"要不要存"（定稿那一节写的三条，2026-09-20 才凑齐）：
    ///   ① 关掉悬浮窗时问一次 ② 设置界面的「运行期调整」页主动保存 ③ 退出程序
    ///
    /// **用的是全程序共用那一个**（`App.WindowAdjustments`），不是自己 new 一个 ——
    /// 设置界面那一页要看见同一份，否则它永远显示"没有待保存的调整"。
    ///
    /// "攒"和"写"这两件事都在两个纯逻辑类里（它们不依赖 WPF，所以能在控制台测）：
    ///   PendingWindowAdjustments —— 攒着待保存的调整
    ///   ConfigFileWriter        —— 把调整写回当前档案
    /// </summary>
    private readonly PendingWindowAdjustments _pendingAdjustments = App.WindowAdjustments;

    /// <summary>把"当前界面的窗口状态"记进待保存清单（拖动结束、松手之后调）</summary>
    private void RecordWindowChange()
    {
        //2026.9.25bug:解决了只是触摸悬浮窗就会标脏的问题
        var scene = _sceneTracker.Current;
        var saved = _scenes.Get(scene)?.Window;
        if (saved is not null
            && saved.X is { } sx
            && saved.Y is { } sy
            && saved.Width is { } sw
            && saved.Height is { } sh
            && Math.Abs(sx - Left) < 0.5
            && Math.Abs(sy - Top) < 0.5
            && Math.Abs(sw - Width) < 0.5
            && Math.Abs(sh - Height) < 0.5)
        {
            DebugLog.Write($"窗口位置与配置一致 → 不记（{scene}）");
            return;
        }

        // 「位置调整后自动保存」（默认关，决策 6：默认询问而不是默认保存）。
        // ★ 这个设置项一直是死的：存了、界面能改、**没有任何代码读它**（2026-09-20 接上）。
        //   勾上就当场落盘，不攒、退出时也不再问。
        //   注意是"松手之后记一次"，不是每帧 —— 拖动过程中每帧写盘既抖磁盘又可能写坏文件。
        if (_settings.General.AutoSavePlacement)
        {
            var one = new Dictionary<GameScene, WindowBounds>
            {
                [scene] = new WindowBounds(Left, Top, Width, Height)
            };


            try
            {
                var written = ConfigFileWriter.WriteWindowBounds(_configPath, one);
                DebugLog.Write($"已自动保存 {scene} 的窗口状态（{written} 个界面）");
            }
            catch (Exception ex)
            {
                // 自动保存失败不弹窗打断用户 —— 记进日志，界面上还有「运行期调整」页可以手动存
                DebugLog.Write("自动保存窗口状态失败：" + ex.Message);
            }

            return;
        }

        _pendingAdjustments.Record(scene, Left, Top, Width, Height);
        DebugLog.Write($"待保存：{scene} 的窗口状态 = ({Left:0},{Top:0}) {Width:0}x{Height:0}");
    }

    /// <summary>
    /// 把待保存的窗口调整写进**当前档案**。
    ///
    /// 悬浮窗关闭时问"要不要保存"走的是这里；设置界面的
    /// 「运行期调整 → 保存到当前档案」也走这里 —— **一条路，两处入口**。
    /// （设置页那次其实可以直接调 <c>ConfigFileWriter</c>：待保存层是共用的，
    ///   `TakeAll` 对两个窗口是同一个动作。这个方法是给关闭时的询问用的。）
    /// </summary>
    public int SavePendingWindowChanges()
    {
        var written = ConfigFileWriter.WriteWindowBounds(_configPath, _pendingAdjustments.TakeAll());
        DebugLog.Write($"窗口状态已保存到档案「{_settings.Profile.Current}」（{written} 个界面）");
        return written;
    }

    /// <summary>
    /// 丢掉待保存的窗口调整，并把当前界面的窗口**当场摆回**已保存的位置。
    ///
    /// 少了后半句，用户点了「放弃」却看见窗口还待在他刚拖的地方 —— 那看起来就是没生效。
    /// （不去动别的界面：屏幕上只有一个窗口，"别的界面摆在哪"要等切过去才知道。）
    /// </summary>
    public void DiscardPendingWindowChanges()
    {
        _pendingAdjustments.TakeAll();
        ReapplyCurrentWindowPlacement();
        DebugLog.Write("已放弃待保存的窗口调整");
    }

    /// <summary>按"配置里已保存的位置"重新摆一次当前界面的窗口</summary>
    private void ReapplyCurrentWindowPlacement()
    {
        var scene = _sceneTracker.Current;
        var profile = _scenes.Get(scene);

        if (profile is not null) ApplyWindowPlacement(scene, profile);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_pendingAdjustments.Any && !AskSaveWindowChanges())
        {
            e.Cancel = true;      // 用户点了"取消"：别退
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// 退出前问一次要不要保存窗口位置。
    /// 返回 false 表示用户选了"取消"（调用方应当中止关闭）。
    ///
    /// 提示里**带上界面名**（定稿 §3.6.2 的要求）—— 否则用户不知道改的是哪一个界面的窗口。
    /// </summary>
    private bool AskSaveWindowChanges()
    {
        // 界面名的顺序由 PendingWindowAdjustments 固定（不跟着记录先后变）
        var names = string.Join("、", _pendingAdjustments.Scenes
            .Select(SceneProfiles.KeyOf)
            .Where(k => k is not null));

        // 带 owner 调用：本窗口是 Topmost，弹窗跟着它才会显示在全屏游戏之上，
        // 否则用户按退出热键后会「什么都没发生」（弹窗其实在游戏后面等着他）。
        var answer = MessageBox.Show(this,
            $"{names}界面的悬浮窗位置已调整，是否保存？\n\n" +
            "保存后下次启动仍然停在这个位置。",
            "osu-live2d-overlay",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;

        try
        {
            SavePendingWindowChanges();
        }
        catch (Exception ex)
        {
            DebugLog.Write("保存窗口状态失败：" + ex.Message);
            MessageBox.Show(this, "保存失败：" + ex.Message + "\n\n配置没有被改动。",
                            "osu-live2d-overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cursorTimer?.Stop();

        if (_hwnd != IntPtr.Zero)
        {
            Win32.UnregisterHotkey(_hwnd, HOTKEY_TOGGLE_MODE);
            Win32.UnregisterHotkey(_hwnd, HOTKEY_QUIT);
        }

        base.OnClosed(e);
    }
}