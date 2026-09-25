// ============================================================
// OverviewViewModel.cs —— 「概览」页（软件的门面）
// ============================================================
using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

public partial class OverviewViewModel : ObservableObject
{
    private readonly ConfigStore _store;

    public OverviewViewModel(ConfigStore store)
    {
        _store = store;
        Reload();
    }

    /// <summary>档案名列表（下拉用）</summary>
    public ObservableCollection<string> Profiles { get; } = new();

    [ObservableProperty] private string _selectedProfile = "";

    /// <summary>正在用哪一份档案</summary>
    [ObservableProperty] private string _profileName = "";

    /// <summary>有没有没保存的改动（配置或设置任一处）</summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>现在这个模型的说明（目录名 + 入口；缺哪个就直说缺哪个）</summary>
    [ObservableProperty] private string _modelSummary = "";

    /// <summary>这个模型在不在（用来决定那行说明的颜色）</summary>
    [ObservableProperty] private bool _modelLooksFine = true;

    /// <summary>一共有几份档案</summary>
    [ObservableProperty] private string _profileCountText = "";

    /// <summary>回执（切换成功 / 没得切）</summary>
    public event Action<string>? Notify;

    private void Reload()
    {
        ProfileName = _store.ProfileName;
        IsDirty = _store.IsDirty;

        // 模型：目录存不存在、入口填没填 —— 这两件事错了角色就是不出来，
        // 所以在门面上直接说清楚（体检里也有，但首页上看到更早）
        var model = _store.Config.Model;
        var exists = !string.IsNullOrWhiteSpace(model.Directory) && Directory.Exists(model.Directory);
        var hasEntry = !string.IsNullOrWhiteSpace(model.Entry);

        ModelLooksFine = exists && hasEntry;

        if (string.IsNullOrWhiteSpace(model.Directory))
            ModelSummary = "还没选模型目录 —— 去「形象」页选一个。";
        else if (!exists)
            ModelSummary = "模型目录不存在：" + model.Directory;
        else if (!hasEntry)
            ModelSummary = "模型目录找到了，但还没选入口文件（.model3.json）。";
        else
            ModelSummary = $"{Path.GetFileName(model.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}" +
                           $"（{model.Entry}）";

        Profiles.Clear();
        foreach (var entry in ProfileLocator.List(_store.BaseDirectory))
            Profiles.Add(entry.Name);

        ProfileCountText = Profiles.Count > 0 ? $"共 {Profiles.Count} 份" : "还没有档案";

        // 下拉默认落在当前那一份上（不在列表里就留空，避免选了个不存在的）
        SelectedProfile = Profiles.Contains(_store.ProfileName) ? _store.ProfileName : "";
    }

    [RelayCommand]
    private void SwitchProfile()
    {
        var target = SelectedProfile;

        if (string.IsNullOrWhiteSpace(target))
        {
            Notify?.Invoke("先选一份档案。");
            return;
        }

        if (target == _store.ProfileName)
        {
            Notify?.Invoke("已经在用这一份了。");
            return;
        }

        try
        {
            _store.SwitchProfile(target);
            Reload();

            // 悬浮窗读的是启动时那份快照，换档案它不会当场跟 —— 不说清用户会以为没生效
            Notify?.Invoke($"已切到「{target}」；悬浮窗要重启才用上新档案。");
        }
        catch (Exception ex)
        {
            Notify?.Invoke("切换失败：" + ex.Message);
        }
    }
}
