// ============================================================
// ModelViewModel.cs —— 「形象」页
//
// 【这一版是最小竖切】
//   · 当前模型：模型目录 → 扫描 → 模型入口 / 待机动作
//   · 扫描结果（一行说明 + 问题清单）
//   · 目光跟随鼠标
//   **标识列表（表情库 / 动作库）与预览区不在这里
//   预览区只做在「行为」，而没有预览的清单只是几行点了没反应的字，
//   于是那两样整块搬去「行为」页。这一页只管"用哪个模型"。
//
// 【为什么模型入口是"选"而不是"打"】
//   用户自己的原话：
//   > "模型入口该由扫描器决定，界面上应该是 readonly ，让用户手打很危险"
//   所以入口由 `ModelScanner.FindEntries` 找出来，用户只能从找到的里面挑。
//   这也是体检第 1/2 条存在的理由。
//
// 【扫描什么时候跑】
//   目录变了、入口变了、以及用户点「重新扫描」。**不在每次输入时跑** ——
//   扫描要读 model3.json 和一堆 exp3/motion3，是个文件系统动作。
//
// 【换目录之后为什么要重扫两遍】
//   第一遍只找入口（`FindEntries` 不需要入口），定下入口之后第二遍才谈得上
//   表情和动作组（`Scan` 要先知道入口）。顺序反了就只能扫出"没有入口"。
// ============================================================
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

public partial class ModelViewModel : ObservableObject
{
    private readonly ConfigStore _store;

    /// <summary>正在从配置往界面上灌值（这期间的变化不算用户改动，不标脏）</summary>
    private bool _loading;

    /// <summary>正在扫描（防止"改了入口 → 重扫 → 又改入口"转圈）</summary>
    private bool _scanning;

