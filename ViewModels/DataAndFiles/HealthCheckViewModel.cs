// ============================================================
// HealthCheckViewModel.cs —— 「数据与档案 → 配置体检」页
//
// 【它是干什么的】
//   把 `ConfigHealthCheck` 产出的问题清单（级别 / 问题 / FieldPath）摆成能看的样子：
//   按**板块分组**（模型 / 语音 / 常态区间 / 界面感知…），每组标数量，顶上给一句汇总。
//
// 【它检查的是"当前配置"】
//   也就是 `ConfigStore.Config` 里那一份 —— 用户在界面上改了什么，它看到的就是什么
//   （**不需要先保存**）。不要把它想成"给磁盘上的文件做体检"
//    请不要通过编写json来改动你的配置文件！
//
// 【它不表态】
//   `ConfigHealthCheck` 只管"哪里不对"，不管"该怎么办"
//   要不要跳过去、跳过去高不高亮，是界面层的决定。这个 VM 就是那个界面层。
//
// 【第一版没做的两件事，别当成忘了】
//   ① **就地修复**（"删掉这个情绪"这类按钮）：要改配置，得先想清"改完要不要立刻存"
//   ② **点击定位**（跳到那个设置项 + 滚动 + 高亮）：需要跨页面导航，
//      而 `SettingIndex` 目前只索引"当前这一页"。等页面多起来、导航成型了再做 ——
//      那也正是决策 29 说"SettingIndex 要一开始就做"的原因：锚点先埋好，跳转随时能接。
// ============================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

/// <summary>一条体检问题（给界面看的版本）</summary>
public sealed class HealthIssueItem
{
    public HealthIssueItem(HealthIssue issue)
    {
        Level = issue.Level;
        Message = issue.Message;
        FieldPath = issue.FieldPath;
    }

    public HealthLevel Level { get; }

    /// <summary>
    /// 问题级别（"错误"/"警告"/"提示"）。
    /// 暴露成字符串是为了让 XAML 的 `DataTrigger` 能直接比 ——
    /// 拿枚举当触发值要绕一层类型转换，字符串更稳、也更符合"界面上一处一个词"。
    /// </summary>
    public string LevelText => Level switch
    {
        HealthLevel.Error => "错误",
        HealthLevel.Warning => "警告",
        _ => "提示"
    };

    /// <summary>问题是什么 + 后果</summary>
    public string Message { get; }

    /// <summary>给程序用的坐标。显示出来是为了让用户知道"改配置文件时该找哪一项"</summary>
    public string FieldPath { get; }
}

/// <summary>按板块聚在一起的一组问题（界面上一张卡片）</summary>
public sealed class HealthIssueGroup
{
    public HealthIssueGroup(string name, List<HealthIssueItem> items, int maxSeverity)
    {
        Name = name;
        Items = items;
        MaxSeverity = maxSeverity;
    }

    public string Name { get; }

    public List<HealthIssueItem> Items { get; }

    /// <summary>组内最严重的那个级别 —— 用来决定这一组卡片用什么颜色、以及排序</summary>
    public int MaxSeverity { get; }

    public string CountText => $"{Items.Count} 条";
}

