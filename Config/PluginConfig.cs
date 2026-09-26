// ============================================================
// PluginConfig.cs —— 配置模型
//
// 设计原则：**插件不做"针对特定模型"的增强，把可配的都做成配置**。
//   · 模型路径、动作/表情清单 —— 换个人用别的模型只改这里
//   · 连击/失误 配哪个表情 —— 用户自己挑
//   · 位置与大小 —— 用户自己调
// 所以这个文件里没有任何Live2d模型专属的硬编码。
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
    /// 被摸到的时候播什么（★ 2026-09-25 新增）。
    ///
    /// **一个列表**：点一下角色，从里面随机挑一条播。
    /// 每一条就是一个 <see cref="ReactionConfig"/>（瞬时表情 / 动作 / 语音情绪）——
    /// 和「连击触发」「失误反应」用的是同一个类型，所以界面控件与播放那条链都是现成的。
    ///
    /// **它和「允许触摸」是两件事，别混**：
    ///   · 「允许触摸」在每个界面的档位里（`界面感知.选歌.允许触摸`）—— 管**哪些界面能点**
    ///   · 这一段是全局的 —— 管**点到了播什么**
    ///   两个都满足才会响。（打歌界面本来就是穿透的，鼠标根本到不了，所以永远点不到。）
    ///
    /// **为什么是"随机挑"而不是"按顺序轮"**：用户点它就是想逗一下，每次播同一个会显得死。
    /// 代价是连点两次可能挑到同一条 —— 要避免得记住"上一次"，那就不纯了
    /// （先不做，真觉得别扭再说）。
    /// </summary>
    [JsonPropertyName("触摸")] public List<ReactionConfig> Touch { get; set; } = new List<ReactionConfig>();

    
    /// <summary>
    /// 配置结构版本，为将来迁移用。
    /// 键名是**英文**（与其他中文键名不同）—— 它是给程序看的元数据，不是给用户配的。
    /// **两个配置文件各自有一份**：这个管档案，`AppSettings`（settings.json）管软件设置。
    /// （这里故意不写 `<see cref="AppSettings"/>` —— 验证工程常常只链接本文件，
    ///   引用一个它没有的类型会报 CS1574 警告；注释里直接写名字就够了。）
    /// </summary>
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;

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
    /// 读写配置统一的序列化选项。
    /// 运行期改配置（比如把拖动后的窗口位置写回去）也要用它，所以是 internal 而不是 private。
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // 允许配置文件里写注释
        AllowTrailingCommas = true,                        // 允许末尾多余逗号
        WriteIndented = true,

        // **必须关掉"非 ASCII 转义"**：默认行为会把中文键名写成 \u754C\u9762\u611F\u77E5 这种
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>从给定路径读取档案配置；读不到就用默认值（并生成一份模板）。
    /// 路径由 <c>ProfileLocator</c> 算出来（`profiles\&lt;当前档案名&gt;.json`）。</summary>
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
        Voice.Emotions ??= new List<string>();

        foreach (var trigger in ComboTriggers) trigger.Reaction ??= new ReactionConfig();

        Touch ??= new List<ReactionConfig>();

        // 手改 JSON 时可能写出 null 元素（`"触摸": [null]`）——
        // 挑到"什么都没有"的一条，表现就是"点了没反应"，所以直接扔掉。
        Touch.RemoveAll(reaction => reaction is null);

        
        Scenes ??= BuildDefaultScenes();
        NormalizeScenes();
    }

    /// <summary>
    /// 生成默认的四份档位：都启用、站位沿用全局视图参数，窗口位置留空（窗口不动）。
    /// 只在"配置文件里还没有界面感知这一段"时用 —— 也就是一次迁移。
    /// </summary>
    private SceneProfiles BuildDefaultScenes()
    {
        var entry = Model.Entry ?? "";
        var usable = !string.IsNullOrWhiteSpace(entry);

        SceneProfile Make(bool clickThrough) => new()
        {
            Enabled = usable,
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

    /// <summary>语音文件所在目录</summary>
    [JsonPropertyName("目录")] public string Directory { get; set; } = "";

    /// <summary>音量 0~1</summary>
    [JsonPropertyName("音量")] public double Volume { get; set; } = 0.8;

    /// <summary>两次语音之间至少间隔多少毫秒（0 = 不限）</summary>
    [JsonPropertyName("最小间隔毫秒")] public int MinIntervalMs { get; set; } = 300;

    /// <summary>
    /// 情绪清单。**每一项就是一个子文件夹名**，情绪名 = 文件夹名。
    ///
    /// 只有一个填法（用户定的）：以前还支持「逐条列文件」和「另写子目录」两种，
    /// 结果是界面上必须同时摆出"文件列表"和"选文件夹"两套控件，用户还得分清两者的关系 ——
    /// 那是纯自找的复杂度。现在一个情绪 = 一个文件夹，界面只剩一件事：选文件夹 → 多一个情绪。
    /// </summary>
    [JsonPropertyName("情绪")]
    [JsonConverter(typeof(EmotionNameListConverter))]
    public List<string> Emotions { get; set; } = new();
}

/// <summary>
/// 「情绪」的读取转换器：新写法是字符串数组，老写法是对象数组（名称 / 文件 / 子目录）。
///
/// 为什么需要它：老配置里写着情绪名，直接换成字符串数组会让它们**全部消失** ——
/// 语音一声不响地不响了，而且看不出来是升级造成的。所以读的时候把老写法就地折成名字。
///
/// 只在读的时候生效；**写出来永远是字符串数组**（新写法）。
/// 老写法里的「文件」列表没法自动变成文件夹（那些文件是平铺在语音目录根下的），
/// 所以只能保住名字 —— 折叠后如果找不到同名文件夹，VoiceLibrary 会给出明确提示。
/// </summary>
internal sealed class EmotionNameListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var names = new List<string>();

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();          // 既不是数组就整段跳过 —— 配置坏了也别崩
            return names;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    Add(names, reader.GetString());
                    break;

                case JsonTokenType.StartObject:
                    using (var doc = JsonDocument.ParseValue(ref reader))
                        Add(names, LegacyNameOf(doc.RootElement));
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        return names;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var name in value ?? new List<string>()) writer.WriteStringValue(name);
        writer.WriteEndArray();
    }

    private static void Add(List<string> names, string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length > 0) names.Add(trimmed);
    }

    /// <summary>老写法里挑一个名字出来：「名称」→「子目录」→「文件」第一个所在的文件夹</summary>
    private static string? LegacyNameOf(JsonElement emotion)
    {
        foreach (var key in new[] { "名称", "子目录" })
            if (emotion.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String)
            {
                var value = text.GetString()?.Trim() ?? "";
                if (value.Length > 0) return Path.GetFileName(value.TrimEnd('\\', '/'));
            }

        if (emotion.TryGetProperty("文件", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.String) continue;
                var dir = Path.GetDirectoryName(file.GetString() ?? "");
                if (!string.IsNullOrWhiteSpace(dir)) return Path.GetFileName(dir.TrimEnd('\\', '/'));
            }

        return null;
    }
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

