// ============================================================
// AboutView.xaml.cs —— 「应用 → 关于」页
//
// 与别页同一套骨架（SettingIndex），但它**没有 Toast**：
// 这一页全只读，没有会产生回执的操作。
// ============================================================
using System.Windows;
using System.Windows.Controls;

namespace OsuLive2dOverlay;

public partial class AboutView : UserControl
{
    public AboutView(AboutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SettingIndex.Rebuild(this);
}
