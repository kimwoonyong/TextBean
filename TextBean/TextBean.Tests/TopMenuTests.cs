using System.Windows.Controls;
using System.Windows.Input;
using TextBean.ViewModels;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 위 줄 메뉴 막대 (D-158 ~ D-160). 메뉴는 열지 않는다(D-082) — 하위 항목은 열지 않아도 항목 목록에 있다.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class TopMenuTests
{
    private static Menu TopMenu(TabScene scene) => FindDescendant<Menu>(scene.Window)!;

    private static MenuItem Top(TabScene scene, string header)
        => TopMenu(scene).Items.OfType<MenuItem>().Single(m => Equals(m.Header, header));

    private static MenuItem Item(TabScene scene, string menu, string header)
        => Top(scene, menu).Items.OfType<MenuItem>().Single(m => Equals(m.Header, header));

    [Fact]
    public void 메뉴_다섯_개에_예전_단추와_같은_명령이_묶이고_도구_모음은_없다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var shell = scene.Shell;

        Assert.Equal(["파일", "편집", "보기", "보안", "도움말"], TopMenu(scene).Items.OfType<MenuItem>().Select(m => (string)m.Header));
        Assert.Null(FindDescendant<ToolBar>(scene.Window));

        (string Menu, string Header, ICommand Command)[] expected =
        [
            ("파일", "새 문서", shell.NewDocumentCommand), ("파일", "새 폴더", shell.NewFolderCommand),
            ("파일", "이름 변경", shell.RenameCommand), ("파일", "삭제", shell.DeleteCommand),
            ("파일", "파일 탐색기에서 열기", shell.OpenInExplorerCommand), ("파일", "휴지통 비우기", shell.EmptyTrashCommand),
            ("파일", "금고 폴더 변경", shell.ChangeRootCommand), ("파일", "종료", shell.ExitCommand),
            ("편집", "전체 복사", shell.CopyAllCommand), ("편집", "열었을 때 상태 보기", shell.OpenBackupCommand),
            ("편집", "검색", shell.OpenFindBarCommand), ("편집", "상세 검색", shell.OpenSearchWindowCommand),
            ("보기", "새로고침", shell.RefreshCommand),
            ("보안", "잠그기", shell.LockCommand),
            ("도움말", "단축키", shell.OpenShortcutsCommand),
        ];
        Assert.All(expected, e => Assert.Same(e.Command, Item(scene, e.Menu, e.Header).Command));

        var key = Top(scene, "보안").Items.OfType<MenuItem>().First();          // 키 상태에 따라 「키 입력」 / 「키 변경」
        Assert.Equal(shell.ChangeKeyLabel, key.Header);
        Assert.Same(shell.ChangeKeyCommand, key.Command);
    });

    [Fact]
    public void 오른쪽_단축키_글자는_키_표를_따르고_바꾸면_따라간다() => Run(() =>
    {
        using var scene = TabScene.Open(1);

        Assert.Equal("Ctrl+F", Item(scene, "편집", "검색").InputGestureText);
        Assert.Equal("Ctrl+Shift+F", Item(scene, "편집", "상세 검색").InputGestureText);
        Assert.Equal("F5", Item(scene, "보기", "새로고침").InputGestureText);
        Assert.Equal("F1", Item(scene, "도움말", "단축키").InputGestureText);
        Assert.True(string.IsNullOrEmpty(Item(scene, "파일", "새 문서").InputGestureText));      // 기본 키가 없다

        var map = new Dictionary<string, string?>(scene.Shell.Shortcuts) { ["newDocument"] = "Ctrl+K" };
        Wait(scene.Shell.SetShortcutsAsync(map));

        Assert.Equal("Ctrl+K", Item(scene, "파일", "새 문서").InputGestureText);
    });

    [Fact]
    public void 보기_글꼴은_후보마다_한_줄이고_지금_글꼴에_체크가_있다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var fonts = Top(scene, "보기").Items.OfType<MenuItem>().Single(m => Equals(m.Header, "글꼴"));

        var items = fonts.Items.OfType<FontMenuItem>().ToList();
        Assert.Equal(FontChoices.All.Select(c => c.Label), items.Select(i => i.Label));
        Assert.Equal([FontChoices.DefaultId], items.Where(i => i.IsCurrent).Select(i => i.Id));
    });

    [Fact]
    public void 보기_테마는_밝게_어둡게_두_줄이고_지금_테마에_체크가_있다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var themes = Top(scene, "보기").Items.OfType<MenuItem>().Single(m => Equals(m.Header, "테마"));

        var items = themes.Items.OfType<ThemeMenuItem>().ToList();
        Assert.Equal(["밝게", "어둡게"], items.Select(i => i.Label));
        Assert.Equal([ThemeChoices.DefaultId], items.Where(i => i.IsCurrent).Select(i => i.Id));
    });
}
