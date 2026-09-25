// ============================================================
// UpdateView.xaml.cs —— 「应用 → 更新」页（占位版）
//
// 没有 ViewModel：这一页现在只有一个置灰按钮，没有任何状态。
// 等真的做联网检查更新时，再给它配 VM（那时才有"检查中/有新版本/失败"这些状态）。
// ============================================================
using System.Windows;
using System.Windows.Controls;

namespace OsuLive2dOverlay;

public partial class UpdateView : UserControl
{
    public UpdateView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SettingIndex.Rebuild(this);
}