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
    [JsonPropertyName("常驻组件")] public List<string> PersistentParts { get; set; } = new List<string>();
    [JsonPropertyName("常态区间")] public List<ComboRanger> Ranges { get; set; } = new List<ComboRanger>();
    [JsonPropertyName("视图")] public ViewConfig View { get; set; } = new();
    [JsonPropertyName("连击触发")] public List<ComboTrigger> ComboTriggers { get; set; } = new();
    [JsonPropertyName("失误触发")] public MissConfig Miss { get; set; } = new();
    [JsonPropertyName("语音")] public VoiceConfig Voice { get; set; } = new();
    [JsonPropertyName("口型")] public MouthConfig Mouth { get; set; } = new();
    [JsonPropertyName("表现")] public PerformanceConfig Performance { get; set; } = new PerformanceConfig();

    /// <summary>
    /// 界面感知：四个界面各一份档位（显示什么、显示在哪、能不能点）。
    ///
    /// **可空是有意的**：null = 配置文件里没有这一段（老配置，或者第一次生成的模板）。
    /// 不能直接写成 `= new()` —— 那样就分不清"用户从来没配过"和"用户配了、但四个界面都关掉"，
    /// 而这两种情况的处理正好相反：前者要把原来的全局设置搬进来（不然升级后角色直接消失），
    /// 后者要尊重用户关掉的决定。
    /// </summary>
    [JsonPropertyName("界面感知")] public SceneProfiles? Scenes { get; set; }

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
                var config =  JsonSerializer.Deserialize<PluginConfig>(json, Options) ?? new PluginConfig();
                config.Normalize();
                return config;
            }

            // 先生成默认值、再写盘：这样模板是一份**完整**的配置（含界面感知那一段），
            // 而不是一堆 null —— 设置界面将来是直接读它来渲染的。
            var fresh = new PluginConfig();
            fresh.Normalize();
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, Options));
            return fresh;
        }
        catch
        {   
            var fallback = new PluginConfig();
            fallback.Normalize();
            return fallback ;   // 配置坏了也别崩，用默认值跑
        }
    }
    private void Normalize()
    {
        Model ??= new ModelConfig();
        View ??= new ViewConfig();
        Voice ??= new VoiceConfig();
        Mouth ??= new MouthConfig();
        Miss ??= new MissConfig();
        Performance ??= new PerformanceConfig();

        PersistentParts ??= new List<string>();
        Ranges ??= new List<ComboRanger>();
        ComboTriggers ??= new List<ComboTrigger>();

        Miss.SmallReaction ??= new ReactionConfig();
        Miss.BigReaction ??= new ReactionConfig();
        Voice.Emotions ??= new List<EmotionConfig>();

        foreach (var trigger in ComboTriggers) trigger.Reaction ??= new ReactionConfig();

        // 界面感知：老配置里没有这一段 → 用原来的全局模型与视图参数生成一份四档。
        // 不能让它默认"四档全关"：这一段直接决定角色显示不显示，那样老用户升级后角色会消失。
        // 有这一段就照它来（哪怕四档全关，那是用户自己的决定），只补齐可能被手改坏的嵌套对象。
        Scenes ??= BuildDefaultScenes();
        NormalizeScenes();
    }

    /// <summary>
    /// 生成默认的四份档位：都启用、都用全局模型入口与视图参数，窗口位置留空（窗口不动）。
    /// 只在"配置文件里还没有界面感知这一段"时用 —— 也就是一次迁移。
    /// </summary>
    private SceneProfiles BuildDefaultScenes()
    {
        var entry = Model.Entry ?? "";
        var usable = !string.IsNullOrWhiteSpace(entry);

        SceneProfile Make(bool clickThrough) => new()
        {
            Enabled = usable,
            Model = entry,
            Character = new CharacterPlacement
            {
                X = View.OffsetX,
                Y = View.OffsetY,
                Scale = View.Zoom <= 0 ? 1.0 : View.Zoom,
                AngleDegrees = 0
            },
            // 窗口位置故意不填（X / Y 留 null）→ 窗口保持现在待的地方，不因为升级跳走
            Window = new WindowPlacement { ClickThrough = clickThrough }
        };

        return new SceneProfiles
        {
            MainMenu = Make(clickThrough: false),
            SongSelect = Make(clickThrough: false),
            Playing = Make(clickThrough: true),      // 打歌必须穿透，不然鼠标点不到谱面
            Result = Make(clickThrough: false)
        };
    }

    /// <summary>补齐四份档位里可能缺失的嵌套对象（配置是界面生成的，但手改 JSON 时什么都可能出现）</summary>
    private void NormalizeScenes()
    {
        var scenes = Scenes!;
        scenes.MainMenu ??= new SceneProfile();
        scenes.SongSelect ??= new SceneProfile();
        scenes.Playing ??= new SceneProfile();
        scenes.Result ??= new SceneProfile();

        foreach (var profile in new[] { scenes.MainMenu, scenes.SongSelect, scenes.Playing, scenes.Result })
        {
            profile.Window ??= new WindowPlacement();
            profile.Character ??= new CharacterPlacement();
            profile.Model ??= "";
        }
    }
}

