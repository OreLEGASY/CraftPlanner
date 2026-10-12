using System.Windows;

namespace CraftPlanner.App.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    public static string? Show(Window owner, string title, string label, string initial = "")
    {
        var dlg = new InputDialog { Owner = owner, Title = title };
        dlg.LabelText.Text = label;
        dlg.InputBox.Text = initial;
        return dlg.ShowDialog() == true ? dlg.InputBox.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            MessageBox.Show(this, "値を入力してください。", Title);
            return;
        }
        DialogResult = true;
    }
}
