using System.Windows.Controls;
using System.Windows.Documents;
using TextBean.Services;
using TextBean.ViewModels;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 본문 글꼴 설정 (D-155 ~ D-157). AppFonts.Body 는 정적이라 바꾼 시험은 반드시 기본으로 되돌린다 —
/// 화면 시험 묶음(병렬 금지) 안에서 돈다.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class FontSettingTests
{
    private static string SettingsPath(TempVault vault) => Path.Combine(vault.Root, "settings.json");

    /// 닫힌 하위 메뉴 항목은 시각 트리에 없다 — 논리 트리로 찾는다
    private static T? LogicalDescendant<T>(System.Windows.DependencyObject root, Func<T, bool> match) where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T hit && match(hit)) return hit;
            if (LogicalDescendant(child, match) is { } deeper) return deeper;
        }
        return null;
    }

    [Fact]
    public void 후보는_D2Coding_기본과_맑은_고딕이고_모르는_id_는_기본이다()
    {
        Assert.Equal(["d2coding", "malgun"], FontChoices.All.Select(c => c.Id));
        Assert.Equal(FontChoices.DefaultId, FontChoices.All[0].Id);
        Assert.Equal("d2coding", FontChoices.Find("없는글꼴").Id);
        Assert.Equal("d2coding", FontChoices.Find(null).Id);
    }

    [Fact]
    public async Task 고르면_저장되고_다시_켜면_그_글꼴이다()
    {
        using var vault = new TempVault();
        var (shell, _, _) = await ShellFixture.BuildAsync(vault);
        Assert.Equal("d2coding", shell.BodyFont.Id);
        Assert.True(shell.FontMenu.Single(m => m.Id == "d2coding").IsCurrent);

        await shell.SetBodyFontAsync("malgun");

        Assert.Equal("malgun", shell.BodyFont.Id);
        Assert.Equal(["malgun"], shell.FontMenu.Where(m => m.IsCurrent).Select(m => m.Id));
        var settings = new AppSettingsService(SettingsPath(vault));
        await settings.LoadAsync();
        Assert.Equal("malgun", settings.Current.BodyFont);
        shell.Dispose();

        var (again, _, _) = await ShellFixture.BuildAsync(vault);
        Assert.Equal("malgun", again.BodyFont.Id);
        again.Dispose();
    }

    [Fact]
    public async Task 설정에_모르는_글꼴이_적혀_있으면_기본_글꼴로_뜬다()
    {
        using var vault = new TempVault();
        var settings = new AppSettingsService(SettingsPath(vault));
        await settings.LoadAsync();
        settings.Current.BodyFont = "지운글꼴";
        await settings.SaveAsync();

        var (shell, _, _) = await ShellFixture.BuildAsync(vault);

        Assert.Equal("d2coding", shell.BodyFont.Id);
        shell.Dispose();
    }

    [Fact]
    public async Task 저장에_실패하면_알리고_지금은_고른_글꼴을_쓴다()
    {
        using var vault = new TempVault();
        var (shell, dlg, _) = await ShellFixture.BuildAsync(vault);
        if (File.Exists(SettingsPath(vault))) File.Delete(SettingsPath(vault));
        Directory.CreateDirectory(SettingsPath(vault));                       // 같은 이름 폴더 — 파일을 못 만든다

        await shell.SetBodyFontAsync("malgun");

        Assert.Equal("malgun", shell.BodyFont.Id);
        Assert.Equal("글꼴 저장 실패", dlg.LastErrorTitle);
        shell.Dispose();
    }

    /// 실제 창: 고르면 열린 서식 본문(문서 안 글자까지) · .txt 본문이 바로 바뀌고, 찾기 칸은 D2Coding 그대로 (D-156)
    [Fact]
    public void 고르면_열린_본문이_바로_바뀌고_찾기_칸은_그대로다() => Run(() =>
    {
        using var scene = TabScene.Open(["문서", "메모.txt"]);
        try
        {
            scene.Shell.ActiveTab = scene.Tab(0);
            scene.Window.UpdateLayout();
            var rich = Assert.IsType<RichTextBox>(scene.Window.FindActiveBodyTextBox());
            new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text = "본문 글";
            scene.Shell.ActiveTab = scene.Tab(1);
            scene.Window.UpdateLayout();
            var plain = Assert.IsType<TextBox>(scene.Window.FindActiveBodyTextBox());
            var findBox = FindDescendant<TextBox>(scene.Window, t => t.Name == "FindBox")!;

            Wait(scene.Shell.SetBodyFontAsync("malgun"));
            scene.Window.UpdateLayout();

            var run = rich.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().First(r => r.Text.Length > 0);
            Assert.Equal("Malgun Gothic", rich.FontFamily.Source);
            Assert.Equal("Malgun Gothic", run.FontFamily.Source);                // 문서 안 글자도 따라온다(문서에 글꼴 이름이 없다 — D-149)
            Assert.Equal("Malgun Gothic", plain.FontFamily.Source);
            Assert.Same(AppFonts.Fixed, findBox.FontFamily);                     // 고정폭 칸은 그대로

            // 보기 ▸ 글꼴 (D-158) — 메뉴는 열지 않고 항목 목록으로 본다(D-082). ✓ 는 지금 글꼴 하나
            var fontMenu = FindDescendant<MenuItem>(scene.Window, m => m.Name == "FontMenu")
                           ?? LogicalDescendant<MenuItem>(scene.Window, m => m.Name == "FontMenu")!;
            var items = fontMenu.Items.OfType<FontMenuItem>().ToList();
            Assert.Equal(FontChoices.All.Count, items.Count);
            Assert.Equal(["malgun"], items.Where(i => i.IsCurrent).Select(i => i.Id));
            Snapshot(scene.Window, "font-malgun");

            Wait(scene.Shell.SetBodyFontAsync("d2coding"));
            Assert.Same(AppFonts.Fixed, rich.FontFamily);
        }
        finally
        {
            AppFonts.Select(FontChoices.Find(FontChoices.DefaultId));           // 정적 — 다른 시험에 새지 않게
        }
    });
}
