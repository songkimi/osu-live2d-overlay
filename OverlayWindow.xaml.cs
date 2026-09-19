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
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace OsuLive2dOverlay;

public partial class OverlayWindow : Window
{
    // ---- 窗口扩展样式 ----
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // ---- 窗口消息 ----
    private const int WM_HOTKEY = 0x0312;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_EXITSIZEMOVE = 0x0232;     // 系统原生拖动/缩放结束（= 用户松开鼠标）
    private const int HTCAPTION = 2;
    private const int HTBOTTOMRIGHT = 17;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_T = 0x54;
    private const uint VK_Q = 0x51;
    private const uint VK_R = 0x52;
    private const uint VK_V = 0x56;
    private const uint VK_1 = 0x31;
    private const uint VK_2 = 0x32;
    private const uint VK_3 = 0x33;
    private const uint VK_4 = 0x34;
    private const int HOTKEY_TOGGLE_MODE = 9001;
    private const int HOTKEY_QUIT = 9002;
    private const int HOTKEY_RELOAD = 9003;
    private const int HOTKEY_TEST_1 = 9004;
    private const int HOTKEY_TEST_2 = 9005;
    private const int HOTKEY_TEST_3 = 9006;
    private const int HOTKEY_TEST_RANGE = 9008;
    private const int HOTKEY_TEST_VOICE = 9007;
    private const double ResizeGripSize = 18;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // ---- 目光跟随：读全局光标位置（窗口点击穿透时 WebView2 收不到鼠标事件）----
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly PluginConfig _config;
    private readonly TosuClient _client = new();
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
    /// 都是配置的事（属于将来的 UI 层），这个类一概不管。
    /// </summary>
    private readonly GameStateTracker _sceneTracker = new();

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isRunningMode = true;

    /// <summary>
    /// 上一次真正应用到窗口上的穿透状态。用来判断"这次的穿透状态是不是变了" ——
    /// 只有真变了才需要强制重合成一次（见 NudgeRepaint）。
    /// 初值 true 与启动时"还不知道在哪个界面"的默认选择一致，免得启动就抖一下。
    /// </summary>
    private bool _clickThroughApplied = true;

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
    private readonly List<string> _voiceEmotionNames = new();   // 测试热键按顺序试听用
    private int _voiceTestIndex;

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

        var exeDir = AppContext.BaseDirectory;
        _config = PluginConfig.Load(Path.Combine(exeDir, "config.json"));
        _webDir = Path.Combine(exeDir, "web");
        // 常态区间来自配置：省略「最大」表示"以上"，补成 int.MaxValue 交给状态机（它只认闭区间）
        _tracker = new ComboTracker(
            _config.ComboTriggers.Select(t => t.Threshold),
            _config.Ranges.Select(r => new ComboRange(r.Min, r.Max ?? int.MaxValue)));

        // 界面档位：配置加载时已经保证非空（老配置会被迁移成四档），这里只是兜底
        _scenes = _config.Scenes ?? new SceneProfiles();

        DebugLog.Start(_config.DebugMode);

        // 配置体检：门槛配反了不会崩，但"小额"那一档就永远轮不到 —— 在日志里说一声，
        // 免得改数字的人（包括我自己）对着配置猜半天。
        if (_config.Miss.BigThreshold > 0 && _config.Miss.BigThreshold <= _config.Miss.SmallThreshold)
            DebugLog.Write($"配置提醒：大额门槛({_config.Miss.BigThreshold}) " +
                           $"应大于小额门槛({_config.Miss.SmallThreshold})，否则小额那一档用不到");

        // 语音是"配置 + 文件系统"的事，和 WebView2 无关 —— 所以在构造时就解析好。
        // 这样即使浏览器那一层出问题，也能立刻知道是"语音配错了"还是"页面没起来"。
        _voicePayload = BuildVoicePayload();

        _client.ComboUpdated += OnComboUpdated;
        _client.SnapshotReceived += OnSnapshotReceived;

