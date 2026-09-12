// ============================================================
// VoiceLibrary.cs —— 把配置里的「情绪 → 文件」解析成「情绪 → 能播的地址」
//
// 为什么要单独一个文件：这里要碰文件系统（找文件、过滤格式、拼 URL），
// 而"哪件事说哪类话"是配置的事、"怎么播"是网页的事 —— 三件事分开放，
// 以后想加"开局语音""结算语音"只要在配置里多一个情绪，这里不用动。
//
// 解析规则（越往下越省事，见 EmotionConfig 的注释）：
//   ① 「文件」列表   → 直接用（相对「目录」，可以带子目录）
//   ② 「子目录」      → 扫描 目录\子目录
//   ③ 都没写          → 扫描 目录\名称（同名文件夹约定）
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
    /// 返回：能用的情绪 + 一份"哪里配错了"的说明（说明只在调试模式下给用户看，不影响其它的照常播）。
    /// </summary>
    public static (List<ResolvedEmotion> Emotions, List<string> Problems) Resolve(VoiceConfig voice)
    {
        var result = new List<ResolvedEmotion>();
        var problems = new List<string>();

        foreach (var emotion in voice.Emotions)
        {
            var name = emotion.Name?.Trim() ?? "";
            if (name.Length == 0) continue;   // 没名字的情绪无从触发，直接跳过

            var files = ResolveFiles(voice, emotion, problems);
            if (files.Count == 0)
            {
                problems.Add($"情绪「{name}」里没有找到音频文件");
                continue;
            }

            result.Add(new ResolvedEmotion(name, files.Select(ModelHost.VoiceUrl).ToList()));
        }

        if (result.Count == 0 && voice.Emotions.Count > 0)
            problems.Add("一条语音都没解析出来，检查「语音.情绪」里的文件路径");

        return (result, problems);
    }

    /// <summary>按三条规则依次尝试，返回相对「语音目录」的文件相对路径</summary>
    private static List<string> ResolveFiles(VoiceConfig voice, EmotionConfig emotion, List<string> problems)
    {
        // ① 显式列了文件
        var listed = (emotion.Files ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();

        if (listed.Count > 0)
        {
            var found = new List<string>();
            foreach (var f in listed)
            {
                var full = Path.Combine(voice.Directory, f);
                if (File.Exists(full)) found.Add(f.Replace('\\', '/'));
                else problems.Add($"语音文件不存在：{full}");
            }
            return found;
        }

        // ② / ③ 扫文件夹（先看「子目录」，再按同名文件夹约定）
        var sub = !string.IsNullOrWhiteSpace(emotion.SubDirectory) ? emotion.SubDirectory : emotion.Name;
        var dir = Path.Combine(voice.Directory, sub ?? "");

        if (!Directory.Exists(dir))
        {
            problems.Add($"情绪「{emotion.Name}」既没写文件，也没有对应的文件夹：{dir}");
            return new List<string>();
        }

        return Directory.EnumerateFiles(dir)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)          // 顺序固定，行为可复现
            .Select(f => Path.GetRelativePath(voice.Directory, f).Replace('\\', '/'))
            .ToList();
    }
}
