// ============================================================
// ExpressionCatalog.cs —— 表情清单：标识 → "它到底改了什么参数"
//
// 为什么需要这一层：
//   页面（web/index.html）的职责是"把参数挂上去、什么时候撤"，它**不该去碰文件系统**。
//   文件在哪、里面写了什么，是 C# 侧的事 —— 这边读好、随消息下发，页面直接用。
//   顺带一个好处：读文件只在**启动时**做一次，之后每次触发都只是查字典。
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
///
/// 那三行 [JsonPropertyName] 不能省：这份数据要**序列化**成 JSON 发给页面，
/// 而序列化只认属性名原样（PropertyNameCaseInsensitive 只管反序列化那一个方向）。
/// 少了它就会输出 {"Id":...,"Value":...,"Blend":...}，页面里读的是 p.id / p.value，
/// 拿到 undefined —— 不报错，就是表情一个都不出来。
///
/// Blend 留 null 时按 "Add" 处理（页面只认 "Multiply"/"Overwrite" 两个特殊值）。
/// </summary>
public sealed record ExpressionParam(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("value")] float Value,
    [property: JsonPropertyName("blend")] string? Blend = "Add");

/// <summary>给设置界面用的"表情一览"里的一行</summary>
public sealed record ExpressionInfo(string Id, string DisplayName, int ParameterCount);

/// <summary>
/// exp3 文件的根。
/// 顶层还有 "Type"，用不上但留着 —— 将来要区分文件种类时用得到。
/// 属性名叫什么无所谓，靠特性对上 JSON 里的 "Parameters"。
/// </summary>
public class Exp3Root
{
    [JsonPropertyName("Type")] public string Type { get; set; } = "";

    /// <summary>要改的参数表</summary>
    [JsonPropertyName("Parameters")] public List<ExpressionParam>? Parameters { get; set; }
}

/// <summary>
/// cdi3 文件的根。
/// 顶层还有 Version / ParameterGroups / Parts / CombinedParameters，用不上就不声明 ——
/// 反序列化只认你声明了的字段，多出来的一律忽略。
/// </summary>
public class Cid3Root
{
    [JsonPropertyName("Parameters")] public List<Cdi3Parameter>? Parameters { get; set; }
}

/// <summary>cdi3 里的一个参数。文件里还有 GroupId，我们用不上，所以不声明。</summary>
public sealed record Cdi3Parameter(string? Id, string? Name);

