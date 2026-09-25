// ============================================================
// PreviewPane.xaml.cs —— 共享的角色预览区
//
// 【它把悬浮窗那套"把页面喂起来"重走了一遍，但**不是抄一遍**】
//   浏览器参数、init 报文形状都在 `PageHost`（那两样是真正会重复的东西）；
//   扫描 / 补丁 / 虚拟主机映射本来就是公开的静态方法，直接调。
//
// 【它和悬浮窗的三处不同，都是刻意的】
//   ① **补丁目录自己的**（`PageHost.PatchDirectoryFor("preview")`）——
//      预览区读的是设置里那份**还没保存**的配置，和悬浮窗共用一个目录会互相覆盖，
//      症状是"预览里是对的，一启动悬浮窗又变回去了"，查起来毫无线索。
//   ② **不发语音**（init 里 voice = null）：预览是"看"的，不是"听"的。
//   ③ **不起第二个浏览器进程直到真要用**：懒加载 + 收起时挂起。
//
// 【已知边界：这一块我没法自测】
//   执行沙箱里 WebView2 起不了浏览器进程（`EnsureCoreWebView2Async` 直接
//   `COMException 0x8000FFFF`），所以**真机表现只能由用户验**。
//   能在这里验的是"边框该画多大"那套算术（`Logic/PreviewScale.cs`，有 7 条用例）。
// ============================================================
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace OsuLive2dOverlay;

public partial class PreviewPane : UserControl
{
    /// <summary>当前用哪一份配置起着的（换档案时 ConfigStore 会换掉整个对象）</summary>
    private PluginConfig? _config;

    /// <summary>
    /// 当前这只预览区用的表情清单（`StartAsync` 里扫出来存下的）。
    /// 留着它是为了"常驻层改了要立刻重发"—— 那一句话不该再扫一次模型（见 `UpdatePersistentLayers`）。
    /// </summary>
    private ExpressionCatalog? _catalog;

    /// <summary>这一档现在该显示的窗口尺寸（DIP）——边框按它画</summary>
    private double _windowWidth;
    private double _windowHeight;

    private bool _started;

    /// <summary>正在起（防止连点"显示预览"起两只）</summary>
    private bool _starting;

    /// <summary>调试模式（画面上显示开发期信息）—— 跟着设置走，由页面喂进来</summary>
    private bool _debugMode;

    /// <summary>页面就绪了没有（没就绪时发消息会被丢，见悬浮窗那边同样的处理）</summary>
    private bool _pageReady;

    /// <summary>
    /// 页面报过 `fatal`（这一屏永远画不出东西）或者页面本身没加载起来。
    /// 它让"下次这一页露出来时重新起一份"—— 否则用户把模型目录改对了也看不到变化。
    /// </summary>
    private bool _pageDead;

    /// <summary>
    /// 这只预览区的编号（进程内从 1 递增）。
    ///
    /// **为什么日志里要带它**：2026-09-23 查"反复切页就掉帧"时，日志里只有一堆
    /// `预览区：页面就绪`，**分不清是哪一只实例说的** —— 于是"每切一次页就新起一只"
    /// 这个关键事实得数行数才看得出来。带编号之后
    /// `#7 控件已创建 → #7 页面就绪 → #8 控件已创建` 一眼就能看出在累积。
    /// </summary>
    private static int _instanceCounter;
    private readonly int _instance = ++_instanceCounter;

    /// <summary>日志前缀：`预览区#3：…`</summary>
    private string LogTag => $"预览区#{_instance}";

    /// <summary>最近一次要显示的站位 —— 页面就绪之后补发一次，否则角色不出现</summary>
    private GameScene _scene = GameScene.Unknown;
    private CharacterPlacement? _placement;

    /// <summary>
    /// 无参构造 —— XAML 里要能直接摆一个（有参构造的元素**不能带 x:Name**）。
    ///
    /// 它**不认识 ConfigStore**：要显示哪份配置由页面通过
    /// <see cref="EnsureStartedAsync"/> 喂进来。这样"配置换了要不要重建"
    /// 就退化成一次引用比较（换档案时 `ConfigStore.Config` 会换成新对象），
    /// 不需要让预览反过来去订阅全局状态。
    /// </summary>
    public PreviewPane()
    {
        InitializeComponent();

        DebugLog.Write($"{LogTag}：控件已创建（WebView2 还没起 —— 懒加载）");
    }

