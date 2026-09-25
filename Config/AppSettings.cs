// ============================================================
// AppSettings.cs —— **软件自身**的设置（存 settings.json）
//
// 【为什么必须和"配置档案"分开】（2026-09-20 用户提出）
//   "配置档案"是**按角色切换**的 —— 换个模型/换套规则玩 = 换一份 config.json。
//   如果把外观 / 字体 / 字号 / 热键这些东西也放进去，用户每换个角色，
//   辛辛苦苦调舒服的界面就得**从头再来一遍**。
//
//   判据一句话：**"这东西是描述角色的，还是描述我怎么用这个软件的？"**
//     · 描述角色（模型 / 规则 / 语音 / 站位）→ `PluginConfig`（config.json，随档案走）
//     · 我怎么用（外观 / 字体 / 字号 / 托盘 / 数据源 / 目录 / 日志）→ **这里**（全局唯一）
//
// 【连带决定】每界面的"悬浮窗位置与尺寸"**归档案**（用户拍板）
//   它和"角色站位"是配套的 —— 都是"这个角色在这个界面怎么摆"。
//   所以它留在 `界面感知` 里，跟着 config.json 走。
//
// 【迁移】首次运行时自动从旧的 config.json 里把软件设置"搬"过来一次，
//   用户无感（见 <see cref="Load"/>）。搬完 config.json 里那些段会在下次保存时自然消失 ——
//   因为 `PluginConfig` 里已经**没有**那些属性了，序列化时不会再写出来。
// ============================================================
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuLive2dOverlay;

public sealed class AppSettings
{
    /// <summary>通用：外观、语言、字体、字号、开机自启、托盘、关闭行为</summary>
    [JsonPropertyName("通用")] public GeneralConfig General { get; set; } = new();

    /// <summary>悬浮窗的**全局**行为（每界面一份的窗口状态在"界面感知"里，随档案走）</summary>
    [JsonPropertyName("悬浮窗")] public WindowSettings Overlay { get; set; } = new();

    /// <summary>
    /// 数据源：tosu 的地址端口 + 两个可执行文件路径 + 自动启动。
    ///
    /// 两个**路径**在设置界面里被放进了「文件位置」页（那里集中所有"文件在哪"），
    /// 而"怎么连"（IP / 端口 / 自动启动）留在「数据源」页 ——
    /// **字段只有一个编辑入口**，只是它在哪一页出现另说。
    /// </summary>
    [JsonPropertyName("数据源")] public DataSourceConfig DataSource { get; set; } = new();

    /// <summary>体检的严格/宽松（只作用于体检清单第 7~10 条）</summary>
    [JsonPropertyName("体检")] public HealthCheckConfig HealthCheck { get; set; } = new();

    /// <summary>日志：目录、保留月数、详细程度</summary>
    [JsonPropertyName("日志")] public LogSettings Log { get; set; } = new();

    /// <summary>
    /// 调试模式：把"表情未播放""库里拒绝"这类开发期信息显示在画面上。
    /// **从 config.json 搬过来的** —— 它描述的是"软件怎么运行"，不是"角色怎么演"。
    /// </summary>
    [JsonPropertyName("调试模式")] public bool DebugMode { get; set; }

    /// <summary>
    /// 配置档案：**当前生效的是哪一份**（2026-09-20 多档案功能）。
    ///
    /// 它为什么在"软件设置"里而不是档案里 —— 「我现在在用哪套配置」
    /// 描述的正是 <b>我怎么用这个软件</b>，而不是"这个角色怎么演"。
    /// 档案自己不可能知道自己是不是当前那份（那是循环定义）。
    /// </summary>
    [JsonPropertyName("档案")] public ProfileSettings Profile { get; set; } = new();

    /// <summary>结构版本，给将来的迁移判断用（两个配置文件各自有一份）</summary>
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;

    /// <summary>旧 config.json 里**属于软件设置**的那几个键 —— 迁移时按这份清单找。</summary>
    private static readonly string[] LegacyKeys =
        ["通用", "悬浮窗", "数据源", "体检", "日志", "调试模式"];