public partial class HealthCheckViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private bool _loading;

    public HealthCheckViewModel(ConfigStore store)
    {
        _store = store;
        _loading = true;
        CheckMode = store.Settings.HealthCheck.ModeText;
        _loading = false;

        Recheck();

        // 换了档案 = 被检查的对象整份换掉了，必须重查。
        // 不重查的话，用户切到另一个角色之后看到的还是上一个角色的检查结果 ——
        // 而这一页的全部价值就是"你现在用的这份配置有没有问题"。
        _store.ConfigChanged += Recheck;
    }

    /// <summary>按板块分好组的问题清单</summary>
    public ObservableCollection<HealthIssueGroup> IssueGroups { get; } = new();

    public IReadOnlyList<string> CheckModeOptions { get; } = new[] { "宽松", "严格" };

    /// <summary>检查模式：宽松 = 能兜住的问题只提醒；严格 = 必须改掉。只作用于"区间与标识"那几条。</summary>
    [ObservableProperty] private string _checkMode = "宽松";

    /// <summary>顶上一句话汇总</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>一条问题都没有（界面显示"没发现问题"的空状态）</summary>
    [ObservableProperty] private bool _hasNoIssues;

    /// <summary>上次检查的时间（让人知道"这是刚才的结果"）</summary>
    [ObservableProperty] private string _checkedAt = "";

    public event Action<string>? Notify;

    // ------------------------------------------------------------

    [RelayCommand]
    private void Recheck()
    {
        var config = _store.Config;

        // 体检本身不碰文件系统 —— "外部世界"由探测器问好了再传进来。
        // 数据源那一段（第 21 条）在**软件设置**里，所以两个都传 ——
        // 只传一个的话，第 21 条要么查不到、要么拿"没探测过"当"不存在"发出假警报。
        var environment = HealthEnvironmentProbe.Probe(config, _store.Settings.DataSource);
        var issues = ConfigHealthCheck.Check(config, environment, _store.Settings.HealthCheck.Mode,
                                             _store.Settings.DataSource);

        // 按板块分组：`模型.入口` → "模型"、`语音.情绪[1]` → "语音"、`界面感知.打歌.…` → "界面感知"
        var groups = issues
            .GroupBy(issue => GroupOf(issue.FieldPath))
            .Select(g => new HealthIssueGroup(
                        g.Key,
                        g.Select(issue => new HealthIssueItem(issue)).ToList(),
                        g.Max(issue => (int)issue.Level)))
            // 最严重的排前面 —— 用户先看要紧的
            .OrderByDescending(g => g.MaxSeverity)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();

        IssueGroups.Clear();
        foreach (var group in groups) IssueGroups.Add(group);

        HasNoIssues = issues.Count == 0;
        Summary = issues.Count == 0 ? "没有发现问题" : string.Join(" · ", Summarize(issues));
        CheckedAt = DateTime.Now.ToString("HH:mm:ss");
    }

    private static IEnumerable<string> Summarize(IReadOnlyList<HealthIssue> issues)
    {
        var errors = issues.Count(i => i.Level == HealthLevel.Error);
        var warnings = issues.Count(i => i.Level == HealthLevel.Warning);
        var infos = issues.Count(i => i.Level == HealthLevel.Info);

        if (errors > 0) yield return $"{errors} 个错误";
        if (warnings > 0) yield return $"{warnings} 个警告";
        if (infos > 0) yield return $"{infos} 个提示";
    }

    /// <summary>
    /// 从 FieldPath 取"板块名"：`语音.情绪[1]` → `语音`。
    /// 遇到第一个 `.` 或 `[` 就停 —— 数组下标不算板块的一部分。
    /// </summary>
    private static string GroupOf(string fieldPath)
    {
        var cut = fieldPath.IndexOfAny(['.', '[']);
        return cut <= 0 ? fieldPath : fieldPath[..cut];
    }

    /// <summary>
    /// 改检查模式就**立刻存**，不等"保存"按钮。
    ///
    /// 为什么这一项特殊：它只有两个值、改它没有"改到一半"的状态，
    /// 而它的作用就是"让报告重新判定一遍"—— 存了立刻重算才算即时的。
    /// （其余设置项仍然走"标脏 → 点保存"那套，两套不要混。）
    /// </summary>
    partial void OnCheckModeChanged(string value)
    {
        if (_loading) return;

        _store.Settings.HealthCheck.ModeText = value;
        _store.MarkSettingsDirty();

        try
        {
            _store.SaveSettings();
        }
        catch (Exception ex)
        {
            // 存不下也要让用户知道
            Notify?.Invoke($"保存检查模式失败：{ex.Message}");
        }

        Recheck();
    }
}
