using System.ComponentModel;
using System.Windows;
using TextBean.ViewModels;

namespace TextBean.Views;

/// <summary>
/// 금고 검색 창. **모덜리스**여야 한다 — 결과를 클릭해 뒤 창에서 문서를 봐야 하고,
/// 창은 그대로 떠 있어 다음 결과로 이어갈 수 있어야 한다.
///
/// 닫아도 파괴하지 않는다. 다시 열 때 검색어를 잃으면 비밀값을 또 쳐야 하고,
/// 그건 이 앱에서 가장 피하고 싶은 일이다.
/// </summary>
public partial class SearchWindow : Window
{
    public SearchWindow()
    {
        Platform.AppTheme.Use(this);         // InitializeComponent 앞에 (D-163)
        InitializeComponent();

        // 창을 파괴하면 재사용할 수 없다. 숨기고, 검색어는 지운다 —
        // 검색어 자체가 비밀값이라 화면 밖이라도 컨트롤에 남겨 둘 이유가 없다.
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Conceal();
    }

    /// 숨기고 검색어를 지운다 — ✕ 와 같다. 주 창이 트레이로 숨을 때도 부른다(소유 창은 주인을 따라 숨지 않는다).
    public void Conceal()
    {
        Hide();

        if (DataContext is SearchViewModel vm) vm.Query = "";
    }

    /// 창이 살아 있는 채로 다시 띄운다. 검색어 상자에 바로 칠 수 있게 포커스를 준다.
    public void Present()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    /// 종료 경로에서만 부른다. Closing 가드를 넘어 실제로 닫는다.
    public void ForceClose()
    {
        Closing -= OnClosing;
        Close();
    }
}
