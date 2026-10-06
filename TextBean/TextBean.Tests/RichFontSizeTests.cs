using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TextBean.Models;
using TextBean.ViewModels;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 글자 크기 (D-151 ~ D-153). 화면 밖 · 포커스 없음 — 명령은 본문을 대상으로 직접 부른다.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class RichFontSizeTests
{
    private sealed class Scene : IDisposable
    {
        private readonly Window _window;

        public Scene(object? dataContext = null)
        {
            Box = new RichTextBox { FontSize = RichFormat.DefaultSize, DataContext = dataContext };
            RichBodyBehavior.SetAttach(Box, true);
            if (dataContext is null) Box.Document = new FlowDocument(new Paragraph(new Run("abcdef")));
            _window = new Window
            {
                Left = -20000, Top = -20000, Width = 400, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Content = Box
            };
            _window.Show();
        }

        public RichTextBox Box { get; }

        public void Dispose() => _window.Close();
    }

    private static void Select(RichTextBox box, int from, int to)
    {
        var map = RichTextMap.Build(box.Document);
        box.Selection.Select(map.PointerAt(from)!, map.PointerAt(to)!);
    }

    private static double SizeOf(RichTextBox box, int at)
    {
        var map = RichTextMap.Build(box.Document);
        return (double)new TextRange(map.PointerAt(at)!, map.PointerAt(at + 1)!).GetPropertyValue(TextElement.FontSizeProperty);
    }

    [Theory]
    [InlineData(13, true, 16)]
    [InlineData(16, true, 20)]
    [InlineData(32, true, 32)]          // 끝에서는 그대로
    [InlineData(10, false, 10)]
    [InlineData(13, false, 12)]
    [InlineData(13.75, true, 16)]       // 목록에 없는 크기는 그보다 큰 첫 값
    [InlineData(13.75, false, 13)]
    public void 다음_이전_크기는_목록_안에서_움직인다(double current, bool larger, double expected)
        => Assert.Equal(expected, RichFormat.StepSize(current, larger));

    [Fact]
    public void 고른_곳에_크기를_칠하고_실행취소_한_번에_되돌린다() => Run(() =>
    {
        using var scene = new Scene();
        var box = scene.Box;
        Select(box, 1, 3);

        RichFormat.SetFontSize.Execute("20", box);

        Assert.Equal(20, SizeOf(box, 1));
        Assert.Equal(RichFormat.DefaultSize, SizeOf(box, 4));
        Assert.Equal("20", RichFormat.GetCurrentSize(box));
        box.Undo();
        Assert.Equal(RichFormat.DefaultSize, SizeOf(box, 1));
    });

    [Fact]
    public void 목록에_없는_크기는_안_되고_읽기_전용이면_안_된다() => Run(() =>
    {
        using var scene = new Scene();
        Assert.False(RichFormat.SetFontSize.CanExecute("15", scene.Box));
        Assert.False(RichFormat.SetFontSize.CanExecute("크게", scene.Box));
        Assert.True(RichFormat.SetFontSize.CanExecute("16", scene.Box));

        scene.Box.IsReadOnly = true;
        Assert.False(RichFormat.SetFontSize.CanExecute("16", scene.Box));
        Assert.False(RichFormat.IncreaseFontSize.CanExecute(null, scene.Box));
    });

    [Fact]
    public void 크게_작게는_첫_글자_크기_기준으로_섞인_선택을_하나로_맞춘다() => Run(() =>
    {
        using var scene = new Scene();
        var box = scene.Box;
        Select(box, 0, 2);
        RichFormat.SetFontSize.Execute("10", box);

        Select(box, 0, 5);                                                     // 10 과 13 이 섞였다
        RichFormat.IncreaseFontSize.Execute(null, box);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal(12, SizeOf(box, i)));

        RichFormat.DecreaseFontSize.Execute(null, box);
        RichFormat.DecreaseFontSize.Execute(null, box);                        // 10 에서 더 작아지지 않는다
        Assert.Equal(10, SizeOf(box, 0));
    });

    [Fact]
    public void 커서를_옮기면_지금_크기가_바뀐다() => Run(() =>
    {
        using var scene = new Scene();
        var box = scene.Box;
        Select(box, 0, 2);
        RichFormat.SetFontSize.Execute("24", box);

        Select(box, 4, 4);
        Assert.Equal("13", RichFormat.GetCurrentSize(box));
        Select(box, 1, 1);
        Assert.Equal("24", RichFormat.GetCurrentSize(box));
    });

    [Fact]
    public void Ctrl_대괄호는_막지_않고_우리_명령에_묶는다() => Run(() =>
    {
        using var scene = new Scene();
        ICommand? Bound(Key key)
            => scene.Box.InputBindings.OfType<KeyBinding>().FirstOrDefault(b => b.Key == key && b.Modifiers == ModifierKeys.Control)?.Command;

        Assert.Same(RichFormat.IncreaseFontSize, Bound(Key.OemCloseBrackets));
        Assert.Same(RichFormat.DecreaseFontSize, Bound(Key.OemOpenBrackets));
        Assert.DoesNotContain(RichBodyBehavior.BlockedGestures, g => g.Key is Key.OemOpenBrackets or Key.OemCloseBrackets);
        Assert.NotNull(ShortcutCatalog.Reject("Ctrl+OemCloseBrackets"));            // 본문이 쓰므로 창 단축키로는 못 고른다
    });

    [Fact]
    public void 크기가_저장되고_서식_지우기로_기본_크기가_된다() => Run(() =>
    {
        using var vault = new TempVault();
        var store = TestKeys.Store(vault.Root);
        var path = Path.Combine(vault.Root, "a.tbx");
        Wait(store.CreateAsync(path));
        Wait(store.SaveAsync(path, DocumentBody.Plain("abcdef"), binding: null));
        var vm = new EditorViewModel(store, new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer());
        Wait(vm.LoadAsync(path));
        using (var scene = new Scene(vm))
        {
            Select(scene.Box, 0, 3);
            RichFormat.SetFontSize.Execute("32", scene.Box);
            Assert.True(vm.IsDirty);
            Assert.True(Wait(vm.TrySaveAsync()));
        }

        var again = new EditorViewModel(store, new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer());
        Wait(again.LoadAsync(path));
        using var reopened = new Scene(again);
        Assert.Equal(32, SizeOf(reopened.Box, 0));

        Select(reopened.Box, 0, 6);
        RichFormat.ClearFormatting.Execute(null, reopened.Box);
        Assert.Equal(RichFormat.DefaultSize, SizeOf(reopened.Box, 0));
    });
}
