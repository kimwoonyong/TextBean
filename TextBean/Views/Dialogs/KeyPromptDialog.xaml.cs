using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TextBean.Models;

namespace TextBean.Views.Dialogs;

public partial class KeyPromptDialog : Window
{
    private readonly bool _confirm;

    public KeyPromptDialog(string title, string message, bool confirm)
    {
        Platform.AppTheme.Use(this);         // InitializeComponent 앞에 (D-163)
        InitializeComponent();
        Title = title;
        Message.Text = message;
        _confirm = confirm;
        ConfirmPanel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            First.Focus();
            UpdateCapsLockHint();
        };

        // 다른 창에서 켰다 끌 수도 있다 — 돌아올 때와 키를 뗄 때마다 다시 본다
        Activated += (_, _) => UpdateCapsLockHint();
        PreviewKeyUp += (_, _) => UpdateCapsLockHint();
    }

    public KeyEntry Entry => new(Value(First, FirstShown), _confirm ? Value(Second, SecondShown) : null);

    /// 창을 닫은 뒤 입력칸의 키를 비운다. 창 객체가 수거될 때까지 키가 컨트롤에 남지 않게 한다.
    public void ClearSecrets()
    {
        First.Clear();
        Second.Clear();
        FirstShown.Clear();
        SecondShown.Clear();
    }

    private static string Value(PasswordBox hidden, TextBox shown)
        => shown.Visibility == Visibility.Visible ? shown.Text : hidden.Password;

    private void OnEyeChanged(object sender, RoutedEventArgs e)
    {
        var show = ((ToggleButton)sender).IsChecked == true;
        if (ReferenceEquals(sender, FirstEye)) Reveal(First, FirstShown, show);
        else Reveal(Second, SecondShown, show);
    }

    /// <summary>
    /// 한 번에 한 칸만 키를 든다. 보이는 동안은 평문 칸이 원본이고, 가리면 PasswordBox 로 돌려놓은 뒤 평문 칸을 비운다 —
    /// 가린 뒤에도 평문 칸에 키가 남아 있지 않게 한다.
    /// </summary>
    private static void Reveal(PasswordBox hidden, TextBox shown, bool show)
    {
        if (show)
        {
            shown.Text = hidden.Password;
            hidden.Clear();
            hidden.Visibility = Visibility.Collapsed;
            shown.Visibility = Visibility.Visible;
            shown.Focus();
            shown.CaretIndex = shown.Text.Length;
        }
        else
        {
            hidden.Password = shown.Text;
            shown.Clear();
            shown.Visibility = Visibility.Collapsed;
            hidden.Visibility = Visibility.Visible;
            hidden.Focus();
        }
    }

    /// 보이는 칸에서 복사·잘라내기·끌어 놓기로 키가 밖으로 나가지 않게 한다. PasswordBox 는 원래 막는다 (PROHIBITED-CUSTOM-02).
    private void OnShownCopying(object sender, DataObjectCopyingEventArgs e) => e.CancelCommand();

    private void UpdateCapsLockHint()
        => CapsLockHint.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