    /// <summary>预览是不是已经起来了（页面自己判"要不要现在起"）</summary>
    public bool IsStarted => _started;

    // ------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------

    /// <summary>
    /// 懒加载：**第一次真的要被看见时**才起 WebView2。
    /// 可以重复调（已在起 / 已起好都会直接返回）。
    /// </summary>
    /// <param name="config">
    /// 现在要显示的配置。**换了对象就要重建** —— 换档案时 `ConfigStore.Config`
    /// 会换成新对象，而 init 里带着模型地址，不重建就还是上一个角色的模型。
    /// 用引用比较而不是让预览去订阅全局状态：这样它不需要认识 ConfigStore。
    /// </param>
    public async Task EnsureStartedAsync(PluginConfig config, bool debugMode)
    {
        if (_starting) return;

        var configChanged = !ReferenceEquals(_config, config);
        _config = config;
        _debugMode = debugMode;

        // 三种情况：
        //   ① 已经起好了、配置也没换过 → 什么都不用做（页面在跑，站位/表情由页面那边再推一次）
        //   ② 配置**换了对象**（换档案）→ 重建：init 里带着模型地址，不重建就还是上一个角色
        //   ③ 这一页**已经废了**（页面报过 fatal：模型没加载出来）→ 也重建：
        //      不重建的话，用户把模型目录改对了、界面却还是那句话，看起来像"这软件修不好"
        //
        // 注意这里**没有"恢复"这件事**了：页面切走时预览区已经被 `Shutdown()` 释放
        //（★ 2026-09-23，见那个方法的说明）；下次回来是一只**新建的**预览区，走 ①②③ 之外的第四条路：`StartAsync`。
        if (_started && !configChanged && !_pageDead) return;

        _starting = true;
        try
        {
            if (_started) await RebuildAsync();
            else await StartAsync();
        }
        catch (Exception ex)
        {
            // 起不来不该把设置界面拖垮 —— 界面上留一句人话，日志里留全的
            DebugLog.Write($"{LogTag}：WebView2 初始化异常：" + ex);
            Hint.Text = "预览没能启动：" + ex.Message;
            Hint.Visibility = Visibility.Visible;
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>
    /// 把 WebView2 **真的释放掉**（下次要用时 `EnsureStartedAsync` 会重新起一只）。
    ///
    /// ★ 2026-09-23：这条规矩是查"反复切页人物就卡（30 帧）"查出来的。
    ///
    /// **原因不是渲染变慢，而是实例在累积**：
    ///   `MainWindow` 每次点导航都 `new` 一个页面（`CreatePage` 是委托，点了才造），
    ///   而每个页面里都有一只预览区 —— 于是一次切页 = **新起一只 WebView2**
    ///   （建环境 → 导航 → 加载页面 → 加载模型与贴图 → 建一个 WebGL 上下文）。
    ///   旧的原来只走 `TrySuspendAsync()`（挂起）**从不释放**：WPF 那边没人再引用它，
    ///   但浏览器那边还活着、显存还占着。
    ///   真机日志里 5 分钟内留下了 **16 次「控件已创建 / 页面就绪」** —— 16 只渲染实例，
    ///   显存与合成器被摊薄，表现就是"切几次之后开始掉帧"。
    ///
    /// 所以生命周期只留一条最简单的规矩：**预览区跟着页面生死**。
    /// 切走 = 释放，回来 = 重新起一只（约 0.3 秒，和之前一样）；
    /// 任何时刻最多只有**一只**预览实例（外加悬浮窗那只），不随切页次数增长。
    ///
    /// > 顺带删掉了原来的 `Suspend()` / `Resume()`：它们只在"页面被缓存、只是暂时藏起来"
    /// > 的前提下才有意义，而页面从来都是**销毁重建**的 —— `Resume()` 一次都没被走到过（死代码）。
    /// > 这也是一处**设计与实现对不上**的信号：当初按"页面会被缓存"写了挂起/恢复，
    /// > 但导航其实是每次新建。以后看到"某个方法一直没人调用"，就该回头核对它依赖的前提。
    /// </summary>
    public void Shutdown()
    {
        if (!_started) return;

        _started = false;
        _pageReady = false;
        _pageDead = false;

        try { Web.Dispose(); }
        catch (Exception ex) { DebugLog.Write($"{LogTag}：释放失败（无害，进程退出时会一并收掉）：" + ex.Message); }

        DebugLog.Write($"{LogTag}：已释放（页面切走了）");
    }

    /// <summary>页面被换掉（切导航 / 关窗口）→ 释放</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e) => Shutdown();

    /// <summary>配置换过之后重建：把整条链重走一遍（换档案、换模型都归它管）</summary>
    private async Task RebuildAsync()
    {
        DebugLog.Write($"{LogTag}：配置换了 → 重建");
        Shutdown();
        await StartAsync();
    }

    // ------------------------------------------------------------
    // 起页面（和悬浮窗同一条路，但参数/报文来自 PageHost）
    // ------------------------------------------------------------

    private async Task StartAsync()
    {
        if (_started) return;

        _pageDead = false;      // 新起的一页是活的（上一次说不定是"模型路径写错"那种能改好的事）
        var config = _config ?? new PluginConfig();
        var exeDir = AppContext.BaseDirectory;
        var webDir = Path.Combine(exeDir, "web");

        Hint.Text = "预览正在启动…";
        Hint.Visibility = Visibility.Visible;

        // 上一次可能因为页面报 fatal 被收起来了（见 ShowFatalHint）—— 重建时重新露出来
        Web.Visibility = Visibility.Visible;

        var environment = await CoreWebView2Environment.CreateAsync(null, null,
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = PageHost.BrowserArguments });

        await Web.EnsureCoreWebView2Async(environment);

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        // 模型：扫一遍 → 表情清单 → 打补丁（目录是预览区自己的）
        var scan = ModelScanner.Scan(config.Model.Directory, config.Model.Entry);
        var catalog = ExpressionCatalog.Build(scan, config.Model.Directory);
        _catalog = catalog;      // 留着给"常驻层改了立刻重发"用（见 UpdatePersistentLayers）
        var patchDir = PageHost.PatchDirectoryFor("preview");

        string? patchProblem = null;
        try
        {
            var patch = ModelHost.WritePatchedModel(config, scan, patchDir);
            if (patch.Problems.Count > 0) patchProblem = patch.Problems[0];
        }
        catch (Exception ex)
        {
            patchProblem = ex.Message;
        }

        // 语音映射**不给**：预览不播声音（见文件头第 ③ 条）
        ModelHost.SetupVirtualHosts(webDir, config.Model.Directory, patchDir, null,
            (host, dir) => core.SetVirtualHostNameToFolderMapping(host, dir, CoreWebView2HostResourceAccessKind.Allow));

        core.WebMessageReceived += OnWebMessage;

        core.NavigationCompleted += (_, args) =>
        {
            // ★ 2026-09-23：**不管成没成都记一行，而且把"最后落在哪个地址"也记下来。**
            //
            // 起因：用户报过一次"预览区白屏，右上角还有一张简笔哭脸"。
            // 那张哭脸不是我们画的 —— 它是**浏览器自己的错误页**（Chromium 的 neterror 页：
            // 白底 + 一个简笔表情 + "无法访问此页面"）。也就是说那一次**页面根本没加载成功**。
            // 而 `IsSuccess` 只在**网络层失败**时为 false：像"虚拟主机映射没生效 / 文件没找到"
            // 这类情况，浏览器是**拿到了一个响应**（404/空页面）的，`IsSuccess` 会是 true ——
            // 于是它会被当成"页面就绪"走过去，白屏就此静悄悄地发生，日志里什么都没有。
            //
            // 记 `core.Source` 就能把它抓住：落在错误页上时，那个地址是
            // **`chrome-error://chromewebdata/`**（而不是我们的 `https://…/index.html`）。
            DebugLog.Write($"{LogTag}：导航完成 成功={args.IsSuccess} 状态={args.WebErrorStatus} " +
                           $"实际地址={core.Source}");

            if (!args.IsSuccess)
            {
                DebugLog.Write($"{LogTag}：页面加载失败 " + args.WebErrorStatus);
                Hint.Text = "预览页面加载失败：" + args.WebErrorStatus;
                Hint.Visibility = Visibility.Visible;

                // 同上：这句话要说给人看，就得先把 WebView2 收起来（airspace）
                Web.Visibility = Visibility.Collapsed;
                _pageDead = true;
                return;
            }

            _pageReady = true;
            SendInit(catalog);
            ResendCurrent();

            Hint.Visibility = patchProblem is null ? Visibility.Collapsed : Visibility.Visible;
            if (patchProblem is not null) Hint.Text = "模型补丁有问题：" + patchProblem;

            DebugLog.Write($"{LogTag}：页面就绪");
        };

        DebugLog.Write($"{LogTag}：开始导航 → {PageHost.EntryUrl}");
        core.Navigate(PageHost.EntryUrl);

        _started = true;
    }

    private void SendInit(ExpressionCatalog catalog)
    {
        // voice 传 null：预览不播声音（见文件头第 ③ 条）
        SendJson(PageHost.BuildInitPayload(_config ?? new PluginConfig(), _debugMode, null, catalog));
    }

    // ------------------------------------------------------------
    // 对外：显示什么
    // ------------------------------------------------------------

    /// <summary>
    /// 让预览显示**这一档**的站位与窗口尺寸。
    /// 设置界面在调哪一档，就传哪一档 —— 预览要如实反映"你现在改成了什么样"。
    /// </summary>
    public void ShowPlacement(GameScene scene, CharacterPlacement placement,
                              double windowWidth, double windowHeight)
    {
        _scene = scene;
        _placement = placement;

        SetWindowSize(windowWidth, windowHeight);
        ResendCurrent();
    }

    /// <summary>这个界面不显示角色（没启用 / 认不出）</summary>
    public void HideCharacter()
    {
        _placement = null;
        SetWindowSize(0, 0);
        ResendCurrent();
    }

    /// <summary>
    /// 常驻层变了（用户在「行为」页勾了常驻组件）→ **立刻重发一次**，不等保存、不等重开页面。
    ///
    /// 用户对设置界面的要求是"改动要立刻看得到效果"（原话），而常驻层是唯一
    /// **勾一下就该当场看见**的东西（隐藏耳朵/发夹这类）。页面那边是同一个 `case "layers"` 在接。
    /// </summary>
    public void UpdatePersistentLayers()
    {
        if (_catalog is null || _config is null) return;

        SendJson(new { type = "layers", persistent = PageHost.PersistentLayers(_config, _catalog) });
    }

    /// <summary>试播一个表情：**播一次就走**（瞬时层），不是"挂住不放"。</summary>
    ///
    /// ★ 2026-09-23 用户拍板："动作和表情都只是暂时播放"。
    ///   原本 §3.1 分的是"表情一直保持 / 动作播一次"，现在不区分了 ——
    ///   所以这里和 <see cref="PreviewMotion"/> 是同一种行为：播一小段，然后回落。
    ///
    /// 参数由调用方一起给（清单那一行里就带着 `ExpressionCatalog.ParametersOf`）——
    /// 页面拿到就能直接往模型上写，不必自己去读 exp3 文件。
    /// </summary>
    public void PreviewExpression(string id, IReadOnlyList<ExpressionParam> parameters)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        SendJson(new { type = "expression", expression = id.Trim(), @params = parameters });
    }

