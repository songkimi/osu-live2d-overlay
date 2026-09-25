// ============================================================
// FileLocationView.xaml.cs —— 文件位置页
//
// 与前几页同一套骨架（SettingIndex + Toast），加一件"跟控件相关"的事：
// **「浏览」按钮** —— 文件用 OpenFileDialog、目录用 OpenFolderDialog，
// 都是对话框，按约定不能放进 ViewModel。
//
// 判断该用哪个：看这一行有没有"默认文件夹"——
// 有默认文件夹的（日志）是目录，没有的（osu / tosu 程序）是文件。
// 这个判断由 ViewModel 的 `IsFolder` 明确告知，不靠界面猜。
// ============================================================
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OsuLive2dOverlay;

public partial class FileLocationView : UserControl
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public FileLocationView(FileLocationViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel;
        viewModel.Notify += ShowToast;

        _toastTimer.Tick += (_, _) => HideToast();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SettingIndex.Rebuild(this);
    }

    /// <summary>
    /// 选文件或选目录。
    /// 判断依据是**这一行到底是文件还是目录** —— 由条目自己说了算（`IsFile`），
    /// 界面不猜。
    /// </summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileLocationEntryViewModel entry) return;

        var current = entry.EffectivePath;
        var startFolder = File.Exists(current)
            ? Path.GetDirectoryName(current) ?? AppContext.BaseDirectory
            : Directory.Exists(current) ? current : AppContext.BaseDirectory;

        if (entry.IsFile)
        {
            var dialog = new OpenFileDialog
            {
                Title = $"选择「{entry.Title}」",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                InitialDirectory = startFolder,
            };

            if (dialog.ShowDialog() == true)
                entry.Path = dialog.FileName;
        }
        else
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"选择「{entry.Title}」的位置",
                InitialDirectory = startFolder,
            };

            if (dialog.ShowDialog() == true)
                entry.Path = dialog.FolderName;
        }
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
