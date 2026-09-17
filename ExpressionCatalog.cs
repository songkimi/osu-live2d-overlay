// ============================================================
// ExpressionCatalog.cs —— 表情清单：标识 → "它到底改了什么参数"
//
// 为什么需要这一层：
//   页面（web/index.html）的职责是"把参数挂上去、什么时候撤"，它**不该去碰文件系统**。
//   文件在哪、里面写了什么，是 C# 侧的事 —— 这边读好、随消息下发，页面直接用。
//   顺带一个好处：读文件只在启动时做一次，之后每次触发都只是查字典。
//
// 这里的两份数据都是从文件里**读出来的明文**，不是猜的：
//   · exp3 —— 里面没有"表情"，只有一句句参数赋值：
//       { "Id": "Param150", "Value": 1, "Blend": "Add" }   → 把 Param150 加 1
//     VTS 导出的模型（绒绒）一个表情只改一个参数；
//     官方模型（Epsilon）一个表情改十几个参数（Angry 改 15 个）。
//   · cdi3 —— Live2D **官方标准**的"显示辅助文件"，给参数配人话名字：
//       绒绒的 Param150 叫「星星眼」，Epsilon 的 PARAM_TERE 叫「照れ」。
//     VTS 成品和官方模型都可能带它。反过来 .vtube.json 是 VTube Studio 专有的，
//     官方模型根本没有 —— 所以不去碰它。
// ============================================================
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuLive2dOverlay;

/// <summary>
/// exp3 里的一条参数赋值。
/// 序列化成页面认识的样子：{id, value, blend} —— 页面按小写字段读，所以这里显式标名字，
/// 不依赖全局的序列化策略（那个一改，页面就瞎了）。
/// </summary>
public sealed record ExpressionParam(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("value")] float Value,
    [property: JsonPropertyName("blend")] string Blend);

