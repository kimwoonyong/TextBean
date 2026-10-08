using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 표 열 너비 끌기 · 표 전체 줄이기 · 칸 안 정렬 (D-170 ~ D-172). 화면 밖 · 실제 마우스 없음 — 끌기는 좌표로 직접 부른다(D-082).
/// 실제 마우스에서 커서가 ↔ 로 보이는지는 실기 확인 항목이다.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class TableLayoutTests
{
    /// 창 안에 띄운 본문 — 실행취소 · 글자 위치(GetCharacterRect)는 창 안에서만 된다 [실측]
    private static RichTextBox Host(params Block[] blocks)
    {
        var box = new RichTextBox { Width = 600, Height = 320, FontSize = 13, BorderThickness = new Thickness(0), Padding = new Thickness(10) };
        RichBodyBehavior.SetAttach(box, true);
        var document = new FlowDocument();
        document.Blocks.AddRange(blocks);
        document.Blocks.Add(new Paragraph());
        box.Document = document;
        var window = new Window { Left = -20000, Top = -20000, SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false, ShowInTaskbar = false, Content = box };
        window.Show();
        box.UpdateLayout();
        Pump();
        return box;
    }

    private static void Close(RichTextBox box) => Window.GetWindow(box)?.Close();

    private static Table Filled(int rows, int columns)
    {
        var table = RichTable.Create(rows, columns);
        var all = table.RowGroups[0].Rows;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                ((Paragraph)all[r].Cells[c].Blocks.FirstBlock).Inlines.Add(new Run($"r{r}c{c}"));
        return table;
    }

    private static Table TableIn(RichTextBox box) => box.Document.Blocks.OfType<Table>().Single();

    private static double[] Widths(Table table) => [.. table.Columns.Select(c => c.Width.IsAbsolute ? c.Width.Value : -1)];

    private static double ContentX(TableCell cell)
        => cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward).X;

    private static Point Middle(TableColumnResizer.Edge edge) => new(edge.X, (edge.Top + edge.Bottom) / 2);

    private static string Text(Table table)
        => string.Join("|", table.RowGroups[0].Rows.SelectMany(r => r.Cells).Select(c => new TextRange(c.ContentStart, c.ContentEnd).Text.Trim()));

    // ── 폭 계산 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 열_경계는_그_열만_바꾸고_24px_와_합계_상한을_지킨다()
    {
        double[] start = [100, 150, 80];

        Assert.Equal([140, 150, 80], TableColumnResizer.Resized(start, 0, 40, 600));
        Assert.Equal([100, 110, 80], TableColumnResizer.Resized(start, 1, -40, 600));
        Assert.Equal([24, 150, 80], TableColumnResizer.Resized(start, 0, -500, 600));          // 최소 24px
        Assert.Equal([370, 150, 80], TableColumnResizer.Resized(start, 0, 1000, 600));         // 합계 600 상한
        Assert.Equal([100.0, 150.0, 80.0], TableColumnResizer.Resized(start, 0, 0.3, 600));    // 정수 px
    }

    [Fact]
    public void 바깥선은_모든_열을_같은_비율로_바꾸고_넘친_표는_줄이기만_된다()
    {
        double[] start = [100, 200, 100];

        Assert.Equal([50, 100, 50], TableColumnResizer.Resized(start, 2, -200, 600));
        Assert.Equal([150, 300, 150], TableColumnResizer.Resized(start, 2, 1000, 600));        // 합계 600 상한
        Assert.Equal([24, 36, 24], TableColumnResizer.Resized(start, 2, -1000, 600));       // 합계 72(24×3)까지 비율로 · 열마다 24px 이상

        // 창을 줄여 이미 상한(300)보다 넓은 표 — 넓히지는 못하고 줄일 수는 있다
        Assert.Equal(400, TableColumnResizer.Resized(start, 2, 100, 300).Sum());
        Assert.Equal(300, TableColumnResizer.Resized(start, 2, -100, 300).Sum());
        Assert.Equal([100, 200, 100], TableColumnResizer.Resized(start, 0, 50, 300));
    }

    // ── 경계 찾기 ───────────────────────────────────────────────────────────

    [Fact]
    public void 경계는_실제_칸_위치에_있고_3px_안에서만_잡힌다() => Run(() =>
    {
        var table = Filled(2, 3);
        table.Columns[0].Width = new GridLength(100);
        table.Columns[1].Width = new GridLength(150);
        table.Columns[2].Width = new GridLength(80);
        var box = Host(table);
        var cells = table.RowGroups[0].Rows[0].Cells;

        var edges = TableColumnResizer.Edges(box);
        Assert.Equal(3, edges.Count);
        Assert.InRange(edges[0].X - (ContentX(cells[1]) - cells[1].Padding.Left), -1, 1);     // 경계 = 다음 칸 글자 − 여백 [실측 — R-4]
        Assert.InRange(edges[1].X - (ContentX(cells[2]) - cells[2].Padding.Left), -1, 1);
        Assert.Equal(80, edges[2].X - edges[1].X, 0.5);                                          // 바깥선 = 마지막 열 폭만큼 오른쪽
        Assert.True(edges[2].IsOuter);

        Assert.Same(edges[0].Table, TableColumnResizer.HitEdge(box, Middle(edges[0]) with { X = edges[0].X + 3 })?.Table);
        Assert.Null(TableColumnResizer.HitEdge(box, Middle(edges[0]) with { X = edges[0].X + 6 }));
        Assert.Null(TableColumnResizer.HitEdge(box, new Point(edges[0].X, edges[0].Bottom + 10)));   // 표 아래
        box.IsReadOnly = true;
        Assert.Null(TableColumnResizer.HitEdge(box, Middle(edges[0])));                            // 읽기 전용이면 안 된다
        Close(box);
    });

    [Fact]
    public void 같은_폭_나눔_표의_경계는_본문_폭을_똑같이_나눈다() => Run(() =>
    {
        var table = Filled(1, 3);
        var box = Host(table);
        var cells = table.RowGroups[0].Rows[0].Cells;

        var widths = TableColumnResizer.ColumnWidths(box, table);
        Assert.Equal(TableColumnResizer.LayoutWidth(box), widths.Sum(), 0.5);
        Assert.InRange(TableColumnResizer.Edges(box)[0].X - (ContentX(cells[1]) - cells[1].Padding.Left), -1, 1);
        Close(box);
    });

    // ── 끌기 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 끌면_그_열만_바뀌고_놓으면_실행취소_한_번에_원래대로() => Run(() =>
    {
        var box = Host(new Paragraph(new Run("앞")), Filled(2, 3));
        var table = TableIn(box);
        var before = TableColumnResizer.ColumnWidths(box, table);
        box.CaretPosition = table.RowGroups[0].Rows[1].Cells[2].ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var edge = TableColumnResizer.Edges(box)[0];
        var changes = 0;
        box.TextChanged += (_, _) => changes++;

        Assert.True(TableColumnResizer.TryBegin(box, Middle(edge)));
        Assert.True(TableColumnResizer.IsDragging(box));
        TableColumnResizer.MoveTo(box, Middle(edge) with { X = edge.X - 40 });
        TableColumnResizer.End(box);
        Pump();

        var after = TableIn(box);
        Assert.NotSame(table, after);                                          // 새 폭을 단 복제 표로 갈아 끼웠다
        Assert.Equal([Math.Round(before[0]) - 40, Math.Round(before[1]), Math.Round(before[2])], Widths(after));
        Assert.Equal(Text(table), Text(after));
        Assert.True(changes > 0);                                              // 「고쳐짐」이 켜진다
        Assert.Same(after.RowGroups[0].Rows[1].Cells[2], RichTable.CellAt(box));   // 커서는 같은 칸
        Assert.False(TableColumnResizer.IsDragging(box));

        box.Undo();
        Pump();
        Assert.All(TableIn(box).Columns, c => Assert.True(c.Width.IsStar));    // 한 번에 원래(같은 폭 나눔)대로
        Assert.Equal(Text(table), Text(TableIn(box)));
        Close(box);
    });

    [Fact]
    public void 바깥선을_끌면_비율로_줄고_저장해도_남는다() => Run(() =>
    {
        var box = Host(Filled(1, 2));
        var outer = TableColumnResizer.Edges(box).Single(e => e.IsOuter);
        var total = TableColumnResizer.ColumnWidths(box, TableIn(box)).Sum();

        TableColumnResizer.TryBegin(box, Middle(outer));
        TableColumnResizer.MoveTo(box, Middle(outer) with { X = outer.X - total / 2 });
        TableColumnResizer.End(box);
        Pump();

        var widths = Widths(TableIn(box));
        Assert.Equal(widths[0], widths[1], 1);                                 // 같은 비율
        Assert.InRange(widths.Sum(), total / 2 - 2, total / 2 + 2);

        var saved = RichTextMap.Save(new TextRange(box.Document.ContentStart, box.Document.ContentEnd));
        var reopened = RichTextMap.Load(saved);
        var table = reopened.Blocks.OfType<Section>().SelectMany(s => s.Blocks).Concat(reopened.Blocks).OfType<Table>().Single();
        Assert.Equal(widths, Widths(table));
        Close(box);
    });

    [Fact]
    public void 넓히기는_본문_폭까지만이다() => Run(() =>
    {
        var table = Filled(1, 2);
        table.Columns[0].Width = new GridLength(100);
        table.Columns[1].Width = new GridLength(100);
        var box = Host(table);
        var edge = TableColumnResizer.Edges(box)[0];

        TableColumnResizer.TryBegin(box, Middle(edge));
        TableColumnResizer.MoveTo(box, Middle(edge) with { X = edge.X + 2000 });
        TableColumnResizer.End(box);
        Pump();

        var widths = Widths(TableIn(box));
        Assert.Equal(Math.Round(TableColumnResizer.LayoutWidth(box)), widths.Sum(), 1);   // 합계는 본문 폭에서 멈춘다 — 넘으면 오른쪽이 잘린다 [실측]
        Assert.Equal(100, widths[1]);                                                    // 다른 열은 그대로
        Close(box);
    });

    [Fact]
    public void 움직이지_않고_놓으면_아무것도_안_바뀐다() => Run(() =>
    {
        var box = Host(Filled(1, 2));
        var table = TableIn(box);
        var edge = TableColumnResizer.Edges(box)[0];

        TableColumnResizer.TryBegin(box, Middle(edge));
        TableColumnResizer.End(box);
        Pump();

        Assert.Same(table, TableIn(box));
        Assert.All(table.Columns, c => Assert.True(c.Width.IsStar));
        Assert.False(box.CanUndo);
        Close(box);
    });

    [Fact]
    public void 끄는_사이_표가_빠지면_버린다() => Run(() =>
    {
        var box = Host(Filled(1, 2));
        var table = TableIn(box);
        var old = box.Document;
        var edge = TableColumnResizer.Edges(box)[0];

        TableColumnResizer.TryBegin(box, Middle(edge));
        TableColumnResizer.MoveTo(box, Middle(edge) with { X = edge.X - 50 });
        box.Document = new FlowDocument(new Paragraph(new Run("다시 연 문서")));   // 다시 열기 · 테마 바꾸기
        TableColumnResizer.End(box);
        Pump();

        Assert.Empty(box.Document.Blocks.OfType<Table>());
        Assert.Same(table, old.Blocks.OfType<Table>().Single());              // 버린 문서 안에서도 갈아 끼우지 않았다
        Assert.All(table.Columns, c => Assert.True(c.Width.IsStar));           // 빠진 표도 원래 폭으로 되돌렸다
        Close(box);
    });

    [Fact]
    public void 칸_안_그림이_있어도_끌고_나면_그림이_남는다() => Run(() =>
    {
        var box = Host(Filled(1, 2));
        var cell = TableIn(box).RowGroups[0].Rows[0].Cells[0];
        var pixels = BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgra32, null, new byte[40 * 20 * 4], 160);
        box.CaretPosition = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        RichImage.Insert(box, RichImage.Prepare(pixels, RichBodyBehavior.OwnedImages(box)));
        Pump();
        var edge = TableColumnResizer.Edges(box)[0];

        TableColumnResizer.TryBegin(box, Middle(edge));
        TableColumnResizer.MoveTo(box, Middle(edge) with { X = edge.X - 30 });
        TableColumnResizer.End(box);
        box.UpdateLayout();
        Pump();

        var image = TableIn(box).RowGroups[0].Rows[0].Cells[0].Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines)
                                .OfType<InlineUIContainer>().Select(c => c.Child).OfType<Image>().Single();
        Assert.Equal(40, ((BitmapSource)image.Source).PixelWidth);
        Close(box);
    });

    // ── 칸 안 정렬 ──────────────────────────────────────────────────────────

    [Fact]
    public void 고른_칸들을_가운데로_정렬하고_실행취소_한_번에_돌아온다() => Run(() =>
    {
        var box = Host(Filled(2, 3));
        var rows = TableIn(box).RowGroups[0].Rows;
        // 글자 자리로 고른다(RichTableTests.SelectCells 와 같다) — 칸 경계로 고르면 WPF 가 끝을 행 끝으로 넓힌다 (D-138)
        box.Selection.Select(rows[0].Cells[0].ContentStart.GetInsertionPosition(LogicalDirection.Forward),
                             rows[1].Cells[1].ContentEnd.GetInsertionPosition(LogicalDirection.Backward));

        Assert.True(RichTable.AlignCells.CanExecute("Center", box));
        RichTable.AlignCells.Execute("Center", box);

        TextAlignment Of(int r, int c) => ((Paragraph)rows[r].Cells[c].Blocks.FirstBlock).TextAlignment;
        Assert.Equal(TextAlignment.Center, Of(0, 0));
        Assert.Equal(TextAlignment.Center, Of(1, 1));
        Assert.Equal(TextAlignment.Center, Of(0, 1));
        Assert.NotEqual(TextAlignment.Center, Of(0, 2));                       // 직사각형 밖
        Assert.NotEqual(TextAlignment.Center, Of(1, 2));

        box.Undo();
        Assert.NotEqual(TextAlignment.Center, Of(0, 0));
        Assert.NotEqual(TextAlignment.Center, Of(1, 1));

        RichTable.AlignCells.Execute("Right", box);
        var saved = RichTextMap.Save(new TextRange(box.Document.ContentStart, box.Document.ContentEnd));
        var reopened = RichTextMap.Load(saved);
        var table = reopened.Blocks.OfType<Section>().SelectMany(s => s.Blocks).Concat(reopened.Blocks).OfType<Table>().Single();
        Assert.Equal(TextAlignment.Right, ((Paragraph)table.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock).TextAlignment);
        Close(box);
    });

    /// 기존 D-138 결함(이번에 발견) — 왼쪽 위에서 오른쪽 아래로 고르면 WPF 가 끝을 다음 칸 시작에 두어 오른쪽 열이 하나 더 잡혔다
    [Theory]
    [InlineData(0, 0, 1, 1, "r0c2")]        // 가운데에서 끝남 → 0 · 1 열만 지워진다 (예전엔 표 전체가 지워졌다 [실측])
    [InlineData(0, 1, 1, 2, "r0c0")]        // 표 마지막 칸에서 끝남
    public void 정방향으로_고른_칸들의_열만_지운다(int r1, int c1, int r2, int c2, string remaining) => Run(() =>
    {
        var box = Host(Filled(2, 3));
        var rows = TableIn(box).RowGroups[0].Rows;
        box.Selection.Select(rows[r1].Cells[c1].ContentStart.GetInsertionPosition(LogicalDirection.Forward),
                             rows[r2].Cells[c2].ContentEnd.GetInsertionPosition(LogicalDirection.Backward));

        RichTable.DeleteColumn.Execute(null, box);

        var left = Assert.Single(box.Document.Blocks.OfType<Table>());
        Assert.Single(left.Columns);
        Assert.Equal(remaining, new TextRange(left.RowGroups[0].Rows[0].Cells[0].ContentStart, left.RowGroups[0].Rows[0].Cells[0].ContentEnd).Text.Trim());
        Close(box);
    });

    [Fact]
    public void 정렬은_표_안에서만_되고_읽기_전용이면_안_된다() => Run(() =>
    {
        var box = Host(new Paragraph(new Run("표 밖")), Filled(1, 2));
        box.CaretPosition = box.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        Assert.False(RichTable.AlignCells.CanExecute("Center", box));

        box.CaretPosition = TableIn(box).RowGroups[0].Rows[0].Cells[0].ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        Assert.True(RichTable.AlignCells.CanExecute("Center", box));
        Assert.False(RichTable.AlignCells.CanExecute("Justify", box));
        box.IsReadOnly = true;
        Assert.False(RichTable.AlignCells.CanExecute("Center", box));
        Close(box);
    });
}