/// <summary>
/// 表情清单。启动时建一次（<see cref="Build"/>），之后全是查字典。
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

    /// <summary>
    /// 读文件时的选项。
    ///
    /// PropertyNameCaseInsensitive **必须开**，原因值得记一笔：
    ///   同一份数据要伺候两个方向 —— 读 exp3 文件时，文件里写的是 "Id"、"Parameters"（大写开头）；
    ///   发给页面时，JS 读的是 p.id / p.value（小写）。
    ///   而 [JsonPropertyName] 是**双向生效**的，我们把 ExpressionParam 标成了小写，
    ///   反序列化时就会拿 "id" 去匹配文件里的 "Id" —— 对不上，参数表直接变成空的。
    ///   打开这个开关，两个方向就都通了。
    /// </summary>
    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private ExpressionCatalog(
        Dictionary<string, IReadOnlyList<ExpressionParam>> parameters,
        Dictionary<string, string> displayNames,
        IReadOnlyList<string> problems)
    {
        _parameters = parameters;
        _displayNames = displayNames;
        Problems = problems;
    }

    // ---------------- 查询（触发时走这里，不碰磁盘） ----------------

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

        // cdi3 全模型只有一个，先找出来 —— 找不到也不影响表情本身能用
        var parameterNames = ReadParameterNames(FindCdi3(modelDirectory));

        foreach (var resource in scan.Expressions)
        {
            if (resource.Files.Count == 0) continue;

            var (list, problem) = ReadExpression(Path.Combine(modelDirectory, resource.Files[0]));
            if (problem is not null) problems.Add(problem);

            parameters[resource.Id] = list;
            displayNames[resource.Id] = Describe(resource.Id, list, parameterNames);
        }

        return new ExpressionCatalog(parameters, displayNames, problems);
    }

    // ---------------- 读文件 ----------------

    /// <summary>
    /// 读一个 exp3 文件，取出它的参数表。
    ///
    /// 返回 (参数表, 问题)，两个字段不会同时有内容：
    ///   成功 → (有内容, null)
    ///   失败 → (空表, "人话原因")
    ///
    /// 绝不抛异常：一个表情的文件坏掉，不该让整个程序起不来。
    /// 注意"空"分两种，别混成一句：缺 Parameters 字段 = 文件坏了；
    /// Parameters 是空数组 = 作者故意留空（官方模型的 Normal 就是"恢复默认脸"）。
    /// </summary>
    public static (IReadOnlyList<ExpressionParam> Parameters, string? Problem) ReadExpression(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (Array.Empty<ExpressionParam>(), "文件路径为空");

        try
        {
            var fullPath = Path.GetFullPath(filePath);          // 非法路径会在这里抛 → 必须在 try 内
            if (!File.Exists(fullPath))
                return (Array.Empty<ExpressionParam>(), "文件不存在");

            var root = JsonSerializer.Deserialize<Exp3Root>(File.ReadAllText(fullPath), FileOptions);

            if (root?.Parameters is null)
                return (Array.Empty<ExpressionParam>(), $"{filePath} 缺少 Parameters 字段，文件可能损坏");

            if (root.Parameters.Count == 0)
                return (Array.Empty<ExpressionParam>(), "参数表是空的（作者留空 = 恢复默认脸）");

            var result = new List<ExpressionParam>(root.Parameters.Count);
            foreach (var item in root.Parameters)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;   // 没写参数名的条目没有意义
                result.Add(item);
            }

            return result.Count > 0
                ? (result.AsReadOnly(), null)
                : (Array.Empty<ExpressionParam>(), $"{filePath} 里的参数都没写名字");
        }
        catch (Exception ex)
        {
            return (Array.Empty<ExpressionParam>(), $"读取文件失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 读 cdi3，取出"参数 Id → 人话名字"。
    ///
    /// 没有 cdi3 / 读不动 → 空表。这**不算错** —— 很多模型就是没给自己的参数配名字，
    /// 那就老老实实显示标识（"3.exp3"），别编一个名字出来。所以这里不返回原因，
    /// 空表本身就是答案。
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadParameterNames(string? cdi3Path)
    {
        var empty = new Dictionary<string, string>().AsReadOnly();
        if (string.IsNullOrWhiteSpace(cdi3Path)) return empty;

        try
        {
            var fullPath = Path.GetFullPath(cdi3Path);
            if (!File.Exists(fullPath)) return empty;

            var root = JsonSerializer.Deserialize<Cid3Root>(File.ReadAllText(fullPath), FileOptions);
            if (root?.Parameters is not { Count: > 0 } rawList) return empty;

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var param in rawList)
            {
                if (string.IsNullOrWhiteSpace(param.Id) || string.IsNullOrWhiteSpace(param.Name)) continue;
                names[param.Id] = param.Name;
            }
            return names;
        }
        catch
        {
            // cdi3 坏了只意味着"没有名字可显示"，表情该怎么演还怎么演
            return empty;
        }
    }

    /// <summary>
    /// 给一个表情起个"人话名字"。
    ///
    /// 规则：**恰好只改一个参数、且那个参数在 cdi3 里有名字**时才用它的名字；
    ///       其它情况一律返回标识本身。
    ///
    /// 为什么要卡"恰好一个"：这话的本质是在问**这个名字能不能代表整个表情**。
    ///   绒绒的 3.exp3 只改 Param150，整个表情就是"开星星眼" → 叫「星星眼」准确。
    ///   Epsilon 的 Angry 一口气改 15 个参数（眉毛、眼睛、嘴、脸颊…），
    ///   挑哪一个都只是它的一部分，显示出来反而误导 ——
    ///   而这种模型的文件名本身就是人看得懂词（Angry / Blushing / Sad），标识够用了。
    ///
    /// 空白不算名字（这个方法对外公开，任何人都能塞一个字典进来）。
    /// </summary>
    public static string Describe(string id, IReadOnlyList<ExpressionParam> parameters,
                                  IReadOnlyDictionary<string, string> parameterNames)
    {
        if (parameters.Count == 1
            && parameterNames.TryGetValue(parameters[0].Id ?? "", out var name)
            && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return id;
    }

    /// <summary>
    /// 给设置界面用的表情一览（每个表情一行：标识 / 人话名字 / 参数个数）。
    ///
    /// 按标识做 Ordinal 排序 —— 用默认排序会把 "10.exp3" 排到 "2.exp3" 前面，用户看着像乱的。
    ///
    /// **调用方负责保证 modelDirectory 存在**：目录不对时请在外面提示并停下，
    /// 这里不判断也不兜底（目录不存在会直接抛 DirectoryNotFoundException）。
    /// </summary>
    public static IReadOnlyList<ExpressionInfo> ListAll(ModelScanResult scan, string modelDirectory)
    {
        var parameterNames = ReadParameterNames(FindCdi3(modelDirectory));

        var rows = new List<ExpressionInfo>();
        foreach (var resource in scan.Expressions)
        {
            if (resource.Files.Count == 0) continue;

            var (list, _) = ReadExpression(Path.Combine(modelDirectory, resource.Files[0]));
            rows.Add(new ExpressionInfo(resource.Id, Describe(resource.Id, list, parameterNames), list.Count));
        }

        rows.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return rows;
    }

    /// <summary>
    /// 模型目录下找一个 cdi3。
    /// 约定跟入口文件同名，但这里不挑名字，找到第一个就用；**递归找**是因为
    /// 有些模型把 cdi3 放在子目录里（比如 runtime\ 下），只翻顶层会漏掉。
    /// </summary>
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
}
