using System.IO;
using System.IO.Packaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TextBean.ViewModels;

namespace TextBean.Views.Platform;

/// <summary>
/// 서식 본문의 그림 (D-139 ~ D-146). 그림은 글자 줄 안의 인라인 그림(InlineUIContainer + Image)이다 — 표 칸 · 글자 옆에도 둔다.
/// <para>
/// <b>그림 원본은 메모리 패키지 주소로 등록한다 (D-146).</b> 메모리 그림(BitmapSource)을 그대로 쓰면 RichTextBox 실행취소가
/// 그림을 담지 못해 지웠다 되돌리면 빈 칸(Grid)이 된다 — 그 상태로 자동 저장되면 그림이 사라진다 [실측].
/// PackageStore 에 올린 pack 주소 그림은 실행취소 · 다시 실행을 거쳐도 남는다 [실측]. 디스크에는 쓰지 않는다.
/// 등록한 주소는 문서를 다시 불러오거나 탭을 닫을 때 내린다(Release).
/// </para>
/// 폭만 바꾸면 실행취소에도, 변경 알림(수정됨)에도 잡히지 않는다 [실측] — 크기 바꾸기는 같은 그림 · 새 폭으로 갈아 끼운다.
/// </summary>
public static class RichImage
{
    /// 긴 변이 이보다 크면 줄인다 (D-141). 캡처는 줄이면 PNG 가 오히려 커져 아주 큰 그림만 줄인다 [실측].
    public const int MaxSide = 2560;
    public const double MinWidth = 32;

    public static readonly RoutedUICommand InsertFromFile = new("그림 넣기", nameof(InsertFromFile), typeof(RichImage));
    public static readonly RoutedUICommand OriginalSize = new("원래 크기", nameof(OriginalSize), typeof(RichImage));
    public static readonly RoutedUICommand DeleteImage = new("그림 지우기", nameof(DeleteImage), typeof(RichImage));

    /// 우클릭 메뉴에서 그림을 골랐을 때만 보이는 항목의 Tag.
    public const string MenuTag = "image";

    private const string Scheme = "textbean-img";
    private static readonly Uri PartUri = new("/image.png", UriKind.Relative);

    public static void Attach(RichTextBox box)
    {
        box.CommandBindings.Add(new CommandBinding(InsertFromFile, (_, _) => InsertFile(box), (_, e) => e.CanExecute = !box.IsReadOnly));
        box.CommandBindings.Add(new CommandBinding(OriginalSize,
            (_, _) => { if (SelectedImage(box) is { } c) Resize(box, c, FitWidth(box, ((Image)c.Child).Source)); },
            (_, e) => e.CanExecute = !box.IsReadOnly && SelectedImage(box) is not null));
        box.CommandBindings.Add(new CommandBinding(DeleteImage,
            (_, _) => { if (SelectedImage(box) is { } c) Edit(box, () => c.SiblingInlines!.Remove(c)); },
            (_, e) => e.CanExecute = !box.IsReadOnly && SelectedImage(box) is not null));
    }

    private static void Edit(RichTextBox box, Action change)
    {
        box.BeginChange();
        try { change(); }
        finally { box.EndChange(); }
    }

    // ── 원본 준비 ────────────────────────────────────────────────────────────

    /// 긴 변이 MaxSide 를 넘으면 비율대로 줄인다.
    public static BitmapSource Limit(BitmapSource source)
    {
        var longSide = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longSide <= MaxSide) return source;

        var scale = (double)MaxSide / longSide;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    public static byte[] ToPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// 줄이고 PNG 로 만들어 메모리 패키지에 올린 그림. 올린 주소는 owned 에 남긴다 — 내릴 때 쓴다.
    public static BitmapImage Prepare(BitmapSource raw, ICollection<Uri> owned) => Register(ToPng(Limit(raw)), owned);

