// ============================================================
// PluginConfig.cs —— 配置模型
//
// 设计原则（用户定的）：**插件不做"针对特定模型"的增强，把可配的都做成配置**。
//   · 模型路径、动作/表情清单 —— 换个人用别的模型只改这里
//   · 连击/失误 配哪个表情 —— 用户自己挑
//   · 位置与大小 —— 用户自己调
// 所以这个文件里没有任何"绒绒"专属的硬编码。
// ============================================================
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuLive2dOverlay;

public sealed class PluginConfig
{
    [JsonPropertyName("模型")] public ModelConfig Model { get; set; } = new();
    [JsonPropertyName("视图")] public ViewConfig View { get; set; } = new();
    [JsonPropertyName("连击触发")] public List<ComboTrigger> ComboTriggers { get; set; } = new();
    [JsonPropertyName("失误触发")] public MissConfig Miss { get; set; } = new();
    [JsonPropertyName("语音")] public VoiceConfig Voice { get; set; } = new();
    [JsonPropertyName("口型")] public MouthConfig Mouth { get; set; } = new();
    [JsonPropertyName("表情持续时间毫秒")] public int ExpressionDurationMs { get; set; } = 1500;

    /// <summary>是否让角色目光跟随鼠标。
    /// 说明：窗口是点击穿透的，WebView2 收不到鼠标事件，所以由 C# 侧读全局光标位置发给页面。</summary>
    [JsonPropertyName("目光跟随鼠标")] public bool FollowCursor { get; set; } = true;

    /// <summary>
    /// 调试模式：把"表情未播放""库里拒绝"这类开发期信息显示在画面上。
    /// 默认 false —— 面向玩家时这些都属噪音（多数情况下玩家本来也察觉不到，提示出来反而像报错）。
    /// </summary>
    [JsonPropertyName("调试模式")] public bool DebugMode { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // 允许配置文件里写注释
        AllowTrailingCommas = true,                        // 允许末尾多余逗号
        WriteIndented = true
    };

    /// <summary>从程序目录读取 config.json；读不到就用默认值（并生成一份模板）</summary>
    public static PluginConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<PluginConfig>(json, Options) ?? new PluginConfig();
            }

            var fresh = new PluginConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, Options));
            return fresh;
        }
        catch
        {
            return new PluginConfig();   // 配置坏了也别崩，用默认值跑
        }
    }
}

public sealed class ModelConfig
{
    /// <summary>模型所在目录（可以放在仓库外，避免授权问题）</summary>
    [JsonPropertyName("目录")] public string Directory { get; set; } = "";

    /// <summary>模型定义文件名</summary>
    [JsonPropertyName("入口")] public string Entry { get; set; } = "";

    /// <summary>待机动作的分组名（注册进 model3.json 时使用）</summary>
    [JsonPropertyName("待机动作")] public string IdleGroup { get; set; } = "Idle";

    /// <summary>待机动作文件（相对模型目录）</summary>
    [JsonPropertyName("待机动作文件")] public string IdleFile { get; set; } = "";

    /// <summary>表情清单：名称 → 文件。用户按自己模型的情况填写</summary>
    [JsonPropertyName("表情")] public List<ExpressionConfig> Expressions { get; set; } = new();
}

public sealed class ExpressionConfig
{
    [JsonPropertyName("名称")] public string Name { get; set; } = "";
    [JsonPropertyName("文件")] public string File { get; set; } = "";
}

public sealed class ViewConfig
{
    /// <summary>缩放倍数（1 = 模型刚好完整装进画布）</summary>
    [JsonPropertyName("缩放")] public double Zoom { get; set; } = 1.0;

    /// <summary>上下位置（正数下移，用于放大后把脸移进画面）</summary>
    [JsonPropertyName("上下位置")] public double OffsetY { get; set; }

    /// <summary>左右位置</summary>
    [JsonPropertyName("左右位置")] public double OffsetX { get; set; }
}

public sealed class ComboTrigger
{
    /// <summary>连击达到这个数时触发</summary>
    [JsonPropertyName("阈值")] public int Threshold { get; set; }

    /// <summary>播放哪个表情（对应"模型.表情"里的名称）</summary>
    [JsonPropertyName("表情")] public string Expression { get; set; } = "";

