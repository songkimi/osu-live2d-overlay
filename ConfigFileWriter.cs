// ============================================================
// ConfigFileWriter.cs —— 把运行期调整写回 config.json
//
// 【难点不在"写"，在"别写坏别人的东西"】
//   config.json 里还有用户手写的一堆内容：中文注释键（`_说明` 那种）、模型路径、
//   语音配置、连击规则…… 这个类的职责是**只改那几个键，其它一个字都不动**。
//
//   为什么必须这么小心：配置是给人读、给人改的。写错一次，用户看到的就是一份被程序
//   "洗过"的文件 —— 注释没了、字段顺序乱了、他自己写的说明消失了。那比功能出 bug
//   更让人难受，而且他根本不知道是自己哪里做错了。
//
// 【两个坑，都在下面注释里标了】
//   ① 别用"反序列化成 PluginConfig → 改字段 → 序列化回去"（会把原文洗一遍）
//   ② 中文默认会被转义成 \uXXXX（转义只在写的时候发生，读的时候等价，所以藏得深）
// ============================================================
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OsuLive2dOverlay;

public static class ConfigFileWriter
{
    /// <summary>
    /// 把各界面的窗口位置与尺寸写回配置文件。
    /// 只动 `界面感知 → &lt;界面&gt; → 悬浮窗` 下的 X / Y / 宽 / 高，其它内容原样保留。
    /// </summary>
    /// <param name="configPath">配置文件路径（运行时用 exe 同级那份）</param>
    /// <param name="changes">哪个界面 → 什么位置</param>
    /// <returns>真正写进去的界面个数</returns>
    /// <exception cref="JsonException">文件内容不是合法 JSON（或顶层不是对象）时抛出 —— 让上层去告诉用户，不能假装成功</exception>
    public static int WriteWindowBounds(string configPath, IReadOnlyDictionary<GameScene, WindowBounds> changes)
    {
        if (!File.Exists(configPath) || changes.Count == 0) return 0;

        // 用 JsonNode 而不是反序列化成 PluginConfig：前者**保留原文结构**，
        // 后者会把用户手写的注释键、字段顺序、以及程序不认识的键统统丢掉。
        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                   ?? throw new JsonException("配置文件的顶层不是一个 JSON 对象");

        var scenes = root["界面感知"] as JsonObject ?? new JsonObject();
        root["界面感知"] = scenes;

        var written = 0;
        foreach (var (scene, bounds) in changes)
        {
            var key = SceneProfiles.KeyOf(scene);
            if (key is null) continue;                       // 认不出的场景：配置里没有它的位置

            var profile = scenes[key] as JsonObject ?? new JsonObject();
            scenes[key] = profile;

            var window = profile["悬浮窗"] as JsonObject ?? new JsonObject();
            profile["悬浮窗"] = window;

            // 拖过之后这几个值就"设过了" —— 从此不再是 null（null 的含义是"没设过、保持原样"）
            window["X"] = bounds.Left;
            window["Y"] = bounds.Top;
            window["宽"] = bounds.Width;
            window["高"] = bounds.Height;
            written++;
        }

        // 一个都没写成（比如传进来的全是认不出的界面）→ 别碰文件。
        // 无谓地重写一次会平白产生 .bak、还会改掉文件的修改时间 —— "什么都没做"就该真的什么都没做。
        if (written == 0) return 0;

        // 用项目统一的序列化选项：它关掉了"非 ASCII 转义"，
        // 否则写出来的「悬浮窗」会变成 \u60AC\u6D6E\u7A97，配置文件就没法看了。
        var json = root.ToJsonString(PluginConfig.Options);

        // 落盘两步走，保证**正主文件永远不是半成品**（要么是旧的、要么是新的）：
        var bakPath = configPath + ".bak";
        var tempPath = configPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Copy(configPath, bakPath, overwrite: true);
        File.Move(tempPath, configPath, overwrite: true);

        // 为什么不用 File.Replace：它是一步到位的原子替换，但依赖 ReplaceFile 这个 API ——
        // 跨卷、某些网络盘、以及受限环境里会直接失败。File.Move(覆盖) 在 Windows 上是
        // MoveFileEx(REPLACE_EXISTING)，同样是原子的，适用面却宽得多。

        return written;
    }
}