    private static BitmapImage Register(byte[] png, ICollection<Uri> owned)
    {
        var packageUri = new Uri($"{Scheme}://{Guid.NewGuid():N}");
        var package = Package.Open(new MemoryStream(), FileMode.Create, FileAccess.ReadWrite);
        using (var part = package.CreatePart(PartUri, "image/png").GetStream()) part.Write(png);
        PackageStore.AddPackage(packageUri, package);
        owned.Add(packageUri);

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = PackUriHelper.Create(packageUri, PartUri);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// 올린 그림 원본을 내린다. 문서를 새로 불러오면 실행취소 기록도 비워지므로 그때 · 탭을 닫을 때 부른다.
    public static void Release(ICollection<Uri> owned)
    {
        foreach (var uri in owned)
        {
            var package = PackageStore.GetPackage(uri);
            PackageStore.RemovePackage(uri);
            package?.Close();
        }
        owned.Clear();
    }

    // ── 넣기 · 크기 · 지우기 ─────────────────────────────────────────────────

    /// 본문에서 그림이 쓸 수 있는 폭. 아직 배치 전(숨은 탭 · 시험)이면 제한 없음.
    public static double AvailableWidth(RichTextBox box)
    {
        static double Side(double value) => double.IsNaN(value) ? 0 : value;
        var page = box.Document.PagePadding;
        var width = box.ActualWidth - box.Padding.Left - box.Padding.Right - box.BorderThickness.Left - box.BorderThickness.Right
                    - Side(page.Left) - Side(page.Right) - SystemParameters.VerticalScrollBarWidth;
        return width > MinWidth ? width : double.PositiveInfinity;
    }

    /// 처음 넣는 폭 · 「원래 크기」 = 원래 폭과 본문 폭 중 작은 쪽 (D-145).
    private static double FitWidth(RichTextBox box, ImageSource source) => Math.Round(Math.Min(source.Width, AvailableWidth(box)));

    private static Image NewImage(ImageSource source, double width) => new() { Source = source, Stretch = Stretch.Uniform, Width = width };

    /// 캐럿 자리에 넣는다. 고른 것이 있으면 그 자리를 갈아 끼운다. 실행취소 한 번.
    public static InlineUIContainer Insert(RichTextBox box, BitmapImage source)
    {
        InlineUIContainer? added = null;
        Edit(box, () =>
        {
            if (!box.Selection.IsEmpty) box.Selection.Text = "";
            added = new InlineUIContainer(NewImage(source, FitWidth(box, source)), box.Selection.Start.GetInsertionPosition(LogicalDirection.Forward));
            box.CaretPosition = added.ElementEnd;
        });
        return added!;
    }

    /// 같은 그림 · 새 폭으로 갈아 끼우고 고른다. 폭은 [MinWidth, 본문 폭] 으로 자른다. 높이는 비율대로 따라온다(Stretch=Uniform).
    public static InlineUIContainer Resize(RichTextBox box, InlineUIContainer container, double width)
    {
        var source = ((Image)container.Child).Source;
        width = Math.Round(Math.Clamp(width, MinWidth, Math.Max(MinWidth, AvailableWidth(box))));

        InlineUIContainer? fresh = null;
        Edit(box, () =>
        {
            var at = container.ElementStart;
            container.SiblingInlines!.Remove(container);
            fresh = new InlineUIContainer(NewImage(source, width), at);
            box.Selection.Select(fresh.ElementStart, fresh.ElementEnd);
        });
        return fresh!;
    }

    private static void InsertFile(RichTextBox box)
    {
        if (box.DataContext is not EditorViewModel vm || vm.PickImageFile() is not { } path) return;

        BitmapSource raw;
        try
        {
            raw = ImageFileImport.Load(path);
        }
        catch (ImageImportException ex)
        {
            vm.ImageInsertFailed(ex.Message, ex.InnerException);
            return;
        }

        Insert(box, Prepare(raw, RichBodyBehavior.OwnedImages(box)));
    }

    // ── 고른 그림 ────────────────────────────────────────────────────────────

    /// 선택이 그림 하나뿐이면 그 그림. 글자가 섞였거나 그림이 둘 이상이면 null.
    public static InlineUIContainer? SelectedImage(RichTextBox box)
        => box.Selection.IsEmpty ? null : ImageIn(box.Selection.Start, box.Selection.End);

    public static InlineUIContainer? ImageIn(TextPointer start, TextPointer end)
    {
        InlineUIContainer? found = null;
        for (var at = start; at is not null && at.CompareTo(end) < 0; at = at.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (at.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    return null;
                case TextPointerContext.ElementStart when at.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer container:
                    if (found is not null) return null;
                    found = container;
                    break;
            }
        }
        return found?.Child is Image { Source: BitmapSource } ? found : null;
    }

    /// 복사할 범위가 그림 하나뿐이면 그 그림 PNG — 밖의 프로그램용 (D-143).
    public static byte[]? PngOf(TextRange range)
        => ImageIn(range.Start, range.End) is { Child: Image { Source: BitmapSource source } } ? ToPng(source) : null;

    /// 우클릭 메뉴를 열 때 — 그림 항목은 그림 하나를 골랐을 때만.
    public static void UpdateMenu(RichTextBox box, ContextMenu menu)
    {
        var picked = !box.IsReadOnly && SelectedImage(box) is not null;
        foreach (var item in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, MenuTag)))
            item.Visibility = picked ? Visibility.Visible : Visibility.Collapsed;
    }
}
