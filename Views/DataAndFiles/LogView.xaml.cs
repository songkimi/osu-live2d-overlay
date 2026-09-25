// ============================================================
// LogView.xaml.cs —— 日志页的"跟控件直接相关"的那几件事
//
// 这一页比前两页多两件事，都不是业务（业务在 LogViewModel 里）：
//   ① **定时拉增量**：日志从别的线程来，用轮询代替逐条派发（理由见 LogViewModel 的说明）
//   ② **自动滚动**：新行进来滚到底，用户手动往上翻就停下
//
// 另外"导出"和"打包诊断"需要选保存路径 —— 那是**对话框**，
// 按约定不能放进 ViewModel（VM 不许认识控件），所以在这里做，选好路径再交给 VM 写。
// ============================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OsuLive2dOverlay;

public partial class LogView : UserControl
{
    /// <summary>
    /// 刷新间隔。250ms 是个折中：够快（看着像实时），又不至于让 UI 线程一直在忙。
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly LogViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = RefreshInterval };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public LogView(LogViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Notify += ShowToast;

        _refreshTimer.Tick += OnRefreshTick;
        _toastTimer.Tick += (_, _) => HideToast();

        // 页面被换掉就停掉定时器 —— 不然切走之后它还在后台空转
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SettingIndex.Rebuild(this);

        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    // ------------------------------------------------------------
    // 定时拉增量 + 自动滚到底
    // ------------------------------------------------------------
    private void OnRefreshTick(object? sender, EventArgs e)
    {
        var before = _viewModel.Lines.Count;

        _viewModel.RefreshFromBuffer();

        // 有新行、而且用户没在往上翻 → 滚到底
        if (_viewModel.AutoScroll && _viewModel.Lines.Count > before)
            LogList.ScrollIntoView(_viewModel.Lines[^1]);
    }

    /// <summary>
    /// 判断"这次滚动是谁引起的"：
    ///   · 内容变高（ExtentHeightChange != 0）→ 是新日志进来了，不管
    ///   · 内容没变高、位置却动了（VerticalChange != 0）→ **是用户自己滚的**
    /// 用户往上翻就关掉自动滚动（否则新日志会一直把他拽到底部）；翻回底部再打开。
    /// </summary>
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0) return;

        if (e.VerticalChange < 0)
        {
            _viewModel.AutoScroll = false;
            return;
        }

        if (e.VerticalChange > 0)
        {
            var atBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1;
            if (atBottom) _viewModel.AutoScroll = true;
        }
    }

    // ------------------------------------------------------------
    // 需要选路径的两件事（对话框只能在这里做）
    // ------------------------------------------------------------
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出日志",
            FileName = $"osu-live2d-overlay-日志-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".txt",
        };

        if (dialog.ShowDialog() == true)
            _viewModel.ExportTo(dialog.FileName);
    }

    private void OnPackClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "一键打包诊断",
            FileName = $"osu-live2d-overlay-诊断-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
        };

        if (dialog.ShowDialog() == true)
            _viewModel.PackDiagnosticsTo(dialog.FileName);
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
