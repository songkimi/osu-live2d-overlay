// ============================================================
// ConfigStore.cs —— 配置的读写门面
//
// 【它管两类文件】（2026-09-20 拆分；同日加多档案）
//   · `profiles\<档案名>.json` —— **配置档案**：模型 / 规则 / 语音 / 界面感知（**按角色切换**）
//   · `settings.json`          —— **软件设置**：外观 / 字体 / 数据源 / 热键 / 日志（**全局唯一**）
//
//   "当前档案是哪一个"记在 settings.json 里（`档案.当前`），
//   因为它描述的是"我怎么用这个软件"，不是"这个角色怎么演"。
//   路径规则全部委托给 `ProfileLocator` —— 那个文件是唯一的真相。
//
// 【两边各有各的脏标记】
//   存"档案"时不该把"改了一半的软件设置"也写进去，反之亦然。
//   所以 MarkConfigDirty / MarkSettingsDirty、SaveConfig / SaveSettings 都是分开的；
//   只有只读的 <see cref="IsDirty"/> 把两边合起来，给顶部状态条显示"● 未保存"用。
//
// 【它不管什么】（管多了就变上帝类）
//   · 不管"哪些字段能改" —— 那是配置类自己的事
//   · 不管"悬浮窗运行时要置灰" —— 那是页面按 IsEditable 决定的
//   · **不引 System.Windows** —— 所以它能进控制台测（同《项目结构约定》§二 的依赖规矩）
// ============================================================
using System;
using System.IO;
using System.Text.Json;

namespace OsuLive2dOverlay;

public sealed class ConfigStore
{
    private readonly string _settingsPath;
    private readonly string _baseDirectory;
    private readonly string? _legacyConfigPath;

    /// <summary>
    /// 当前档案文件的完整路径。
    /// **它不是只读的** —— 切换档案就是换掉它（见 <see cref="SwitchProfile"/>），
    /// 所以这次改动之后再也没有任何地方能"自己拼一个 config.json 路径出来"。
    /// </summary>
    private string _configPath = "";

    private string _profileName = ProfileLocator.DefaultProfileName;

    private bool _configDirty;
    private bool _settingsDirty;

    /// <param name="settingsPath">settings.json 的完整路径（与 exe 同目录）</param>
    /// <param name="baseDirectory">exe 所在目录 —— 档案目录 <c>profiles\</c> 相对它</param>
    /// <param name="legacyConfigPath">老版本 config.json 的路径（迁移用；传 null 就不迁移）</param>
    public ConfigStore(string settingsPath, string baseDirectory, string? legacyConfigPath = null)
    {
        _settingsPath = settingsPath;
        _baseDirectory = baseDirectory;
        _legacyConfigPath = legacyConfigPath;

        Config = new PluginConfig();
        Settings = new AppSettings();
    }

    /// <summary>配置档案（profiles\&lt;当前档案名&gt;.json）</summary>
    public PluginConfig Config { get; private set; }

    /// <summary>软件设置（settings.json）。同上。</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>exe 所在目录（档案目录相对它算）</summary>
    public string BaseDirectory => _baseDirectory;

    /// <summary>当前档案文件的完整路径</summary>
    public string ConfigPath => _configPath;

    /// <summary>档案目录 <c>&lt;exe&gt;\profiles</c></summary>
    public string ProfilesDirectory => ProfileLocator.ProfilesDirectoryOf(_baseDirectory);

    /// <summary>当前生效的档案名</summary>
    public string ProfileName => _profileName;

    public string SettingsPath => _settingsPath;

    /// <summary>有**任何**改动还没落盘（顶部状态条的"● 未保存"用它）</summary>
    public bool IsDirty => _configDirty || _settingsDirty;

    public bool IsConfigDirty => _configDirty;
    public bool IsSettingsDirty => _settingsDirty;

    /// <summary>未保存标记变了（顶部状态条与页面底部栏订阅它来刷新）</summary>
    public event Action? DirtyChanged;

    /// <summary>
    /// **换了档案** —— Config 已经指向另一份配置，各页面必须重新读一遍。。
    /// </summary>
    public event Action? ConfigChanged;

    // ------------------------------------------------------------
    // 读
    // ------------------------------------------------------------

