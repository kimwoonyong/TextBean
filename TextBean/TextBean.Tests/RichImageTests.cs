using System.IO;
using System.IO.Packaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TextBean.Models;
using TextBean.ViewModels;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 서식 본문의 그림 (D-139 ~ D-146). 화면 밖 · 포커스 · 클립보드 없음 — 붙여넣기 · 손잡이는 이벤트를 직접 올린다.
/// 실행취소 · 폭 계산은 본문이 창에 붙어 있어야 해서 화면 밖 창에 띄운다.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class RichImageTests
{
    private static BitmapSource Picture(int width, int height)
        => BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                               Enumerable.Repeat((byte)180, width * height * 4).ToArray(), width * 4);

    private sealed class Scene : IDisposable
    {
        private readonly Window _window;

        public Scene(string text = "ab", double width = 600, object? dataContext = null)
        {
            Box = new RichTextBox { DataContext = dataContext };
            RichBodyBehavior.SetAttach(Box, true);
            EditorBehavior.SetInterceptCopy(Box, true);
            if (dataContext is null) Box.Document = new FlowDocument(new Paragraph(new Run(text)));
            _window = new Window
            {
                Left = -20000, Top = -20000, Width = width, Height = 400, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Content = Box
            };
            _window.Show();
            _window.UpdateLayout();
        }

        public RichTextBox Box { get; }

        public void Dispose() => _window.Close();
    }

    private static List<InlineUIContainer> Pictures(RichTextBox box)
    {
        var found = new List<InlineUIContainer>();
        void Walk(DependencyObject at)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(at).OfType<DependencyObject>())
            {
                if (child is InlineUIContainer container) found.Add(container);
                Walk(child);
            }
        }
        Walk(box.Document);
        return found;
    }

    private static Image ImageOf(InlineUIContainer container) => Assert.IsType<Image>(container.Child);

    private static DataObjectPastingEventArgs PastePicture(RichTextBox box, BitmapSource picture)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, picture);
        var args = new DataObjectPastingEventArgs(data, isDragDrop: false, DataFormats.Bitmap) { RoutedEvent = DataObject.PastingEvent };
        box.RaiseEvent(args);
        return args;
    }

    /// 그림을 고르고 그려질 때까지 기다린다 — 손잡이는 그림이 그려진 뒤 붙는다
    private static void Select(RichTextBox box, InlineUIContainer container)
    {
        box.Selection.Select(container.ElementStart, container.ElementEnd);
        Settle(box);
    }

    private static void Settle(RichTextBox box)
    {
        box.UpdateLayout();
        Pump(System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── 붙여넣기 판정 (D-144) ────────────────────────────────────────────────

    [Fact]
    public void 글자가_먼저고_글자_없이_그림만이면_그림이다() => Run(() =>
    {
        var picture = new DataObject();
        picture.SetData(DataFormats.Bitmap, Picture(10, 10));
        var excel = new DataObject();
        excel.SetText("A\tB");
        excel.SetData(DataFormats.Bitmap, Picture(10, 10));
        var file = new DataObject();
        file.SetData(DataFormats.FileDrop, new[] { @"C:\x.png" });

        Assert.Equal(PasteChoice.Image, PasteFilter.Choose(picture));
        Assert.Equal(PasteChoice.PlainText, PasteFilter.Choose(excel));
        Assert.Equal(PasteChoice.Reject, PasteFilter.Choose(file));
        Assert.Equal(PasteChoice.Rich, PasteFilter.Choose(ClipboardService.BuildDataObject(new ClipboardPayload(" ", null, [1], RichImage.ToPng(Picture(4, 4))))));
    });

    // ── 넣기 · 실행취소 (D-146) ──────────────────────────────────────────────

    [Fact]
    public void 그림을_붙이면_캐럿_자리에_들어가고_실행취소_다시실행에도_남는다() => Run(() =>
    {
        using var scene = new Scene();
        var box = scene.Box;
        box.CaretPosition = box.Document.ContentStart.GetPositionAtOffset(2)!;      // a 뒤

        var args = PastePicture(box, Picture(400, 200));

        Assert.True(args.CommandCancelled);                                   // 기본 붙여넣기(메모리 그림)에 맡기지 않았다
        var container = Assert.Single(Pictures(box));
        Assert.IsType<BitmapImage>(ImageOf(container).Source);
        Assert.Single(RichBodyBehavior.OwnedImages(box));
        Assert.Equal("ab", RichTextMap.Build(box.Document).Text);              // 그림은 검색용 글자를 내지 않는다

        box.Undo();
        Assert.Empty(Pictures(box));
        box.Redo();
        Assert.Equal(400, ((BitmapSource)ImageOf(Assert.Single(Pictures(box))).Source).PixelWidth);   // 빈 칸(Grid)이 아니다
    });

    /// 메모리 그림을 그대로 쓰면 지웠다 되돌릴 때 빈 칸이 되고, 그대로 자동 저장되면 그림이 사라진다 [실측]
    [Fact]
    public void 그림을_지웠다_되돌려도_그림이_남는다() => Run(() =>
    {
        using var scene = new Scene();
        PastePicture(scene.Box, Picture(300, 100));
        var container = Pictures(scene.Box)[0];

        Select(scene.Box, container);
        scene.Box.Selection.Text = "";
        Assert.Empty(Pictures(scene.Box));
        scene.Box.Undo();

        var back = Assert.Single(Pictures(scene.Box));
        Assert.Equal(300, ((BitmapSource)ImageOf(back).Source).PixelWidth);
    });

    [Fact]
    public void 긴_변이_2560을_넘을_때만_줄인다() => Run(() =>
    {
        var wide = RichImage.Limit(Picture(4000, 1000));
        Assert.Equal((2560, 640), (wide.PixelWidth, wide.PixelHeight));

        var tall = RichImage.Limit(Picture(1000, 3200));
        Assert.Equal((800, 2560), (tall.PixelWidth, tall.PixelHeight));

        var normal = Picture(2000, 1000);
        Assert.Same(normal, RichImage.Limit(normal));
    });

    [Fact]
    public void 처음_폭은_본문_폭을_넘지_않는다() => Run(() =>
    {
        using var scene = new Scene(width: 500);

        PastePicture(scene.Box, Picture(1600, 800));
        var image = ImageOf(Pictures(scene.Box)[0]);
        Assert.True(image.Width < 500 && image.Width <= RichImage.AvailableWidth(scene.Box));

        PastePicture(scene.Box, Picture(100, 50));
        Assert.Equal(100, ImageOf(Pictures(scene.Box)[1]).Width);           // 작은 그림은 원래 폭
    });

    [Fact]
    public void 표_칸_안에도_들어간다() => Run(() =>
    {
        using var scene = new Scene();
        var table = RichTable.Create(1, 2);
        scene.Box.Document.Blocks.Add(table);
        var cell = table.RowGroups[0].Rows[0].Cells[1];
        scene.Box.CaretPosition = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward);

        PastePicture(scene.Box, Picture(50, 50));

        Assert.Single(Pictures(scene.Box));
        Assert.Same(cell, RichTable.CellAt(scene.Box));
    });

    // ── 크기 조절 (D-142) ────────────────────────────────────────────────────

    /// 손잡이(0 왼쪽 위 · 1 오른쪽 위 · 2 왼쪽 아래 · 3 오른쪽 아래)를 끌고 놓는다. 놓으면 새 그림에 손잡이가 다시 붙을 때까지 기다린다.
    private static void Drag(RichTextBox box, int corner, double dx, double dy, bool cancel = false)
        => DragThrough(box, corner, [new Point(dx, dy)], cancel);

    /// <summary>
    /// 마우스를 시작점에서 points(시작점 기준 이동)로 차례로 옮기며 끈다 — 실제 마우스 대신 손잡이의 Pointer 를 바꿔 끼운다.
    /// 옮길 때마다 배치를 돌려 손잡이가 그림을 따라 움직이게 둔다(흔들림이 생기던 조건). 놓기 전 미리보기 폭들을 돌려준다.
    /// </summary>
    private static List<double> DragThrough(RichTextBox box, int corner, Point[] points, bool cancel = false)
    {
        var resizer = RichBodyBehavior.GetResizer(box)!;
        var handle = resizer.Handles[corner];
        var image = (Image)resizer.AdornedElement;
        var start = new Point(300, 200);
        var pointer = start;
        resizer.Pointer = () => pointer;
        var widths = new List<double>();

        handle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        foreach (var point in points)
        {
            pointer = new Point(start.X + point.X, start.Y + point.Y);
            handle.RaiseEvent(new DragDeltaEventArgs(0, 0) { RoutedEvent = Thumb.DragDeltaEvent });   // 손잡이 기준 이동량은 쓰지 않는다
            box.UpdateLayout();
            widths.Add(image.Width);
        }
        handle.RaiseEvent(new DragCompletedEventArgs(0, 0, cancel) { RoutedEvent = Thumb.DragCompletedEvent });
        Settle(box);
        return widths;
    }

    /// 손잡이 기준 이동량을 쓰면 그림이 커지며 손잡이가 따라 움직여 커졌다 줄었다 했다 [사용자 실기 — D-147]
    [Fact]
    public void 오른쪽으로만_끌면_폭이_흔들리지_않고_마우스를_따라_커진다() => Run(() =>
    {
        using var scene = new Scene();
        PastePicture(scene.Box, Picture(200, 100));
        Select(scene.Box, Pictures(scene.Box)[0]);

        var widths = DragThrough(scene.Box, 3, [.. Enumerable.Range(1, 6).Select(i => new Point(i * 10, 0))]);

        Assert.Equal([208, 216, 224, 232, 240, 248], widths.Select(w => Math.Round(w)));      // 대각선 투영: 200 × (1 + x·200/(200²+100²))
        Assert.Equal(248, ImageOf(Pictures(scene.Box)[0]).Width);

        Select(scene.Box, Pictures(scene.Box)[0]);
        var left = DragThrough(scene.Box, 0, [.. Enumerable.Range(1, 4).Select(i => new Point(-i * 10, 0))]);
        Assert.True(left.Zip(left.Skip(1)).All(p => p.Second > p.First), "왼쪽 손잡이를 왼쪽으로 끌면 계속 커져야 한다");
    });

    [Fact]
    public void 그림을_고르면_네_모서리_손잡이가_뜨고_끌면_비율대로_바뀌고_실행취소_한_번이다() => Run(() =>
    {
        using var scene = new Scene();
        var box = scene.Box;
        PastePicture(box, Picture(200, 100));
        Select(box, Pictures(box)[0]);

        var resizer = Assert.IsType<ImageResizeAdorner>(RichBodyBehavior.GetResizer(box));
        Assert.Equal(4, resizer.Handles.Count);

        Drag(box, 3, 50, 0);                                       // 오른쪽 아래를 오른쪽으로
        var image = ImageOf(Assert.Single(Pictures(box)));
        Assert.Equal(240, image.Width);                                        // 대각선 투영: 50·200/(200²+100²) = 0.2
        scene.Box.UpdateLayout();
        Assert.Equal(120, image.ActualHeight, 0.5);                            // 높이는 비율대로
        Assert.NotNull(RichBodyBehavior.GetResizer(box));                      // 새 그림에 손잡이가 다시 붙었다
        Snapshot(Window.GetWindow(box)!, "image-handles");                    // TEXTBEAN_TEST_PNG 가 있을 때만

        Drag(box, 0, -30, -30);          // 왼쪽 위를 바깥으로 = 커진다
        Assert.Equal(276, ImageOf(Pictures(box)[0]).Width);                   // (-30,-30)·(-240,-120) / (240²+120²) = 0.15

        box.Undo();
        Assert.Equal(240, ImageOf(Assert.Single(Pictures(box))).Width);
        Assert.IsType<BitmapImage>(ImageOf(Pictures(box)[0]).Source);
    });

    [Fact]
    public void 손잡이는_최소_폭과_본문_폭_안에서만_움직이고_취소하면_그대로다() => Run(() =>
    {
        using var scene = new Scene(width: 500);
        var box = scene.Box;
        PastePicture(box, Picture(200, 100));
        Select(box, Pictures(box)[0]);

        Drag(box, 3, -1000, 0);
        Assert.Equal(RichImage.MinWidth, ImageOf(Pictures(box)[0]).Width);

        Select(box, Pictures(box)[0]);
        Drag(box, 3, 5000, 0);
        Assert.Equal(Math.Round(RichImage.AvailableWidth(box)), ImageOf(Pictures(box)[0]).Width);

        var before = ImageOf(Pictures(box)[0]).Width;
        Select(box, Pictures(box)[0]);
        Drag(box, 3, -40, 0, cancel: true);
        Assert.Equal(before, ImageOf(Pictures(box)[0]).Width);
    });

    [Fact]
    public void 읽기_전용이거나_글자가_섞이면_손잡이가_없다() => Run(() =>
    {
        using var scene = new Scene();
        var box = scene.Box;
        PastePicture(box, Picture(100, 50));

        box.Selection.Select(box.Document.ContentStart, box.Document.ContentEnd);    // 글자 + 그림
        Assert.Null(RichBodyBehavior.GetResizer(box));
        Assert.Null(RichImage.SelectedImage(box));

        box.IsReadOnly = true;
        Select(box, Pictures(box)[0]);
        Assert.Null(RichBodyBehavior.GetResizer(box));
    });

    [Fact]
    public void 원래_크기와_그림_지우기는_그림을_골랐을_때만이다() => Run(() =>
    {
        using var scene = new Scene();
        var box = scene.Box;
        PastePicture(box, Picture(120, 60));
        var menu = new ContextMenu();
        var item = new MenuItem { Tag = RichImage.MenuTag };
        menu.Items.Add(item);

        box.CaretPosition = box.Document.ContentStart;
        RichImage.UpdateMenu(box, menu);
        Assert.Equal(Visibility.Collapsed, item.Visibility);
        Assert.False(RichImage.DeleteImage.CanExecute(null, box));

        Select(box, Pictures(box)[0]);
        RichImage.Resize(box, Pictures(box)[0], 60);
        RichImage.UpdateMenu(box, menu);
        Assert.Equal(Visibility.Visible, item.Visibility);

        RichImage.OriginalSize.Execute(null, box);
        Assert.Equal(120, ImageOf(Pictures(box)[0]).Width);

        RichImage.DeleteImage.Execute(null, box);
        Assert.Empty(Pictures(box));
    });

    // ── 파일에서 넣기 (D-140) ────────────────────────────────────────────────

    private static (EditorViewModel vm, FakeDialogs dialogs) Editor(TempVault vault)
    {
        var store = TestKeys.Store(vault.Root);
        var path = Path.Combine(vault.Root, "문서.tbx");
        Wait(store.CreateAsync(path));
        var dialogs = new FakeDialogs();
        var vm = new EditorViewModel(store, new FakeClipboard(), dialogs, new FakeAutoSaveTimer());
        Wait(vm.LoadAsync(path));
        return (vm, dialogs);
    }

    [Fact]
    public void 고른_그림_파일을_넣고_취소하면_아무것도_안_한다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, dialogs) = Editor(vault);
        using var scene = new Scene(dataContext: vm);
        var file = vault.WriteRaw("캡처.png", RichImage.ToPng(Picture(64, 32)));

        RichImage.InsertFromFile.Execute(null, scene.Box);                    // 취소(null)
        Assert.Equal(1, dialogs.PickImageFileCount);
        Assert.Empty(Pictures(scene.Box));

        dialogs.PickImageFileResult = file;
        RichImage.InsertFromFile.Execute(null, scene.Box);
        Assert.Equal(64, ((BitmapSource)ImageOf(Assert.Single(Pictures(scene.Box))).Source).PixelWidth);
        Assert.True(vm.IsDirty);
        Assert.True(File.Exists(file));                                        // 원본은 그대로
    });

    [Theory]
    [InlineData("메모.txt", "그림 파일")]
    [InlineData("깨진.png", "그림으로 열 수 없는")]
    [InlineData("큰.png", "20MB")]
    public void 그림이_아니거나_크면_알리고_넣지_않는다(string name, string expected) => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, dialogs) = Editor(vault);
        using var scene = new Scene(dataContext: vm);
        var content = name == "큰.png" ? new byte[ImageFileImport.MaxBytes + 1] : "그림 아님"u8.ToArray();
        dialogs.PickImageFileResult = vault.WriteRaw(name, content);

        RichImage.InsertFromFile.Execute(null, scene.Box);

        Assert.Empty(Pictures(scene.Box));
        Assert.Equal("그림 넣기", dialogs.LastErrorTitle);
        Assert.Contains(expected, dialogs.LastErrorMessage);
        Assert.DoesNotContain(name, dialogs.LastErrorMessage);                // 금고 밖 이름을 싣지 않는다
        Assert.DoesNotContain(vault.Root, dialogs.LastErrorMessage);
    });

    // ── 저장 · 닫기 ──────────────────────────────────────────────────────────

    [Fact]
    public void 그림과_폭이_저장되고_다시_열린다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, _) = Editor(vault);
        using (var scene = new Scene(dataContext: vm))
        {
            scene.Box.CaretPosition.InsertTextInRun("앞뒤");
            scene.Box.CaretPosition = scene.Box.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetPositionAtOffset(1)!;
            PastePicture(scene.Box, Picture(300, 150));
            RichImage.Resize(scene.Box, Pictures(scene.Box)[0], 120);
            Assert.True(Wait(vm.TrySaveAsync()));
        }

        var again = new EditorViewModel(TestKeys.Store(vault.Root), new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer());
        Wait(again.LoadAsync(Path.Combine(vault.Root, "문서.tbx")));
        using var reopened = new Scene(dataContext: again);

        var image = ImageOf(Assert.Single(Pictures(reopened.Box)));
        Assert.Equal(120, image.Width);
        Assert.Equal(300, ((BitmapSource)image.Source).PixelWidth);
        Assert.Equal("앞뒤", again.Text);
    });

    [Fact]
    public void 탭을_닫으면_올린_그림_원본을_내린다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, _) = Editor(vault);
        using var scene = new Scene(dataContext: vm);
        PastePicture(scene.Box, Picture(10, 10));
        var uri = Assert.Single(RichBodyBehavior.OwnedImages(scene.Box));
        Assert.NotNull(PackageStore.GetPackage(uri));

        vm.Dispose();

        Assert.Empty(RichBodyBehavior.OwnedImages(scene.Box));
        Assert.Null(PackageStore.GetPackage(uri));
    });

    // ── 복사 · 30초 비움 (D-143) ─────────────────────────────────────────────

    [Fact]
    public void 그림_하나만_복사하면_밖의_프로그램용_그림도_싣는다() => Run(() =>
    {
        using var scene = new Scene();
        PastePicture(scene.Box, Picture(40, 20));
        var container = Pictures(scene.Box)[0];

        var only = RichBodyBehavior.PayloadOf(new TextRange(container.ElementStart, container.ElementEnd));
        var mixed = RichBodyBehavior.PayloadOf(new TextRange(scene.Box.Document.ContentStart, scene.Box.Document.ContentEnd));
        Assert.NotNull(only.Png);
        Assert.Null(mixed.Png);                                                // 섞이면 Rtf · XamlPackage 가 싣는다

        var data = ClipboardService.BuildDataObject(only, "id-1");
        Assert.Equal(40, Assert.IsAssignableFrom<BitmapSource>(data.GetData(DataFormats.Bitmap)).PixelWidth);
        Assert.True(data.GetDataPresent("PNG"));
        Assert.Equal(false, data.GetData("CanIncludeInClipboardHistory"));
        Assert.Equal(false, data.GetData("CanUploadToCloudClipboard"));
        Assert.Equal(false, data.GetData("ExcludeClipboardContentFromMonitorProcessing"));
        Assert.Equal("id-1", data.GetData(ClipboardService.CopyIdFormat));
    });

    /// 그림만 복사하면 글자가 공백 한 칸이다 — 글자로 비교하면 남이 복사한 공백과 가를 수 없다 [실측]
    [Fact]
    public void 삼십초_비움은_복사_표지가_같을_때만_지운다() => Run(() =>
    {
        var ours = ClipboardService.BuildDataObject(new ClipboardPayload(" "), "id-1");
        var someoneElse = new DataObject();
        someoneElse.SetText(" ");

        Assert.True(ClipboardService.IsOurs(ours, "id-1"));
        Assert.False(ClipboardService.IsOurs(ours, "id-2"));                  // 우리가 그 뒤에 또 복사했다
        Assert.False(ClipboardService.IsOurs(someoneElse, "id-1"));           // 같은 글자라도 남의 것
        Assert.False(ClipboardService.IsOurs(null, "id-1"));
    });
}
