// ============================================================
// HealthEnvironmentProbe.cs —— 探测"外部世界"，喂给配置体检
//
// 【为什么需要它】
//   `ConfigHealthCheck.Check(config, env, mode)` 的第二个参数是 `HealthEnvironment`：
//   模型目录在不在、入口文件在不在、语音目录在不在、模型里有哪些标识、
//   每个情绪文件夹里有几个可用音频。
//
//   体检**故意不自己摸文件系统**（见 ConfigHealthCheck.cs 文件头）——
//   那样它就没法在控制台里测（总不能为了测一条规则去建一堆真目录）。
//   所以"谁来探测"这件事必须有个答案，就是这里。
//
// 【为什么不写在 ConfigHealthCheck.cs 里】
//   探测是 IO + Model/Voice 的活，而 ConfigHealthCheck 的规矩是"纯逻辑、能控制台测"
//   （《项目结构约定》§二：Logic/Config 里不该出现 IO）。
//
// 【探测失败怎么办：一律"当作没有"，绝不抛】
//   扫模型失败 → 当作"模型里什么都没有"，体检于是会报"引用的表情找不到"。
//   那正是用户需要知道的事，比在这里崩掉强得多。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OsuLive2dOverlay;

public static class HealthEnvironmentProbe
{
    /// <summary>
    /// 按当前配置探测一遍环境。
    /// 这是 IO，会读磁盘（扫模型目录、数语音文件），所以调用时机要挑 ——
    /// 打开体检页时、或用户点「重新检查」时。
    /// </summary>
    /// <param name="config">配置档案（模型 / 语音）</param>
    /// <param name="dataSource">
    /// 软件设置里的「数据源」（★ 2026-09-23 加的，第 21 条要用）。
    /// 传 null 就不去问那两个 exe 在不在 —— 而 `ConfigHealthCheck` 那边也会因为
    /// 拿不到 dataSource 而不查那一条，**两边是一致**的（不会出现"要查却没数据"的假警报）。
    /// </param>
    public static HealthEnvironment Probe(PluginConfig config, DataSourceConfig? dataSource = null)
    {
        var modelDir = config.Model.Directory ?? "";
        var entry = config.Model.Entry ?? "";
        var voiceDir = config.Voice.Directory ?? "";

        var modelDirExists = !string.IsNullOrWhiteSpace(modelDir) && Directory.Exists(modelDir);
        var entryExists = modelDirExists
                          && !string.IsNullOrWhiteSpace(entry)
                          && File.Exists(Path.Combine(modelDir, entry));
        var voiceDirExists = !string.IsNullOrWhiteSpace(voiceDir) && Directory.Exists(voiceDir);

        return new HealthEnvironment(
            modelDirExists,
            entryExists,
            voiceDirExists,
            ScanExpressionIds(modelDir, entry, modelDirExists && entryExists),
            ScanMotionIds(modelDir, entry, modelDirExists && entryExists),
            CountVoiceFiles(config.Voice, voiceDir),
            ExeExists(dataSource?.TosuPath),
            ExeExists(dataSource?.OsuPath));
    }

    /// <summary>
    /// 这个程序文件在不在。
    /// **没配路径时返回 false** —— 但体检只在"开关开着"时才看这个值（第 21 条），
    /// 而那种情况它会先报"路径还空着"，不会把"空"说成"文件不存在"。
    /// </summary>
    private static bool ExeExists(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim());

    /// <summary>模型里有哪些表情标识。扫不动就返回空集合（体检会据此报"找不到"，那是对的）。</summary>
    private static IReadOnlySet<string> ScanExpressionIds(string modelDir, string entry, bool canScan)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!canScan) return set;

        try
        {
            foreach (var resource in ModelScanner.Scan(modelDir, entry).Expressions)
                set.Add(resource.Id);
        }
        catch
        {
            // 扫描失败 = 当作什么都没有，交给体检去报
        }

        return set;
    }

    private static IReadOnlySet<string> ScanMotionIds(string modelDir, string entry, bool canScan)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!canScan) return set;

        try
        {
            foreach (var resource in ModelScanner.Scan(modelDir, entry).Motions)
                set.Add(resource.Id);
        }
        catch
        {
        }

        return set;
    }

    /// <summary>
    /// 每个情绪文件夹里有几个可用音频。
    /// 复用 <see cref="VoiceLibrary.Resolve"/> —— 它已经知道"哪些扩展名算音频"、
    /// "只扫一层"这些规矩；在这里重新写一遍必然会和播放那边不一致。
    /// </summary>
    private static IReadOnlyDictionary<string, int> CountVoiceFiles(VoiceConfig voice, string voiceDir)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            var (emotions, _) = VoiceLibrary.Resolve(voice, voiceDir);
            foreach (var emotion in emotions)
                counts[emotion.Name] = emotion.Urls.Count;
        }
        catch
        {
        }

        return counts;
    }
}