    /// <summary>
    /// 三个文件都读：settings.json → 当前档案 → 配置本体。
    ///
    /// 先读软件设置，因为"当前是哪个档案"记在里面；
    /// 知道了档案名，才谈得上"配置该读哪个文件"。
    ///
    /// 读不到/读坏了都会拿到默认值（解析层一直宽松兜底），不会崩。
    /// </summary>
    public void Load()
    {
        Settings = AppSettings.Load(_settingsPath, _legacyConfigPath);

        _profileName = ProfileLocator.NormalizeName(Settings.Profile.Current);
        _configPath = ProfileLocator.EnsureProfile(_baseDirectory, _profileName, _legacyConfigPath);

        Config = PluginConfig.Load(_configPath);
        SetDirty(false, false);
    }

    /// <summary>只重读配置档案(放弃修改)</summary>
    public void ReloadConfig()
    {
        Config = PluginConfig.Load(_configPath);
        SetDirty(false, _settingsDirty);
    }

    /// <summary>只重读软件设置</summary>
    public void ReloadSettings()
    {
        Settings = AppSettings.Load(_settingsPath, _legacyConfigPath);
        SetDirty(_configDirty, false);
    }

    /// <summary>
    /// 切到另一个档案。
    ///
    ///   ① 记住选择并**立刻写进 settings.json** —— 下次启动要接着用这一份
    ///   ② 换掉配置路径（目标档案不存在就顺手建一份模板出来）
    ///   ③ 重读配置 —— 这一步之后，整个界面看到的都是新档案
    ///   ④ 通知各页面刷新
    ///
    /// **调用方有责任先把当前档案存盘**（见 <c>ProfileStore.SwitchTo</c>）：
    /// 第 ③ 步的重读会把内存里没保存的改动冲掉。
    /// </summary>
    public void SwitchProfile(string name)
    {
        var normalized = ProfileLocator.NormalizeName(name);

        _profileName = normalized;
        Settings.Profile.Current = normalized;
        _configPath = ProfileLocator.EnsureProfile(_baseDirectory, normalized);

        WriteAtomic(_settingsPath, JsonSerializer.Serialize(Settings, PluginConfig.Options));

        Config = PluginConfig.Load(_configPath);

        // 两边都清脏：设置刚写盘了；配置的未保存改动已被"换文件"作废（调用方先存过）
        SetDirty(false, false);

        ConfigChanged?.Invoke();
    }

    // ------------------------------------------------------------
    // 标脏
    // ------------------------------------------------------------

    /// <summary>改了档案里的东西。页面改任何"角色相关"字段后调它。</summary>
    public void MarkConfigDirty() => SetDirty(true, _settingsDirty);

    /// <summary>改了软件设置。页面改"外观/数据源"这类字段后调它。</summary>
    public void MarkSettingsDirty() => SetDirty(_configDirty, true);

    // ------------------------------------------------------------
    // 写
    // ------------------------------------------------------------

    /// <summary>把配置档案落盘（原子写 + .bak）。</summary>
    public void SaveConfig()
    {
        WriteAtomic(_configPath, JsonSerializer.Serialize(Config, PluginConfig.Options));
        SetDirty(false, _settingsDirty);
    }

    /// <summary>把软件设置落盘（原子写 + .bak）。</summary>
    public void SaveSettings()
    {
        WriteAtomic(_settingsPath, JsonSerializer.Serialize(Settings, PluginConfig.Options));
        SetDirty(_configDirty, false);
    }

    /// <summary>
    /// 原子写：先写临时文件 → 备份现有文件 → 用改名顶替。
    /// </summary>
    private static void WriteAtomic(string path, string json)
    {
        var temp = path + ".tmp";
        var backup = path + ".bak";

        try
        {
            File.WriteAllText(temp, json);

            if (File.Exists(path))
            {
                File.Copy(path, backup, overwrite: true);
                File.Move(temp, path, overwrite: true);
            }
            else
            {
                // 第一次保存：没有原文件可备份，也没有可替换的目标
                File.Move(temp, path);
            }
        }
        finally
        {
            // 中途失败时把临时文件收掉
            // 而且它会一直躺在用户的 profiles\ 目录里，看起来像个档案
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 收不掉就算了 */ }
        }
    }

    // ------------------------------------------------------------
    private void SetDirty(bool configDirty, bool settingsDirty)
    {
        if (_configDirty == configDirty && _settingsDirty == settingsDirty) return;

        _configDirty = configDirty;
        _settingsDirty = settingsDirty;
        DirtyChanged?.Invoke();
    }
}