    /// <summary>试播一个动作组：播一次（动作定格不了，本来就只能播一次）</summary>
    public void PreviewMotion(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        SendJson(new { type = "motion", action = id.Trim() });
    }

    // ------------------------------------------------------------

    /// <summary>把"现在该显示什么"再发一遍（页面就绪后 / 站位变了之后都要）</summary>
    private void ResendCurrent()
    {
        if (!_pageReady || _placement is null)
        {
            SendJson(new { type = "scene", scene = _scene.ToString(), show = false, opacity = 0.0, character = (object?)null });
            return;
        }

        SendJson(new
        {
            type = "scene",
            scene = _scene.ToString(),
            show = true,

            // 预览里角色永远是"看得见"的 —— 加载期隐藏那套是悬浮窗的事，
            // 这里没有"正在加载"的概念（用户在配置，不是在打歌）。
            opacity = 1.0,

            character = new
            {
                x = _placement.X,
                y = _placement.Y,
                scale = _placement.Scale,
                angle = _placement.AngleDegrees
            }
        });
    }

    private void SendJson(object payload)
    {
        if (!_pageReady || Web.CoreWebView2 is null) return;

        try { Web.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(payload)); }
        catch (Exception ex) { DebugLog.Write($"{LogTag}：发消息失败：" + ex.Message); }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 页面也会往回说话（模型加载好了、报错、渲染自检…）—— 记进日志就够了。
        //
        // ★ **绝不能原样记**。教训来自 2026-09-20：页面当时在调试模式下会发画面快照
        //   （`{"type":"snapshot","dataUrl":"data:image/png;base64,…"}`，一张约 270KB），
        //   我原样写日志 → **一次会话 34MB**，而且每条都是一次大字符串拼接 + 一次文件写，
        //   把 UI 线程拖到 **5 帧**（用户真机实测）。
        //   （那个快照功能已经撤掉了，但"别把大消息原样写日志"这条留在这儿 ——
        //     它防的是这一类，不是某一个功能。）
        var json = e.WebMessageAsJson;
        DebugLog.Write(json.Length <= 300
            ? $"{LogTag} [页面] " + json
            : $"{LogTag} [页面] 大消息（{json.Length:N0} 字符，已省略）：" +
              json[..Math.Min(100, json.Length)] + "…");