    /// <summary>
    /// 读 settings.json。
    ///
    /// 读不到时按顺序做两件事：
    ///   ① **试着从旧 config.json 里搬**（升级上来的用户，设置不该丢）
    ///   ② 搬不到就用默认值，并**写一份模板**出来（与 PluginConfig.Load 同一个套路）
    ///
    /// 全程不抛异常 —— 配置坏了也要能启动（解析层一直是"宽松兜底、永不崩"）。
    /// </summary>
    /// <param name="path">settings.json 的完整路径</param>
    /// <param name="legacyConfigPath">旧 config.json 的路径（迁移用；传 null 就不尝试迁移）</param>
    public static AppSettings Load(string path, string? legacyConfigPath = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, PluginConfig.Options) ?? new AppSettings();
                settings.Normalize();
                return settings;
            }

            // ① 升级路径：从旧 config.json 里搬
            var migrated = legacyConfigPath is null ? null : TryMigrateFrom(legacyConfigPath);
            if (migrated is not null)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(migrated, PluginConfig.Options));
                return migrated;
            }

            // ② 全新用户：默认值 + 写一份模板
            var fresh = new AppSettings();
            fresh.Normalize();
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, PluginConfig.Options));
            return fresh;
        }
        catch
        {
            var fallback = new AppSettings();
            fallback.Normalize();
            return fallback;
        }
    }

    /// <summary>
    /// 从旧格式的 config.json 里把软件设置读出来。
    /// **一个键都没有就返回 null**（说明是全新用户，或者已经迁移过了）——
    /// 这样"没东西可搬"和"搬出来是空的"能区分开。
    /// </summary>
    private static AppSettings? TryMigrateFrom(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!LegacyKeys.Any(key => root.TryGetProperty(key, out _))) return null;

            var settings = new AppSettings();

            settings.General = Read<GeneralConfig>(root, "通用") ?? settings.General;
            settings.Overlay = Read<WindowSettings>(root, "悬浮窗") ?? settings.Overlay;
            settings.DataSource = Read<DataSourceConfig>(root, "数据源") ?? settings.DataSource;
            settings.HealthCheck = Read<HealthCheckConfig>(root, "体检") ?? settings.HealthCheck;
            settings.Log = Read<LogSettings>(root, "日志") ?? settings.Log;

            if (root.TryGetProperty("调试模式", out var debug) && debug.ValueKind == JsonValueKind.True)
                settings.DebugMode = true;

            settings.Normalize();
            return settings;
        }
        catch
        {
            // 搬不动就别搬 —— 用默认值启动，总比因为迁移失败而打不开程序强
            return null;
        }
    }

    private static T? Read<T>(JsonElement root, string key) where T : class
        => root.TryGetProperty(key, out var element)
            ? element.Deserialize<T>(PluginConfig.Options)
            : null;

    /// <summary>
    /// 补齐所有嵌套对象。
    /// 手改 JSON 时什么都可能出现（`"通用": null` 是最常见的一种），
    /// 少补一处，程序就会崩在启动路径上。
    /// </summary>
    public void Normalize()
    {
        General ??= new GeneralConfig();
        Overlay ??= new WindowSettings();
        DataSource ??= new DataSourceConfig();
        HealthCheck ??= new HealthCheckConfig();
        Log ??= new LogSettings();
        Profile ??= new ProfileSettings();

        // 字段级兜底：空字符串在界面上就是"没填"，不该让它变成 null 传下去
        General.Appearance = Blank(General.Appearance, "跟随系统");
        General.Language = Blank(General.Language, "跟随系统");
        General.Font = Blank(General.Font, "跟随系统");
        DataSource.Ip = Blank(DataSource.Ip, DataSourceConfig.DefaultIp);
        HealthCheck.ModeText = Blank(HealthCheck.ModeText, "宽松");

        // 数据源的两个数字：手改 JSON 时最容易写出连不上的值，
        // 而它们的症状是"就是连不上，也不报错" —— 所以在读盘这一步就夹回合法范围。
        // （界面上另有一道校验，那是给用户看的提示；这里是兜底，两者不冲突。）
        DataSource.Port = DataSourceConfig.NormalizePort(DataSource.Port);
        DataSource.StartupWaitSeconds =
            DataSourceConfig.NormalizeStartupWaitSeconds(DataSource.StartupWaitSeconds);

        // 当前档案名不允许空 —— "现在用的是哪一份"必须总有个答案，
        // 否则启动时就不知道该去读 profiles 里的哪个文件
        Profile.Current = ProfileLocator.NormalizeName(Profile.Current);
    }

    private static string Blank(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}

