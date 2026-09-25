// ============================================================
// RunTimeView.xaml.cs —— 运行期调整页
//
// 与前几页同一套骨架（Toast）。这一页额外做一件"只有 View 能做"的事：
// **每次露出来就重读一遍**。
//
// 【为什么必须重读】
//   这个页面是随「数据与档案」的 Tab 容器一次性造好的，造完之后可能一直没人碰。
//   而"用户去拖悬浮窗"这件事随时可能发生 —— 不重读的话，
//   用户切回这一页看到的还是造页面那一刻的快照（永远是"没有待保存的调整"）。
//   数据在别处变、界面不刷新，就是"改了界面不更新"那一类问题。
//
// 用 IsVisibleChanged 而不是 TabControl 的 SelectionChanged：
// 这一页不需要知道"自己在哪个 Tab 里"，只要知道自己露出来了就够 ——
// 少一层对父容器的假设。
// ============================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OsuLive2dOverlay;

public partial class RunTimeView : UserControl
{
    private readonly RunTimeViewModel _viewModel;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public RunTimeView(RunTimeViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.Notify += ShowToast;
        _toastTimer.Tick += (_, _) => HideToast();
    }

    /// <summary>露出来了就重读 —— 见文件头"为什么必须重读"</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) _viewModel.Refresh();
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