        // tosu 断了（osu 退出 / tosu 自己关了）→ 界面状态回到"不知道"。
        // 不这么做的话，角色会停在上一个界面不动 —— "关掉游戏、角色还挂在屏幕上"就是这个原因：
        // 断开之后没有任何数据再推过来，状态机自己永远不会变。
        _client.Disconnected += () =>
        {
            _sceneTracker.Update(null, false);
            ApplyScene();
        };
        _client.StatusChanged += text =>
        {
            DebugLog.Write("tosu：" + text);
            Dispatcher.Invoke(() => ShowStatus(text, autoHide: false));
        };
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
        RegisterHotKey(_hwnd, HOTKEY_TOGGLE_MODE, MOD_CONTROL | MOD_ALT, VK_T);
        RegisterHotKey(_hwnd, HOTKEY_QUIT, MOD_CONTROL | MOD_ALT, VK_Q);
        RegisterHotKey(_hwnd, HOTKEY_RELOAD, MOD_CONTROL | MOD_ALT, VK_R);
        // 测试热键：不开游戏也能验证"消息 → 表情/语音"这一段通不通
        RegisterHotKey(_hwnd, HOTKEY_TEST_1, MOD_CONTROL | MOD_ALT, VK_1);
        RegisterHotKey(_hwnd, HOTKEY_TEST_2, MOD_CONTROL | MOD_ALT, VK_2);
        RegisterHotKey(_hwnd, HOTKEY_TEST_3, MOD_CONTROL | MOD_ALT, VK_3);
        RegisterHotKey(_hwnd, HOTKEY_TEST_RANGE, MOD_CONTROL | MOD_ALT, VK_4);
        RegisterHotKey(_hwnd, HOTKEY_TEST_VOICE, MOD_CONTROL | MOD_ALT, VK_V);
        RegisterHotKey(_hwnd, HOTKEY_TEST_RANGE, MOD_CONTROL | MOD_ALT, VK_4);

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
                AdditionalBrowserArguments =
                    "--enable-unsafe-swiftshader --autoplay-policy=no-user-gesture-required " +
                    "--disable-direct-composition " +
                    "--disable-features=CalculateNativeWinOcclusion"
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
                ShowStatus("找不到模型目录：" + _config.Model.Directory, autoHide: false);

            // 语音目录：解析成"情绪 → 地址"的表，随 init 一起发给页面（已在构造时算好）
            if (_voiceDir is not null)
                DebugLog.Write($"语音目录已映射：{_voiceDir}（{_voiceEmotionNames.Count} 类情绪）");

            // 先把模型扫一遍 —— 配置里引用的是"标识"，补丁要靠这份扫描结果把标识翻回文件路径
            var scan = ModelScanner.Scan(_config.Model.Directory, _config.Model.Entry);
            foreach (var problem in scan.Problems) DebugLog.Write("扫描：" + problem);

            // 表情清单：把每个 exp3 文件里"要改哪些参数"读进来（页面合成时要用），
            // 顺带拿 cdi3 里的名字给它起个人话名字（"3.exp3" → "星星眼"）
            _catalog = ExpressionCatalog.Build(scan, _config.Model.Directory);
            foreach (var problem in _catalog.Problems) DebugLog.Write("表情：" + problem);
            if (scan.Expressions.Count > 0)
            {
                DebugLog.Write($"表情清单 {scan.Expressions.Count} 个，例如 " +
                               string.Join("、", scan.Expressions.Take(5)
                                   .Select(r => $"{r.Id} → {_catalog.DisplayName(r.Id)}")));
            }

            // 生成"补过资源"的模型定义到临时目录，再把四个目录映射成虚拟主机
            var patchDir = Path.Combine(Path.GetTempPath(), "osu-live2d-overlay");
            try
            {
                var patch = ModelHost.WritePatchedModel(_config, scan, patchDir);

                // 配置里写了模型里没有的标识（多半是打错字）—— 这是"不修就没法用"，必须说出来
                foreach (var problem in patch.Problems)
                {
                    DebugLog.Write("补丁：" + problem);
                    ShowStatus(problem, autoHide: false);
                }

                ShowStatus($"模型已补丁：{scan.Expressions.Count} 个表情 / {scan.Motions.Count} 个动作 → {Path.GetFileName(patch.Path)}",
                           autoHide: true);
            }
            catch (Exception ex)
            {
                ShowStatus("生成模型补丁失败：" + ex.Message, autoHide: false);
            }

