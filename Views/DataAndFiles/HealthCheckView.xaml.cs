// ============================================================
// HealthCheckView.xaml.cs —— 与 GeneralView 同一套做法
//
// View 只做两件跟"控件"直接相关的事（《项目结构约定》§四 第 1 条）：
//   ① 页面加载完重建 SettingIndex（要等可视树长出来）
//   ② 把 VM 发来的 Notify 显示成 toast
//
// 这一页比通用页多一层循环（问题组 → 组内的问题），但骨架一样：
// 绑 VM、转命令、其余不碰。
// ============================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OsuLive2dOverlay;

public partial class HealthCheckView : UserControl
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public HealthCheckView(HealthCheckViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel;
        viewModel.Notify += ShowToast;

        _toastTimer.Tick += (_, _) => HideToast();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 索引必须等可视树建好才建 —— 在那之前模板里的控件还不存在。
        // 体检的"点击定位"以后就靠这个索引找到「体检.检查模式」这类控件。
        SettingIndex.Rebuild(this);
    }

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