        // ★ 2026-09-23：页面不在画面上写任何字了（见 web/index.html 里 log() 那段说明），
        //   "这一屏永远画不出东西"这种话改由**这里**显示 —— 它是 WPF 画的，
        //   而这个窗口正是用户此刻盯着的那一个（悬浮窗那边没地方显示，就只进日志）。
        ShowFatalHint(json);
    }

    /// <summary>
    /// 页面报 `fatal`（模型没加载出来、缺 Cubism Core 之类）→ 把这句话显示在提示行上。
    ///
    /// **必须把 WebView2 收起来**：它是原生窗口，会把自己的区域整个盖在 WPF 元素之上
    ///（airspace），不收起来的话这句话就藏在它后面，等于没说。
    /// 收起来之后剩下的是那条"悬浮窗边框线" + 这句话 —— 正好表达"这里本来该有角色，但没有"。
    /// </summary>
    private void ShowFatalHint(string json)
    {
        // 便宜的预筛：绝大多数消息都不是 fatal，不值得每条都解一次 JSON
        if (!json.Contains("\"fatal\"", StringComparison.Ordinal)) return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "fatal") return;
            if (!root.TryGetProperty("text", out var text)) return;

            var message = text.GetString() ?? "";
            Dispatcher.Invoke(() =>
            {
                Hint.Text = message;
                Hint.Visibility = Visibility.Visible;
                Web.Visibility = Visibility.Collapsed;

                // 记下"这一页已经废了"：下次这一页露出来时会**重新起一份**，
                // 这样"模型目录写错 → 改对 → 切走再切回来"就能恢复。
                _pageDead = true;
            });
        }
        catch
        {
            // 报文格式意外时忽略，不影响主流程（和悬浮窗那边的处理一致）
        }
    }

    // ------------------------------------------------------------
    // 边框：窗口多大，就画多大（等比）
    // ------------------------------------------------------------

    private void SetWindowSize(double width, double height)
    {
        if (Math.Abs(_windowWidth - width) < 0.5 && Math.Abs(_windowHeight - height) < 0.5) return;

        _windowWidth = width;
        _windowHeight = height;
        ApplyFrame();
    }

    /// <summary>按当前尺寸把边框和渲染区摆好（**只有适配显示一种模式**，见 XAML 文件头）</summary>
    private void ApplyFrame()
    {
        var frame = PreviewScale.Fit(_windowWidth, _windowHeight,
                                     FrameHost.ActualWidth, FrameHost.ActualHeight);

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            FrameLabel.Text = "这一档还没设窗口尺寸";
            Frame.Visibility = Visibility.Collapsed;
            return;
        }

        Frame.Visibility = Visibility.Visible;
        Frame.Width = frame.Width;
        Frame.Height = frame.Height;

        FrameLabel.Text = $"悬浮窗 {_windowWidth:0}×{_windowHeight:0} DIP · 显示比例 {frame.Label}";
    }

    private void OnPaneSizeChanged(object sender, SizeChangedEventArgs e) => ApplyFrame();
}
