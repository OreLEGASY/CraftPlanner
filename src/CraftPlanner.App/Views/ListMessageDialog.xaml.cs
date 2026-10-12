using System.Windows;

namespace CraftPlanner.App.Views;

/// <summary>メッセージと項目一覧を表示する汎用ダイアログ（参照一覧・読み込みエラーなど）。</summary>
public partial class ListMessageDialog : Window
{
    private Action? _extra;

    public ListMessageDialog()
    {
        InitializeComponent();
    }

    public static void Show(Window owner, string title, string message, IEnumerable<string> lines, string? extraButton = null, Action? extraAction = null)
    {
        var dlg = new ListMessageDialog { Owner = owner, Title = title };
        dlg.MessageText.Text = message;
        dlg.Lines.ItemsSource = lines.ToList();
        if (extraButton != null)
        {
            dlg.ExtraButton.Content = extraButton;
            dlg.ExtraButton.Visibility = Visibility.Visible;
            dlg._extra = extraAction;
        }
        dlg.ShowDialog();
    }

    private void Extra_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _extra?.Invoke();
    }
}
