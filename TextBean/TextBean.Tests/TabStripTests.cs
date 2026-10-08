using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TextBean.Models;
using TextBean.ViewModels;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 실제 MainWindow 에 탭을 열어 화면 밖에서 배치해 본다. 시험 호스트 규칙(D-082)을 따른다 —
/// 선택은 셸(ActiveTab)로만 바꾸고, 포커스·팝업 경로를 타지 않는다.
/// </summary>
internal sealed class TabScene : IDisposable
{
    /// 사용자 화면의 제목과 비슷하게. 같은 제목("문서")은 다른 폴더에 둔다.
    public static readonly string[] DefaultTitles =
    [
        "문서", "테스트11", "테스트", "12341234a", "테스트 분서", "문서123", @"폴더1\문서", "뭐지", "새버전테스트",
        "일반텍스트파일", "계정 정보", "서버목록", "메모2", "새 문서", "와이파이 비밀번호", "인증서 보관 위치",
        "구독 서비스 정리", "여행 준비물", "자동차 보험", "의료 기록 요약", "업무 계정"
    ];

    public TempVault Vault { get; }
    public ShellViewModel Shell { get; }
    public FakeDialogs Dialogs { get; }
    public TextBean.Services.DocumentStore Store { get; }
    public MainWindow Window { get; }
    public TabStrip Strip { get; }
    public TabPanel Panel { get; }
    public IReadOnlyList<string> Paths { get; }

    private TabScene(IEnumerable<string> titles, double width, double height, TempVault? vault = null)
    {
        Vault = vault ?? new TempVault();
        try
        {
            var (shell, dialogs, store, _, _) = Wait(ShellFixture.BuildWithKeysAsync(Vault, TestKeys.Service()));
            Shell = shell;
            Dialogs = dialogs;
            Store = store;
            AssertFakes(shell);

            var paths = titles.Select(t => t.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                                               ? Path.Combine(Vault.Root, t)
                                               : Path.Combine(Vault.Root, t + ".tbx")).ToList();
            foreach (var path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(path, "평문");
                else Wait(store.CreateAsync(path));
            }
            Paths = paths;

            Wait(shell.RefreshAsync());
            foreach (var path in paths) Wait(shell.OpenAsync(path));

            Window = new MainWindow { DataContext = shell };
            ShowOffscreen(Window, width, height);

            Strip = FindDescendant<TabStrip>(Window) ?? throw new InvalidOperationException("탭 줄을 찾지 못했다");
            Panel = Strip.HeaderPanel;
        }
        catch
        {
            // 만드는 중 던지면 using 이 성립하지 않아 Dispose 가 돌지 않는다 — 금고가 %TEMP%\TextBeanTests 에 남았다 [실측 — 적대 검토]
            CleanUp();
            throw;
        }
    }

    public ScrollViewer Scroll => Strip.HeaderScroll;

    /// <summary>
    /// 좌표 비교 허용 오차(px). <c>Assert.Equal(a, b, 1)</c> 은 차이가 아니라 소수 첫째 자리 <b>반올림</b>을 비교해,
    /// 831.75 와 831.7499999… 를 831.8 · 831.7 로 갈라 실패했다 [실측 — 아이콘 칸이 16px 이 되며 경계가 .75 에 떨어짐].
    /// </summary>
    public const double Tolerance = 0.05;

    public T Part<T>(string name) where T : class
        => Strip.Template.FindName(name, Strip) as T ?? throw new InvalidOperationException($"{name} 없음");

    /// 탭의 보이는 범위(줄 기준 — 0 = 줄 왼쪽 끝).
    public Rect InViewport(int index)
        => Items[index].TransformToVisual(Scroll).TransformBounds(new Rect(Items[index].RenderSize));

    /// <summary>
    /// 시험 단언: 왼쪽 끝 = 맨 왼쪽 탭의 경계. 경계 함수만 보면 그 함수가 틀려도 통과한다 — ScrollTo 가 그 값을 그대로 넣는다 [실측 — 적대 검토].
    /// 그래서 화면 좌표로도 본다: 맨 왼쪽 탭이 줄 왼쪽 끝에서 시작하고 앞 탭은 보이지 않는다.
    /// 허용치 2.5 는 선택 탭의 마진 −2 다(선택 탭은 2px 앞 · 4px 넓게 그려진다).
    /// </summary>
    public void AssertLeftEdgeOnBoundary()
    {
        var bounds = Strip.TabBoundaries();
        Assert.Equal(bounds[Strip.StartIndex], Scroll.HorizontalOffset, Tolerance);

        if (Strip.StartIndex == 0)
        {
            Assert.Equal(0, Scroll.HorizontalOffset, Tolerance);
            return;
        }

        Assert.InRange(InViewport(Strip.StartIndex).Left, -2.5, 0.5);
        var previous = InViewport(Strip.StartIndex - 1).Right;
        Assert.True(previous <= 2.5, $"앞 탭이 {previous:F1}px 보인다");
    }

    public void SetWidth(double width)
    {
        Window.Width = width;
        Pump();
    }

    public static TabScene Open(int tabs, double width = 1120, double height = 600)
        => new(DefaultTitles.Take(tabs), width, height);

    public static TabScene Open(IEnumerable<string> titles, double width = 1120, double height = 600)
        => new(titles, width, height);

    /// 금고를 시험이 만들어 넘긴다 — 장면이 실패할 때 그 금고가 지워지는지 그것 하나만 보려고.
    public static TabScene Open(IEnumerable<string> titles, TempVault vault) => new(titles, 1120, 600, vault);

    public IReadOnlyList<TabItem> Items
        => [.. Strip.Items.Cast<object>().Select(o => (TabItem)Strip.ItemContainerGenerator.ContainerFromItem(o))];

    public EditorViewModel Tab(int index) => Shell.Tabs[index];

    /// <summary>
    /// 그 탭의 서식 본문(RichTextBox)에 사용자가 친 것처럼 본문을 바꾼다 (D-125).
    /// 화면이 붙은 .tbx 탭의 저장은 본문에서 뽑으므로, 편집기 Text 에 직접 넣으면 저장되지 않는다.
    /// 본문은 한 번 그려진 탭에만 있다 — 그 탭을 잠깐 활성으로 두고 배치한다(숨긴 창에서도 배치된다).
    /// </summary>
    public void TypeInto(int index, string text)
    {
        var previous = Shell.ActiveTab;
        Shell.ActiveTab = Tab(index);
        Window.UpdateLayout();
        var body = Assert.IsType<RichTextBox>(Window.FindActiveBodyTextBox());
        new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text = text;
        Shell.ActiveTab = previous;
        Window.UpdateLayout();
    }

    /// 실제 화면에서는 시험 호스트가 가짜 대화상자·클립보드를 쓰는지 확인할 길이 셸뿐이다.
    /// MessageBox · 폴더 창은 어떤 걸쇠에도 걸리지 않는다 [실측 — 계획 검토].
    private static void AssertFakes(ShellViewModel shell)
    {
        object? Field(string name) => typeof(ShellViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(shell);

        if (Field("_dialogs") is not FakeDialogs) throw new InvalidOperationException("셸이 진짜 대화상자를 쓴다");
        if (Field("_clipboard") is not FakeClipboard) throw new InvalidOperationException("셸이 진짜 클립보드를 쓴다");
    }

    public void Dispose() => CleanUp();

    /// 앞 단계가 던져도 금고는 지운다.
    private void CleanUp()
    {
        try
        {
            // 트레이로 숨긴 창도 닫는다 — 숨은 창은 IsVisible 이 false 지만 아직 열려 있다(창 수에 든다)
            if (Window is not null && PresentationSource.FromVisual(Window) is not null)
            {
                // 시험이 중간에 실패해 "대기 중" · "대화상자 중" 이 남으면 닫기가 취소되고 10초 뒤 엉뚱한 실패로 원래 실패를 덮는다
                Window.AcceptsRequests = true;
                Dialogs.IsShowing = false;
                Close(Window);
            }
        }
        finally
        {
            try { Shell?.Dispose(); }
            finally { Vault.Dispose(); }
        }
    }
}

[Collection(WpfScreenCollection.Name)]
public class TabStripTests
{
    /// 행은 배치 칸(LayoutInformation)으로 가른다 — 선택 탭은 마진 −2 로 2px 위·4px 넓게 그려진다 [실측].
    private static double SlotTop(TabItem item) => LayoutInformation.GetLayoutSlot(item).Top;

