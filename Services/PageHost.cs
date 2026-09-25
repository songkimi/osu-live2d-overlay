// ============================================================
// PageHost.cs —— "把 web/ 那套页面喂起来"里**两处真正会重复的东西**
//
// 【为什么要有它】（2026-09-20，做设置界面的预览区时）
//   预览区要在设置窗口里再渲染一份 Live2D。它和悬浮窗走的是同一个页面、
//   同一套协议 —— 于是下面这两样东西就有**两份真相**的风险：
//
//     ① **浏览器参数**：少一个 `--enable-unsafe-swiftshader`，
//        这台机器上就可能创建不出 WebGL 上下文，症状是"模型加载成功但什么都不显示"。
//        两处各写一份，迟早只改一处。
//     ② **init 报文**：页面认的就是这一包字段名。改一处漏一处，
//        页面上就是"某个功能莫名其妙不生效"，而且**两边表现还不一样**，最难查。
//
// 【它不管什么】（管多了就又变成上帝类）
//   · 不建 WebView2 环境、不导航 —— 那是调用方的事（它俩的窗口生命周期完全不同）
//   · 不做扫描/补丁 —— 那些本来就是公开的静态方法，直接调就行
//   · **不引 WebView2、不引 WPF** —— 纯字符串与数据整理，能进控制台测
//
// 【每个调用方要有自己的补丁目录】见 PatchDirectoryFor：
//   悬浮窗和预览区要是共用同一个目录，谁后写谁说了算 ——
//   而它们读的可能是**不同的配置**（预览区看的是设置里还没保存的那份）。
// ============================================================
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace OsuLive2dOverlay;

public static class PageHost
{
    /// <summary>
    /// 启动 WebView2 时用的浏览器参数。
    ///
    /// 四个参数各有各的来历（详见 `OverlayWindow` 里那段长注释）：
    ///   · `--enable-unsafe-swiftshader`  GPU 路径不可用时兜底，**不然可能没有 WebGL**
    ///   · `--autoplay-policy=no-user-gesture-required`
    ///     语音的关键：这个窗口是穿透的，等不到用户手势，不加它 `play()` 直接被拒
    ///   · 后两个防"窗口被别的窗口盖住时渲染被挂起"（osu 全屏时经常处于被覆盖状态）
    /// </summary>
    public const string BrowserArguments =
        "--enable-unsafe-swiftshader --autoplay-policy=no-user-gesture-required " +
        "--disable-direct-composition " +
        "--disable-features=CalculateNativeWinOcclusion";

    /// <summary>页面入口地址（vendor 依赖也在这个目录下）</summary>
    public static string EntryUrl => $"https://{ModelHost.AppHost}/index.html";

    /// <summary>补丁后的模型定义地址（init 里用它）</summary>
    public static string PatchedModelUrl => $"https://{ModelHost.PatchHost}/{ModelHost.PatchedFileName}";

    /// <summary>
    /// 某个调用方**自己的**补丁目录。
    ///
    /// 每个用页面的地方一份，别共用：悬浮窗和预览区读的可能是**不同的配置**
    /// （预览区看的是设置里还没保存的那份），共用目录就会互相覆盖
    /// —— 症状是"预览里是对的，一启动悬浮窗又变回去了"，查起来毫无线索。
    /// </summary>
    public static string PatchDirectoryFor(string name)
        => Path.Combine(Path.GetTempPath(), "osu-live2d-overlay", name);

    /// <summary>
    /// 组装 `init` 报文 —— **页面协议的唯一出处**。
    ///
    /// 字段名就是页面认的那几个，改这里等于改协议：两边（悬浮窗 / 预览区）会一起变，
    /// 这正是要的效果。
    /// </summary>
    /// <param name="config">要显示的那份配置（悬浮窗用启动时的快照；预览区用设置里那<b>内存</b>份）</param>
    /// <param name="debugMode">调试模式（画面上显示开发期信息）</param>
    /// <param name="voicePayload">语音表，由调用方解析好传进来（见 <c>VoiceLibrary.Resolve</c>）</param>
    /// <param name="catalog">表情清单 —— 「常驻组件」要把每个标识翻译成"改哪些参数"</param>
    public static object BuildInitPayload(
        PluginConfig config, bool debugMode, object? voicePayload, ExpressionCatalog catalog)
    {
        return new
        {
            type = "init",
            modelUrl = PatchedModelUrl,

            // 「待机动作」填的是标识（组名）。未注册的（VTS 成品的 待机.motion3）会被补丁
            // 注册成同名组，已注册的（官方模型的 Idle）直接用 —— 页面调用方式完全一样。
            idle = config.Model.IdleGroup,

            view = new { zoom = config.View.Zoom, offsetY = config.View.OffsetY, offsetX = config.View.OffsetX },
            expressionDurationMs = config.Performance.ExpressionDurationMs,
            debug = debugMode,
            voice = voicePayload,

            // 常驻层：用户勾的"一直在"的形象元素（鞋子、高光…）。
            // 整批发给页面，之后由页面自己管生死；空列表就什么都不挂。
            persistent = PersistentLayers(config, catalog),

            mouth = new
            {
                enabled = config.Mouth.Enabled,
                parameter = config.Mouth.Parameter,
                strength = Math.Clamp(config.Mouth.Strength, 0.0, 1.0)
            }
        };
    }

    /// <summary>
    /// 常驻层那批数据：用户勾的"一直在"的形象元素（隐藏耳朵、隐藏发夹…），
    /// 每个标识翻成"要挂住哪些参数"。
    ///
    /// ★ 单独抽出来是因为它有**两个**使用者：
    ///   ① `init`（页面刚起来时下发一次）；
    ///   ② 「行为」页勾选常驻组件之后**立刻重发**（用户要求"改了要马上看得到效果"，
    ///      不能等保存、更不能等重开页面）—— 页面那边是同一个 `case "layers"` 在接。
    /// 两边各拼一份的话，改一处漏一处就是"勾了没反应"，而那是很难查的一类。
    /// </summary>
    public static IReadOnlyList<PersistentLayer> PersistentLayers(PluginConfig config, ExpressionCatalog catalog)
        => config.PersistentParts
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new PersistentLayer(id.Trim(), catalog.ParametersOf(id)))
            .ToList();
}

/// <summary>
/// 一条常驻层（标识 + 要挂住的参数）。
/// **字段名必须和页面认的 JSON 对齐**（`id` / `params`）—— 它直接进 `{type:"layers"}` 报文。
/// </summary>
public sealed record PersistentLayer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("params")] IReadOnlyList<ExpressionParam> Parameters);