// ============================================================
// 以下是"软件设置"用的几个配置段。
//
// 【它们为什么在本文件而不是 PluginConfig.cs】
//   2026-09-20 拆档案/设置时一起搬过来的 ——
//   "软件设置的类定义躺在'档案'文件里"本身就是混淆的开始。
// ============================================================

/// <summary>悬浮窗的**全局**行为。每界面一份的窗口状态在 <see cref="SceneProfile.Window"/> 里。</summary>
public sealed class WindowSettings
{
    /// <summary>拖动窗口时吸到屏幕边缘的距离（DIP）。默认 16 —— 太大反而像被粘住。</summary>
    [JsonPropertyName("吸附距离")] public double SnapDistance { get; set; } = 16;

    /// <summary>锁住窗口位置（防误拖）</summary>
    [JsonPropertyName("位置锁定")] public bool LockPosition { get; set; }

    /// <summary>结束悬浮窗（停止运行）的全局热键</summary>
    [JsonPropertyName("结束热键")] public string StopHotkey { get; set; } = "Ctrl+Alt+Q";

    /// <summary>临时允许交互（解除点击穿透，好把窗口拖走）的全局热键</summary>
    [JsonPropertyName("临时交互热键")] public string TempInteractiveHotkey { get; set; } = "Ctrl+Alt+E";

    
}

/// <summary>数据源：tosu 的地址端口 + 两个可执行文件路径 + 自动启动（定稿 §2④-2）</summary>
public sealed class DataSourceConfig
{
    /// <summary>默认监听地址。**只在这里写一份** —— 界面、兜底、迁移都引它</summary>
    public const string DefaultIp = "127.0.0.1";

    /// <summary>tosu 的默认端口</summary>
    public const int DefaultPort = 24050;

    /// <summary>默认的"等多久算失败"（秒）。6 秒是用户按自己机器的启动速度定的，别人的机器可能更慢，所以要能改</summary>
    public const int DefaultStartupWaitSeconds = 6;

    /// <summary>等待秒数的可取值范围 —— 太小会误报、太大等于卡住</summary>
    public const int MinStartupWaitSeconds = 1;
    public const int MaxStartupWaitSeconds = 60;

    /// <summary>端口的合法范围</summary>
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    /// <summary>tosu 暴露 JSON 数据的地址</summary>
    [JsonPropertyName("IP")] public string Ip { get; set; } = DefaultIp;

    /// <summary>tosu 的 WebSocket 端口</summary>
    [JsonPropertyName("端口")] public int Port { get; set; } = DefaultPort;

    /// <summary>tosu 可执行文件路径（自动启动用；空 = 没配）</summary>
    [JsonPropertyName("tosu路径")] public string TosuPath { get; set; } = "";

    /// <summary>osu 可执行文件路径（自动启动用；空 = 没配）</summary>
    [JsonPropertyName("osu路径")] public string OsuPath { get; set; } = "";

    [JsonPropertyName("启动时自动启动tosu")] public bool AutoStartTosu { get; set; }
    [JsonPropertyName("启动时自动启动osu")] public bool AutoStartOsu { get; set; }

    /// <summary>退出时关闭 tosu。**只关"自己启动的"那个进程**，别去关用户自己开的（决策 23）</summary>
    [JsonPropertyName("退出时关闭tosu")] public bool CloseTosuOnExit { get; set; }

    /// <summary>
    /// osu 退出时退出本程序
    /// </summary>
    [JsonPropertyName("osu退出时退出程序")] public bool ExitWhenOsuExits { get; set; }

    /// <summary>自动启动之后"等多久还没收到数据就算失败"（秒）。别人的机器可能更慢，所以要能改。</summary>
    [JsonPropertyName("启动等待秒数")] public int StartupWaitSeconds { get; set; } = DefaultStartupWaitSeconds;

