// ============================================================
// UpdateView.xaml.cs —— 「应用 → 更新」页
//
// 和别的页同一套骨架，只有两件跟控件直接相关的事：
//   ① 页面加载完重建 SettingIndex
//   ② 把 VM 发来的 Notify 显示成 toast
//
// 它**不做业务**：联网、比版本号在 Sources/ 和 Logic/ 里，状态在 VM 里。
// ============================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OsuLive2dOverlay;

public partial class UpdateView : UserControl
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public UpdateView(UpdateViewModel viewModel)
    {
        InitializeComponent();

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

        // 错误类不自动消失（和别的页一致）：它需要用户真的看见
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