    private static double SlotBottom(TabItem item) => LayoutInformation.GetLayoutSlot(item).Bottom;

    // ── T-2 · T-3 한 줄 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(15)]
    [InlineData(21)]
    public void 탭이_넘쳐도_머리글은_한_줄이다(int tabs) => Run(() =>
    {
        using var scene = TabScene.Open(tabs);
        Snapshot(scene.Window, $"t2-{tabs}-tabs");

        var rows = scene.Items.Select(i => Math.Round(SlotBottom(i))).Distinct().Count();
        var tallest = scene.Items.Max(i => i.ActualHeight);

        Assert.Equal(1, rows);
        Assert.True(scene.Panel.ActualHeight <= tallest + 0.5, $"머리글 높이 {scene.Panel.ActualHeight} > 탭 높이 {tallest}");
    });

    /// 지금은 다른 줄의 탭을 고르면 그 줄이 맨 아래로 내려와 모든 탭이 자리를 바꾼다 [실측 — research].
    [Fact]
    public void 탭을_골라도_다른_탭들이_줄을_바꾸지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        var before = scene.Items.Select(SlotTop).ToList();

        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        Snapshot(scene.Window, "t3-after-select");

        var after = scene.Items.Select(SlotTop).ToList();
        Assert.Equal(before, after);
    });

    // ── T-1 템플릿 (LL-014) ───────────────────────────────────────────────────

    /// 템플릿 안의 실수는 창을 만들기만 해서는 안 드러난다 — 배치까지 해야 깨진다 [실측 — research].
    [Fact]
    public void 실제_템플릿에_부품과_내용_호스트가_있고_탭마다_제목과_닫기가_그려진다() => Run(() =>
    {
        using var scene = TabScene.Open(5);

        foreach (var part in new[] { "PART_HeaderArea", "PART_HeaderScroll", "PART_HeaderPanel", "PART_EndSpacer",
                                     "PART_ScrollLeft", "PART_ScrollRight", "PART_TabList", "PART_SelectedContentHost" })
            Assert.NotNull(scene.Strip.Template.FindName(part, scene.Strip));

        for (var i = 0; i < scene.Items.Count; i++)
        {
            var texts = FindDescendants<TextBlock>(scene.Items[i]).Select(t => t.Text).ToList();
            Assert.Contains(scene.Tab(i).TabTitle, texts);
            Assert.Contains(FindDescendants<Button>(scene.Items[i]), b => (b.Content as string) == "✕");
        }
    });

    [Theory]
    [InlineData("PART_HeaderArea")]
    [InlineData("PART_HeaderScroll")]
    [InlineData("PART_HeaderPanel")]
    [InlineData("PART_EndSpacer")]
    [InlineData("PART_ScrollLeft")]
    [InlineData("PART_ScrollRight")]
    [InlineData("PART_TabList")]
    [InlineData("PART_SelectedContentHost")]
    public void 부품이_하나라도_빠진_템플릿은_조용히_비지_않고_던진다(string missing) => Run(() =>
    {
        string Named(string name) => name == missing ? "" : $"x:Name=\"{name}\"";
        var template = $"""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TabControl">
              <Grid>
                <DockPanel {Named("PART_HeaderArea")}>
                  <Button {Named("PART_TabList")} DockPanel.Dock="Right" />
                  <Button {Named("PART_ScrollRight")} DockPanel.Dock="Right" />
                  <Button {Named("PART_ScrollLeft")} DockPanel.Dock="Right" />
                  <ScrollViewer {Named("PART_HeaderScroll")}>
                    <StackPanel Orientation="Horizontal">
                      <TabPanel {Named("PART_HeaderPanel")} IsItemsHost="True" />
                      <Border {Named("PART_EndSpacer")} />
                    </StackPanel>
                  </ScrollViewer>
                </DockPanel>
                <ContentPresenter {Named("PART_SelectedContentHost")} ContentSource="SelectedContent" />
              </Grid>
            </ControlTemplate>
            """;
        var strip = new TabStrip { Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(template) };

        var error = Assert.Throws<InvalidOperationException>(() => strip.ApplyTemplate());
        Assert.Contains(missing, error.Message);
    });

    /// 넘기는 동안 본문 입력이 끊기지 않게 ◀ ▶ ▾ 는 포커스를 가져가지 않는다. 탭의 ✕ 도 — 포커스를 받으면 WPF 가 BringIntoView 를 불러
    /// 오른쪽에 잘린 탭의 ✕ 를 누르는 순간 줄이 탭 하나만큼 넘어가 ✕ 가 커서 밑에서 빠진다 [실측 — 적대 검토]. 클릭이 먹히는지는 실기 확인.
    [Fact]
    public void 넘김_버튼과_탭의_닫기_버튼은_포커스를_가져가지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(15);

        foreach (var name in new[] { "PART_ScrollLeft", "PART_ScrollRight", "PART_TabList" })
            Assert.False(scene.Part<Button>(name).Focusable, name);
        foreach (var item in scene.Items)
            Assert.False(FindDescendants<Button>(item).Single(b => (b.Content as string) == "✕").Focusable);
    });

    // ── T-4 버튼 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 넘치지_않으면_버튼을_숨기고_넘치면_보이며_끝에_닿은_쪽은_비활성이다() => Run(() =>
    {
        using (var few = TabScene.Open(3))
        {
            Assert.False(few.Strip.IsOverflowing);
            Assert.Equal(Visibility.Collapsed, few.Part<Button>("PART_ScrollLeft").Visibility);
            Assert.Equal(Visibility.Collapsed, few.Part<Button>("PART_TabList").Visibility);
        }

        using var many = TabScene.Open(15);
        many.Shell.ActiveTab = many.Tab(0);
        Pump();

        Assert.True(many.Strip.IsOverflowing);
        Assert.Equal(Visibility.Visible, many.Part<Button>("PART_TabList").Visibility);
        Assert.False(many.Part<Button>("PART_ScrollLeft").IsEnabled);
        Assert.True(many.Part<Button>("PART_ScrollRight").IsEnabled);

        many.Strip.Step(100);
        Pump();
        Assert.True(many.Part<Button>("PART_ScrollLeft").IsEnabled);
        Assert.False(many.Part<Button>("PART_ScrollRight").IsEnabled);
    });

    /// 뷰포트 폭으로 판정하면 같은 폭에서 버튼이 있을 때도 없을 때도 스스로 맞는다 [실측 — 계획 검토, D-083].
    [Fact]
    public void 버튼_표시는_같은_폭이면_넓은_쪽에서_오든_좁은_쪽에서_오든_같다() => Run(() =>
    {
        using var scene = TabScene.Open(8);
        var area = scene.Part<FrameworkElement>("PART_HeaderArea");
        var chrome = scene.Window.ActualWidth - area.ActualWidth;
        var tabs = scene.Panel.ActualWidth + scene.Panel.Margin.Left + scene.Panel.Margin.Right;
        var fitsWithoutButtons = tabs + 10 + chrome;     // 버튼 없이는 들어가지만, 버튼이 있으면 넘칠 폭

        scene.SetWidth(fitsWithoutButtons + 300);
        scene.SetWidth(fitsWithoutButtons);
        var fromWide = scene.Strip.IsOverflowing;

        scene.SetWidth(fitsWithoutButtons - 300);
        Assert.True(scene.Strip.IsOverflowing);
        var toggles = 0;
        scene.Part<Button>("PART_TabList").IsVisibleChanged += (_, _) => toggles++;
        scene.SetWidth(fitsWithoutButtons);
        var fromNarrow = scene.Strip.IsOverflowing;

        Assert.False(fromWide);
        Assert.False(fromNarrow);
        Assert.True(toggles <= 1, $"버튼이 {toggles}번 깜빡였다");
    });

    // ── T-5 경계 맞춤 (D-076) ───────────────────────────────────────────────────

    [Fact]
    public void 넘김은_늘_왼쪽_끝을_탭_경계에_맞추고_끝에서도_온전한_탭이다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();

        scene.Strip.Step(1);
        Pump();
        Assert.Equal(1, scene.Strip.StartIndex);
        scene.AssertLeftEdgeOnBoundary();

        // 한 처리기에서 두 번 — 오프셋은 배치까지 미뤄진다. 목표에서 세지 않으면 한 칸만 간다 [실측 — 계획 검토]
        scene.Strip.Step(1);
        scene.Strip.Step(1);
        Pump();
        Assert.Equal(3, scene.Strip.StartIndex);
        scene.AssertLeftEdgeOnBoundary();

        scene.Strip.Step(100);
        Pump();
        Assert.Equal(scene.Strip.LastStartIndex, scene.Strip.StartIndex);
        scene.AssertLeftEdgeOnBoundary();
        Assert.True(scene.InViewport(scene.Items.Count - 1).Right <= scene.Scroll.ViewportWidth + 2.5);  // 마지막 탭까지 보인다
        Snapshot(scene.Window, "t5-end");

        // 끝에서 탭을 가능한 많이 보인다 — 한 칸 앞에서 시작하면 마지막 탭이 줄에 안 들어간다
        var bounds = scene.Strip.TabBoundaries();
        var lastRight = scene.Scroll.HorizontalOffset + scene.InViewport(scene.Items.Count - 1).Right;
        Assert.True(lastRight - bounds[scene.Strip.StartIndex - 1] > scene.Scroll.ViewportWidth);
    });

    /// 정밀 휠로 조금 굴리다 방향을 바꾸면 모은 값을 버린다 — 남겨 두면 반대로 한 칸 덜 간다.
    [Fact]
    public void 휠_방향이_바뀌면_모은_값을_버린다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        scene.Strip.Step(3);
        Pump();

        foreach (var delta in new[] { -80, 120 })
        {
            scene.Scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        }
        Pump();

        Assert.Equal(2, scene.Strip.StartIndex);
    });

    /// 경계가 아닌 자리로 밀린 뒤(ScrollViewer 가 범위를 줄이며 끝을 당기는 등) 다시 보이게 하면 경계로 돌아온다.
    [Fact]
    public void 경계가_아닌_자리에_있어도_보이게_넘기면_경계로_돌아온다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();

        scene.Scroll.ScrollToHorizontalOffset(13.7);       // 탭 0 이 보이는 채로 경계가 아닌 자리
        Pump();
        scene.Strip.RevealSelected();
        Pump();

        scene.AssertLeftEdgeOnBoundary();
    });

    /// 창을 줄이면 활성 탭이 줄 밖으로 밀린다 — 머리글 폭이 바뀔 때도 보이게 넘긴다.
    [Fact]
    public void 창이_좁아져도_활성_탭은_보인다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(9);
        Pump();

        scene.SetWidth(scene.Window.ActualWidth - 450);

        Assert.True(scene.InViewport(9).Right <= scene.Scroll.ViewportWidth + 2.5);
        scene.AssertLeftEdgeOnBoundary();
    });

    [Theory]
    [InlineData(new[] { -120 }, 1)]
    [InlineData(new[] { -40, -40, -40 }, 1)]      // 정밀 휠 — 모아서 한 칸
    [InlineData(new[] { -240 }, 2)]               // 빠른 휠 — 한 번에 두 칸
    [InlineData(new[] { -120, 120 }, 0)]          // 아래 · 위
    public void 휠_한_칸은_탭_하나다(int[] deltas, int expected) => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();

        foreach (var delta in deltas)
        {
            scene.Scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        }
        Pump();

        Assert.Equal(expected, scene.Strip.StartIndex);
        scene.AssertLeftEdgeOnBoundary();
    });

    /// WPF 는 탭이 포커스를 받으면 스스로 BringIntoView 를 불러 픽셀 단위로 넘긴다 — 탭 클릭 · Home/End [실측 — 계획 검토].
    /// 포커스 없이 같은 경로(TabItem.BringIntoView)를 부른다.
    [Fact]
    public void 오른쪽에_잘린_탭을_보이게_해도_왼쪽_끝은_탭_경계다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();

        var cut = Enumerable.Range(0, scene.Items.Count)
                            .First(i => scene.InViewport(i).Right > scene.Scroll.ViewportWidth + 2.5);
        var offsets = new List<double>();
        scene.Scroll.ScrollChanged += (_, e) => { if (e.HorizontalChange != 0) offsets.Add(e.HorizontalOffset); };
        scene.Items[cut].BringIntoView();
        Pump();

        scene.AssertLeftEdgeOnBoundary();
        var bounds = scene.Strip.TabBoundaries();
        Assert.All(offsets, o => Assert.Contains(bounds, b => Math.Abs(b - o) < 0.5));   // 픽셀 넘김을 한 번도 거치지 않는다
        Assert.True(scene.InViewport(cut).Right <= scene.Scroll.ViewportWidth + 2.5);

        // 이미 활성인 탭이 잘린 뒤 다시 보이게 — 선택 변화가 없다
        scene.Shell.ActiveTab = scene.Tab(cut);
        Pump();
        scene.Strip.Step(-100);
        Pump();
        scene.Items[cut].BringIntoView();
        Pump();
        scene.AssertLeftEdgeOnBoundary();
        Assert.True(scene.InViewport(cut).Right <= scene.Scroll.ViewportWidth + 2.5);
    });

    /// 화면 밖 왼쪽 탭이 넓어지면(이름 바꾸기 — 그때는 제목 뒤 ● 도) 보이는 탭이 모두 밀리고 앞 탭 ✕ 조각이 드러났다 [실측 — 계획 검토].
    [Fact]
    public void 왼쪽_밖_탭의_폭이_바뀌거나_닫혀도_맨_왼쪽_탭은_그대로_왼쪽_끝이다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(14);
        Pump();
        scene.Strip.Step(-100);
        scene.Strip.Step(4);
        Pump();
        var anchor = scene.Tab(scene.Strip.StartIndex);

        scene.Tab(1).UpdatePath(Path.Combine(scene.Vault.Root, "아주 길게 늘어난 제목이 된 문서.tbx"));   // 폭이 바뀐다
        Pump();
        Assert.Same(anchor, scene.Tab(scene.Strip.StartIndex));
        scene.AssertLeftEdgeOnBoundary();

        Wait(scene.Shell.CloseTabAsync(scene.Tab(2)));     // 앞 탭이 닫힌다
        Pump();
        Assert.Same(anchor, scene.Tab(scene.Strip.StartIndex));
        scene.AssertLeftEdgeOnBoundary();
    });

    // ── T-5d 끌어 옮긴 뒤 (적대 검토) ──────────────────────────────────────────
    // 끌어 놓기(Tabs.Move)는 폭의 합도 선택도 바꾸지 않아, 맨 왼쪽 자리를 다시 맞추는 훅이 하나도 돌지 않았다.
    // 시험은 놓기 처리기가 부르는 셸 MoveTabTo 를 그대로 부른다.

    /// 맨 왼쪽 탭보다 앞의 탭을 뒤로 옮기면 경계가 그 탭 폭만큼 바뀌는데 오프셋이 그대로라, 앞 탭 ✕ 조각이 드러났다.
    [Fact]
    public void 맨_왼쪽보다_앞의_탭을_뒤로_옮겨도_왼쪽_끝은_탭_경계다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        scene.Strip.Step(4);
        Pump();

        scene.Shell.MoveTabTo(scene.Tab(0), scene.Tab(8), after: true);
        Pump();

        Assert.Equal(4, scene.Strip.StartIndex);
        scene.AssertLeftEdgeOnBoundary();
    });

    /// 맨 왼쪽 탭을 옮긴 뒤 기준 탭이 옮긴 탭에 남아, 다음 폭 변화 때 줄이 그 탭의 새 자리로 몇 칸 뛰었다.
    [Fact]
    public void 맨_왼쪽_탭을_옮긴_뒤_폭이_바뀌어도_그_자리의_탭이_왼쪽_끝에_남는다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(14);
        Pump();
        scene.Strip.Step(-100);
        scene.Strip.Step(4);
        Pump();
        var moved = scene.Tab(4);

        scene.Shell.MoveTabTo(moved, scene.Tab(10), after: true);
        Pump();
        var leftmost = scene.Tab(4);
        Assert.NotSame(moved, leftmost);

        scene.Tab(13).UpdatePath(Path.Combine(scene.Vault.Root, "아주 길게 늘어난 제목이 된 문서.tbx"));   // 폭이 바뀐다
        Pump();

        Assert.Same(leftmost, scene.Tab(scene.Strip.StartIndex));
        scene.AssertLeftEdgeOnBoundary();
    });

    /// 보이는 활성 탭을 맨 왼쪽 탭 앞에 놓은 뒤 어느 탭 폭이 바뀌면(그때는 입력하자마자 붙던 ● 도) 편집 중인 그 탭이 왼쪽 밖으로 밀렸다.
    [Fact]
    public void 활성_탭을_맨_왼쪽_앞에_놓은_뒤_폭이_바뀌어도_그_탭은_보인다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        scene.Strip.Step(3);
        Pump();
        var moved = scene.Tab(6);
        scene.Shell.ActiveTab = moved;
        Pump();

        scene.Shell.MoveTabTo(moved, scene.Tab(3), after: false);
        Pump();
        scene.Tab(13).UpdatePath(Path.Combine(scene.Vault.Root, "아주 길게 늘어난 제목이 된 문서.tbx"));   // 폭이 바뀐다
        Pump();

        var shown = scene.InViewport(scene.Shell.Tabs.IndexOf(moved));
        Assert.True(shown.Left >= -2.5 && shown.Right <= scene.Scroll.ViewportWidth + 2.5, $"활성 탭이 {shown.Left:F1}..{shown.Right:F1}");
        scene.AssertLeftEdgeOnBoundary();
    });

    /// 끝 근처에서 옮긴 뒤 끝까지 넘기면 — 끝 여백과 끝 자리가 옮기기 전 순서로 남아 왼쪽 끝이 경계를 벗어났다(앞 탭 ✕ 조각) [실측 — 2차 검토].
    /// 어긋남은 탭 폭 조합에 달려 창 폭 몇 가지로 본다.
    [Theory]
    [InlineData(1040.0)]
    [InlineData(1120.0)]
    [InlineData(1150.0)]
    [InlineData(1200.0)]
    public void 끝_근처에서_탭을_옮긴_뒤_끝까지_넘겨도_왼쪽_끝은_탭_경계이고_마지막_탭이_보인다(double width) => Run(() =>
    {
        using var scene = TabScene.Open(15, width);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        var last = scene.Strip.LastStartIndex;
        scene.Strip.Step(last - 1);
        Pump();

        scene.Shell.MoveTabTo(scene.Tab(last), scene.Tab(last - 1), after: false);
        Pump();
        scene.Strip.Step(100);
        Pump();

        scene.AssertLeftEdgeOnBoundary();
        Assert.Equal(scene.Strip.LastStartIndex, scene.Strip.StartIndex);
        Assert.True(scene.InViewport(scene.Items.Count - 1).Right <= scene.Scroll.ViewportWidth + 2.5, "마지막 탭이 잘렸다");
    });

    // ── T-3 · T-6 보이게 넘김 ────────────────────────────────────────────────────

    [Fact]
    public void 고른_탭은_다_보이게_넘긴다() => Run(() =>
    {
        using var scene = TabScene.Open(21);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();

        scene.Shell.ActiveTab = scene.Tab(17);
        Pump();

        var bounds = scene.InViewport(17);
        Assert.True(bounds.Left >= -2.5 && bounds.Right <= scene.Scroll.ViewportWidth + 2.5);
        Snapshot(scene.Window, "t6-reveal-17");
        scene.AssertLeftEdgeOnBoundary();
    });

    /// 트리·검색에서 이미 열린 문서를 열면 선택 변화가 없다 — 셸 신호로 넘긴다 (D-077).
    [Fact]
    public void 밀려난_활성_탭을_다시_열면_보이게_넘기고_활성_탭이_없어도_괜찮다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(14);
        Pump();
        scene.Strip.Step(-100);
        Pump();
        Assert.True(scene.InViewport(14).Left > scene.Scroll.ViewportWidth);

        Wait(scene.Shell.OpenAsync(scene.Paths[14]));
        Pump();
        Assert.True(scene.InViewport(14).Right <= scene.Scroll.ViewportWidth + 2.5);

        scene.Shell.ActiveTab = null;
        Pump();
    });

    // ── T-7 ▾ 목록 (열지 않는다 — D-082) ─────────────────────────────────────────

    [Fact]
    public void 목록_한_줄은_제목을_그대로_그리고_같은_제목에만_폴더를_붙이며_탭과_같은_상태_아이콘을_앞에_둔다() => Run(() =>
    {
        using var scene = TabScene.Open(["문서", @"폴더1\문서", "a_b", "평문.txt"]);
        var template = (DataTemplate)scene.Window.FindResource("TabListItemTemplate");
        var entries = scene.Shell.TabListEntries();

        var rendered = entries.Select(entry =>
        {
            var presenter = new ContentPresenter { Content = entry, ContentTemplate = template };
            var probe = new Window { Content = presenter };
            ShowOffscreen(probe, 400, 80);
            var texts = FindDescendants<TextBlock>(presenter).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            var icons = Icon(presenter, scene.Window);
            var iconBeforeTitle = IconLeft(presenter) < TitleLeft(presenter, entry.Tab.TabTitle);
            Close(probe);
            return (texts, icons, iconBeforeTitle);
        }).ToList();

        // 머리글도 같은 아이콘을 탭 상태대로, 제목 앞에 — 목록만 보면 머리글의 연결이 끊기거나 아이콘이 제목 뒤로 가도 통과했다 [실측 — 2차 검토]
        Assert.Equal("locked", Icon(scene.Items[0], scene.Window));
        Assert.Equal("plain", Icon(scene.Items[3], scene.Window));          // 트리의 평문 그림 그대로 — 읽기 전용이어도 흐리지 않다
        Assert.All(scene.Items, item => Assert.True(IconLeft(item) < TitleLeft(item, ((EditorViewModel)item.DataContext).TabTitle)));

        Assert.Contains("금고 맨 위", rendered[0].texts);
        Assert.Contains("폴더1", rendered[1].texts);
        Assert.Contains("a_b", rendered[2].texts);                   // '_' 를 단축키로 먹지 않는다
        Assert.DoesNotContain(rendered[2].texts, t => t is "금고 맨 위" or "폴더1");
        Assert.Equal("locked", rendered[0].icons);
        Assert.Equal("plain", rendered[3].icons);
        Assert.All(rendered, r => Assert.True(r.iconBeforeTitle));    // 머리글처럼 제목 앞
        Assert.Contains("✓", rendered[3].texts);                     // 마지막에 연 탭이 활성
        Assert.DoesNotContain("✓", rendered[0].texts);               // 활성이 아닌 탭에는 없다
    });

    // ── 상태 아이콘 (적대 검토 SCROLL-2 · 사용자 판정 J-A · D-091 트리와 같은 그림) ──────────

    public sealed record IconProbe(SaveState SaveState, bool IsPlainText, bool IsPlainTextFile);

    /// <summary>
    /// 보이는 상태 아이콘을 말로: 그림("locked" · "plain") · "dim"(읽기 전용 흐림) · "warn"(저장 실패) · "dot"(저장 전 빨간 점)을 + 로 잇는다.
    /// 그림은 트리가 쓰는 자원과 <b>같은 객체</b>인지로 가른다 — 탭과 트리가 같은 그림을 쓰는 것이 이 아이콘의 뜻이다 (D-091).
    /// </summary>
    private static string Icon(DependencyObject root, FrameworkElement resources)
    {
        var doc = FindDescendants<Image>(root).First(i => i.Name == "Doc");
        var parts = new List<string>();
        if (doc.IsVisible)
        {
            parts.Add(ReferenceEquals(doc.Source, resources.FindResource("DocumentPlainIcon")) ? "plain"
                      : ReferenceEquals(doc.Source, resources.FindResource("DocumentLockedIcon")) ? "locked" : "other");
            if (doc.Opacity != 1)
                parts.Add(doc.Opacity == (double)resources.FindResource("DimmedOpacity") ? "dim" : $"opacity {doc.Opacity}");
        }
        if (FindDescendants<Viewbox>(root).First(v => v.Name == "Warn").IsVisible) parts.Add("warn");
        if (FindDescendants<System.Windows.Shapes.Ellipse>(root).First(e => e.Name == "Dot").IsVisible) parts.Add("dot");
        return string.Join("+", parts);
    }

    /// 아이콘 칸(16×16)의 왼쪽 — 칸 안의 그림은 상태마다 바뀌므로 칸으로 잰다.
    private static double IconLeft(UIElement root)
    {
        var slot = (UIElement)VisualTreeHelper.GetParent(FindDescendants<Image>(root).First(i => i.Name == "Doc"));
        return slot.TranslatePoint(new Point(0, 0), root).X;
    }

    private static double TitleLeft(UIElement root, string title)
        => FindDescendants<TextBlock>(root).First(t => t.Text == title).TranslatePoint(new Point(0, 0), root).X;

    /// 칸은 상태와 상관없이 16×16 이다. 제목 뒤 ● 는 입력할 때마다 탭을 12px 넓혀 아슬아슬한 폭에서 ◀▶▾ 가 깜빡였다 [실측 — 적대 검토].
    /// 그림은 트리와 같고, 상태는 그 위에 얹는다(D-091).
    [Theory]
    [InlineData(SaveState.Saved, false, false, "locked")]
    [InlineData(SaveState.Saving, false, false, "locked+dot")]
    [InlineData(SaveState.Failed, false, false, "warn")]
    [InlineData(SaveState.ReadOnly, false, false, "locked+dim")]     // 다른 키 · 열었을 때 — 트리의 "지금 키로 안 열림"과 같은 흐림
    [InlineData(SaveState.Saved, true, true, "plain")]               // 평문도 고칠 수 있다 (D-117) — 상태는 금고 문서와 같이 얹는다
    [InlineData(SaveState.ReadOnly, false, true, "plain+dim")]       // 읽지 못한 .txt — 트리처럼 평문 그림, 열리지 않았으니 흐림
    [InlineData(SaveState.Failed, true, true, "warn")]
    [InlineData(SaveState.Saving, true, true, "plain+dot")]
    public void 상태_아이콘은_트리_그림에_상태를_얹고_칸_크기는_같다(SaveState state, bool plain, bool plainFile, string expected) => Run(() =>
    {
        var window = new MainWindow();
        var template = (DataTemplate)window.Resources["TabStateIcon"];
        var presenter = new ContentPresenter
        {
            Content = new IconProbe(state, plain, plainFile), ContentTemplate = template,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        var probe = new Window { Content = presenter };
        ShowOffscreen(probe, 100, 60);
        var shown = Icon(presenter, window);
        var size = presenter.RenderSize;
        Close(probe);

        Assert.Equal(expected, shown);
        Assert.Equal(new Size(16, 16), size);
    });

    /// <summary>
    /// 트리와 탭이 같은 파일을 같은 그림 · 같은 흐림으로 보인다(D-091). 탭만 디스크 · 자물쇠 · 종이를 쓰면 같은 파일이 두 그림이었고,
    /// 자물쇠가 트리에서는 "암호화됨", 탭에서는 "읽기 전용"이라 거꾸로 읽혔다(사용자 지적).
    /// </summary>
    [Fact]
    public void 트리와_탭은_같은_문서를_같은_그림과_같은_흐림으로_보인다() => Run(() =>
    {
        using var scene = TabScene.Open(["문서", "메모.txt"]);
        var other = Path.Combine(scene.Vault.Root, "다른 키 문서.tbx");
        Wait(TestKeys.Store(scene.Vault.Root, "different-key-98765").CreateAsync(other));
        var binary = Path.Combine(scene.Vault.Root, "이진.txt");                  // 평문으로 읽지 못하는 .txt
        File.WriteAllBytes(binary, [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x00, 0x10, 0x00, 0x80, 0x00]);
        Wait(scene.Shell.RefreshAsync());
        Wait(scene.Shell.OpenAsync(other));
        Wait(scene.Shell.OpenAsync(binary));

        // 트리의 한 줄(아이콘 + 이름). 폴더 아닌 문서는 아이콘을 품은 패널이 하나뿐이다
        FrameworkElement? TreeRow(string path) => FindDescendants<StackPanel>(scene.Window).FirstOrDefault(p =>
            p.DataContext is TreeNodeViewModel node && string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase)
            && FindDescendant<Image>(p) is not null);

        // "지금 키로 열리는가"는 배경에서 잰다 — 흐려질 때까지 기다린다
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((TreeRow(other) is not { } row || row.Opacity == 1) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(30);
            Pump(DispatcherPriority.Background);
        }
        Pump();

        foreach (var (path, expected) in new[] { (scene.Paths[0], "locked"), (scene.Paths[1], "plain"), (other, "locked+dim") })
        {
            var index = scene.Shell.Tabs.ToList().FindIndex(t => string.Equals(t.CurrentPath, path, StringComparison.OrdinalIgnoreCase));
            var tab = scene.Items[index];
            var tree = TreeRow(path) ?? throw new InvalidOperationException($"트리에서 {path} 를 찾지 못했다");
            var tabDoc = FindDescendants<Image>(tab).First(i => i.Name == "Doc");

            Assert.Equal(expected, Icon(tab, scene.Window));
            Assert.Same(FindDescendant<Image>(tree)!.Source, tabDoc.Source);      // 같은 그림
            Assert.Equal(tree.Opacity, tabDoc.Opacity);                           // 같은 흐림
        }

        // 읽지 못한 .txt: 그림은 트리처럼 평문이다 — 예전에는 "다른 키로 잠긴 암호 문서" 그림이었다 [실측 — 3차 검토].
        // 흐림은 탭만 — 트리는 파일을 열어 보지 않고, 탭은 열지 못했다는 것을 안다(사용자 판정)
        var unreadable = scene.Shell.Tabs.First(t => string.Equals(t.CurrentPath, binary, StringComparison.OrdinalIgnoreCase));
        Assert.False(unreadable.IsPlainText);
        Assert.True(unreadable.IsPlainTextFile);
        var unreadableTab = scene.Items[scene.Shell.Tabs.IndexOf(unreadable)];
        Assert.Equal("plain+dim", Icon(unreadableTab, scene.Window));
        Assert.Same(FindDescendant<Image>(TreeRow(binary)!)!.Source, FindDescendants<Image>(unreadableTab).First(i => i.Name == "Doc").Source);
    });

    /// 탭이 머리글에 3px 남기고 들어가는 폭에서 입력 · 저장해도 탭 폭이 그대로라 버튼이 깜빡이지 않고 줄이 움직이지 않는다.
    [Fact]
    public void 아슬아슬한_폭에서_입력하고_저장해도_버튼과_줄이_그대로다() => Run(() =>
    {
        using var scene = TabScene.Open(8);
        var area = scene.Part<FrameworkElement>("PART_HeaderArea");
        var chrome = scene.Window.ActualWidth - area.ActualWidth;
        var tabs = scene.Panel.ActualWidth + scene.Panel.Margin.Left + scene.Panel.Margin.Right;
        scene.SetWidth(tabs + 3 + chrome);
        Assert.Same(scene.Tab(7), scene.Shell.ActiveTab);
        Assert.False(scene.Strip.IsOverflowing);
        var toggles = 0;
        scene.Part<Button>("PART_TabList").IsVisibleChanged += (_, _) => toggles++;
        var width = scene.Items[7].ActualWidth;

        scene.Tab(7).Text += "x";
        Pump();
        Assert.Equal(SaveState.Saving, scene.Tab(7).SaveState);
        Assert.Equal(width, scene.Items[7].ActualWidth, TabScene.Tolerance);
        Assert.Equal("locked+dot", Icon(scene.Items[7], scene.Window));     // 머리글이 탭 상태를 따른다 — 저장 전 = 빨간 점

        Assert.True(Wait(scene.Tab(7).TrySaveAsync()));
        Pump();

        Assert.Equal(SaveState.Saved, scene.Tab(7).SaveState);
        Assert.Equal("locked", Icon(scene.Items[7], scene.Window));
        Assert.Equal(0, toggles);
        Assert.False(scene.Strip.IsOverflowing);
        Assert.Equal(0, scene.Scroll.HorizontalOffset, TabScene.Tolerance);
    });

    [Fact]
    public void 목록에서_고르면_그_탭으로_가고_그_본문에_포커스를_준다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        System.Windows.Controls.Primitives.TextBoxBase? focused = null;
        scene.Window.FocusBody = textBox => focused = textBox;      // 실제 포커스는 잡지 않는다 (D-082)

        var entries = scene.Shell.TabListEntries();
        var menu = scene.Strip.BuildTabListMenu(entries);
        Assert.False(menu.IsOpen);
        Assert.Same(entries, menu.ItemsSource);
        Assert.Same(scene.Strip.TabListItemTemplate, menu.ItemTemplate);

        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, new MenuItem { DataContext = entries[2] }));
        Pump();

        Assert.Same(entries[2].Tab, scene.Shell.ActiveTab);
        Assert.NotNull(focused);
        // 고른 탭의 본문인지 직접 본다 — 찾는 함수끼리 비교하면 그 함수가 다른 탭 본문을 돌려줘도 통과한다 [실측 — 적대 검토].
        // entries[2] 는 첫 탭도 활성 탭(마지막에 연 탭)도 아니다.
        Assert.Same(entries[2].Tab, focused.DataContext);
    });

    /// 목록은 연 순간의 스냅숏이다. 그 사이 닫힌 탭을 고르면 닫힌 편집기가 활성이 되어 본문이 비었다 [실측 — 적대 검토].
    [Fact]
    public void 목록을_연_뒤_닫힌_탭을_고르면_아무_일도_없다() => Run(() =>
    {
        using var scene = TabScene.Open(6);
        System.Windows.Controls.Primitives.TextBoxBase? focused = null;
        scene.Window.FocusBody = textBox => focused = textBox;
        var active = scene.Shell.ActiveTab;
        var entries = scene.Shell.TabListEntries();
        var menu = scene.Strip.BuildTabListMenu(entries);
        var closed = entries[2].Tab;

        Wait(scene.Shell.CloseTabAsync(closed));
        Pump();
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, new MenuItem { DataContext = entries[2] }));
        Pump();

        Assert.DoesNotContain(closed, scene.Shell.Tabs);
        Assert.Same(active, scene.Shell.ActiveTab);
        Assert.Null(focused);
    });

    [Fact]
    public void 목록은_버튼_오른쪽_끝에_맞춰_연다()
        => Assert.Equal(new Point(28 - 200, 22), TabStrip.RightAlignedPlacement(new Size(200, 120), new Size(28, 22)));

    // ── T-8 긴 제목 ──────────────────────────────────────────────────────────

    [Fact]
    public void 긴_제목은_말줄임되고_줄보다_넓은_탭에도_예외가_없다() => Run(() =>
    {
        var longTitle = "아주아주 긴 이름을 가진 문서는 최대 폭에서 말줄임으로 줄어들고 전체 제목은 툴팁으로 보입니다 끝까지 아주 깁니다";
        using var scene = TabScene.Open([.. TabScene.DefaultTitles.Take(10), longTitle]);
        var index = scene.Items.Count - 1;

        var title = FindDescendants<TextBlock>(scene.Items[index]).First(t => t.Text == longTitle);
        Assert.True(title.ActualWidth <= 240.5, $"제목 칸 {title.ActualWidth}");
        Assert.Equal(longTitle, title.ToolTip);
        var close = FindDescendants<Button>(scene.Items[index]).First(b => (b.Content as string) == "✕");
        Assert.True(close.TransformToVisual(scene.Items[index]).TransformBounds(new Rect(close.RenderSize)).Right
                    <= scene.Items[index].ActualWidth + 0.5);

        // 줄이 그 탭보다 좁다 — 그 탭 왼쪽에 맞추고 예외가 없어야 한다
        scene.SetWidth(scene.Window.ActualWidth - scene.Scroll.ViewportWidth + 150);
        scene.Shell.ActiveTab = scene.Tab(index);
        Pump();
        Assert.Equal(index, scene.Strip.StartIndex);
        Snapshot(scene.Window, "t8-narrow-long");
        scene.AssertLeftEdgeOnBoundary();
    });

    // ── T-9 Home/End ─────────────────────────────────────────────────────────

    /// HandlesScrolling 이 없으면 안쪽 ScrollViewer 가 End 를 먹어 선택은 그대로 둔 채 줄만 끝으로 간다 [실측 — 계획 검토].
    /// 키를 TabStrip 까지 보내면 TabItem 포커스 경로(실제 키보드 포커스)를 타므로 그 앞에서 끊는다 (D-082). 실제 선택은 실기 확인.
    [Fact]
    public void 탭_줄의_ScrollViewer_는_End_키를_먹지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        var before = scene.Scroll.HorizontalOffset;
        bool? handledByScrollViewer = null;
        scene.Part<FrameworkElement>("PART_HeaderArea").AddHandler(System.Windows.Input.Keyboard.KeyDownEvent,
            new System.Windows.Input.KeyEventHandler((_, e) => { handledByScrollViewer = e.Handled; e.Handled = true; }),
            handledEventsToo: true);

        scene.Scroll.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(scene.Scroll)!, 0, System.Windows.Input.Key.End)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        Pump();

        Assert.False(handledByScrollViewer);
        Assert.Equal(before, scene.Scroll.HorizontalOffset);
    });

    // ── T-10 · T-11 끌기 ────────────────────────────────────────────────────────

    [Fact]
    public void 삽입선은_탭_줄_안쪽_층에_그리고_마우스_판정을_막지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(15);

        scene.Strip.ShowInsertion(scene.Items[3], after: true);
        var adorner = scene.Strip.InsertionAdorner!;
        var layer = AdornerLayer.GetAdornerLayer(scene.Panel)!;

        Assert.Contains(adorner, layer.GetAdorners(scene.Panel) ?? []);
        Assert.NotSame(AdornerLayer.GetAdornerLayer(scene.Window.Content as Visual ?? scene.Window), layer);
        Assert.IsType<ScrollContentPresenter>(System.Windows.Media.VisualTreeHelper.GetParent(layer));  // 스크롤 영역이 자른다
        Assert.False(adorner.IsHitTestVisible);

        scene.Strip.HideInsertion();
        Assert.Null(scene.Strip.InsertionAdorner);
    });

    private static Point EdgePoint(TabScene scene, bool right)
    {
        var view = scene.Scroll.TransformToVisual(scene.Strip).TransformBounds(new Rect(scene.Scroll.RenderSize));
        return new Point(right ? view.Right - 5 : view.Left + 5, view.Top + view.Height / 2);
    }

    [Fact]
    public void 끄는_중_가장자리에_머물면_탭_하나씩_넘기고_가운데나_다른_끌기는_넘기지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        var tab = new DataObject(TabStrip.DragFormat, 0);

        scene.Strip.DragHover(EdgePoint(scene, right: true), tab);
        Assert.True(scene.Strip.IsDragScrolling);
        scene.Strip.DragTick();
        Pump();
        Assert.Equal(1, scene.Strip.StartIndex);
        scene.AssertLeftEdgeOnBoundary();
        Assert.NotNull(scene.Strip.InsertionAdorner);              // 넘긴 뒤 마지막 커서 자리로 다시 그렸다

        scene.Strip.DragHover(EdgePoint(scene, right: false), tab);
        scene.Strip.DragTick();
        Pump();
        Assert.Equal(0, scene.Strip.StartIndex);

        var middle = new Point(scene.Strip.ActualWidth / 3, EdgePoint(scene, right: true).Y);
        scene.Strip.DragHover(middle, tab);
        Assert.False(scene.Strip.IsDragScrolling);

        scene.Strip.DragHover(EdgePoint(scene, right: true), new DataObject("TextBean.TreeNode", "x"));   // 트리에서 온 끌기
        Assert.False(scene.Strip.IsDragScrolling);
    });

    /// 끄는 중 ◀ 위에 머물면 왼쪽으로, ▶ · ▾ 위면 오른쪽으로 (사용자 판정 J-B). 버튼 묶음을 통째로 오른쪽 끝으로 보면 ◀ 위에서 반대로 갔다.
    [Fact]
    public void 끄는_중_버튼_위에_머물면_그_버튼_방향으로_넘긴다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        scene.Strip.Step(3);
        Pump();
        var tab = new DataObject(TabStrip.DragFormat, 0);

        Point Over(string part)
        {
            var button = scene.Part<Button>(part);
            return button.TranslatePoint(Center(button), scene.Strip);
        }

        foreach (var (part, expected) in new[] { ("PART_ScrollLeft", 2), ("PART_ScrollRight", 3), ("PART_TabList", 4) })
        {
            scene.Strip.DragHover(Over(part), tab);
            scene.Strip.DragTick();
            Pump();
            Assert.Equal(expected, scene.Strip.StartIndex);
        }

        // ◀ 바로 위 2px(버튼 위 마진)도 ◀ 다 — 그 띠에서는 오른쪽으로 넘겼다 [실측 — 2차 검토]
        var left = scene.Part<Button>("PART_ScrollLeft");
        scene.Strip.DragHover(left.TranslatePoint(new Point(left.ActualWidth / 2, -1), scene.Strip), tab);
        scene.Strip.DragTick();
        Pump();
        Assert.Equal(3, scene.Strip.StartIndex);
        scene.Strip.DragEnded();
    });

    /// DragLeave 의 좌표는 커서 자리가 아니라 미뤄 판정한다 — 같은 차례에 줄 안 자식의 DragEnter(→ DragHover)가 오면 줄 안이다.
    /// 실제 OLE 순서는 아래 OleDrag 시험이 본다.
    [Fact]
    public void 줄_안_자식으로_넘어가면_멈추지_않고_줄을_떠나거나_끝나면_멈춘다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        var tab = new DataObject(TabStrip.DragFormat, 0);
        var edge = EdgePoint(scene, right: true);

        scene.Strip.DragHover(edge, tab);
        scene.Strip.DragLeft();                                     // 줄 안 자식 사이 — 새 자식의 DragEnter 가 뒤따른다
        scene.Strip.DragHover(edge, tab);
        Pump();
        Assert.True(scene.Strip.IsDragScrolling);

        scene.Strip.ShowInsertion(scene.Items[2], after: false);
        scene.Strip.DragLeft();                                     // 뒤따르는 DragEnter 가 없다 — 줄을 떠났다
        Pump();
        Assert.False(scene.Strip.IsDragScrolling);
        Assert.Null(scene.Strip.InsertionAdorner);

        scene.Strip.DragHover(edge, tab);
        scene.Strip.ShowInsertion(scene.Items[2], after: false);
        scene.Strip.DragEnded();
        Assert.False(scene.Strip.IsDragScrolling);
        Assert.Null(scene.Strip.InsertionAdorner);
    });

    [Fact]
    public void 가장자리_타이머는_실제로_돈다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        scene.Strip.DragScrollInterval = TimeSpan.FromMilliseconds(40);

        scene.Strip.DragHover(EdgePoint(scene, right: true), new DataObject(TabStrip.DragFormat, 0));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (scene.Strip.StartIndex == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            Pump(DispatcherPriority.Background);
        }
        scene.Strip.DragEnded();

        Assert.True(scene.Strip.StartIndex > 0);
    });

    /// DragOver 는 커서가 움직이는 동안 계속 온다. 올 때마다 타이머를 처음부터 다시 세면 한 번도 넘어가지 않는다 [추정 — 계획 검토].
    [Fact]
    public void 끄는_중_DragOver_가_계속_와도_가장자리_넘김은_이어진다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        scene.Strip.DragScrollInterval = TimeSpan.FromMilliseconds(120);
        var tab = new DataObject(TabStrip.DragFormat, 0);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (scene.Strip.StartIndex == 0 && DateTime.UtcNow < deadline)
        {
            scene.Strip.DragHover(EdgePoint(scene, right: true), tab);     // 간격(120ms)보다 자주
            Thread.Sleep(30);
            Pump(DispatcherPriority.Background);
        }
        scene.Strip.DragEnded();

        Assert.True(scene.Strip.StartIndex > 0);
    });

    /// MainWindow 처리기: 트리 끌기는 넘기지 않고, 놓으면 멈춘다. DragEventArgs 는 내부 생성자라 리플렉션으로 만든다.
    [Fact]
    public void 창의_끌기_처리기는_탭_끌기만_넘기고_놓으면_멈춘다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        var edge = EdgePoint(scene, right: true);

        scene.Strip.RaiseEvent(Drag(DragDrop.DragOverEvent, new DataObject("TextBean.TreeNode", "x"), scene.Strip, edge));
        Assert.False(scene.Strip.IsDragScrolling);

        var tab = new DataObject(TabStrip.DragFormat, 0);
        scene.Strip.RaiseEvent(Drag(DragDrop.DragOverEvent, tab, scene.Strip, edge));
        Assert.True(scene.Strip.IsDragScrolling);

        // 줄 안 다른 자식으로: DragLeave 뒤 같은 차례에 DragEnter — 창이 DragEnter 도 받아야 멈추지 않는다.
        // 끊었다 다시 켜는 것도 안 된다 — 자식을 지날 때마다 삽입선이 깜빡이고 넘김 타이머가 처음부터 다시 센다
        var line = scene.Strip.InsertionAdorner;
        Assert.NotNull(line);
        scene.Strip.RaiseEvent(Drag(DragDrop.DragLeaveEvent, tab, scene.Strip, edge));
        scene.Strip.RaiseEvent(Drag(DragDrop.DragEnterEvent, tab, scene.Strip, edge));
        Pump();
        Assert.True(scene.Strip.IsDragScrolling);
        Assert.Same(line, scene.Strip.InsertionAdorner);

        scene.Strip.RaiseEvent(Drag(DragDrop.DragLeaveEvent, tab, scene.Strip, edge));
        Pump();
        Assert.False(scene.Strip.IsDragScrolling);

        scene.Strip.RaiseEvent(Drag(DragDrop.DragOverEvent, tab, scene.Strip, edge));
        Assert.True(scene.Strip.IsDragScrolling);
        scene.Strip.RaiseEvent(Drag(DragDrop.DropEvent, tab, scene.Strip, edge));
        Assert.False(scene.Strip.IsDragScrolling);
        Assert.Null(scene.Strip.InsertionAdorner);
    });

    // ── 실제 OLE 경로 (적대 검토) ─────────────────────────────────────────────

    /// <summary>
    /// 실제 끌기에서 OS 가 부르는 WPF 의 OLE 대상(System.Windows.OleDropTarget)을 리플렉션으로 만들어 직접 부른다.
    /// DoDragDrop(사용자 마우스를 잡는 OLE 루프)은 돌리지 않는다 (D-082). DragLeave 좌표가 커서 자리가 아니라는 것,
    /// 대상이 바뀌는 호출은 DragLeave · DragEnter 만 올린다는 것은 이 경로에서만 드러난다 [실측 — 적대 검토].
    /// </summary>
    private sealed class OleDrag(Window window, IDataObject data)
    {
        private const int LeftButton = 1;

        private static readonly Type TargetType = typeof(DragDrop).Assembly.GetType("System.Windows.OleDropTarget")
            ?? throw new InvalidOperationException("System.Windows.OleDropTarget 을 찾지 못했다");

        private static readonly Type Contract = TargetType.GetInterfaces().Single(i => i.Name == "IOleDropTarget");

        private readonly object _target = Activator.CreateInstance(TargetType, new WindowInteropHelper(window).Handle)!;

        public void Enter(Visual over, Point at) => Call("OleDragEnter", data, LeftButton, Screen(over, at), (int)DragDropEffects.Move);

        public void Over(Visual over, Point at) => Call("OleDragOver", LeftButton, Screen(over, at), (int)DragDropEffects.Move);

        public void Leave() => Call("OleDragLeave");

        public void Drop(Visual over, Point at) => Call("OleDrop", data, LeftButton, Screen(over, at), (int)DragDropEffects.Move);

        /// OLE 의 POINTL(화면 픽셀). 시험 창은 화면 밖 음수 좌표에 있다.
        private static long Screen(Visual over, Point at)
        {
            var screen = over.PointToScreen(at);
            return ((long)(int)Math.Round(screen.Y) << 32) | (uint)(int)Math.Round(screen.X);
        }

        private void Call(string name, params object[] args) => Contract.GetMethod(name)!.Invoke(_target, args);
    }

    private static Point Center(FrameworkElement element) => new(element.ActualWidth / 2, element.ActualHeight / 2);

    /// 창 밖 · 트리 · 도구 모음으로 나가면 DragLeave 좌표가 줄 안으로 읽혀 자동 넘김과 삽입선이 계속됐다 [실측 — 적대 검토].
    [Fact]
    public void 실제_OLE_경로에서_줄을_떠나면_자동_넘김과_삽입선이_멈춘다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        scene.Strip.DragScrollInterval = TimeSpan.FromSeconds(30);      // 저절로 넘기지 않게 — 타이머가 도는지만 본다
        var tree = FindDescendant<TreeView>(scene.Window)!;
        var toolbar = FindDescendant<Menu>(scene.Window)!;                  // 위 줄 메뉴 막대 (D-158)

        OleDrag StartAtEdge()
        {
            var drag = new OleDrag(scene.Window, new DataObject(TabStrip.DragFormat, 1));
            drag.Enter(scene.Items[1], Center(scene.Items[1]));
            drag.Over(scene.Strip, EdgePoint(scene, right: true));      // 다른 탭 위로 — DragLeave · DragEnter 만 온다
            Pump();
            Assert.True(scene.Strip.IsDragScrolling);                   // 줄 안 자식 사이의 DragLeave 로는 멈추지 않는다
            Assert.NotNull(scene.Strip.InsertionAdorner);
            return drag;
        }

        StartAtEdge().Leave();                                          // 창 밖으로
        Pump();
        Assert.False(scene.Strip.IsDragScrolling);
        Assert.Null(scene.Strip.InsertionAdorner);

        foreach (var (name, over, at) in new (string, FrameworkElement, Point)[]
                 { ("트리", tree, new Point(20, 3)), ("위 메뉴", toolbar, Center(toolbar)) })
        {
            var drag = StartAtEdge();
            drag.Over(over, at);
            Pump();
            Assert.False(scene.Strip.IsDragScrolling, name);
            Assert.Null(scene.Strip.InsertionAdorner);
            drag.Leave();
        }
    });

    /// 끌던 탭이 왼쪽 밖으로 밀릴 만큼 자동 넘김한 뒤 오른쪽에 놓으면 왼쪽 끝이 경계에서 벗어났다 — 권고 3(화면 밖 자리로 옮기기)의 쓰임 그대로다.
    [Fact]
    public void 실제_OLE_경로로_끌며_넘긴_뒤_놓아도_왼쪽_끝은_탭_경계다() => Run(() =>
    {
        using var scene = TabScene.Open(15);
        scene.Shell.ActiveTab = scene.Tab(0);
        Pump();
        scene.Strip.DragScrollInterval = TimeSpan.FromSeconds(30);
        var dragged = scene.Tab(0);
        var drag = new OleDrag(scene.Window, new DataObject(TabStrip.DragFormat, 0));

        drag.Enter(scene.Items[0], Center(scene.Items[0]));
        drag.Over(scene.Strip, EdgePoint(scene, right: true));
        for (var i = 0; i < 4; i++)
        {
            scene.Strip.DragTick();
            Pump();
        }
        Assert.Equal(4, scene.Strip.StartIndex);

        var target = scene.Items[10];
        var at = new Point(target.ActualWidth * 0.75, target.ActualHeight / 2);
        drag.Over(target, at);
        drag.Over(target, at);
        drag.Drop(target, at);
        scene.Strip.DragEnded();                                        // DoDragDrop 의 finally
        Pump();

        Assert.Equal(10, scene.Shell.Tabs.IndexOf(dragged));
        Assert.Equal(4, scene.Strip.StartIndex);
        scene.AssertLeftEdgeOnBoundary();
    });

    private static DragEventArgs Drag(RoutedEvent routed, IDataObject data, DependencyObject target, Point point)
    {
        var ctor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)
            ?? throw new InvalidOperationException("DragEventArgs 내부 생성자를 찾지 못했다");
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, point]);
        args.RoutedEvent = routed;
        return args;
    }

    // ── T-13 시험 호스트 걸쇠 ──────────────────────────────────────────────────

    /// 걸쇠가 소리 없이 꺼지면 대화상자가 사용자 화면에 뜬다. 일부러 중첩 루프를 만들어 걸쇠가 잡는지 본다.
    [Fact]
    public void 시험_호스트는_중첩_메시지_루프를_잡는다()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Run(() =>
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                var inner = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => inner.Continue = false);
                Dispatcher.PushFrame(inner);       // 깊이 2 — ShowDialog 가 하는 일
            });
            Pump();
        }));

        Assert.Contains("중첩 메시지 루프", error.Message);
    }

    /// 본문(깊이 0)에서 바로 ShowDialog 를 하면 깊이 1 이라 중첩 걸쇠가 못 잡는다 — 스레드 모달 걸쇠가 따로 있다.
    /// 대화상자 없이 ShowDialog 가 내는 신호만 낸다.
    [Fact]
    public void 시험_호스트는_스레드_모달을_잡는다()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Run(() =>
        {
            ComponentDispatcher.PushModal();
            ComponentDispatcher.PopModal();
        }));

        Assert.Contains("스레드 모달", error.Message);
    }

    [Fact]
    public void 시험_호스트는_닫지_않은_창을_잡는다()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Run(() => ShowOffscreen(new Window(), 200, 100)));

        Assert.Contains("창을 닫지", error.Message);
    }

    [Fact]
    public void 시험_프로세스의_Application_은_App_이_아니다() => Run(() =>
    {
        Assert.Equal(typeof(Application), Application.Current!.GetType());
    });

    /// 화면 시험이 깨질 때마다 테스트 키로 잠긴 더미 금고가 %TEMP%\TextBeanTests 에 쌓였다 [실측 — 적대 검토].
    /// 그 장면의 금고 하나만 본다 — 공용 폴더를 앞뒤로 비교하면 다른 시험 프로세스의 금고가 섞여 거짓 실패했다 [실측 — 2차 검토].
    [Fact]
    public void 장면을_만들다_실패해도_임시_금고를_남기지_않는다() => Run(() =>
    {
        using var vault = new TempVault();

        Assert.ThrowsAny<Exception>(() => TabScene.Open(["파일 이름에 쓸 수 없는<글자"], vault));

        Assert.False(Directory.Exists(vault.Root), "장면이 실패했는데 금고가 남았다");
    });
}