    /// <summary>
    /// 把地址与端口拼成 WebSocket 地址。**这个拼法全程序只此一处**（★ 2026-09-23）。
    ///
    /// 路径 `/ws` 是 tosu 的约定，只该有一个地方知道它。原先另有三位读者各拼各的：
    /// 悬浮窗（写死的常量）、以及将来的"测试连接"与自动启动后的等待逻辑 ——
    /// 只要有一处写错（比如漏掉 `/ws`），症状就是"连不上，但不报错"。
    /// </summary>
    public static string BuildUrl(string ip, int port)
        => $"ws://{(string.IsNullOrWhiteSpace(ip) ? DefaultIp : ip.Trim())}:{NormalizePort(port)}/ws";

    /// <summary>端口兜底：越界（手改配置写成 0 / 70000）就退回默认，别拿着一个连不上的端口去连</summary>
    public static int NormalizePort(int port)
        => port is >= MinPort and <= MaxPort ? port : DefaultPort;

    /// <summary>等待秒数兜底：范围外的值夹回范围内（0 或负数会让"等待"当场就超时）</summary>
    public static int NormalizeStartupWaitSeconds(int seconds)
        => seconds switch
        {
            < MinStartupWaitSeconds => DefaultStartupWaitSeconds,
            > MaxStartupWaitSeconds => MaxStartupWaitSeconds,
            _ => seconds
        };

    /// <summary>
    /// 把用户打的端口原文解析成数字。失败时 <paramref name="reason"/> 是错误原因。
    /// </summary>
    public static bool TryParsePortText(string? text, out int port, out string reason)
    {
        port = 0;
        reason = "";

        // 前后空格先去掉：从别处复制粘贴太常见了，而它会让 int.TryParse 整句失败
        var trimmed = (text ?? "").Trim();

        if (trimmed.Length == 0)
        {
            reason = "端口还没填";
            return false;
        }

        // NumberStyles.None：**不接受全角、正负号、千位分隔符**。
        // 默认的 NumberStyles.Integer 会放过前导空白与正号，那正是"看起来对、
        // 其实存进去的是别的东西"的来源。全角数字会在这里被挡下。
        if (!int.TryParse(trimmed, System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out port))
        {
            reason = "端口要填半角数字（全角数字 １２３ 认不出来）";
            return false;
        }

        if (port is < MinPort or > MaxPort)
        {
            reason = $"端口要在 {MinPort}~{MaxPort} 之间";
            return false;
        }

        return true;
    }

