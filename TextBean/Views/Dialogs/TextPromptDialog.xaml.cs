using System.Windows;

namespace TextBean.Views.Dialogs;

public partial class TextPromptDialog : Window
{
    public TextPromptDialog(string title, string initial)
    {
        Platform.AppTheme.Use(this);         // InitializeComponent 앞에 (D-163)
        InitializeComponent();
        Title = title;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public string Value => Input.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
