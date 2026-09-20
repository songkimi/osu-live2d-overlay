// ============================================================
// VoiceLibrary.cs —— 把配置里的「情绪」解析成「情绪 → 能播的地址」
//
// 只有一个填法（用户定的，2026-09-19 砍掉了另外两种）：
//   **一个情绪 = 语音目录下的一个子文件夹，情绪名就是文件夹名。**
//
// 为什么砍：以前还支持「逐条列文件」和「另写子目录」两种写法，于是界面上就得
// 同时摆出"文件列表"和"选文件夹"两套控件，用户还得分清两者的先后关系 ——
// 复杂度全是自找的。只留文件夹之后，界面只剩一件事：选文件夹 → 多一个情绪。
//
// 扫描是**非递归**的：只看这个文件夹里这一层，它的子文件夹不管
// （要分类就再建一个情绪，别在情绪里再套一层）。
//
// 为什么要单独一个文件：这里要碰文件系统（找文件夹、过滤格式、拼 URL），
// 而"哪件事说哪类话"是配置的事、"怎么播"是网页的事 —— 三件事分开放。
// ============================================================
using System.IO;

namespace OsuLive2dOverlay;

public static class VoiceLibrary
{
    /// <summary>认得出来的音频后缀。浏览器对 wav/mp3/ogg 支持最好</summary>
    private static readonly string[] AudioExtensions =
        { ".wav", ".mp3", ".ogg", ".m4a", ".aac", ".flac", ".opus" };

    /// <summary>一类情绪解析好之后的结果：名称 + 一串可播放地址</summary>
    public sealed record ResolvedEmotion(string Name, List<string> Urls);

    /// <summary>
    /// 解析整份语音配置。
    ///
    /// <paramref name="voiceRoot"/> 传**已经确认存在的绝对目录**（调用方校验过），
    /// 不在这里再判一次：目录不存在属于"整个语音功能没法用"，由调用方统一报错，
    /// 这里只负责"每个情绪能不能解析出来"。
    ///
    /// 返回：能用的情绪 + 一份"哪里配错了"的说明（说明只在调试模式下给用户看，不影响其它的照常播）。
    /// </summary>
    public static (List<ResolvedEmotion> Emotions, List<string> Problems) Resolve(VoiceConfig voice, string voiceRoot)
    {
        var result = new List<ResolvedEmotion>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in voice.Emotions ?? new List<string>())
        {
            var name = raw?.Trim() ?? "";
            if (name.Length == 0) continue;          // 空名字的情绪无从触发，直接跳过

            // 名字必须是**一个文件夹名**：带分隔符既能指到语音目录外面去，
            // 也会让"情绪名 = 文件夹名"这条规则失效（界面上的情绪名和磁盘对不上）。
            if (name.IndexOfAny(new[] { '\\', '/' }) >= 0 || Path.IsPathRooted(name))
            {
                problems.Add($"情绪名「{name}」不能带路径，它就该是一个文件夹名");
                continue;
            }

            if (!seen.Add(name))
            {
                problems.Add($"情绪「{name}」配了不止一次，后面这次被忽略");
                continue;
            }

            var dir = Path.Combine(voiceRoot, name);

            if (!Directory.Exists(dir))
            {
                problems.Add($"情绪「{name}」没有对应的文件夹：{dir}");
                continue;
            }

            var files = Directory.EnumerateFiles(dir)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)          // 顺序固定，行为可复现
                .Select(f => Path.GetRelativePath(voiceRoot, f).Replace('\\', '/'))
                .ToList();

            if (files.Count == 0)
            {
                problems.Add($"情绪「{name}」的文件夹里没有音频文件：{dir}");
                continue;
            }

            result.Add(new ResolvedEmotion(name, files.Select(ModelHost.VoiceUrl).ToList()));
        }

        if (result.Count == 0 && (voice.Emotions?.Count ?? 0) > 0)
            problems.Add("一条语音都没解析出来，检查「语音.目录」下每个情绪的文件夹");

        return (result, problems);
    }
}