public class ComboRanger
{
    [JsonPropertyName("最小")] public int Min { get; set; }
    [JsonPropertyName("最大")] public int? Max { get; set; }
    [JsonPropertyName("表情")] public string Expression { get; set; } = "";
}
public sealed class ModelConfig
{
    /// <summary>模型所在目录（可以放在仓库外，避免授权问题）</summary>
    [JsonPropertyName("目录")] public string Directory { get; set; } = "";

    /// <summary>模型定义文件名</summary>
    [JsonPropertyName("入口")] public string Entry { get; set; } = "";

    /// <summary>待机动作的分组名（注册进 model3.json 时使用）</summary>
    [JsonPropertyName("待机动作")] public string IdleGroup { get; set; } = "";

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

public class ComboTrigger
{
    [JsonPropertyName("阈值")] public int Threshold { get; set; }
    [JsonPropertyName("反应")] public ReactionConfig Reaction { get; set; } = new ReactionConfig();
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
public class MissConfig
{
    [JsonPropertyName("小额门槛")] public int SmallThreshold { get; set; }
    [JsonPropertyName("小额反应")] public ReactionConfig SmallReaction { get; set; } = new ReactionConfig();
    [JsonPropertyName("大额门槛")] public int BigThreshold { get; set; }
    [JsonPropertyName("大额反应")] public ReactionConfig BigReaction { get; set; } = new ReactionConfig();
}
public class ReactionConfig
{
    [JsonPropertyName("瞬时表情")] public string Expression { get; set; } = "";
    [JsonPropertyName("动作")] public string Action { get; set; } = "";
    [JsonPropertyName("语音情绪")] public string VoiceEmotion { get; set; } = "";
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
/// <summary>
/// 表现
/// </summary>
public class PerformanceConfig
{
    [JsonPropertyName("时长策略")] public string LengthStrategyText { get; set; } = "取最长";
    [JsonIgnore]
    public LengthStrategyKind LengthStrategy => LengthStrategyText switch
    {
        "跟随表情" => LengthStrategyKind.ExpressionFirst,
        _ => LengthStrategyKind.Longest
    };
    [JsonPropertyName("重复触发策略")] public string RepeatStrategyText { get; set; } = "保留正在播放";
    [JsonIgnore]
    public RepeatStrategyKind RepeatStrategy => RepeatStrategyText switch
    {
        "切换到新的" => RepeatStrategyKind.SwitchToNew,
        _ => RepeatStrategyKind.KeepPlaying
    };

    [JsonPropertyName("表情持续时间毫秒")] public int ExpressionDurationMs { get; set; } = 1500;

}
public enum LengthStrategyKind { Longest, ExpressionFirst }
public enum RepeatStrategyKind { KeepPlaying, SwitchToNew }