            ModelHost.SetupVirtualHosts(_webDir, _config.Model.Directory, patchDir, _voiceDir,
                (host, dir) => core.SetVirtualHostNameToFolderMapping(host, dir, CoreWebView2HostResourceAccessKind.Allow));

            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, args) =>
            {
                DebugLog.Write("页面导航完成：成功=" + args.IsSuccess + " " + args.WebErrorStatus);
                if (!args.IsSuccess)
                    ShowStatus("页面加载失败：" + args.WebErrorStatus, autoHide: false);
                else
                    SendInit();
            };

            var startUrl = $"https://{ModelHost.AppHost}/index.html";
            DebugLog.Write("开始导航：" + startUrl);
            core.Navigate(startUrl);
        }
        catch (Exception ex)
        {
            DebugLog.Write("WebView2 初始化异常：" + ex);
            ShowStatus("WebView2 初始化失败：" + ex.Message, autoHide: false);
        }
    }

    /// <summary>把模型地址、待机动作、视图参数、表情持续时间、语音表告诉页面</summary>
    private void SendInit()
    {
        // 用补丁目录里的模型定义（它内部把贴图/动作/表情都指向了 model.local 的绝对地址）
        var url = $"https://{ModelHost.PatchHost}/{ModelHost.PatchedFileName}";

        SendJson(new
        {
            type = "init",
            modelUrl = url,
            // 「待机动作」填的是标识（组名）。未注册的（VTS 成品的 待机.motion3）会被补丁注册成
            // 同名组，已注册的（官方模型的 Idle）直接用 —— 两种情况页面调用的方式完全一样。
            idle = _config.Model.IdleGroup,
            view = new { zoom = _config.View.Zoom, offsetY = _config.View.OffsetY, offsetX = _config.View.OffsetX },
            expressionDurationMs = _config.Performance.ExpressionDurationMs,
            debug = _config.DebugMode,
            voice = _voicePayload,
            // 常驻层：用户勾的"一直在"的形象元素（鞋子、高光…）。
            // 整批发给页面，之后由页面自己管生死；空列表就什么都不挂。
            persistent = _config.PersistentParts
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => new { id = id.Trim(), @params = _catalog.ParametersOf(id) })
                .ToList(),
            mouth = new
            {
                enabled = _config.Mouth.Enabled,
                parameter = _config.Mouth.Parameter,
                strength = Math.Clamp(_config.Mouth.Strength, 0.0, 1.0)
            }
        });
    }

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

        _voiceEmotionNames.Clear();
        _voiceEmotionNames.AddRange(emotions.Select(e => e.Name));

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
            ShowStatus("向页面发消息失败：" + ex.Message, autoHide: false);
        }
    }

    // ---------------- 测试触发（Ctrl+Alt+1/2/3）----------------

    /// <summary>测试用：按配置里第 index 条连击触发造一个 Step 事件，走正式处理路径</summary>
    private void SendTestCombo(int index)
    {
        var trigger = _config.ComboTriggers.ElementAtOrDefault(index);
        if (trigger is null)
        {
            ShowStatus($"配置里没有第 {index + 1} 条连击触发", autoHide: false);
            return;
        }

        var evt = new ComboEvent(
            Kind: ComboEventKind.Step,
            Combo: trigger.Threshold,
            PreviousCombo: Math.Max(0, trigger.Threshold - 1),
            Level: index + 1,
            Threshold: trigger.Threshold,
            MaxCombo: trigger.Threshold,
            RangeIndex: 0);

        HandleEvent(evt);
        ShowStatus($"[测试] 模拟跨过 {trigger.Threshold} 连击", autoHide: true);
    }

    private int _missTestIndex;

    /// <summary>
    /// 测试用：轮流模拟"够大额 / 够小额 / 还没到门槛"三种断连击。
    /// 造出事件后交给 HandleEvent —— 和真实游戏走同一条路，所以门槛对不对连按三下就知道。
    /// </summary>
    private void SendTestMiss()
    {
        int previous = (_missTestIndex++ % 3) switch
        {
            0 => _config.Miss.BigThreshold + 100,                 // 够大额
            1 => _config.Miss.SmallThreshold + 5,                 // 够小额
            _ => Math.Max(0, _config.Miss.SmallThreshold - 1)     // 还没到门槛 → 应该毫无反应
        };

        var evt = new ComboEvent(
            Kind: ComboEventKind.Break,
            Combo: 0,
            PreviousCombo: previous,
            Level: 0,
            Threshold: 0,
            MaxCombo: previous,
            RangeIndex: -1);

        HandleEvent(evt);
        ShowStatus($"[测试] 模拟断之前 {previous} 连击", autoHide: true);
    }

    /// <summary>测试用：按顺序轮流试听每一类情绪（每按一次换一类，方便把语音文件都过一遍）</summary>
    private void SendTestVoice()
    {
        if (_voiceEmotionNames.Count == 0)
        {
            ShowStatus("没有可用的语音情绪，检查 config.json 的「语音」", autoHide: false);
            return;
        }

        var name = _voiceEmotionNames[_voiceTestIndex % _voiceEmotionNames.Count];
        _voiceTestIndex++;

        SendJson(new { type = "voice", emotion = name, reason = "测试试听" });
        ShowStatus($"[测试] 语音情绪 → {name}（再按一次听下一类）", autoHide: true);
    }
    private int _rangeTestIndex;
    private static readonly int[] RangeTestCombos = { 10, 60, 110, 170, 300 };

    /// <summary>
    /// 测试用：把连击数依次推过几个值，正好落在各个常态区间里。
    /// 走的是和真实游戏完全同一条路（同一个状态机、同一个 HandleEvent），
    /// 只是数据是自己造的 —— 所以不开游戏也能验证"区间 → 常态表情"这整条链。
    /// </summary>
    private void SendTestRange()
    {
        var combo = RangeTestCombos[_rangeTestIndex % RangeTestCombos.Length];
        _rangeTestIndex++;

        // 这里故意**绕过** OnComboUpdated 那道"只在打歌时喂"的门：
        // 测试热键的目的就是不开游戏也能验证"区间 → 常态表情"这一整条链。
        var events = _tracker.Update(combo, combo);
        foreach (var evt in events) HandleEvent(evt);

        ShowStatus($"[测试] 连击推到 {combo} → {events.Count} 个事件", autoHide: true);
    }

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
                    DebugLog.Write("页面就绪（模型已加载）");
                    ShowStatus("模型已就绪" + (DebugLog.FilePath.Length > 0 ? $"（调试日志：{DebugLog.FilePath}）" : ""),
                               autoHide: true);
                    SendCurrentSteady();     // 页面刚起来，把当前的常态表情补给它
                    SendCurrentScene();      // 以及"现在在哪个界面"
                    StartCursorTracking();
                    break;

                case "log":
                    DebugLog.Write("页面：" + text);
                    ShowStatus(text ?? "", autoHide: true);
                    break;

                case "error":
                    DebugLog.Write("页面错误：" + text);
                    ShowStatus("页面错误：" + text, autoHide: false);
                    break;

                // 排查用：页面导出的画面快照（base64 PNG）→ 存成文件。
                // "看不见角色"这类问题，看一张图比看一串数字快得多。
                // 只在调试模式下收：玩家机器上不该凭空多出一个每次都写文件的通道。
                case "snapshot":
                    if (_config.DebugMode &&
                        root.TryGetProperty("dataUrl", out var shot) &&
                        shot.GetString() is { } dataUrl)
                        SaveSnapshot(dataUrl);
                    break;
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
        // gameplay.gameMode == 3 表示"已经进游戏场景"（暂停时它也是 3）——
        // 这是实测出来的判据：加载期间它是 0，所以能精确地把"还在加载"挡在外面，
        // 不用像原先设想的那样"等 800 毫秒看状态稳不稳定"。
        var scene = _sceneTracker.Update(snapshot.MenuState, snapshot.GameMode == 3);
        if (scene is null) return;                   // 状态没变（含"还在加载"），什么都不用做

        DebugLog.Write($"界面切换：{scene.Value}（menu.state={Text(snapshot.MenuState)}，" +
                       $"gameMode={Text(snapshot.GameMode)}）");

        // 进入打歌 = 新的一局开始了 → 连击记忆清零。
        // 不清的后果很实在：上一局打到 1399 结束，新局从 0 重新数，
        // "0 < 1399" 会被连击状态机判成断连击 → 开局瞬间做一次假的失误反应。
        if (scene.Value == GameScene.Playing)
        {
            _tracker.Reset();
            DebugLog.Write("新的一局：连击记忆已清零");
        }

        ApplyScene();
    }

    /// <summary>把可空的 tosu 字段打成能读的日志文字（"缺失" = 这一包里根本没读到）</summary>
    private static string Text(int? value) => value?.ToString() ?? "缺失";

    /// <summary>
    /// 把页面发来的画面快照（base64 data URL）存成 PNG，放在和日志同一个目录里。
    /// 只在排查时用 —— 页面每次切界面都会顺手导一张，出问题时打开就能看到"当时画的是什么"。
    /// </summary>
    private static void SaveSnapshot(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return;

            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            var dir = Path.Combine(Path.GetTempPath(), "osu-live2d-overlay");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"snapshot-{DateTime.Now:HHmmss}.png");
            File.WriteAllBytes(path, bytes);
            DebugLog.Write("画面快照已保存：" + path);
        }
        catch (Exception ex)
        {
            DebugLog.Write("画面快照保存失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把"当前界面该长什么样"落到页面和窗口上。
    /// 两个入口都走这里：场景真的变了（OnSnapshotReceived），以及页面刚就绪要补发（SendCurrentScene）。
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

        DebugLog.Write($"界面表现：{scene} → " + (usable
            ? $"显示角色（模型 {profile!.Model}｜穿透 {profile.Window.ClickThrough}）"
            : "不显示角色") + $"（页面就绪={_pageReady}）");

        if (_pageReady)
        {
            SendJson(new
            {
                type = "scene",
                scene = scene.ToString(),
                show = usable,
                // 站位只在"要显示"的时候才有意义。不显示时发 null，页面按"维持原样"处理 ——
                // 免得隐藏期间位置被清成 0，下次显示时角色先闪一下再跳回去。
                character = usable ? new
                {
                    x = profile!.Character.X,
                    y = profile.Character.Y,
                    scale = profile.Character.Scale,
                    angle = profile.Character.AngleDegrees
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
    /// 页面就绪时把"当前界面该长什么样"补发一次。
    /// 理由和常态表情那次一样：程序启动那一刻页面还在加载，消息直接被丢掉 ——
    /// 不补这一下，页面会一直不知道自己该按哪个界面表现。
    /// </summary>
    private void SendCurrentScene() => ApplyScene();

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

    private void OnComboUpdated(int combo, int maxCombo)
    {
        // 门：连击状态机**只在打歌中被喂数据**。
        //
        // 为什么非要有这道门：Update 判断"断连击"的唯一依据是 **combo 比上次小**，
        // 而这个判断只在打歌中成立。打歌之外 tosu 照样报连击 ——
        // 结算画面停在最终连击值（1399），退出到选歌那一刻变成 0。
        // 数字确实变小了，但那不是断连击，只是这条数据归零。
        // 放进状态机就会凭空造出一个 Break → 失误表情/语音在选歌界面乱触发。
        //
        // 这里能直接读 Current，靠的是 TosuClient 的投递顺序：
        // 每一包都是**先抛 SnapshotReceived、再抛 ComboUpdated**（见 TosuClient.RunAsync），
        // 所以轮到连击时，界面状态已经是同一包数据解析出来的最新值，不会慢一拍。
        if (_sceneTracker.Current != GameScene.Playing) return;

        var events = _tracker.Update(combo, maxCombo);
        if (events.Count == 0) return;

        if (!_pageReady)
        {
            // 页面还没就绪就丢消息，并在界面上说清楚（否则会表现成"完全没反应"）
            ShowStatus("页面未就绪，忽略这次触发", autoHide: true);
            return;
        }

        foreach (var evt in events) HandleEvent(evt);
    }

    /// <summary>
    /// 把一个事件交给 TriggerResolver 翻译成"该发什么"，再把动作发给页面。
    /// 正常路径和测试热键都走这里 —— 保证"测的"和"真跑的"是同一条路。
    /// 这里只负责"怎么发"（SendJson / 日志 / 状态栏），"发什么"由 TriggerResolver 决定。
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

            // 动作（motion）单独一条消息 —— 页面还没支持，先发着，页面忽略即可
            if (!string.IsNullOrWhiteSpace(action.Action))
                SendJson(new { type = "motion", action = action.Action, reason = action.Note });

            // 语音也是独立的一条：表情没播出来语音照样说（这就是"解耦"落在代码上的样子）
            if (!string.IsNullOrWhiteSpace(action.Emotion))
                SendJson(new { type = "voice", emotion = action.Emotion, reason = action.Note, combo = evt.Combo });

            DebugLog.Write(action.Note +
                           $" → 表情「{action.Expression}」动作「{action.Action}」语音「{action.Emotion}」");

            Dispatcher.Invoke(() => ShowStatus(action.Note, autoHide: true));
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
        const double SnapDistance = 16;                  // DIP，全局手感值
        var area = ActiveArea();
        var maxLeft = Math.Max(area.Left, area.Right - Width);
        var maxTop = Math.Max(area.Top, area.Bottom - Height);

        // 先在"吸附之前"把窗口收回屏幕内。
        // 这条对**调整模式的系统原生拖动**尤其重要：那条路径不受上面那个 Clamp 管，
        // 用户可以把它拖出屏幕，松手时得先捞回来。
        Left = Math.Clamp(Left, area.Left, maxLeft);
        Top = Math.Clamp(Top, area.Top, maxTop);

        if (_scenes.Get(_sceneTracker.Current)?.Window.SnapToEdges != true) return;

        if (Math.Abs(Left - area.Left) < SnapDistance) Left = area.Left;
        else if (Math.Abs(maxLeft - Left) < SnapDistance) Left = maxLeft;

        if (Math.Abs(Top - area.Top) < SnapDistance) Top = area.Top;
        else if (Math.Abs(maxTop - Top) < SnapDistance) Top = maxTop;
    }

    // ---------------- 状态提示（会被 WebView2 遮住，所以只在调整模式/出错时看得到） ----------------

    private System.Windows.Threading.DispatcherTimer? _statusTimer;

    /// <summary>
    /// 状态提示：既写进 WPF 的状态栏（调整模式下可见），也发给页面显示
    /// —— 因为 WebView2 会盖住 WPF 元素，运行时只有页面里的提示看得见。
    /// </summary>
    private void ShowStatus(string text, bool autoHide)
    {
        DebugLog.Write("提示：" + text);
        StatusText.Text = text;
        StatusBar.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        if (_pageReady)
            SendJson(new { type = "status", text, keep = !autoHide });

        _statusTimer?.Stop();
        if (!autoHide) return;

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            StatusBar.Visibility = Visibility.Collapsed;
        };
        _statusTimer.Start();
    }

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
        void Apply(IntPtr h, bool transparent)
        {
            if (h == IntPtr.Zero) return;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            ex |= WS_EX_NOACTIVATE;
            if (transparent) ex |= WS_EX_TRANSPARENT;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(h, GWL_EXSTYLE, ex);
        }

        Apply(root, clickThrough);                                                          // 顶层：按界面切换
        EnumChildWindows(root, (h, _) => { Apply(h, true); return true; }, IntPtr.Zero);     // 子窗口：永远穿透，别清除
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
            if (!_pageReady || !GetCursorPos(out var p)) return;

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
            case WM_HOTKEY:
                switch (wParam.ToInt32())
                {
                    case HOTKEY_TOGGLE_MODE:
                        _isRunningMode = !_isRunningMode;
                        ApplyMode();
                        // 退出调整模式后，WebView2 的子窗口可能已重建，重新应用一次穿透
                        if (_isRunningMode) Dispatcher.BeginInvoke(() => ApplyExStyleToWindowTree(_hwnd, true));
                        handled = true;
                        break;

                    case HOTKEY_RELOAD:
                        Web.CoreWebView2?.Reload();
                        handled = true;
                        break;

                    // ---- 测试热键：模拟连击/断连击事件 ----
                    case HOTKEY_TEST_1:
                        SendTestCombo(0);
                        handled = true;
                        break;

                    case HOTKEY_TEST_2:
                        SendTestCombo(1);
                        handled = true;
                        break;

                    case HOTKEY_TEST_3:
                        SendTestMiss();
                        handled = true;
                        break;

                    case HOTKEY_TEST_VOICE:
                        SendTestVoice();
                        handled = true;
                        break;

                    case HOTKEY_TEST_RANGE:


                    case HOTKEY_QUIT:
                        Close();
                        handled = true;
                        break;
                }
                break;

            case WM_EXITSIZEMOVE:
                // 系统原生拖动 / 缩放结束了（= 用户松开鼠标）→ 这才是判定"要不要吸到边上"的时刻。
                // 调整模式走的是系统原生拖动（WM_NCHITTEST 返回 HTCAPTION），
                // 所以这条消息是那条路径上唯一的"松手"信号。
                SnapToEdge();
                RecordWindowChange();      // 吸完之后的最终位置才是要保存的
                break;

            case WM_NCHITTEST:
                if (_isRunningMode) break;      // 运行模式下窗口是穿透的，系统不会问到这里

                int screenX = (short)(lParam.ToInt32() & 0xFFFF);
                int screenY = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
                Point local = PointFromScreen(new Point(screenX, screenY));
                bool inCorner = local.X >= ActualWidth - ResizeGripSize
                             && local.Y >= ActualHeight - ResizeGripSize;

                handled = true;
                return new IntPtr(inCorner ? HTBOTTOMRIGHT : HTCAPTION);
        }

        return IntPtr.Zero;
    }

    // ---------------- 运行期调整落盘（窗口位置/尺寸） ----------------

    /// <summary>
    /// 运行期拖出来的窗口状态，先攒在内存里**不立刻写盘**（定稿 §3.6.2）：
    /// 用户常常只是"临时挪开一下"，自动保存会把配置弄脏；而且拖动是连续动作，
    /// 每帧写盘既抖磁盘、又可能写坏文件。所以只在**退出程序时问一次**。
    ///
    /// "攒"和"写"这两件事都在两个纯逻辑类里（它们不依赖 WPF，所以能在控制台测）：
    ///   PendingWindowAdjustments —— 攒着待保存的调整
    ///   ConfigFileWriter        —— 把调整写回 config.json
    /// </summary>
    private readonly PendingWindowAdjustments _pendingAdjustments = new();

    /// <summary>把"当前界面的窗口状态"记进待保存清单（拖动结束、松手之后调）</summary>
    private void RecordWindowChange()
    {
        _pendingAdjustments.Record(_sceneTracker.Current, Left, Top, Width, Height);
        DebugLog.Write($"待保存：{_sceneTracker.Current} 的窗口状态 = ({Left:0},{Top:0}) {Width:0}x{Height:0}");
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
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            var written = ConfigFileWriter.WriteWindowBounds(path, _pendingAdjustments.TakeAll());
            DebugLog.Write($"窗口状态已保存到 config.json（{written} 个界面）");
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
            UnregisterHotKey(_hwnd, HOTKEY_TOGGLE_MODE);
            UnregisterHotKey(_hwnd, HOTKEY_QUIT);
            UnregisterHotKey(_hwnd, HOTKEY_RELOAD);
            UnregisterHotKey(_hwnd, HOTKEY_TEST_1);
            UnregisterHotKey(_hwnd, HOTKEY_TEST_2);
            UnregisterHotKey(_hwnd, HOTKEY_TEST_3);
            UnregisterHotKey(_hwnd, HOTKEY_TEST_RANGE);
            UnregisterHotKey(_hwnd, HOTKEY_TEST_VOICE);
        }

        base.OnClosed(e);
    }
}