/// <summary>
/// 表情清单。启动时建一次，之后全是查字典。
/// </summary>
public sealed class ExpressionCatalog
{
    private readonly Dictionary<string, IReadOnlyList<ExpressionParam>> _parameters =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _displayNames =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读文件时遇到的麻烦（人话，给日志用；单个表情坏掉不影响其它的）</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>还没有清单时用的空实例（查什么都是空，不抛异常）</summary>
    public static ExpressionCatalog Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<ExpressionParam>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>());

    private ExpressionCatalog(
        Dictionary<string, IReadOnlyList<ExpressionParam>> parameters,
        Dictionary<string, string> displayNames,
        IReadOnlyList<string> problems)
    {
        _parameters = parameters;
        _displayNames = displayNames;
        Problems = problems;
    }

    /// <summary>这个表情要改哪些参数。没这个标识 / 文件读不出来 → 空表（页面就什么都不做）。</summary>
    public IReadOnlyList<ExpressionParam> ParametersOf(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Array.Empty<ExpressionParam>();
        return _parameters.TryGetValue(id.Trim(), out var list) ? list : Array.Empty<ExpressionParam>();
    }

    /// <summary>这个人话名字（"3.exp3" → "星星眼"）。取不到就返回标识本身。</summary>
    public string DisplayName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        return _displayNames.TryGetValue(id.Trim(), out var name) ? name : id;
    }

    /// <summary>
    /// 把一次扫描的结果整份读成清单。
    /// 扫描出来的表情是磁盘上真实存在的 exp3 文件（绒绒 17 个、Epsilon 8 个），
    /// 全部读一遍也就几十次文件读，换来"之后每次触发只查字典"。
    /// </summary>
    public static ExpressionCatalog Build(ModelScanResult scan, string modelDirectory)
    {
        var parameters = new Dictionary<string, IReadOnlyList<ExpressionParam>>(StringComparer.OrdinalIgnoreCase);
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        // cdi3 只有一个（跟入口文件同名），先找出来 —— 找不到也不影响表情本身能用
        var parameterNames = ReadParameterNames(FindCdi3(modelDirectory));

        foreach (var resource in scan.Expressions)
        {
            if (resource.Files.Count == 0) continue;

            var fullPath = Path.Combine(modelDirectory, resource.Files[0]);
            var (list, problem) = ReadExpression(fullPath);
            if (problem is not null) problems.Add(problem);

            parameters[resource.Id] = list;
            displayNames[resource.Id] = Describe(resource.Id, list, parameterNames);
        }

        return new ExpressionCatalog(parameters, displayNames, problems);
    }

    // ---------------- 以下三个是从文件里读明文 ----------------

    /// <summary>
    /// 读一个 exp3 文件，取出它的参数表。
    /// 读不到 / 格式不对 → 空表 + 一句原因。
    /// 绝不抛异常：一个表情的文件坏掉，不该让整个程序起不来。
    /// </summary>
    public static (IReadOnlyList<ExpressionParam> Parameters, string? Problem) ReadExpression(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (Array.Empty<ExpressionParam>(), "没有拿到表情文件路径");

        try
        {
            if (!File.Exists(filePath))
                return (Array.Empty<ExpressionParam>(), $"表情文件不存在：{filePath}");

            var doc = JsonSerializer.Deserialize<Exp3File>(File.ReadAllText(filePath), JsonOptions);
            var raw = doc?.Parameters;
            if (raw is null || raw.Count == 0)
                return (Array.Empty<ExpressionParam>(), $"表情文件里没有参数：{Path.GetFileName(filePath)}");

            var list = new List<ExpressionParam>(raw.Count);
            foreach (var p in raw)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) continue;          // 没写参数名的条目没有意义
                list.Add(new ExpressionParam(p.Id!, p.Value, p.Blend ?? ""));
            }

            return list.Count > 0
                ? (list, null)
                : (Array.Empty<ExpressionParam>(), $"表情文件里的参数都没写名字：{Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            return (Array.Empty<ExpressionParam>(),
                    $"读表情文件失败：{Path.GetFileName(filePath)} → {ex.Message}");
        }
    }

    /// <summary>
    /// 读 cdi3，取出"参数 Id → 人话名字"。
    /// 没有 cdi3 / 读不动 → 空表。这**不算错** —— 很多模型就是没给自己的参数配名字，
    /// 那就老老实实显示标识（"3.exp3"），别编一个名字出来。
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadParameterNames(string? cdi3Path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(cdi3Path) || !File.Exists(cdi3Path)) return map;

        try
        {
            var doc = JsonSerializer.Deserialize<Cdi3File>(File.ReadAllText(cdi3Path), JsonOptions);
            if (doc?.Parameters is null) return map;

            foreach (var p in doc.Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name)) continue;
                map[p.Id!] = p.Name!;
            }
        }
        catch
        {
            // cdi3 坏了只意味着"没有名字可显示"，表情该怎么演还怎么演
        }

        return map;
    }

    /// <summary>
    /// 给一个表情起个人话名字。
    ///
    /// 规则：**恰好只改一个参数、且那个参数在 cdi3 里有名字**时才用它的名字。
    ///   绒绒的 3.exp3 只改 Param150，cdi3 说 Param150 是「星星眼」→ 显示"星星眼"。
    /// 为什么参数多的时候不用：
    ///   Epsilon 的 Angry 一口气改 15 个参数（眉毛、眼睛、嘴、脸颊…），
    ///   挑哪一个都只是"这个表情的一部分"，显示出来反而误导。
    ///   而这种模型的文件名本身通常就是人看得懂的词（Angry / Blushing / Smile / Sad），
    ///   标识已经够用了。
    /// </summary>
    public static string Describe(string id, IReadOnlyList<ExpressionParam> parameters,
                                 IReadOnlyDictionary<string, string> parameterNames)
    {
        if (parameters.Count == 1
            && parameterNames.TryGetValue(parameters[0].Id, out var name)
            && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return id;
    }

    /// <summary>模型目录下找一个 cdi3（约定跟入口文件同名，这里不挑名字，找到第一个就用）</summary>
    private static string? FindCdi3(string modelDirectory)
    {
        try
        {
            if (!Directory.Exists(modelDirectory)) return null;
            var found = Directory.GetFiles(modelDirectory, "*.cdi3.json", SearchOption.AllDirectories);
            return found.Length > 0 ? found[0] : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- 文件格式（只用到这两份文件里我们要的那几个字段） ----------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class Exp3File
    {
        [JsonPropertyName("Parameters")] public List<Exp3Parameter>? Parameters { get; set; }
    }

    private sealed class Exp3Parameter
    {
        [JsonPropertyName("Id")] public string? Id { get; set; }

        [JsonPropertyName("Value")] public float Value { get; set; }

        /// <summary>缺省时留空串 —— 页面按库的规矩把"不是 Multiply/Overwrite 的"都当 Add 处理</summary>
        [JsonPropertyName("Blend")] public string? Blend { get; set; }
    }

    private sealed class Cdi3File
    {
        [JsonPropertyName("Parameters")] public List<Cdi3Parameter>? Parameters { get; set; }
    }

    private sealed class Cdi3Parameter
    {
        [JsonPropertyName("Id")] public string? Id { get; set; }

        [JsonPropertyName("Name")] public string? Name { get; set; }
    }
}
