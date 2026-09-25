// ============================================================
// ModelView.xaml.cs —— 形象页
//
// 与前几页同一套骨架（SettingIndex + Toast），加一件"只有 View 能做"的事：
// **选模型目录** —— 目录对话框要窗口句柄，按约定不能放进 ViewModel。
//
// 选完之后不做别的事：ViewModel 的 `ModelDirectory` 一改就会自己重扫
// （找入口 → 扫表情与动作组），界面不需要替它编排顺序。
// ============================================================
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OsuLive2dOverlay;

public partial class ModelView : UserControl
{
    private readonly ModelViewModel _viewModel;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public ModelView(ModelViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.Notify += ShowToast;
        _toastTimer.Tick += (_, _) => HideToast();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 每个带设置项的页面都要做：把控件登记成"设置坐标 → 控件"，
        // 体检报告点一条才能跳到这儿（决策 29）
        SettingIndex.Rebuild(this);
    }

    /// <summary>选模型目录。选完 ViewModel 自己会重扫，这里不替它编排顺序。</summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var current = _viewModel.ModelDirectory;
        var startFolder = Directory.Exists(current) ? current : AppContext.BaseDirectory;

        var dialog = new OpenFolderDialog
        {
            Title = "选择模型目录",
            InitialDirectory = startFolder,
        };

        if (dialog.ShowDialog() == true) _viewModel.ModelDirectory = dialog.FolderName;
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
