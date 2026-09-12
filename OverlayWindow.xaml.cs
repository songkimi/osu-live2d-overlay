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
    private const int HOTKEY_TOGGLE_MODE = 9001;
    private const int HOTKEY_QUIT = 9002;
    private const int HOTKEY_RELOAD = 9003;
    private const int HOTKEY_TEST_1 = 9004;
    private const int HOTKEY_TEST_2 = 9005;
    private const int HOTKEY_TEST_3 = 9006;
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
    private readonly CancellationTokenSource _cts = new();
    private readonly string _webDir;

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isRunningMode = true;
    private bool _pageReady;
    private System.Windows.Threading.DispatcherTimer? _cursorTimer;
    private Point _lastCursorSent = new(double.NaN, double.NaN);

    // ---- 语音 ----
    private string? _voiceDir;                             // 解析后的语音目录（没有就不映射）
    private object? _voicePayload;                         // 发给页面的"情绪 → 地址"表
    private readonly List<string> _voiceEmotionNames = new();   // 测试热键按顺序试听用
    private int _voiceTestIndex;

    public OverlayWindow()
    {
        InitializeComponent();

        var exeDir = AppContext.BaseDirectory;
        _config = PluginConfig.Load(Path.Combine(exeDir, "config.json"));
        _webDir = Path.Combine(exeDir, "web");
        _tracker = new ComboTracker(_config.ComboTriggers.Select(t => t.Threshold));

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

        // 默认停在屏幕右侧（正是"游戏右边"）
        Left = SystemParameters.WorkArea.Right - Width - 40;
        Top = SystemParameters.WorkArea.Top + 120;

        ApplyMode();
        RegisterHotKey(_hwnd, HOTKEY_TOGGLE_MODE, MOD_CONTROL | MOD_ALT, VK_T);
        RegisterHotKey(_hwnd, HOTKEY_QUIT, MOD_CONTROL | MOD_ALT, VK_Q);
        RegisterHotKey(_hwnd, HOTKEY_RELOAD, MOD_CONTROL | MOD_ALT, VK_R);
        // 测试热键：不开游戏也能验证"消息 → 表情/语音"这一段通不通
        RegisterHotKey(_hwnd, HOTKEY_TEST_1, MOD_CONTROL | MOD_ALT, VK_1);
        RegisterHotKey(_hwnd, HOTKEY_TEST_2, MOD_CONTROL | MOD_ALT, VK_2);
        RegisterHotKey(_hwnd, HOTKEY_TEST_3, MOD_CONTROL | MOD_ALT, VK_3);
        RegisterHotKey(_hwnd, HOTKEY_TEST_VOICE, MOD_CONTROL | MOD_ALT, VK_V);

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
            // ★ 透明背景必须在导航之前设置
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);

            DebugLog.Write("WebView2：开始创建环境");

            // Live2D 必须有 WebGL 才能渲染。加一个"允许软件渲染"的兜底参数：
            // 如果这台机器上 WebView2 的 GPU 路径不可用（透明窗口下有可能发生），
            // 没有它就会创建不出 WebGL 上下文 → 模型加载成功但什么都画不出来。
            //
            // ★ 第二个参数是语音的关键：WebView2 默认沿用 Chromium 的策略 —— 不允许
            //   "没有用户手势"的媒体自动播放。而这个窗口是点击穿透的，WebView2 永远等不到
            //   一次点击或按键，不加它语音会一句都放不出来（play() 直接被拒绝）。
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--enable-unsafe-swiftshader --autoplay-policy=no-user-gesture-required"
            };

            // 用户数据目录用 WebView2 的默认位置（exe 同级的 osu-live2d-overlay.exe.WebView2）
            var environment = await CoreWebView2Environment.CreateAsync(null, null, options);
            DebugLog.Write("WebView2：环境已创建，开始 EnsureCoreWebView2Async");
            await Web.EnsureCoreWebView2Async(environment);
            DebugLog.Write("WebView2：控件已就绪");
            var core = Web.CoreWebView2;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;      // 调试用；正式发布可改 false

            if (!Directory.Exists(_config.Model.Directory))
                ShowStatus("找不到模型目录：" + _config.Model.Directory, autoHide: false);

            // 语音目录：解析成"情绪 → 地址"的表，随 init 一起发给页面（已在构造时算好）
            if (_voiceDir is not null)
                DebugLog.Write($"语音目录已映射：{_voiceDir}（{_voiceEmotionNames.Count} 类情绪）");

            // 生成"补过动作与表情"的模型定义到临时目录，再把四个目录映射成虚拟主机
            var patchDir = Path.Combine(Path.GetTempPath(), "osu-live2d-overlay");
            try
            {
                var patchedPath = ModelHost.WritePatchedModel(_config, patchDir);
                ShowStatus($"模型已补丁：{_config.Model.Expressions.Count} 个表情 + 待机动作 → {Path.GetFileName(patchedPath)}",
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
            // 没填「待机动作文件」就传空 —— 页面对空值会跳过待机动作，不会一直报"动作不存在"
            idle = string.IsNullOrWhiteSpace(_config.Model.IdleFile)
                ? ""
                : (string.IsNullOrWhiteSpace(_config.Model.IdleGroup) ? "Idle" : _config.Model.IdleGroup),
            view = new { zoom = _config.View.Zoom, offsetY = _config.View.OffsetY, offsetX = _config.View.OffsetX },
            expressionDurationMs = _config.ExpressionDurationMs,
            debug = _config.DebugMode,
            voice = _voicePayload,
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

        try
        {
            _voiceDir = Path.GetFullPath(voice.Directory);
        }
        catch (Exception ex)
        {
            return new { enabled = false, problem = "语音目录路径不合法：" + ex.Message };
        }

        if (!Directory.Exists(_voiceDir))
        {
            _voiceDir = null;      // 目录不存在 → 不映射（映射不存在的目录会抛异常）
            return new { enabled = false, problem = "找不到语音目录：" + voice.Directory };
        }

        var (emotions, problems) = VoiceLibrary.Resolve(voice);
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
    /// ★ 注意：WebView2 的 API 只能在 UI 线程调用（连击事件是从 WebSocket 接收线程来的），
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

    /// <summary>测试用：直接按配置里的第 index 条连击触发发一次事件（不经过游戏）</summary>
    private void SendTestCombo(int index)
    {
        var trigger = _config.ComboTriggers.ElementAtOrDefault(index);
        if (trigger is null)
        {
            ShowStatus($"配置里没有第 {index + 1} 条连击触发", autoHide: false);
            return;
        }

        SendJson(new
        {
            type = "combo",
            expression = trigger.Expression,
            combo = trigger.Threshold,
            step = index + 1,
            threshold = trigger.Threshold
        });
        ShowStatus($"[测试] 模拟 {trigger.Threshold} 连击 → {trigger.Expression}", autoHide: true);
    }

    private int _missTestIndex;

    /// <summary>
    /// 测试用：轮流模拟"够大额 / 够小额 / 还没到门槛"三种断连击。
    /// 走的是和真实游戏同一条逻辑（ResolveMiss），所以门槛到底对不对，连按三下就知道了。
    /// </summary>
    private void SendTestMiss()
    {
        int previous = (_missTestIndex++ % 3) switch
        {
            0 => _config.Miss.BigThreshold + 100,                 // 够大额
            1 => _config.Miss.SmallThreshold + 5,                 // 够小额
            _ => Math.Max(0, _config.Miss.SmallThreshold - 1)     // 还没到门槛 → 应该毫无反应
        };

        var (expression, emotion) = ResolveMiss(previous);

        if (expression.Length == 0 && emotion.Length == 0)
        {
            ShowStatus($"[测试] 模拟断之前 {previous} 连击 → 低于门槛，什么都不做", autoHide: true);
            DebugLog.Write($"[测试] 断之前 {previous} 连击：低于门槛，不发消息");
            return;
        }

        // 合成一个 Break 事件，走正式发送路径
        var evt = new ComboEvent(ComboEventKind.Break, 0, previous, 0, 0, previous);
        DispatchTrigger(evt, expression, emotion, $"[测试] 断连击（断之前 {previous} 连击）");
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
            }
        }
        catch
        {
            // 页面消息格式意外时忽略，不影响主流程
        }
    }

    // ---------------- 连击事件 → 页面 ----------------

    private void OnComboUpdated(int combo, int maxCombo)
    {
        var triggered = _tracker.Update(combo, maxCombo);
        if (triggered is not { } evt) return;

        if (!_pageReady)
        {
            // 页面还没就绪就丢消息，并在界面上说清楚（否则会表现成"完全没反应"）
            ShowStatus("页面未就绪，忽略一次触发：" + evt.Kind, autoHide: true);
            return;
        }

        if (evt.Kind == ComboEventKind.Step)
        {
            var trigger = _config.ComboTriggers.FirstOrDefault(t => t.Threshold == evt.Threshold);
            DispatchTrigger(evt, trigger?.Expression ?? "", trigger?.VoiceEmotion ?? "",
                            $"{evt.Combo} 连击（跨过 {evt.Threshold}）");
            return;
        }

        // 断连击：先看"断之前手里有多少连击"，再决定理不理他
        var (expression, emotion) = ResolveMiss(evt.PreviousCombo);
        if (expression.Length == 0 && emotion.Length == 0)
        {
            DebugLog.Write($"断连击（断之前 {evt.PreviousCombo} 连击）→ 还没到门槛 " +
                           $"{_config.Miss.SmallThreshold}，不反应");
            return;
        }

        DispatchTrigger(evt, expression, emotion, $"断连击（断之前 {evt.PreviousCombo} 连击）");
    }

    /// <summary>
    /// 断连击前手里有 previousCombo 连击 → 该用哪一档（表情 + 语音各一项）。
    /// 两档门槛都不到就返回两个空串，调用方什么都不做 —— 原因见 MissConfig 的注释。
    /// </summary>
    private (string Expression, string Emotion) ResolveMiss(int previousCombo)
    {
        if (previousCombo >= _config.Miss.BigThreshold)
            return (_config.Miss.BigExpression, _config.Miss.BigVoiceEmotion);

        if (previousCombo >= _config.Miss.SmallThreshold)
            return (_config.Miss.SmallExpression, _config.Miss.SmallVoiceEmotion);

        return ("", "");
    }

    /// <summary>
    /// 把一次触发发给页面：表情一条消息、语音一条消息。
    /// ★ 两条消息各查各的配置、互不依赖 —— 这就是"解耦"落在代码上的样子：
    ///   表情没播出来语音照样说，语音文件缺了表情照样演。
    /// </summary>
    private void DispatchTrigger(ComboEvent evt, string expression, string emotion, string note)
    {
        if (!string.IsNullOrWhiteSpace(expression))
            SendJson(new
            {
                type = evt.Kind == ComboEventKind.Step ? "combo" : "miss",
                expression,
                combo = evt.Combo,
                step = evt.Level,
                threshold = evt.Threshold
            });

        if (!string.IsNullOrWhiteSpace(emotion))
            SendJson(new { type = "voice", emotion, reason = note, combo = evt.Combo });

        DebugLog.Write(note + " → 表情「" + expression + "」语音「" + emotion + "」");

        if (expression.Length == 0 && emotion.Length == 0) return;

        Dispatcher.Invoke(() => ShowStatus(note + " → " + (expression.Length > 0 ? expression : emotion),
                                           autoHide: true));
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
        ApplyExStyleToWindowTree(_hwnd, _isRunningMode);

        // WebView2 是原生窗口，WPF 元素盖不到它上面 → 调整时先把它藏起来，露出虚线框
        if (_isRunningMode)
        {
            AdjustLayer.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
        }
        else
        {
            AdjustHint.Text =
                $"拖动移动 · 右下角缩放\n当前视图：缩放 {_config.View.Zoom:0.00}，上下 {_config.View.OffsetY:0.00}，左右 {_config.View.OffsetX:0.00}";
            Web.Visibility = Visibility.Hidden;
            AdjustLayer.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 给本窗口以及**所有子窗口**（含 WebView2 的原生子窗口）设置扩展样式。
    /// 只设顶层窗口是不够的：WebView2 的子窗口自己处理鼠标，会把点击"吃掉"。
    /// </summary>
    private void ApplyExStyleToWindowTree(IntPtr root, bool clickThrough)
    {
        void Apply(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            ex |= WS_EX_NOACTIVATE;
            if (clickThrough) ex |= WS_EX_TRANSPARENT;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(h, GWL_EXSTYLE, ex);
        }

        Apply(root);
        EnumChildWindows(root, (h, _) => { Apply(h); return true; }, IntPtr.Zero);
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

                    case HOTKEY_QUIT:
                        Close();
                        handled = true;
                        break;
                }
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
            UnregisterHotKey(_hwnd, HOTKEY_TEST_VOICE);
        }

        base.OnClosed(e);
    }
}
