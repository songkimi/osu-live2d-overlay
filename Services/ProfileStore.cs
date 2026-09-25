// ============================================================
// ProfileStore.cs —— 配置档案的**操作**：切换 / 新建 / 改名 / 删除 / 导入 / 导出
//
// 【为什么和 ConfigStore 分开】
//   `ConfigStore` 回答"当前这份配置怎么读写、脏了没有"；
//   这里回答"有哪些档案、怎么换一份"。
//   两个问题混在一个类里，那个类很快就会变成"什么都管"的上帝类
//   （约定来自《项目结构约定》§二）。所以分工是：
//     · 路径/命名/迁移 → `ProfileLocator`
//     · 读写当前那份   → `ConfigStore`
//     · 增删换档案     → 这里
//
// 【每个操作的第一步都是"先把当前档案存盘"】
//   因为"切换"在实现上是"换一个文件重读" —— 内存里没保存的改动会被直接冲掉。
//   与其弹一个"你有未保存的改动"来打断用户，不如顺手存了：
//   点"切换档案"的人本来就是要换一套用，这时候丢掉他刚改的东西才是真的糟糕。
//   （和"窗口位置调整后自动保存"不同：那个默认是**询问**，
//     因为拖窗口是无意识的动作，而切档案是明确的意图。）
//
// 【删不掉正在用的那份】
//   拒绝删除当前档案，而不是"删掉之后自动切到别的" ——
//   后者会让用户在一次误点里连丢两个状态（正在用的档案 + 当前选择）。
//   规则简单可预测：先切走，再删。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OsuLive2dOverlay;

public sealed class ProfileStore
{
    private readonly ConfigStore _store;

    public ProfileStore(ConfigStore store) => _store = store;

    /// <summary>档案目录（`&lt;exe&gt;\profiles`）</summary>
    public string Directory => _store.ProfilesDirectory;

    /// <summary>当前生效的档案名</summary>
    public string CurrentName => _store.ProfileName;

    /// <summary>列出所有档案（按名字排序）</summary>
    public IReadOnlyList<ProfileEntry> List() => ProfileLocator.List(_store.BaseDirectory);

    /// <summary>某个档案的文件路径</summary>
    public string PathOf(string name) => ProfileLocator.PathOf(_store.BaseDirectory, name);

    public bool Exists(string name) => File.Exists(PathOf(name));

    // ------------------------------------------------------------
    // 切换与新建
    // ------------------------------------------------------------

    /// <summary>切到另一个档案。已经在用那个就不做任何事（免得白重读一次）。</summary>
    public void SwitchTo(string name)
    {
        var normalized = ProfileLocator.NormalizeName(name);
        if (normalized == _store.ProfileName) return;

        SaveCurrent();
        _store.SwitchProfile(normalized);
    }

    /// <summary>
    /// 把当前这套另存成一个新档案，并切过去。
    ///
    /// 「新建」= 「另存为当前这份」是刻意的：用户想新建档案时，
    /// 想要的几乎总是"在现在这套的基础上改一套新的"，而不是"一份空白配置"。
    /// 真想要空白的人，新建完把各项改掉就是 —— 反过来（新建出空白、
    /// 想复制当前还得靠导出导入）才是真的麻烦。
    /// </summary>
    public void SaveAsNew(string name)
    {
        var normalized = ProfileLocator.NormalizeName(name);

        if (Exists(normalized))
            throw new InvalidOperationException($"已经有一个叫「{normalized}」的档案了。");

        SaveCurrent();
        System.IO.Directory.CreateDirectory(Directory);
        File.Copy(_store.ConfigPath, PathOf(normalized), overwrite: false);

        _store.SwitchProfile(normalized);
    }

    // ------------------------------------------------------------
    // 改名与删除
    // ------------------------------------------------------------

    public void Rename(string oldName, string newName)
    {
        var target = ProfileLocator.NormalizeName(newName);
        if (target == oldName) return;

        if (Exists(target))
            throw new InvalidOperationException($"已经有一个叫「{target}」的档案了。");

        var source = PathOf(oldName);
        if (!File.Exists(source))
            throw new FileNotFoundException($"找不到档案「{oldName}」。");

        var isCurrent = oldName == _store.ProfileName;
        if (isCurrent) SaveCurrent();

        File.Move(source, PathOf(target), overwrite: false);

        // 改的要是正在用的那份，就顺手把 settings.json 里的"当前"也改掉 ——
        // 否则下次启动会去找一个已经不存在的旧名字
        if (isCurrent) _store.SwitchProfile(target);
    }

    public void Delete(string name)
    {
        if (name == _store.ProfileName)
            throw new InvalidOperationException("不能删掉正在用的档案。先切到别的档案，再回来删它。");

        var path = PathOf(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到档案「{name}」。");

        File.Delete(path);

        // 顺手清掉它的备份 —— 留着的话，下次新建同名档案会让人以为"里面还有旧内容"
        var backup = path + ".bak";
        try { if (File.Exists(backup)) File.Delete(backup); } catch { /* 清不掉就算了 */ }
    }

    // ------------------------------------------------------------
    // 换机器用：导入 / 导出
    // ------------------------------------------------------------

    /// <summary>
    /// 从任意位置导一个 json 进来，**并切过去**。
    /// 导入的常见场景就是"我有一份配置想用"，切过去才是把这件事做完 ——
    /// 只复制进来不切换，用户还得自己再找一遍、再点一次切换。
    /// </summary>
    public void Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到要导入的文件。");

        // 先确认它是个 JSON 对象再复制。
        // 否则用户会导进一个 txt、拿到一份空档案，而且**不报任何错** ——
        // 这种"没反应"是最难查的一类问题。
        using (var doc = JsonDocument.Parse(File.ReadAllText(sourcePath)))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("这个文件不是一份配置（它不是一个 JSON 对象）。");
        }

        var name = UniqueName(ProfileLocator.NormalizeName(
            Path.GetFileNameWithoutExtension(sourcePath)));

        System.IO.Directory.CreateDirectory(Directory);
        File.Copy(sourcePath, PathOf(name), overwrite: false);

        _store.SwitchProfile(name);
    }

    /// <summary>把当前档案导出到任意位置（备份、发给人、换机器）。</summary>
    public void Export(string targetPath)
    {
        SaveCurrent();
        File.Copy(_store.ConfigPath, targetPath, overwrite: true);
    }

    // ------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------

    /// <summary>当前档案存盘 —— 任何"要换文件了"的操作之前都得先调它</summary>
    private void SaveCurrent()
    {
        if (_store.IsConfigDirty || !File.Exists(_store.ConfigPath)) _store.SaveConfig();
    }

    /// <summary>重名时"打歌用" → "打歌用 2"、"打歌用 3"……</summary>
    private string UniqueName(string baseName)
    {
        if (!Exists(baseName)) return baseName;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!Exists(candidate)) return candidate;
        }

        return baseName + " " + Guid.NewGuid().ToString("N")[..6];
    }
}