    /// <summary>把用户打的等待秒数原文解析成数字。规则同 <see cref="TryParsePortText"/></summary>
    public static bool TryParseWaitSecondsText(string? text, out int seconds, out string reason)
    {
        seconds = 0;
        reason = "";

        var trimmed = (text ?? "").Trim();

        if (trimmed.Length == 0)
        {
            reason = "等待秒数还没填";
            return false;
        }

        if (!int.TryParse(trimmed, System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out seconds))
        {
            reason = "等待秒数要填半角数字";
            return false;
        }

        if (seconds is < MinStartupWaitSeconds or > MaxStartupWaitSeconds)
        {
            reason = $"等待秒数要在 {MinStartupWaitSeconds}~{MaxStartupWaitSeconds} 之间";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 拼给 <c>TosuClient</c> 与「测试连接」用的 WebSocket 地址。
    /// 放在这里而不是让调用方自己拼 —— 见 <see cref="BuildUrl"/>。
    /// </summary>
    [JsonIgnore]
    public string WebSocketUrl => BuildUrl(Ip, Port);
}

/// <summary>通用设置（定稿 §2⑥）</summary>
public sealed class GeneralConfig
{
    /// <summary>外观：<c>跟随系统</c> / <c>浅色</c> / <c>深色</c></summary>
    [JsonPropertyName("外观")] public string Appearance { get; set; } = "跟随系统";

    /// <summary>语言：<c>跟随系统</c> / <c>中文</c> / <c>English</c>。**只切界面文案，不改配置键名**（决策 4）</summary>
    [JsonPropertyName("语言")] public string Language { get; set; } = "跟随系统";

    /// <summary>字体：<c>跟随系统</c> 或具体字体名（如 <c>微软雅黑</c>）</summary>
    [JsonPropertyName("字体")] public string Font { get; set; } = "跟随系统";

    /// <summary>
    /// 基准字号，**单位 pt**（不是 DIP）。
    /// 名字里带 Points 就是为了提醒这件事 —— WPF 的 FontSize 是 DIP，两者差 96/72 倍。
    /// 各层级由 <c>FontScale</c> 按倍率算出来。
    /// </summary>
    [JsonPropertyName("字号")] public double FontSizePoints { get; set; } = 10.5;

    /// <summary>开机自启动（默认关）。写注册表是即时的，只是下次登录才体现。</summary>
    [JsonPropertyName("开机自启")] public bool AutoStart { get; set; }

    /// <summary>显示托盘图标</summary>
    [JsonPropertyName("托盘图标")] public bool TrayIcon { get; set; } = true;

    /// <summary>
    /// 关闭时最小化到托盘（默认开）。
    /// **它控制"X 号"的行为**（决策 26）：勾选＝缩到托盘、悬浮窗继续跑；取消＝直接退出。
    /// </summary>
    [JsonPropertyName("关闭时最小化到托盘")] public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>位置调整后自动保存（**默认关**，决策 6：默认询问而不是默认保存）</summary>
    [JsonPropertyName("位置调整后自动保存")] public bool AutoSavePlacement { get; set; }
}

/// <summary>体检的检查模式（只作用于体检清单第 7~10 条，定稿 §3.4）</summary>
public sealed class HealthCheckConfig
{
    /// <summary>配置里存的原文：<c>宽松</c> / <c>严格</c></summary>
    [JsonPropertyName("检查模式")] public string ModeText { get; set; } = "宽松";

    /// <summary>
    /// 字符串 → 枚举。**未知值回落"宽松"** ——
    /// 与解析层"未知值回落默认"同一个策略，也与 <c>LengthStrategy</c> 那两处写法一致。
    /// </summary>
    [JsonIgnore]
    public RangeCheckMode Mode => ModeText switch
    {
        "严格" => RangeCheckMode.Strict,
        _ => RangeCheckMode.Lenient
    };
}

/// <summary>
/// 配置档案的软件侧信息：当前生效的是哪一份。
/// 档案文件本身在 <c>&lt;exe&gt;\profiles\&lt;名字&gt;.json</c>，
/// 命名与迁移规则见 <see cref="ProfileLocator"/>。
/// </summary>
public sealed class ProfileSettings
{
    /// <summary>
    /// 当前档案名。它对应 <c>profiles\&lt;这个名字&gt;.json</c>。
    /// 空值会被 <see cref="AppSettings.Normalize"/> 修成「默认」。
    /// </summary>
    [JsonPropertyName("当前")] public string Current { get; set; } = ProfileLocator.DefaultProfileName;
}

/// <summary>
/// 日志：目录、保留月数、详细程度。
///
/// 目录原来单独放在一个 `目录{}` 段里（还带着"档案""角色分布"两个字段），
/// **2026-09-20 整段删掉**：那两个字段是"先有字段再找用途"的产物 ——
/// 代码里没有任何一处读它们（用户一问"角色分布是什么"就露馅了）。
/// 而唯一真在用的"日志目录"，放进日志段里最自然。
/// </summary>
public sealed class LogSettings
{
    /// <summary>
    /// 日志放在哪。**留空 = 程序旁边的 `logs` 文件夹**
    /// （默认值的名字由 <see cref="DirectoryResolver.LogsFolderName"/> 定义，
    /// 设置页显示的路径与 DebugLog 真正写的目录共用那一个常量）。
    /// </summary>
    [JsonPropertyName("目录")] public string Directory { get; set; } = "";

    /// <summary>自动清除多少个月之前的日志（默认 5）</summary>
    [JsonPropertyName("保留月数")] public int RetainMonths { get; set; } = 5;

    /// <summary>详细调试输出（把"表情未播放"这类开发期信息也写进日志）</summary>
    [JsonPropertyName("详细输出")] public bool Verbose { get; set; }
}
