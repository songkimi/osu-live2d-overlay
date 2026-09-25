// ============================================================
// OverlaySettingsView.xaml.cs —— 「悬浮窗」页
//
// 与前几页同一套骨架（SettingIndex + Toast），加两件"只有 View 能做"的事：
//   ① 把预览区接上 —— 界面上一改，右边就得跟着变
//   ② 预览的生命周期：**露出来才起、切走就挂起**（定稿 §1.2 的懒加载约定）
//
// 【为什么预览要在这里推，而不是让 PreviewPane 自己去监听】
//   预览要显示的是"**当前选中那一档**在**当前选中那份站位**下的样子" ——
//   这个"当前选中什么"是页面的状态，不是预览的状态。
//   让预览反过来去猜页面的选择，就又多了一份要同步的真相。
// ============================================================
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OsuLive2dOverlay;

public partial class OverlaySettingsView : UserControl
{
    private readonly OverlaySettingsViewModel _viewModel;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>当前挂着属性监听的哪一档（换 Tab 时要摘掉旧的）</summary>
    private SceneSettingsViewModel? _hooked;

    public OverlaySettingsView(OverlaySettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.Notify += ShowToast;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _toastTimer.Tick += (_, _) => HideToast();

        HookSelected();

        // 页面被切走 / 切回来：预览跟着挂起与恢复
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SettingIndex.Rebuild(this);
    }

    // ------------------------------------------------------------
    // 预览的生命周期
    // ------------------------------------------------------------

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // **懒加载**：第一次真被看见时才起那只 WebView2（启一次约一秒、
            // 还有一份内存），起好之后 PushToPreview 把当前状态发过去。
            // 配置从 `_viewModel` 那边拿 —— 预览区自己不认识 ConfigStore，
            // "换了配置要不要重建"由它做一次引用比较（见 PreviewPane 的说明）。
            await Preview.EnsureStartedAsync(_viewModel.Config, _viewModel.DebugMode);
            PushToPreview();
        }

        // 切走那边**不用做事**：页面被换掉时预览区自己会在 `Unloaded` 里释放 WebView2
        //（★ 2026-09-23：原来这里是 `Suspend()`，但页面是销毁重建的，
        //  挂起的实例再也没人回收 —— 反复切页会攒下十几只，掉帧就是这么来的）。
    }

    // ------------------------------------------------------------
    // 界面 → 预览
    // ------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 换了 Tab（Selected）或换了"看哪一份站位"（PreviewModeIndex）
        if (e.PropertyName is nameof(OverlaySettingsViewModel.Selected)
                            or nameof(OverlaySettingsViewModel.PreviewModeIndex))
        {
            HookSelected();
            PushToPreview();
        }
    }

    /// <summary>把属性监听挂到当前这一档上（含它那四行模式站位）</summary>
    private void HookSelected()
    {
        if (_hooked is not null)
        {
            _hooked.PropertyChanged -= OnSceneChanged;
            foreach (var row in _hooked.Modes) row.PropertyChanged -= OnSceneChanged;
        }

        _hooked = _viewModel.Selected;

        if (_hooked is not null)
        {
            _hooked.PropertyChanged += OnSceneChanged;
            foreach (var row in _hooked.Modes) row.PropertyChanged += OnSceneChanged;
        }
    }

    private void OnSceneChanged(object? sender, PropertyChangedEventArgs e) => PushToPreview();

    /// <summary>
    /// 把"现在该显示什么"发给预览。
    ///
    /// 站位**走的是和悬浮窗同一个取值方法**（`SceneProfile.PlacementFor`）——
    /// 预览要是自己另算一套，"看到的"和"跑到"的迟早对不上，
    /// 而那正是这个软件最不该出的错。
    /// </summary>
    private void PushToPreview()
    {
        var scene = _viewModel.Selected;
        if (scene is null || !scene.Enabled)
        {
            Preview.HideCharacter();
            return;
        }

        // 尺寸留空 = "用程序默认尺寸"。这里必须按**默认尺寸**画框，
        // 而不是显示一个用户没设过的数字（默认尺寸的唯一出处见 OverlayWindow.DefaultWidth）。
        var width = scene.WindowWidth > 0 ? scene.WindowWidth : OverlayWindow.DefaultWidth;
        var height = scene.WindowHeight > 0 ? scene.WindowHeight : OverlayWindow.DefaultHeight;

        Preview.ShowPlacement(scene.Scene, _viewModel.PreviewPlacement, width, height);
    }

    // ------------------------------------------------------------

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastPanel.Visibility = Visibility.Visible;

        _toastTimer.Stop();

        // 错误类不自动消失（定稿 §4）—— 要用户真的看见
        if (message.Contains("失败", StringComparison.Ordinal)) return;

        _toastTimer.Start();
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e) => HideToast();

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastPanel.Visibility = Visibility.Collapsed;
    }
}
