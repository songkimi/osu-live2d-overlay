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

        // 语音目录是可选的。★ 映射一个不存在的目录会抛异常，所以必须先判断
        if (!string.IsNullOrWhiteSpace(voiceDir) && Directory.Exists(voiceDir))
            map(VoiceHost, voiceDir);
    }

    /// <summary>
    /// 生成"补过动作与表情"的模型定义，写到临时目录，返回补丁文件的完整路径。
    /// 做法：把原文里的所有文件引用改写成指向 model.local 的绝对地址，
    ///       再补上 Motions（待机）与 Expressions（配置里的表情）。
    /// </summary>
    public static string WritePatchedModel(PluginConfig config, string patchDir)
    {
        var model = config.Model;
        var entryPath = Path.Combine(model.Directory, model.Entry);

        using var doc = JsonDocument.Parse(File.ReadAllText(entryPath));
        var root = ToDictionary(doc.RootElement);

        var fileRefs = root.TryGetValue("FileReferences", out var fr) && fr is Dictionary<string, object?> d
            ? d
            : new Dictionary<string, object?>();

        // ① 原有引用改成绝对地址（Moc / 贴图 / 物理 / 显示信息）
        if (fileRefs.TryGetValue("Moc", out var moc) && moc is JsonElement me && me.ValueKind == JsonValueKind.String)
            fileRefs["Moc"] = Absolute(me.GetString()!);

        if (fileRefs.TryGetValue("Textures", out var tex) && tex is JsonElement te && te.ValueKind == JsonValueKind.Array)
            fileRefs["Textures"] = te.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => (object?)Absolute(x.GetString()!))
                .ToList();

        foreach (var key in new[] { "Physics", "DisplayInfo", "Pose", "UserData" })
            if (fileRefs.TryGetValue(key, out var v) && v is JsonElement e && e.ValueKind == JsonValueKind.String)
                fileRefs[key] = Absolute(e.GetString()!);

        // ② 同一层目录下的其它引用（如果有）
        foreach (var key in new[] { "Motions", "Expressions" })
            fileRefs.Remove(key);   // 原始文件里通常没有；避免残留旧值

        // ③ 补上待机动作
        if (!string.IsNullOrWhiteSpace(model.IdleFile))
        {
            var group = string.IsNullOrWhiteSpace(model.IdleGroup) ? "Idle" : model.IdleGroup;
            fileRefs["Motions"] = new Dictionary<string, object?>
            {
                [group] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["File"] = Absolute(model.IdleFile),
                        ["FadeInTime"] = 0.5,
                        ["FadeOutTime"] = 0.5
                    }
                }
            };
        }

        // ④ 补上配置里的表情（名字由用户在 config.json 里定）
        if (model.Expressions.Count > 0)
        {
            fileRefs["Expressions"] = model.Expressions
                .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.File))
                .Select(x => (object?)new Dictionary<string, object?>
                {
                    ["Name"] = x.Name,
                    ["File"] = Absolute(x.File)
                })
                .ToList();
        }

        root["FileReferences"] = fileRefs;

        Directory.CreateDirectory(patchDir);
        var outPath = Path.Combine(patchDir, PatchedFileName);
        File.WriteAllText(outPath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));

        return outPath;
    }

    /// <summary>把模型目录里的相对路径转成 model.local 的绝对地址</summary>
    private static string Absolute(string relativePath) => $"https://{ModelHost2}/{Encode(relativePath)}";

    /// <summary>把语音目录里的相对路径转成 voice.local 的绝对地址</summary>
    public static string VoiceUrl(string relativePath) => $"https://{VoiceHost}/{Encode(relativePath)}";

    /// <summary>
    /// 相对路径 → URL 路径：逐段做 URL 编码，但保留分隔用的斜杠。
    /// ★ 文件名里的中文、全角符号（如「喵（平静）.wav」的（）、「喵？」的？）都必须编码，
    ///   否则浏览器按 URL 规则解析时会出错；而整串一起编码会把 / 也编掉，路径就断了。
    /// </summary>
    private static string Encode(string relativePath)
    {
        var normalized = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
        return string.Join("/", normalized.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>JsonElement → 可写字典（只处理我们需要的层级：对象/数组/标量）</summary>
    private static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in element.EnumerateObject())
        {
            dict[p.Name] = p.Value.ValueKind == JsonValueKind.Object
                ? ToDictionary(p.Value)
                : JsonSerializer.Deserialize<object>(p.Value.GetRawText());
        }
        return dict;
    }
}