    public ModelViewModel(ConfigStore store)
    {
        _store = store;

        ReloadFromConfig();

        _store.DirtyChanged += () =>
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            ResetToDefaultCommand.NotifyCanExecuteChanged();
        };
    }

    // ---- 配置字段 ----
    // 键名：模型.目录 / 模型.入口 / 模型.待机动作 / 目光跟随鼠标

    [ObservableProperty] private string _modelDirectory = "";
    [ObservableProperty] private string _modelEntry = "";
    [ObservableProperty] private string _idleGroup = "";
    [ObservableProperty] private bool _followCursor = true;

    /// <summary>目录里找到的入口文件（相对路径）—— 用户只能从这里挑</summary>
    public ObservableCollection<string> EntryOptions { get; } = new();

    /// <summary>扫描出来的、**已注册**的动作组 —— 待机动作只能选这些</summary>
    public ObservableCollection<string> IdleGroupOptions { get; } = new();

    /// <summary>扫描结果一句话（几个表情、几个动作组）</summary>
    [ObservableProperty] private string _scanSummary = "";

    /// <summary>扫描遇到的问题（找不到入口、解析失败……）</summary>
    [ObservableProperty] private string _scanProblems = "";

    [ObservableProperty] private bool _hasScanProblems;

    /// <summary>有目录可选（没选目录时那两个下拉是空的，界面上要说得清）</summary>
    [ObservableProperty] private bool _hasEntries;

    public bool HasUnsavedChanges => _store.IsDirty;

    public event Action<string>? Notify;

    // ------------------------------------------------------------
    // 改动 → 标脏（可能还要重扫）
    // ------------------------------------------------------------

    partial void OnModelDirectoryChanged(string value)
    {
        MarkDirty();
        Rescan();
    }

    partial void OnModelEntryChanged(string value)
    {
        MarkDirty();
        Rescan();       // 换了入口 = 换了模型定义，表情和动作组要重来
    }

    partial void OnIdleGroupChanged(string value) => MarkDirty();

    partial void OnFollowCursorChanged(bool value) => MarkDirty();

    private void MarkDirty()
    {
        if (_loading) return;
        _store.MarkConfigDirty();
    }

    // ------------------------------------------------------------
    // 扫描
    // ------------------------------------------------------------

    /// <summary>
    /// 重扫一遍：目录里有哪些入口 → 定下入口 → 扫出表情与动作组。
    /// 全程不抛 —— 扫描器自己会把麻烦记进 Problems。
    /// </summary>
    [RelayCommand]
    private void Rescan()
    {
        if (_scanning) return;
        _scanning = true;

        try
        {
            // ① 找入口（这一步只要目录，不要入口）
            var entries = ModelScanner.FindEntries(ModelDirectory);

            EntryOptions.Clear();
            foreach (var entry in entries) EntryOptions.Add(entry);
            HasEntries = entries.Count > 0;

            // 配置里那个入口如果已经不在目录里了（模型被换过/删过），
            // 就落到找到的第一个 —— 否则界面显示一个根本不存在的入口，扫描必然全空
            if (!entries.Contains(ModelEntry))
                ModelEntry = entries.Count > 0 ? entries[0] : "";

            // ② 有入口才谈得上表情和动作组
            if (ModelEntry.Length == 0)
            {
                IdleGroupOptions.Clear();
                ScanSummary = ModelDirectory.Length == 0
                    ? "还没选模型目录。"
                    : "这个目录里没有找到 *.model3.json 入口文件。";
                SetProblems(Array.Empty<string>());
                return;
            }

            var scan = ModelScanner.Scan(ModelDirectory, ModelEntry);

            // 待机动作只能选**已注册**的动作组 —— 没注册的组页面不认，选了也不会播
            var groups = scan.Motions.Where(m => m.Registered).Select(m => m.Id).ToList();

            IdleGroupOptions.Clear();
            foreach (var group in groups) IdleGroupOptions.Add(group);

            // 配置里那个组没了 → 挑一个默认的（优先名字像 IDLE 的），不让用户从零手打
            if (!groups.Contains(IdleGroup))
                IdleGroup = PickIdle(groups);

            var registeredExpressions = scan.Expressions.Count(e => e.Registered);
            ScanSummary = $"扫描到 {scan.Expressions.Count} 个表情（已登记 {registeredExpressions} 个）、" +
                          $"{scan.Motions.Count} 个动作组（可选 {groups.Count} 个）。";

            SetProblems(scan.Problems);
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>
    /// 从动作组里挑一个当默认待机动作：优先名字里带 idle 的，其次第一个。
    /// 挑不到就空着（那是"这个模型没注册任何动作组"，属于模型本身的问题，
    /// 会在 Problems 里体现 —— 不该由这里瞎编一个名字）。
    /// </summary>
    private static string PickIdle(IReadOnlyList<string> groups)
    {
        var idle = groups.FirstOrDefault(g =>
            g.Contains("idle", StringComparison.OrdinalIgnoreCase));

        return idle ?? (groups.Count > 0 ? groups[0] : "");
    }

    private void SetProblems(IReadOnlyList<string> problems)
    {
        HasScanProblems = problems.Count > 0;
        ScanProblems = string.Join("\n", problems);
    }

    // ------------------------------------------------------------
    // 保存 / 放弃 / 恢复默认
    // ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Save()
    {
        var model = _store.Config.Model;
        model.Directory = ModelDirectory;
        model.Entry = ModelEntry;
        model.IdleGroup = IdleGroup;
        _store.Config.FollowCursor = FollowCursor;

        try
        {
            _store.SaveConfig();
            Notify?.Invoke("已保存（悬浮窗下次启动才认新模型）");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"保存失败：{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Discard()
    {
        _store.ReloadConfig();
        ReloadFromConfig();
        Notify?.Invoke("已放弃改动");
    }

    /// <summary>恢复默认 = 空目录 / 空入口 / 空待机动作 / 目光跟随打开</summary>
    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void ResetToDefault()
    {
        var defaults = new PluginConfig();

        _loading = true;
        try
        {
            ModelDirectory = "";
            ModelEntry = "";
            IdleGroup = "";
            FollowCursor = defaults.FollowCursor;
        }
        finally
        {
            _loading = false;
        }

        // 清空之后要重扫一次，让"待机动作"那几个下拉跟着空掉
        Rescan();
        MarkDirty();

        Notify?.Invoke("已恢复默认（还要点保存才会生效）");
    }

    [RelayCommand]
    private void OpenModelFolder()
    {
        try
        {
            if (ModelDirectory.Length == 0)
            {
                Notify?.Invoke("还没选模型目录");
                return;
            }

            if (!Directory.Exists(ModelDirectory))
            {
                Notify?.Invoke("这个目录不存在");
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ModelDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"打不开：{ex.Message}");
        }
    }

    // ------------------------------------------------------------

    /// <summary>
    /// 从配置读一遍。
    ///
    /// 灌值本身不算改动（整段带 _loading），但**末尾那次重扫故意留在 _loading 之外**：
    /// 如果配置里存的入口/待机动作在磁盘上已经不存在（模型被换过），
    /// 重扫会把它纠正成实际能找到的 —— 那个纠正**应该**标脏，
    /// 因为配置文件确实还指着一个不存在的东西，用户需要有机会把它存下来。
    /// 配置本身没问题时什么都不会发生，页面照样是干净的。
    /// </summary>
    private void ReloadFromConfig()
    {
        _loading = true;
        try
        {
            var config = _store.Config;

            ModelDirectory = config.Model.Directory;
            ModelEntry = config.Model.Entry;
            IdleGroup = config.Model.IdleGroup;
            FollowCursor = config.FollowCursor;
        }
        finally
        {
            _loading = false;
        }

        // 重扫一次：配置里存的入口/待机动作可能已经不在了（模型被换过），
        // 界面得如实反映"现在这份配置在磁盘上到底能扫出什么"
        Rescan();
    }
}
