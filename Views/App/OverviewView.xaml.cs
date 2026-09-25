// ============================================================
// OverviewView.xaml.cs —— 「概览」页
//
// 和别的页面同一套骨架：SettingIndex（让体检/搜索能定位到这一页的控件）+ Toast。
// 这一页没有"只有 View 能做"的事（不碰预览、不碰窗口），所以 code-behind 很短。
// ============================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OsuLive2dOverlay;

public partial class OverviewView : UserControl
{
    private readonly OverviewViewModel _viewModel;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public OverviewView(OverviewViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.Notify += ShowToast;
        _toastTimer.Tick += (_, _) => HideToast();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SettingIndex.Rebuild(this);

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastPanel.Visibility = Visibility.Visible;

        _toastTimer.Stop();

        // 错误类不自动消失（定稿 §4）—— 要用户真的看见
        if (message.Contains("失败", StringComparison.Ordinal)) return;

        // 换档案的提示比别的长一点（要说清"悬浮窗要重启"），所以给 4 秒
        _toastTimer.Start();
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e) => HideToast();

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastPanel.Visibility = Visibility.Collapsed;
    }
}
