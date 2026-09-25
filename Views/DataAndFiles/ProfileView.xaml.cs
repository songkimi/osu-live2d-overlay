// ============================================================
// ProfileView.xaml.cs —— 配置档案页
//
// 与前几页同一套骨架（Toast），加三件"只有 View 能做的事"：
//   · 导入 / 导出 —— 文件对话框要窗口句柄，不能放进 ViewModel（《项目结构约定》§四）
//   · 删除的"确定吗" —— 同理，用 Confirm 回调接回 ViewModel
//   · 编辑层弹出后把光标放进输入框 —— 纯界面行为
//
// 【为什么删除要问、切换不问】
//   切换是可逆的（再点回来就行），而且切换前会自动把当前档案存盘，什么都不会丢。
//   删除文件不可撤销 —— 只有它值得打断用户一次。
// ============================================================
using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OsuLive2dOverlay;

public partial class ProfileView : UserControl
{
    private readonly ProfileViewModel _viewModel;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public ProfileView(ProfileViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.Notify += ShowToast;
        viewModel.Confirm = AskConfirm;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _toastTimer.Tick += (_, _) => HideToast();
    }

    /// <summary>删除前的确认。用 owner 弹，免得它跑到主窗口后面去。</summary>
    private bool AskConfirm(string message)
    {
        var owner = Window.GetWindow(this);

        var answer = owner is null
            ? MessageBox.Show(message, "osu-live2d-overlay", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, "osu-live2d-overlay",
                              MessageBoxButton.YesNo, MessageBoxImage.Warning);

        return answer == MessageBoxResult.Yes;
    }

    /// <summary>
    /// 编辑层一出现就把光标放进输入框并全选。
    /// 必须等到布局跑完（输入框这时才可见）—— 对不可见的控件调 Focus 是没用的，
    /// 所以派到 Input 优先级去做，而不是当场调。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProfileViewModel.IsEditing) || !_viewModel.IsEditing) return;

        Dispatcher.InvokeAsync(
            () =>
            {
                EditBox.Focus();
                EditBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    /// <summary>回车 = 确定、Esc = 取消。输入框里最自然的两个键。</summary>
    private void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.ConfirmEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入配置档案",
            Filter = "配置档案 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_viewModel.Directory) ? _viewModel.Directory : AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog() == true) _viewModel.ImportFrom(dialog.FileName);
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出当前档案",
            Filter = "配置档案 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = _viewModel.CurrentName + ".json",
            InitialDirectory = AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog() == true) _viewModel.ExportTo(dialog.FileName);
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
