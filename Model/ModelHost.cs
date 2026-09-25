// ============================================================
// ModelHost.cs —— 把模型"接进"WebView2（不改用户模型文件，仓库里也不放模型资产）
//
// 为什么要走"临时目录 + 虚拟主机"这条路：
//   最初想在内存里拦截 model3.json 的请求再返回补丁版，但实测 WebView2 的
//   WebResourceRequested 对"虚拟主机映射"的请求不生效 —— 网页拿到的是原始文件，
//   结果动作组和表情全是 0 个（模型能显示，但不会切表情）。
//   改成：把补丁后的 JSON 落到临时目录，再把该目录也映射成虚拟主机，确定性 100%。
//
// 四条映射：
//   app.local    → 插件的网页（web/ 目录）
//   model.local  → 用户的模型目录（原始资产，只读）
//   patch.local  → 插件生成的临时目录（补丁后的 model3.json）
//   voice.local  → 用户的语音目录（可选；没配就不映射）
// ============================================================
using System.IO;
using System.Text.Json;

namespace OsuLive2dOverlay;

public static class ModelHost
{
    public const string AppHost = "app.local";      // 插件页面（vendor 依赖也在这个目录下）
    public const string ModelHost2 = "model.local"; // 用户模型目录
    public const string PatchHost = "patch.local";  // 补丁文件目录（临时）
    public const string VoiceHost = "voice.local";  // 用户语音目录

    /// <summary>补丁后的模型定义在临时目录里的文件名（ASCII 名，避免 URL 编码麻烦）</summary>
    public const string PatchedFileName = "model.patched.json";

    /// <summary>建立虚拟主机映射（都用 Allow，允许跨域取模型/语音文件）</summary>
    public static void SetupVirtualHosts(string webDir, string modelDir, string patchDir, string? voiceDir,
                                         Action<string, string> map)
    {
        map(AppHost, webDir);
        map(ModelHost2, modelDir);
        map(PatchHost, patchDir);

        // 语音目录是可选的。映射一个不存在的目录会抛异常，所以必须先判断
        if (!string.IsNullOrWhiteSpace(voiceDir) && Directory.Exists(voiceDir))
            map(VoiceHost, voiceDir);
    }

    /// <summary>
    /// 生成"补过资源"的模型定义，写到临时目录。
    ///
    /// 核心原则：**只补"配置里引用了、但模型自己没注册"的资源。**
    ///   · 官方标准模型（Angry / Idle 已经在 model3.json 里）→ 配置引用它，什么都不用改
    ///   · VTS 成品（2.exp3 从没注册过）→ 从扫描结果拿到文件路径，补进去
    ///
    /// 这样我们永远不会动模型原有的东西 —— "补丁把模型自带动作删了"那类问题
    /// 从根上就不存在了（而不是"小心地不要删"）。
    /// </summary>
    public static PatchResult WritePatchedModel(PluginConfig config, ModelScanResult scan, string patchDir)
    {
        var problems = new List<string>();
        var model = config.Model;
        var entryPath = Path.Combine(model.Directory, model.Entry);

        using var doc = JsonDocument.Parse(File.ReadAllText(entryPath));
        var root = ToDictionary(doc.RootElement);

        var fileRefs = root.TryGetValue("FileReferences", out var fr) && fr is Dictionary<string, object?> d
            ? d
            : new Dictionary<string, object?>();

        // ① 原有引用改成绝对地址（Moc / 贴图 / 物理 / 显示信息）—— 和以前一样
        if (fileRefs.TryGetValue("Moc", out var moc) && moc is JsonElement me && me.ValueKind == JsonValueKind.String)
            fileRefs["Moc"] = Absolute(me.GetString()!);

        if (fileRefs.TryGetValue("Textures", out var tex) && tex is List<object?> textures)
            fileRefs["Textures"] = textures
                .Select(x => x is JsonElement te && te.ValueKind == JsonValueKind.String
                    ? (object?)Absolute(te.GetString()!)
                    : x)
                .ToList();

        foreach (var key in new[] { "Physics", "DisplayInfo", "Pose", "UserData" })
            if (fileRefs.TryGetValue(key, out var v) && v is JsonElement e && e.ValueKind == JsonValueKind.String)
                fileRefs[key] = Absolute(e.GetString()!);

        var (usedExpressions, usedMotions) = CollectUsedIds(config);

        // ② 动作：只补未注册的。已注册的（Idle / Tap / Flick…）原样留着，一个字都不动。
        var motions = fileRefs.TryGetValue("Motions", out var rawMotions)
                      && rawMotions is Dictionary<string, object?> motionDict
            ? motionDict
            : new Dictionary<string, object?>();

        foreach (var id in usedMotions)
        {
            var resource = FindById(scan.Motions, id);
            if (resource is null)
            {
                problems.Add($"动作标识「{id}」在模型里找不到（检查配置是不是写错了）");
                continue;
            }
            if (resource.Registered) continue;              // 模型自己有，不用补

            motions[id] = resource.Files
                .Select(f => (object?)new Dictionary<string, object?>
                {
                    ["File"] = Absolute(f),
                    ["FadeInTime"] = 0.5,
                    ["FadeOutTime"] = 0.5
                })
                .ToList();
        }

        if (motions.Count > 0) fileRefs["Motions"] = motions;

        // ③ 表情：同样只补未注册的。名字就用标识本身 ——
        //    这样"配置里的标识 / 补丁注册的名字 / 页面调用的名字"是同一个词，
        //    页面侧不需要做任何翻译。
        var expressions = fileRefs.TryGetValue("Expressions", out var rawExpressions)
                          && rawExpressions is List<object?> expressionList
            ? expressionList
            : new List<object?>();

        foreach (var id in usedExpressions)
        {
            var resource = FindById(scan.Expressions, id);
            if (resource is null)
            {
                problems.Add($"表情标识「{id}」在模型里找不到（检查配置是不是写错了）");
                continue;
            }
            if (resource.Registered) continue;
            if (resource.Files.Count == 0) continue;

            expressions.Add(new Dictionary<string, object?>
            {
                ["Name"] = id,
                ["File"] = Absolute(resource.Files[0])
            });
        }

        if (expressions.Count > 0) fileRefs["Expressions"] = expressions;

        root["FileReferences"] = fileRefs;

        Directory.CreateDirectory(patchDir);
        var outPath = Path.Combine(patchDir, PatchedFileName);
        File.WriteAllText(outPath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));

