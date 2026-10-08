using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 칸 합치기 · 풀기와 합친 칸이 있는 표의 행 · 열 동작 (D-174 ~ D-177). 화면 밖 · 실행취소는 창 안에서만 된다 [실측].
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class TableMergeTests
{
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
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                ((Paragraph)table.RowGroups[0].Rows[r].Cells[c].Blocks.FirstBlock).Inlines.Add(new Run($"r{r}c{c}"));
        return table;
    }

    private static Table TableIn(RichTextBox box) => box.Document.Blocks.OfType<Table>().Single();

    private static TableGrid Grid(RichTextBox box) => TableGrid.Build(TableIn(box));

    private static TableCell At(RichTextBox box, int row, int column) => Grid(box).At(row, column)!;

    private static void CaretIn(RichTextBox box, TableCell cell) => box.CaretPosition = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward);

    /// 글자 자리로 고른다 — 칸 경계로 고르면 WPF 가 끝을 넓힌다 (D-138)
    private static void Select(RichTextBox box, TableCell from, TableCell to)
        => box.Selection.Select(from.ContentStart.GetInsertionPosition(LogicalDirection.Forward), to.ContentEnd.GetInsertionPosition(LogicalDirection.Backward));

    private static void Merge(RichTextBox box, int r1, int c1, int r2, int c2)
    {
        Select(box, At(box, r1, c1), At(box, r2, c2));
        Assert.True(RichTable.MergeCells.CanExecute(null, box));
        RichTable.MergeCells.Execute(null, box);
    }

    private static string[] Paragraphs(TableCell cell)
        => [.. cell.Blocks.OfType<Paragraph>().Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text)];

    private static string Text(TableCell cell) => new TextRange(cell.ContentStart, cell.ContentEnd).Text.Trim();

    /// 지도 모양: 행마다 칸 글자(합친 칸은 덮은 자리마다 같은 글자)
    private static string Shape(RichTextBox box)
    {
        var grid = Grid(box);
        return string.Join(" / ", Enumerable.Range(0, grid.RowCount).Select(r =>
            string.Join(",", Enumerable.Range(0, grid.ColumnCount).Select(c => grid.At(r, c) is { } cell ? Text(cell).Replace("\r\n", "+") : "∅"))));
    }

    // ── 칸 지도 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 칸_지도는_합친_칸을_덮은_자리마다_같은_칸으로_본다() => Run(() =>
    {
        var table = Filled(3, 3);
        var rows = table.RowGroups[0].Rows;
        rows[0].Cells[0].ColumnSpan = 2; rows[0].Cells.RemoveAt(1);
        rows[1].Cells[2].RowSpan = 2; rows[2].Cells.RemoveAt(2);

        var grid = TableGrid.Build(table);

        Assert.Equal(3, grid.ColumnCount);
        Assert.True(grid.HasMerged);
        Assert.Same(grid.At(0, 0), grid.At(0, 1));
        Assert.Same(grid.At(1, 2), grid.At(2, 2));
        Assert.Equal((2, 1), grid.Origin(rows[2].Cells[1]));
        Assert.Equal((0, 1), grid.Corner(rows[0].Cells[0]));
        Assert.Equal(2, grid.InsertIndex(2, 2));                              // 2행에서 2열보다 왼쪽에서 시작하는 칸 = r2c0 · r2c1
        var area = grid.Span(rows[1].Cells[1], rows[1].Cells[2]);
        Assert.Equal(new TableGrid.Area(1, 1, 2, 2), area);                   // 세로 합친 칸을 걸치면 그 칸 전체로 넓힌다
    });

    /// 맨 왼쪽 열을 세로로 합치면 아래 행의 첫 칸은 1열로 간다 — 위에서 내려온 칸이 덮은 자리를 건너뛴다
    [Fact]
    public void 칸_지도는_위에서_내려온_칸이_덮은_자리를_건너뛴다() => Run(() =>
    {
        var table = Filled(2, 2);
        var rows = table.RowGroups[0].Rows;
        rows[0].Cells[0].RowSpan = 2; rows[1].Cells.RemoveAt(0);

        var grid = TableGrid.Build(table);

        Assert.Same(rows[0].Cells[0], grid.At(1, 0));
        Assert.Same(rows[1].Cells[0], grid.At(1, 1));
        Assert.Equal((1, 1), grid.Origin(rows[1].Cells[0]));
    });

    /// 직사각형 안에 걸친 합친 칸이 밖으로 삐져나오면 그 칸 전체가 들어가도록 넓힌다
    [Fact]
    public void 고른_직사각형은_삐져나온_합친_칸까지_넓힌다() => Run(() =>
    {
        var table = Filled(3, 3);
        var rows = table.RowGroups[0].Rows;
        rows[1].Cells[0].ColumnSpan = 2; rows[1].Cells.RemoveAt(1);           // (1,0)~(1,1)

        var grid = TableGrid.Build(table);

        Assert.Equal(new TableGrid.Area(0, 0, 2, 1), grid.Span(rows[0].Cells[0], rows[2].Cells[0]));
    });

    // ── 합치기 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 가로로_합치면_글자를_이어_붙이고_실행취소_한_번에_돌아온다() => Run(() =>
    {
        var box = Host(Filled(2, 3));

        Merge(box, 0, 0, 0, 1);

        var merged = At(box, 0, 0);
        Assert.Equal(2, merged.ColumnSpan);
        Assert.Equal(["r0c0", "r0c1"], Paragraphs(merged));                    // 읽는 순서대로 이어 붙임(사용자 판정)
        Assert.Equal("r0c0+r0c1,r0c0+r0c1,r0c2 / r1c0,r1c1,r1c2", Shape(box));
        Assert.Same(merged, RichTable.CellAt(box));

        box.Undo();
        Assert.Equal("r0c0,r0c1,r0c2 / r1c0,r1c1,r1c2", Shape(box));
        Close(box);
    });

    [Fact]
    public void 사각형으로_합치고_저장해도_남는다() => Run(() =>
    {
        var box = Host(Filled(3, 3));

        Merge(box, 0, 0, 1, 1);

        var merged = At(box, 0, 0);
        Assert.Equal((2, 2), (merged.RowSpan, merged.ColumnSpan));
        Assert.Equal(["r0c0", "r0c1", "r1c0", "r1c1"], Paragraphs(merged));
        Assert.Single(TableIn(box).RowGroups[0].Rows[1].Cells);               // 덮인 칸들은 빠졌다

        var saved = RichTextMap.Save(new TextRange(box.Document.ContentStart, box.Document.ContentEnd));
        var reopened = RichTextMap.Load(saved);
        var table = reopened.Blocks.OfType<Section>().SelectMany(s => s.Blocks).Concat(reopened.Blocks).OfType<Table>().Single();
        var grid = TableGrid.Build(table);
        Assert.Same(grid.At(0, 0), grid.At(1, 1));
        Assert.Equal((2, 2), (grid.At(0, 0)!.RowSpan, grid.At(0, 0)!.ColumnSpan));
        Close(box);
    });

    [Fact]
    public void 빈_칸에_합치면_빈_줄이_앞에_남지_않는다() => Run(() =>
    {
        var table = Filled(1, 3);
        ((Paragraph)table.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock).Inlines.Clear();
        ((Paragraph)table.RowGroups[0].Rows[0].Cells[1].Blocks.FirstBlock).Inlines.Clear();
        var box = Host(table);

        Merge(box, 0, 0, 0, 2);

        Assert.Equal(["r0c2"], Paragraphs(At(box, 0, 0)));
        Close(box);
    });

    /// 글자 칸에서 옆 빈 칸까지 끌어 합치기 — 끝이 빈 칸 시작에 놓여 앞 칸으로 돌아가던 것 [실측 — 렌더]
    [Fact]
    public void 옆_빈_칸까지_골라도_합쳐진다() => Run(() =>
    {
        var table = Filled(2, 3);
        ((Paragraph)table.RowGroups[0].Rows[0].Cells[1].Blocks.FirstBlock).Inlines.Clear();
        var box = Host(table);

        Merge(box, 0, 0, 0, 1);

        Assert.Equal(2, At(box, 0, 0).ColumnSpan);
        Assert.Equal(["r0c0"], Paragraphs(At(box, 0, 0)));
        Close(box);
    });

    [Fact]
    public void 칸_하나면_합칠_수_없고_합친_칸이_아니면_나눌_수_없다() => Run(() =>
    {
        var box = Host(Filled(2, 2));
        CaretIn(box, At(box, 0, 0));
        Assert.False(RichTable.MergeCells.CanExecute(null, box));
        Assert.False(RichTable.SplitCell.CanExecute(null, box));

        Merge(box, 0, 0, 0, 1);
        CaretIn(box, At(box, 0, 0));
        Assert.True(RichTable.SplitCell.CanExecute(null, box));
        box.IsReadOnly = true;
        Assert.False(RichTable.SplitCell.CanExecute(null, box));
        Close(box);
    });

    // ── 풀기 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 풀면_덮였던_자리에_빈_칸이_제자리로_돌아온다() => Run(() =>
    {
        var box = Host(Filled(3, 3));
        Merge(box, 0, 1, 1, 2);
        Assert.Equal("r0c0,r0c1+r0c2+r1c1+r1c2,r0c1+r0c2+r1c1+r1c2 / r1c0,r0c1+r0c2+r1c1+r1c2,r0c1+r0c2+r1c1+r1c2 / r2c0,r2c1,r2c2", Shape(box));

        CaretIn(box, At(box, 0, 1));
        RichTable.SplitCell.Execute(null, box);

        Assert.Equal("r0c0,r0c1+r0c2+r1c1+r1c2, / r1c0,, / r2c0,r2c1,r2c2", Shape(box));   // 글자는 왼쪽 위 칸에 남고 나머지는 빈 칸
        Assert.All(TableIn(box).RowGroups[0].Rows, r => Assert.Equal(3, r.Cells.Count));
        Assert.False(Grid(box).HasMerged);
        Close(box);
    });

    /// 왼쪽에 있던 합친 칸을 풀면 빈 칸이 오른쪽 칸들 앞(제자리)에 들어간다 — 행 끝에 붙이면 자리가 어긋난다
    [Fact]
    public void 왼쪽_합친_칸을_풀어도_빈_칸이_제자리다() => Run(() =>
    {
        var box = Host(Filled(2, 3));
        Merge(box, 0, 0, 1, 1);
        CaretIn(box, At(box, 0, 0));

        RichTable.SplitCell.Execute(null, box);

        Assert.Equal("r0c0+r0c1+r1c0+r1c1,,r0c2 / ,,r1c2", Shape(box));
        Close(box);
    });

    // ── 합친 칸이 걸친 행 · 열 (사용자 판정 Q-4 A — 줄여 남김) ──────────────

    [Fact]
    public void 세로로_합친_칸이_걸친_행을_지우면_합친_칸은_줄어_남는다() => Run(() =>
    {
        var box = Host(Filled(3, 3));
        Merge(box, 0, 2, 2, 2);
        CaretIn(box, At(box, 1, 0));

        RichTable.DeleteRow.Execute(null, box);

        var merged = At(box, 0, 2);
        Assert.Equal(2, merged.RowSpan);
        Assert.Equal(["r0c2", "r1c2", "r2c2"], Paragraphs(merged));           // 글자를 잃지 않는다
        Assert.Equal(2, Grid(box).RowCount);
        Close(box);
    });

    /// 마지막 행 지우기 — 아래 행이 없다. 처음 구현은 없는 아래 행을 읽어 던졌다 [실측 — RichBodyTests 가 잡음]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 마지막_행을_지운다(bool mergedIntoLast) => Run(() =>
    {
        var box = Host(Filled(3, 2));
        if (mergedIntoLast) Merge(box, 1, 1, 2, 1);
        CaretIn(box, At(box, 2, 0));

        RichTable.DeleteRow.Execute(null, box);

        Assert.Equal(2, Grid(box).RowCount);
        Assert.Equal(mergedIntoLast ? "r0c0,r0c1 / r1c0,r1c1+r2c1" : "r0c0,r0c1 / r1c0,r1c1", Shape(box));
        Close(box);
    });

    [Fact]
    public void 합친_칸이_시작하는_행을_지우면_그_칸은_아래_행으로_내려간다() => Run(() =>
    {
        var box = Host(Filled(3, 3));
        Merge(box, 0, 1, 1, 1);
        CaretIn(box, At(box, 0, 0));

        RichTable.DeleteRow.Execute(null, box);

        Assert.Equal("r1c0,r0c1+r1c1,r1c2 / r2c0,r2c1,r2c2", Shape(box));
        Assert.Equal(1, At(box, 0, 1).RowSpan);
        Close(box);
    });

    [Fact]
    public void 가로로_합친_칸이_걸친_열을_지우면_합친_칸은_줄어_남는다() => Run(() =>
    {
        var box = Host(Filled(2, 3));
        Merge(box, 0, 0, 0, 2);
        CaretIn(box, At(box, 1, 1));

        RichTable.DeleteColumn.Execute(null, box);

        Assert.Equal(2, TableIn(box).Columns.Count);
        Assert.Equal(2, At(box, 0, 0).ColumnSpan);
        Assert.Equal(["r0c0", "r0c1", "r0c2"], Paragraphs(At(box, 0, 0)));
        Assert.Equal("r0c0+r0c1+r0c2,r0c0+r0c1+r0c2 / r1c0,r1c2", Shape(box));
        Close(box);
    });

    [Fact]
    public void 합친_칸_한가운데에_행이나_열을_넣으면_합친_칸이_늘어난다() => Run(() =>
    {
        var box = Host(Filled(2, 3));
        Merge(box, 0, 2, 1, 2);                                               // 오른쪽 열 세로 합침
        CaretIn(box, At(box, 0, 0));
        RichTable.InsertRowBelow.Execute(null, box);

        Assert.Equal(3, At(box, 0, 2).RowSpan);
        Assert.Equal(2, TableIn(box).RowGroups[0].Rows[1].Cells.Count);       // 새 행은 합친 칸 자리를 빼고

        Merge(box, 0, 0, 0, 1);                                               // 위 행 가로 합침
        CaretIn(box, At(box, 2, 0));
        RichTable.InsertColumnRight.Execute(null, box);

        Assert.Equal(4, TableIn(box).Columns.Count);
        Assert.Equal(3, At(box, 0, 0).ColumnSpan);
        Assert.All(Enumerable.Range(0, Grid(box).RowCount), r => Assert.All(Enumerable.Range(0, 4), c => Assert.NotNull(Grid(box).At(r, c))));
        Close(box);
    });

    // ── 열 정의 · 실행취소 (D-176) ──────────────────────────────────────────

    /// 예전엔 열을 넣고 되돌리면 열 정의만 남아 열 3 · 칸 2 가 됐다 [실측]
    [Fact]
    public void 열을_넣거나_지우고_되돌리면_열_수와_칸_수가_맞는다() => Run(() =>
    {
        var box = Host(Filled(2, 2));
        CaretIn(box, At(box, 0, 0));

        RichTable.InsertColumnRight.Execute(null, box);
        Assert.Equal((3, 3), (TableIn(box).Columns.Count, TableIn(box).RowGroups[0].Rows[0].Cells.Count));
        box.Undo();
        Assert.Equal((2, 2), (TableIn(box).Columns.Count, TableIn(box).RowGroups[0].Rows[0].Cells.Count));

        CaretIn(box, At(box, 0, 0));
        RichTable.DeleteColumn.Execute(null, box);
        Assert.Equal((1, 1), (TableIn(box).Columns.Count, TableIn(box).RowGroups[0].Rows[0].Cells.Count));
        box.Undo();
        Assert.Equal((2, 2), (TableIn(box).Columns.Count, TableIn(box).RowGroups[0].Rows[0].Cells.Count));
        Close(box);
    });

    // ── 다른 동작과 함께 ───────────────────────────────────────────────────

    [Fact]
    public void Tab_은_합친_칸을_한_칸으로_건너고_마지막에서_열_수만큼_새_행을_만든다() => Run(() =>
    {
        var box = Host(Filled(2, 3));
        Merge(box, 1, 1, 1, 2);
        CaretIn(box, At(box, 1, 0));

        RichTable.NextCell.Execute(null, box);
        Assert.Same(At(box, 1, 1), RichTable.CellAt(box));
        RichTable.NextCell.Execute(null, box);                                 // 마지막 칸 → 새 행

        Assert.Equal(3, Grid(box).RowCount);
        Assert.Equal(3, TableIn(box).RowGroups[0].Rows[2].Cells.Count);
        Close(box);
    });

    [Fact]
    public void 합친_칸이_있으면_칸_덮어쓰기_대신_글자로_넣는다() => Run(() =>
    {
        var source = Host(Filled(1, 2));
        var data = ClipboardService.BuildDataObject(RichBodyBehavior.PayloadOf(
            new TextRange(At(source, 0, 0).ContentStart, At(source, 0, 1).ContentEnd)));
        Close(source);

        var box = Host(Filled(2, 3));
        Merge(box, 0, 0, 0, 1);
        CaretIn(box, At(box, 1, 1));

        var args = new DataObjectPastingEventArgs(data, isDragDrop: false, DataFormats.XamlPackage) { RoutedEvent = DataObject.PastingEvent };
        box.RaiseEvent(args);

        Assert.False(args.CommandCancelled);
        Assert.Equal(DataFormats.UnicodeText, args.FormatToApply);
        Close(box);
    });

    [Fact]
    public void 합친_칸이_있는_표도_열_경계를_찾는다() => Run(() =>
    {
        var box = Host(Filled(2, 3));
        Merge(box, 0, 0, 0, 1);
        box.UpdateLayout();
        Pump();

        var edges = TableColumnResizer.Edges(box);
        Assert.Equal(3, edges.Count);
        var cell = At(box, 1, 1);
        var x = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward).X - cell.Padding.Left;
        Assert.InRange(edges[0].X - x, -1, 1);
        Close(box);
    });

    /// 실제 창의 본문 우클릭 메뉴 — 항목이 표 항목(Tag)이고 명령이 묶였다. 메뉴는 열지 않는다(D-082)
    [Fact]
    public void 우클릭_메뉴에_합치기_나누기_정렬이_표_항목으로_있다() => Run(() =>
    {
        using var scene = TabScene.Open(["문서"]);
        scene.Shell.ActiveTab = scene.Tab(0);
        scene.Window.UpdateLayout();
        var menu = ((RichTextBox)scene.Window.FindActiveBodyTextBox()!).ContextMenu!;
        MenuItem Item(string header) => menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, header));

        Assert.Same(RichTable.MergeCells, Item("칸 합치기").Command);
        Assert.Same(RichTable.SplitCell, Item("칸 나누기").Command);
        Assert.Equal(["Left", "Center", "Right"], new[] { "왼쪽 정렬", "가운데 정렬", "오른쪽 정렬" }.Select(h => Item(h).CommandParameter));
        Assert.All(new[] { "칸 합치기", "칸 나누기", "왼쪽 정렬", "가운데 정렬", "오른쪽 정렬" }, h => Assert.Equal(RichTable.MenuTag, Item(h).Tag));
    });

    [Fact]
    public void 합친_칸을_정렬하면_그_칸_전체가_정렬된다() => Run(() =>
    {
        var box = Host(Filled(2, 2));
        Merge(box, 0, 0, 0, 1);
        CaretIn(box, At(box, 0, 0));

        RichTable.AlignCells.Execute("Center", box);

        Assert.All(At(box, 0, 0).Blocks.OfType<Paragraph>(), p => Assert.Equal(TextAlignment.Center, p.TextAlignment));
        Assert.NotEqual(TextAlignment.Center, ((Paragraph)At(box, 1, 0).Blocks.FirstBlock).TextAlignment);
        Close(box);
    });
}