    /// <summary>
    /// 这次事件说哪一类话（对应"语音.情绪"里的名称）。留空 = 这件事不出声。
    /// ★ 和「表情」是两件独立的事：选表情靠模型，选语音靠情绪，互不依赖。
    /// </summary>
    [JsonPropertyName("语音情绪")] public string VoiceEmotion { get; set; } = "";
}

/// <summary>
/// 断连击时的表现：分两档，**每档都有门槛**。
///
/// 为什么要有门槛（用户按皮肤作者的通行做法定的）：
///   断连击前手里得先有像样的连击，"可惜"这件事才成立。
///   玩得菜的人连 50 都上不去，如果一断就叹气、就安慰，只会让人更难受 ——
///   所以连击还没到「小额门槛」就断了，表情和语音都**什么都不做**。
///   顺带一提，门槛本身就是最好的节流：频繁触发的场景从源头上就没了。
/// </summary>
public sealed class MissConfig
{
    /// <summary>断连击前手里的连击数 ≥ 这个值，才用"小额"那一档</summary>
    [JsonPropertyName("小额门槛")] public int SmallThreshold { get; set; } = 50;

    /// <summary>掉连击数较小时用的表情</summary>
    [JsonPropertyName("小额表情")] public string SmallExpression { get; set; } = "";

    /// <summary>小额失误说哪一类话（留空 = 不出声）</summary>
    [JsonPropertyName("小额语音情绪")] public string SmallVoiceEmotion { get; set; } = "";

    /// <summary>断连击前手里的连击数 ≥ 这个值，改用"大额"那一档</summary>
    [JsonPropertyName("大额门槛")] public int BigThreshold { get; set; } = 100;

    /// <summary>断掉大连击时用的表情</summary>
    [JsonPropertyName("大额表情")] public string BigExpression { get; set; } = "";

    /// <summary>大额失误说哪一类话（留空 = 不出声）</summary>
    [JsonPropertyName("大额语音情绪")] public string BigVoiceEmotion { get; set; } = "";
}

/// <summary>语音（与表情完全解耦的那一半：表情负责"演什么"，语音负责"说什么"）</summary>
public sealed class VoiceConfig
{
    [JsonPropertyName("启用")] public bool Enabled { get; set; }

    /// <summary>语音文件所在目录（和模型一样，建议放在仓库外 —— 从游戏里拆出来的音频不能公开）</summary>
    [JsonPropertyName("目录")] public string Directory { get; set; } = "";

    /// <summary>音量 0~1</summary>
    [JsonPropertyName("音量")] public double Volume { get; set; } = 0.8;

    /// <summary>两次语音之间至少间隔多少毫秒（0 = 不限）</summary>
    [JsonPropertyName("最小间隔毫秒")] public int MinIntervalMs { get; set; } = 300;

    /// <summary>情绪清单：名称 → 一批文件。同一情绪里有多个文件时会随机挑一个</summary>
    [JsonPropertyName("情绪")] public List<EmotionConfig> Emotions { get; set; } = new();
}

/// <summary>
/// 一类情绪（高兴 / 疑惑 / 伤心 …）。
/// 填法有三种，越往下越省事：
///   ① 写「文件」列表 —— 最明确，也最啰嗦
///   ② 写「子目录」   —— 播 目录\子目录 下的所有音频
///   ③ 什么都不写     —— 自动找 目录\名称 这个同名文件夹
/// 所以文件多了以后，直接在语音目录里建「高兴」「伤心」这样的文件夹丢进去就行。
/// </summary>
public sealed class EmotionConfig
{
    [JsonPropertyName("名称")] public string Name { get; set; } = "";
    [JsonPropertyName("文件")] public List<string> Files { get; set; } = new();
    [JsonPropertyName("子目录")] public string SubDirectory { get; set; } = "";
}

/// <summary>
/// 口型：语音在播的这段时间里让角色张嘴（不然只有声音、人不动，很出戏）。
/// 只配了"开关 + 参数名 + 幅度"，没有做模型专属的东西 —— 参数名留空就自动找。
/// </summary>
public sealed class MouthConfig
{
    [JsonPropertyName("启用")] public bool Enabled { get; set; } = true;

    /// <summary>口型参数名。留空 = 自动找（优先 ParamMouthOpenY，其次名字里含 MouthOpen / Mouth 的）</summary>
    [JsonPropertyName("参数")] public string Parameter { get; set; } = "";

    /// <summary>张嘴幅度 0~1（觉得太夸张就调小）</summary>
    [JsonPropertyName("幅度")] public double Strength { get; set; } = 1.0;
}
