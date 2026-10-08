using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TextBean.Services;
using TextBean.ViewModels;
using TextBean.Views;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 테마 (D-161 ~ D-165). AppTheme 은 정적이라 바꾼 시험은 반드시 밝게로 되돌린다 — 화면 시험 묶음(병렬 금지) 안에서 돈다.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class ThemeTests
{
    private static string SettingsPath(TempVault vault) => Path.Combine(vault.Root, "settings.json");

    private static double Luminance(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
    }

    // ── 고르기 · 저장 ──────────────────────────────────────────────────────

    [Fact]
    public void 후보는_밝게_기본과_어둡게이고_모르는_id_는_밝게다()
    {
        Assert.Equal(["light", "dark"], ThemeChoices.All.Select(c => c.Id));
        Assert.Equal(["밝게", "어둡게"], ThemeChoices.All.Select(c => c.Label));
        Assert.Equal("light", ThemeChoices.Find("없는테마").Id);
        Assert.Equal("light", ThemeChoices.Find(null).Id);
    }

    [Fact]
    public async Task 고르면_저장되고_다시_켜면_그_테마다()
    {
        using var vault = new TempVault();
        var (shell, _, _) = await ShellFixture.BuildAsync(vault);
        Assert.Equal("light", shell.Theme.Id);
        Assert.Equal(["light"], shell.ThemeMenu.Where(m => m.IsCurrent).Select(m => m.Id));

        await shell.SetThemeAsync("dark");

        Assert.Equal(["dark"], shell.ThemeMenu.Where(m => m.IsCurrent).Select(m => m.Id));
        var settings = new AppSettingsService(SettingsPath(vault));
        await settings.LoadAsync();
        Assert.Equal("dark", settings.Current.Theme);
        shell.Dispose();

        var (again, _, _) = await ShellFixture.BuildAsync(vault);
        Assert.Equal("dark", again.Theme.Id);
        again.Dispose();
    }

    [Fact]
    public async Task 설정에_모르는_테마면_밝게이고_저장에_실패하면_알리고_지금은_쓴다()
    {
        using var vault = new TempVault();
        var settings = new AppSettingsService(SettingsPath(vault));
        await settings.LoadAsync();
        settings.Current.Theme = "지운테마";
        await settings.SaveAsync();

        var (shell, dlg, _) = await ShellFixture.BuildAsync(vault);
        Assert.Equal("light", shell.Theme.Id);

        File.Delete(SettingsPath(vault));
        Directory.CreateDirectory(SettingsPath(vault));                       // 같은 이름 폴더 — 파일을 못 만든다
        await shell.SetThemeAsync("dark");

        Assert.Equal("dark", shell.Theme.Id);
        Assert.Equal("테마 저장 실패", dlg.LastErrorTitle);
        shell.Dispose();
    }

    // ── 창 ─────────────────────────────────────────────────────────────────

    private static void Reset() => AppTheme.Select(ThemeChoices.DefaultId);

    private static IEnumerable<T> AllDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in AllDescendants<T>(child)) yield return deeper;
        }
    }

    /// Snapshot 은 창 바탕(Fluent 는 Mica — 그리지 않는다)을 빼고 찍는다. 사람이 볼 PNG 는 비슷한 바탕을 깔고 찍는다.
    private static void Shot(Window window, string name, bool dark)
    {
        var root = (FrameworkElement)window.Content;
        var backdrop = new Border
        {
            Width = root.ActualWidth, Height = root.ActualHeight,
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
            Child = new Border { Background = new VisualBrush(root) },
        };
        backdrop.Measure(new Size(root.ActualWidth, root.ActualHeight));
        backdrop.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        Snapshot(backdrop, name);
    }

    /// 탭 · 트리가 Fluent 모양을 이어받고(BasedOn), 본문은 두 테마 모두 본문 글꼴 13 (D-163)
    [Fact]
    public void 고르면_창이_바로_어둡게_바뀌고_탭_트리_본문까지_따라온다() => Run(() =>
    {
        try
        {
            using var scene = TabScene.Open(["문서", "둘째"]);                   // 둘째 = 선택 안 된 탭
            scene.Shell.ActiveTab = scene.Tab(0);
            scene.Window.UpdateLayout();
            Pump();                                                            // 가려져 있다 보인 본문의 Loaded(「13 ▾」 다시 읽기)는 디스패처 차례에 온다 [실측]

#pragma warning disable WPF0001
            Assert.Equal(ThemeMode.Light, scene.Window.ThemeMode);
            var rich = Assert.IsType<RichTextBox>(scene.Window.FindActiveBodyTextBox());
            Assert.Same(AppFonts.Body, rich.Document.FontFamily);              // Fluent 의 FlowDocument 스타일(Georgia 16)을 이겼다
            Assert.Equal(13, rich.Document.FontSize);
            Assert.Equal("13", RichFormat.GetCurrentSize(rich));                 // 「13 ▾」 — 붙는 순간의 Fluent 기본(16)이 남지 않는다 [실측 — 렌더]
            Assert.True(Luminance(FindDescendant<TreeViewItem>(scene.Window)!.Foreground) < 0.3);

            // 눈으로 볼 표본 — 저장 색으로 칠한 글자색 · 형광펜 · 표
            SolidColorBrush Of(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }
            var colors = new Paragraph();
            foreach (var pair in DocumentColors.Text) colors.Inlines.Add(new Run(pair.Name + " ") { Foreground = Of(pair.Stored) });
            var marks = new Paragraph(new Run("형광펜 "));
            foreach (var pair in DocumentColors.Highlight) { marks.Inlines.Add(new Run(pair.Name) { Background = Of(pair.Stored) }); marks.Inlines.Add(new Run(" ")); }
            var table = RichTable.Create(2, 2);
            ((Paragraph)table.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock).Inlines.Add(new Run("서버"));
            ((Paragraph)table.RowGroups[0].Rows[0].Cells[1].Blocks.FirstBlock).Inlines.Add(new Run("db.example.local"));
            rich.Document.Blocks.Clear();
            rich.Document.Blocks.AddRange(new Block[] { new Paragraph(new Run("운영 DB 접속 정보") { FontWeight = FontWeights.Bold }), colors, marks, table });
            Pump();
            Shot(scene.Window, "theme-light", dark: false);

            Wait(scene.Shell.SetThemeAsync("dark"));
            Pump();
            scene.Window.UpdateLayout();

            Assert.Equal(ThemeMode.Dark, scene.Window.ThemeMode);
#pragma warning restore WPF0001
            Assert.True(AppTheme.IsDark);
            rich = Assert.IsType<RichTextBox>(scene.Window.FindActiveBodyTextBox());
            Assert.Equal(DocumentColors.BodyDark, ((SolidColorBrush)rich.Foreground).Color);
            Assert.Same(AppFonts.Body, rich.Document.FontFamily);
            Assert.Equal(13, rich.Document.FontSize);
            Assert.Equal("13", RichFormat.GetCurrentSize(rich));
            Assert.True(Luminance(FindDescendant<TreeViewItem>(scene.Window)!.Foreground) > 0.7);   // 옛 모양(검은 글자)으로 남지 않았다
            Assert.True(Luminance(FindDescendant<TabItem>(scene.Window)!.Foreground) > 0.7);
            // D-168 (사용자 실기 지적) — 선택 안 된 탭 · 서식 단추 글자도 밝다. 직접 만든 탭 줄 · 단추는 Fluent 를 잇지 않아 검정이었다
            Assert.Equal(2, AllDescendants<TabItem>(scene.Window).Count());
            // D-169 (사용자 판정 A2) — 선택 탭만 위쪽 강조선 + 도구 모음 색 바탕, 나머지는 비친다. 머리글 제목이 그려진다(LL-014)
            foreach (var tabItem in AllDescendants<TabItem>(scene.Window))
            {
                var face = (Border)tabItem.Template.FindName("Face", tabItem);
                if (tabItem.IsSelected)
                {
                    Assert.Same(scene.Window.FindResource("FormatBarBrush"), face.Background);
                    Assert.Same(scene.Window.FindResource("AccentBrush"), face.BorderBrush);
                    Assert.Equal(new Thickness(0, 2, 0, 0), face.BorderThickness);
                }
                else Assert.Equal(Colors.Transparent, ((SolidColorBrush)face.Background).Color);
                Assert.Contains(AllDescendants<TextBlock>(tabItem), t => t.Text == ((EditorViewModel)tabItem.DataContext).TabTitle);
            }
            Assert.All(AllDescendants<TabItem>(scene.Window), t => Assert.True(Luminance(t.Foreground) > 0.7, $"탭 {t.IsSelected}"));
            // 선택 탭(D-169)도 같은 색이다 — 단추 줄(StackPanel)을 담은 테두리가 도구 모음
            var bar = FindDescendant<Border>(scene.Window, b => b.Background is SolidColorBrush s && s.Color == Color.FromRgb(0x27, 0x27, 0x27) && b.Child is StackPanel);
            Assert.NotNull(bar);                                               // 서식 도구 모음
            var labels = AllDescendants<TextBlock>(bar!).Where(t => t.Text is "그림" or "서식 지우기").ToList();
            Assert.Equal(2, labels.Count);
            Assert.All(labels, t => Assert.True(Luminance(t.Foreground) > 0.7, t.Text));
            var faint = AllDescendants<System.Windows.Controls.Primitives.ButtonBase>(bar!).Where(b => !b.IsEnabled).Select(b => b.ToolTip ?? b.Content).ToList();
            Assert.Empty(faint);                                               // 흐린 단추(꺼진 단추)가 없다 — 계획 미확정 ②
            Shot(scene.Window, "theme-dark", dark: true);
        }
        finally { Reset(); }
    });

    /// 계획 검토 R-2 — 바꾸면 본문이 새로 만들어진다. 바꾸기 전 편집이 살아 있고, 바꾼 뒤 쓴 글도 저장에 들어간다
    [Fact]
    public void 바꿔도_쓰던_글과_서식이_남고_바꾼_뒤_쓴_글도_저장된다() => Run(() =>
    {
        try
        {
            using var scene = TabScene.Open(["문서"]);
            var tab = scene.Tab(0);
            scene.Shell.ActiveTab = tab;
            scene.Window.UpdateLayout();
            var before = Assert.IsType<RichTextBox>(scene.Window.FindActiveBodyTextBox());
            var red = DocumentColors.FindText("빨강")!;
            var brush = new SolidColorBrush(red.Stored);
            brush.Freeze();
            before.Document.Blocks.Clear();
            before.Document.Blocks.Add(new Paragraph(new Run("바꾸기 전") { Foreground = brush }));
            Pump();
            Assert.True(tab.IsDirty);

            Wait(scene.Shell.SetThemeAsync("dark"));
            Pump();
            scene.Window.UpdateLayout();

            var after = Assert.IsType<RichTextBox>(scene.Window.FindActiveBodyTextBox());
            Assert.NotSame(before, after);                                     // 본문이 새로 만들어졌다 [실측]
            var run = after.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().Single(r => r.Text == "바꾸기 전");
            Assert.Equal(red.Dark, ((SolidColorBrush)run.Foreground).Color);
            Assert.True(tab.IsDirty);                                          // 아직 저장 안 한 편집 — 고쳐짐 그대로

            after.Document.Blocks.Add(new Paragraph(new Run("바꾼 뒤")));
            Pump();
            Assert.True(Wait(tab.TrySaveAsync()));

            var saved = Wait(scene.Store.LoadAsync(scene.Paths[0]));
            Assert.Contains("바꾸기 전", saved.Text);
            Assert.Contains("바꾼 뒤", saved.Text);
            Reset();
            var reopened = RichTextMap.Load(saved.Rich!);
            var savedRun = reopened.Blocks.OfType<Section>().SelectMany(s => s.Blocks).Concat(reopened.Blocks)
                                   .OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().First(r => r.Text == "바꾸기 전");
            Assert.Equal(red.Stored, ((SolidColorBrush)savedRun.Foreground).Color);   // 문서에는 저장 색
        }
        finally { Reset(); }
    });

    /// D-166 (사용자 실기 지적) — 문서가 없어도 본문 자리가 칠해져 트리와 갈리고, 메뉴 줄 아래 · 상태 줄 위에 선이 있다. 두 테마 모두
    [Fact]
    public void 문서가_없어도_본문_자리는_칠해지고_메뉴_상태_줄에_선이_있다() => Run(() =>
    {
        try
        {
            foreach (var dark in new[] { false, true })
            {
                using var scene = TabScene.Open(["문서"]);
                Wait(scene.Shell.SetThemeAsync(dark ? ThemeChoices.DarkId : ThemeChoices.DefaultId));
                Wait(scene.Shell.CloseAllTabsRequestedAsync());
                Pump();
                scene.Window.UpdateLayout();
                Assert.True(scene.Shell.HasNoTabs);

                var bodies = FindDescendant<ItemsControl>(scene.Window, i => i.Name == "TabBodies")!;
                var pane = (Grid)VisualTreeHelper.GetParent(bodies);               // 탭 줄 아래 본문 자리 (D-169 — 탭 줄 뒤는 칠하지 않는다)
                var fill = Assert.IsAssignableFrom<SolidColorBrush>(pane.Background);
                Assert.True(fill.Color.A > 0);                                     // 창 바탕(Mica)이 그대로 비치지 않는다
                Assert.Same(scene.Window.FindResource("TextControlBackground"), pane.Background);   // 문서를 열었을 때 본문과 같은 바탕

                var line = ((SolidColorBrush)scene.Window.FindResource("SeparatorBrush")).Color;
                var menu = FindDescendant<Menu>(scene.Window)!;
                Assert.Equal(new Thickness(0, 0, 0, 1), menu.BorderThickness);
                Assert.Equal(line, ((SolidColorBrush)menu.BorderBrush).Color);
                var status = FindDescendant<System.Windows.Controls.Primitives.StatusBar>(scene.Window)!;
                Assert.Equal(new Thickness(0, 1, 0, 0), status.BorderThickness);
                Assert.Equal(line, ((SolidColorBrush)status.BorderBrush).Color);
                Assert.InRange(status.ActualHeight, 18, 26);                       // D-167 — Fluent 기본은 49px 였다 [실측]
                Shot(scene.Window, $"theme-empty-{(dark ? "dark" : "light")}", dark);
            }
        }
        finally { Reset(); }
    });

    /// 계획 검토 R-2 — 순서를 직접 만든다: 새 본문이 먼저 묶이고 옛 본문이 나중에 풀린다. 옛 본문은 새 본문의 연결을 지우지 않는다
    [Fact]
    public void 옛_본문이_나중에_풀려도_새_본문의_저장_연결은_남는다() => Run(() =>
    {
        using var vault = new TempVault();
        var store = TestKeys.Store(vault.Root);
        var path = Path.Combine(vault.Root, "a.tbx");
        Wait(store.CreateAsync(path));
        var vm = new EditorViewModel(store, new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer());
        Wait(vm.LoadAsync(path));

        var old = new RichTextBox();
        RichBodyBehavior.SetAttach(old, true);
        old.DataContext = vm;
        var fresh = new RichTextBox();
        RichBodyBehavior.SetAttach(fresh, true);
        fresh.DataContext = vm;
        new TextRange(fresh.Document.ContentStart, fresh.Document.ContentEnd).Text = "새 본문";

        old.DataContext = null;                                                // 옛 본문이 나중에 풀린다

        Assert.NotNull(vm.CaptureBody);
        Assert.Contains("새 본문", vm.CaptureBody!().Text);
        Assert.NotNull(vm.CaptureAllForCopy);
    });

    /// 새로 만들어지지 않는 본문(창 밖)도 거둔 바이트로 다시 열려 새 테마 색이 된다 — 창이 본문을 새로 만들지 않아도 맞는 길 (D-165)
    [Fact]
    public void 거두고_바꾼_뒤_다시_열면_새로_만들지_않은_본문도_새_테마_색이다() => Run(() =>
    {
        try
        {
            using var vault = new TempVault();
            var store = TestKeys.Store(vault.Root);
            var path = Path.Combine(vault.Root, "a.tbx");
            Wait(store.CreateAsync(path));
            var vm = new EditorViewModel(store, new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer());
            Wait(vm.LoadAsync(path));
            var box = new RichTextBox();
            RichBodyBehavior.SetAttach(box, true);
            box.DataContext = vm;
            var blue = DocumentColors.FindText("파랑")!;
            var brush = new SolidColorBrush(blue.Stored);
            brush.Freeze();
            box.Document.Blocks.Clear();
            box.Document.Blocks.Add(new Paragraph(new Run("파랑") { Foreground = brush }));

            vm.StashBody();
            AppTheme.Select(ThemeChoices.DarkId);
            vm.ReloadBody();

            var run = box.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>()
                         .Concat(box.Document.Blocks.OfType<Section>().SelectMany(s => s.Blocks).OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>())
                         .Single(r => r.Text == "파랑");
            Assert.Equal(blue.Dark, ((SolidColorBrush)run.Foreground).Color);
        }
        finally { Reset(); }
    });

    /// 눈으로 보는 렌더 — 다른 창 · 대화상자 · 서식 목록(팝업은 열지 않고 내용만 따로 그린다, D-082). TEXTBEAN_TEST_PNG 가 있을 때만 PNG.
    /// 단언은 「만들어지고 테마가 걸린다」까지 — 색이 맞는지는 PNG 로 본다.
    [Fact]
    public void 렌더_다른_창과_서식_목록() => Run(() =>
    {
        try
        {
            foreach (var dark in new[] { false, true })
            {
                AppTheme.Select(dark ? ThemeChoices.DarkId : ThemeChoices.DefaultId);
                var suffix = dark ? "dark" : "light";

                using (var scene = TabScene.Open(["문서"]))
                {
                    // 창은 셸의 테마를 따른다(설정 = 밝게) — 셸로 바꾼다. AppTheme 만 바꾸면 창이 셸을 따라 밝게로 되돌린다
                    Wait(scene.Shell.SetThemeAsync(dark ? ThemeChoices.DarkId : ThemeChoices.DefaultId));
                    Pump();
                    Assert.Equal(dark, AppTheme.IsDark);
                    scene.Shell.ActiveTab = scene.Tab(0);
                    scene.Window.UpdateLayout();
                    var shortcuts = new TextBean.Views.Dialogs.ShortcutsDialog { DataContext = new ShortcutsViewModel(scene.Shell) };
                    ShowOffscreen(shortcuts, 560, 420);
                    Assert.NotNull(FindDescendant<GridViewRowPresenter>(shortcuts));  // 줄이 칸(동작 · 키)으로 그려진다 — 줄 객체 이름이 아니라
                    Shot(shortcuts, $"theme-shortcuts-{suffix}", dark);
                    shortcuts.Close();

                    foreach (var name in new[] { "TextColorPopup", "HighlightPopup", "SizePopup", "TablePopup" })
                    {
                        var popup = (System.Windows.Controls.Primitives.Popup)FindDescendant<FrameworkElement>(scene.Window, e => e.FindName(name) is not null)!.FindName(name);
                        var content = (FrameworkElement)popup.Child;
                        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        content.Arrange(new Rect(content.DesiredSize));
                        Assert.True(content.DesiredSize.Width > 0);
                        Snapshot(content, $"theme-{name}-{suffix}");
                    }
                }

                var search = new SearchWindow();
                ShowOffscreen(search, 600, 400);
                Shot(search, $"theme-search-{suffix}", dark);
                search.ForceClose();

                var key = new TextBean.Views.Dialogs.KeyPromptDialog("키 입력", "문서를 열 키를 입력하세요.", confirm: false);
                ShowOffscreen(key, 420, 260);
                Shot(key, $"theme-key-{suffix}", dark);
                key.Close();

                var notice = new TextBean.Views.Dialogs.TrayNoticeDialog();
                ShowOffscreen(notice, 420, 260);
                Shot(notice, $"theme-notice-{suffix}", dark);
                notice.Close();
            }
        }
        finally { Reset(); }
    }, 60);

    /// 다른 창(상세 검색)도 같은 테마로 만들어지고, 바뀌면 따라간다
    [Fact]
    public void 검색_창도_테마를_따른다() => Run(() =>
    {
        try
        {
            var window = new SearchWindow();
            ShowOffscreen(window, 600, 400);
#pragma warning disable WPF0001
            Assert.Equal(ThemeMode.Light, window.ThemeMode);
            AppTheme.Select(ThemeChoices.DarkId);
            Assert.Equal(ThemeMode.Dark, window.ThemeMode);
#pragma warning restore WPF0001
            Assert.Equal(Color.FromRgb(0x3A, 0x2A, 0x10), ((SolidColorBrush)window.FindResource("WarnPanelBrush")).Color);
            window.ForceClose();                                               // 보통 닫기는 숨기기다
        }
        finally { Reset(); }
    });
}
