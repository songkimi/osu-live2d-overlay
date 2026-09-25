// ============================================================
// ProfileViewModel.cs —— 「数据与档案 → 配置档案」页
//
// 【这一页管什么】
//   一、换一套配置玩。每个档案是**完整的一份** config.json
//       （模型 / 规则 / 语音 / 界面感知都在里面），切换 = 整个软件换一套。
//   二、档案本身的管理：新建（复制当前）、改名、删除、导入、导出。
//
// 【为什么一行一个按钮，而不是"先选中再点下面的按钮"】
//   后者要多维护一份"选中了谁"的状态，而这份状态会因为刷新列表
//   （改完名、切完换）而失效 —— 要么按钮闪一下灰、要么选中跑到别的行上，
//   都是要额外写代码去修的岔子。按钮直接长在行上，就没有这个状态。
//   （而且它和「文件位置」页长得一样：一行 = 一个东西 + 它的几个动作。）
//
// 【对话框不进 ViewModel】（《项目结构约定》§四）
//   弹窗要窗口句柄、要消息循环，属于 View 的事。所以这里只留两个口子：
//     · Notify  —— 变成页面上的一行提示（Toast）
//     · Confirm —— 变成 View 弹的一个"确定吗"（删除用）
//   文件对话框（导入/导出）干脆整件事留在 code-behind 里，调下面两个公开方法。
// ============================================================
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

/// <summary>档案列表里的一行。**不可变** —— 内容变了就整张列表重建（见 Refresh）。</summary>
public sealed class ProfileRow
{
    public ProfileRow(ProfileEntry entry, bool isCurrent)
    {
        Name = entry.Name;
        Path = entry.Path;
        IsCurrent = isCurrent;
        ModifiedText = entry.Modified.ToString("yyyy-MM-dd HH:mm");
    }

    public string Name { get; }
    public string Path { get; }
    public bool IsCurrent { get; }
    public string ModifiedText { get; }

    /// <summary>已经在用的那份不显示「切换」——显示了也不会有人点</summary>
    public bool CanSwitch => !IsCurrent;

    /// <summary>当前那份不给「删除」（<see cref="ProfileStore.Delete"/> 也会拒绝，界面先不给按）</summary>
    public bool CanDelete => !IsCurrent;
}

public partial class ProfileViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private readonly ProfileStore _profiles;

    /// <summary>编辑层正在做的是"新建"（true）还是"改名"（false）</summary>
    private bool _editingNew;

    /// <summary>改名时改的是哪一行（编辑层弹出后列表可能已经重建过，所以按名字记）</summary>
    private string _editingName = "";

    public ProfileViewModel(ConfigStore store)
    {
        _store = store;
        _profiles = new ProfileStore(store);

        Refresh();
    }

    public ObservableCollection<ProfileRow> Profiles { get; } = new();

    /// <summary>页面上那句"正在使用：××"</summary>
    [ObservableProperty] private string _currentName = "";

    // ---- 编辑层（新建 / 改名共用一个）----
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string _editText = "";

    /// <summary>要 View 弹一个"确定吗"（对话框不能进 VM）</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>一条提示（View 把它变成 Toast）</summary>
    public event Action<string>? Notify;

    /// <summary>档案目录的路径（顶部显示，让用户知道东西存在哪）</summary>
    public string Directory => _profiles.Directory;

    // ------------------------------------------------------------
    // 列表
    // ------------------------------------------------------------

    private void Refresh()
    {
        var current = _profiles.CurrentName;

        Profiles.Clear();
        foreach (var entry in _profiles.List())
            Profiles.Add(new ProfileRow(entry, entry.Name == current));

        CurrentName = current;
        OnPropertyChanged(nameof(Directory));
    }

    // ------------------------------------------------------------
    // 换一套用
    // ------------------------------------------------------------

    [RelayCommand]
    private void Switch(ProfileRow? row)
    {
        if (row is null || row.IsCurrent) return;

        try
        {
            _profiles.SwitchTo(row.Name);
            Refresh();

            // 悬浮窗是**启动时读下来的那一份快照**，换了档案它不会当场换模型。
            // 不说一声，用户会以为切换没生效（切换本身是生效的，只是它还没跟上）——
            // 所以这里明确告诉他"怎么让它跟上"。
            Notify?.Invoke(App.Overlay is null
                ? $"已切换到档案「{row.Name}」"
                : $"已切换到档案「{row.Name}」。悬浮窗还在用上一个档案，重启悬浮窗后生效。");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"切换失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // 新建 / 改名（同一个编辑层）
    // ------------------------------------------------------------

    [RelayCommand]
    private void BeginNew()
    {
        _editingNew = true;
        EditTitle = "新建档案";
        EditText = SuggestNewName();
        IsEditing = true;
    }

    [RelayCommand]
    private void BeginRename(ProfileRow? row)
    {
        if (row is null) return;

        _editingNew = false;
        _editingName = row.Name;
        EditTitle = "重命名档案";
        EditText = row.Name;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void ConfirmEdit()
    {
        var name = ProfileLocator.NormalizeName(EditText);

        var problem = ProfileLocator.ValidateName(name);
        if (problem is not null)
        {
            Notify?.Invoke(problem);
            return;   
        }

        try
        {
            if (_editingNew)
            {
                _profiles.SaveAsNew(name);
                Notify?.Invoke($"已新建档案「{name}」，现在用的就是它");
            }
            else
            {
                _profiles.Rename(_editingName, name);
                Notify?.Invoke($"已改名为「{name}」");
            }
        }
        catch (Exception ex)
        {
            Notify?.Invoke(ex.Message);
            return;
        }

        IsEditing = false;
        Refresh();
    }

    /// <summary>新建时的预填名：`新档案 2`、`新档案 3`……避开已有的名字</summary>
    private string SuggestNewName()
    {
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"新档案 {i}";
            if (!_profiles.Exists(candidate)) return candidate;
        }

        return "新档案";
    }

    // ------------------------------------------------------------
    // 删除
    // ------------------------------------------------------------

    [RelayCommand]
    private void Delete(ProfileRow? row)
    {
        if (row is null || row.IsCurrent) return;

        // 删文件不可撤销，所以必须问一次。
        // Confirm 由 View 实现 —— 没接上（比如将来在测试里用）就当作不确认，
        // **默认不动用户的文件**。
        if (Confirm is null || !Confirm($"确定要删除档案「{row.Name}」吗？\n\n" +
                                        "它的配置文件会被删掉，不能撤销。"))
            return;

        try
        {
            _profiles.Delete(row.Name);
            Refresh();
            Notify?.Invoke($"已删除档案「{row.Name}」");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"删除失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // 导入 / 导出（文件对话框在 code-behind，这两件事本身在这里）
    // ------------------------------------------------------------

    /// <summary>导进来**并切过去** —— 用户导一份配置，目的就是要用它</summary>
    public void ImportFrom(string sourcePath)
    {
        try
        {
            _profiles.Import(sourcePath);
            Refresh();
            Notify?.Invoke($"已导入并切换到「{_profiles.CurrentName}」");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"导入失败：{ex.Message}");
        }
    }

    public void ExportTo(string targetPath)
    {
        try
        {
            _profiles.Export(targetPath);
            Notify?.Invoke($"已导出到 {targetPath}");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"导出失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var directory = _profiles.Directory;
            System.IO.Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"打不开：{ex.Message}");
        }
    }
}