        return new PatchResult(outPath, problems);
    }

    /// <summary>
    /// 把配置里所有"被引用到的标识"收集起来（表情 + 动作）。
    /// 从配置反向收集是刻意的：模型里可能有好几百个资源，**只有被引用的才需要补**。
    /// 把整个模型都注册进去既没必要，也会让页面上的可选项变得难以理解。
    ///
    /// 2026-09-20：实现搬去了 `Logic/ExpressionReferences.cs`。
    /// 因为「形象」页也要知道"这个标识被引了几处"，如果再在这儿留着这一份，
    /// 补丁和界面就会各按各的收 —— 那是**两份真相**（这个项目一路在删的东西）。
    /// 现在只有 `ExpressionReferences` 知道"什么算引用"，这里只是它的一个用法。
    /// </summary>
    private static (HashSet<string> Expressions, HashSet<string> Motions) CollectUsedIds(PluginConfig config)
        => ExpressionReferences.UsedIds(config);

    private static ModelResource? FindById(IReadOnlyList<ModelResource> list, string id)
        => list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>把模型目录里的相对路径转成 model.local 的绝对地址</summary>
    private static string Absolute(string relativePath) => $"https://{ModelHost2}/{Encode(relativePath)}";

    /// <summary>把语音目录里的相对路径转成 voice.local 的绝对地址</summary>
    public static string VoiceUrl(string relativePath) => $"https://{VoiceHost}/{Encode(relativePath)}";

    /// <summary>
    /// 相对路径 → URL 路径：逐段做 URL 编码，但保留分隔用的斜杠。
    /// 文件名里的中文、全角符号（如「喵（平静）.wav」的（）、「喵？」的？）都必须编码，
    ///   否则浏览器按 URL 规则解析时会出错；而整串一起编码会把 / 也编掉，路径就断了。
    /// </summary>
    private static string Encode(string relativePath)
    {
        var normalized = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
        return string.Join("/", normalized.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// JsonElement → 可写的普通对象：对象变字典、数组变列表、标量保持原样。
    /// 数组必须转成 List —— 否则「原有的表情要不要保留、配置的表情怎么追加」这类
    /// 合并逻辑根本没法写（JsonElement 是只读的）。
    /// </summary>
    private static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in element.EnumerateObject())
            dict[p.Name] = ToValue(p.Value);
        return dict;
    }

    private static object? ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ToValue).ToList(),
        _ => JsonSerializer.Deserialize<object>(element.GetRawText())
    };
}
/// <summary>模型补丁的结果：补丁文件路径 + 过程中发现的问题（给界面显示）</summary>
public sealed record PatchResult(string Path, IReadOnlyList<string> Problems);
