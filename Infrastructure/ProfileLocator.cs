// ============================================================
// ProfileLocator.cs —— **配置档案**在哪：命名规则、目录、首次迁移
//
// 【一个档案 = 一个文件】
//   `profiles\默认.json`、`profiles\打歌用.json`……
//   **当前生效的那份也是一样的文件**，没有"特例文件"。
//
// 【老版本升级】
//   老版本把配置放在 exe 旁的 config.json。启动时它会**搬**进当前档案的文件
//   （`profiles\<当前档案名>.json`），然后原件改名成 `config.json.old`（见 EnsureProfile）。
//
//   两条规则，各自解决一个问题：
//     · **内容先搬** —— 当前档案还没有文件时，老内容就是用户的全部家当，不能丢
//     · **原件必退场** —— 不管当前是哪个档案。留着同名文件，用户会以为改它有用，
//       而程序根本不读它。那是最坏的一种坑：不报错，只是没用。
//   改名而不是删除：迁移代码有 bug 时，那份内容还在原地。
//
// 【它不引 System.Windows】
//   纯字符串/文件系统逻辑 —— 这样它能进控制台测（《项目结构约定》§二）。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;

namespace OsuLive2dOverlay;

/// <summary>一个档案在磁盘上的样子（名字 + 路径 + 修改时间）。</summary>
public sealed record ProfileEntry(string Name, string Path, DateTime Modified);

public static class ProfileLocator
{
    /// <summary>档案目录名（相对 exe）</summary>
    public const string FolderName = "profiles";

    /// <summary>全新用户的档案名</summary>
    public const string DefaultProfileName = "默认";

    /// <summary>档案文件的扩展名</summary>
    public const string Extension = ".json";

    /// <summary>老版本的配置文件放在 exe 旁边，叫这个名字</summary>
    public const string LegacyFileName = "config.json";

    /// <summary>`&lt;exe&gt;\profiles`</summary>
    public static string ProfilesDirectoryOf(string baseDirectory)
        => Path.Combine(baseDirectory, FolderName);

    /// <summary>档案名 → 文件名</summary>
    public static string FileNameOf(string name) => name + Extension;

    /// <summary>档案名 → 完整路径</summary>
    public static string PathOf(string baseDirectory, string name)
        => Path.Combine(ProfilesDirectoryOf(baseDirectory), FileNameOf(name));

    /// <summary>
    /// 把用户输入的名字收拾成规范形式。
    /// 空 → 「默认」（档案名不允许空，"当前档案叫什么"必须总有个答案）。
    /// 顺手去掉误打的扩展名：用户输入 `默认.json`，文件名不该变成 `默认.json.json`。
    /// </summary>
    public static string NormalizeName(string? name)
    {
        var trimmed = (name ?? "").Trim();

        if (trimmed.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^Extension.Length].Trim();

        return trimmed.Length == 0 ? DefaultProfileName : trimmed;
    }

    /// <summary>
    /// 名字能不能当文件名。
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 60) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name is "." or "..") return false;

        return true;
    }

    /// <summary>
    /// 校验并给出错误原因
    /// 返回 null = 通过。
    /// </summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "档案名不能为空。";
        if (name.Length > 60) return "档案名太长了（最多 60 个字）。";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return @"名字里不能有这些符号：\ / : * ? "" < > |";
        if (name is "." or "..") return "这个名字不能用。";

        return null;
    }

    /// <summary>
    /// 确保"某个档案的文件"存在，并返回它的完整路径。
    ///
    /// 做三件事：
    ///   ① 建 `profiles\` 目录（第一次运行时）
    ///   ② 处理老版本遗留在 exe 旁的 config.json（见下面那段注释）
    ///   ③ 返回目标路径 —— 文件还是不存在也没关系，
    ///      `PluginConfig.Load` 会写一份完整模板出来
    /// </summary>
    /// <param name="baseDirectory">exe 所在目录</param>
    /// <param name="name">档案名（会先 Normalize）</param>
    /// <param name="legacyConfigPath">老 config.json 的路径（只在启动时传，界面里调用传 null）</param>
    public static string EnsureProfile(string baseDirectory, string? name, string? legacyConfigPath = null)
    {
        var normalized = NormalizeName(name);
        var directory = ProfilesDirectoryOf(baseDirectory);
        var target = Path.Combine(directory, FileNameOf(normalized));

        try
        {
            var legacyExists = legacyConfigPath is not null && File.Exists(legacyConfigPath);

            if (legacyExists || !File.Exists(target))
                Directory.CreateDirectory(directory);

            // ① 老 config.json 的内容不能丢：当前档案还没有文件时，把它搬过去。
            //    已经有文件就以那个文件为准 
            if (legacyExists && !File.Exists(target))
                File.Copy(legacyConfigPath!, target, overwrite: false);

            // ② 然后让它改名退场
            if (legacyExists)
            {
                try { File.Move(legacyConfigPath!, legacyConfigPath + ".old", overwrite: true); }
                catch { /* 改名失败不影响使用 —— 内容已经搬过去了 */ }
            }

            if (File.Exists(target)) return target;

            Directory.CreateDirectory(directory);
        }
        catch
        {
            // 建目录/迁移失败不能挡住启动
        }

        return target;
    }

    /// <summary>
    /// 列出所有档案（按名字排序）。
    /// 读不到目录 = 空列表，不抛
    /// </summary>
    public static IReadOnlyList<ProfileEntry> List(string baseDirectory)
    {
        var result = new List<ProfileEntry>();
        var directory = ProfilesDirectoryOf(baseDirectory);

        try
        {
            if (!Directory.Exists(directory)) return result;

            foreach (var file in Directory.EnumerateFiles(directory, "*" + Extension))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!IsValidName(name)) continue;

                result.Add(new ProfileEntry(name, file, File.GetLastWriteTime(file)));
            }
        }
        catch
        {
            return result;
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        return result;
    }
}
