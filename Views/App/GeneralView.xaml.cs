// ============================================================
// GeneralView.xaml.cs —— 页面自己的两件小事：索引、提示
//
// 它**不做业务**（那是 ViewModel 的事）。这里只有两件跟"控件"直接相关的事
// （《项目结构约定》§四 第 1 条：View 只做"把 VM 的状态绑上去"和"把用户操作转成命令"）：
//   ① 页面加载完重建 SettingIndex（要等可视树长出来）
//   ② 把 VM 发来的 <c>Notify</c> 事件显示成 toast
// ============================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OsuLive2dOverlay;

public partial class GeneralView : UserControl
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public GeneralView(GeneralViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel;
        viewModel.Notify += ShowToast;

        // 托盘图标要**当场**跟着开关变（★ 2026-09-24）——
        // 但托盘控件在 MainWindow 上，所以这里只做一次转发：
        // VM 说"该显示/隐藏了"，页面把它交给真正的持有者。
        // （VM 不认识控件、页面不认识托盘，各自只碰自己那一段。）
        viewModel.TrayIconVisibilityChanged += _ =>
        {
            if (Window.GetWindow(this) is MainWindow main) main.ApplyTrayVisibility();
        };

        _toastTimer.Tick += (_, _) => HideToast();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 索引必须等可视树建好才建 —— 在那之前模板里的控件还不存在。
        SettingIndex.Rebuild(this);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastPanel.Visibility = Visibility.Visible;

        _toastTimer.Stop();

        // 错误类**不自动消失**（定稿 §4）：它需要用户真的看见，
        // 所以留着直到点一下。成功类 3 秒后自己收掉。
        if (message.StartsWith("保存失败", StringComparison.Ordinal)) return;

        _toastTimer.Start();
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e) => HideToast();

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastPanel.Visibility = Visibility.Collapsed;
    }
}
