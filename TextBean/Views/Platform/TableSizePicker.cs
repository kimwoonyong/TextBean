using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TextBean.Views.Platform;

/// <summary>
/// 표 크기 고르기 격자 (D-131). 마우스를 올린 칸까지 칠하고 「행 × 열」을 보인다. 누르면 Command 를 "행x열" 로 부르고
/// Picked 를 올린다 — 목록 닫기는 올리는 쪽(색 목록과 같은 처리)이 맡는다.
/// </summary>
public sealed class TableSizePicker : StackPanel
{
    private const double CellSize = 16;
    private const double Gap = 2;

    // 지금 테마의 앱 색 (D-163). 테마를 바꾸면 창 안 내용이 새로 만들어져 다시 읽는다.
    private static Brush Idle => AppTheme.Brush("PopupBrush");
    private static Brush IdleLine => AppTheme.Brush("SwatchLineBrush");
    private static Brush Lit => AppTheme.Brush("PickerLitBrush");
    private static Brush LitLine => AppTheme.Brush("AccentBrush");

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(TableSizePicker));

    public static readonly DependencyProperty CommandTargetProperty =
        DependencyProperty.Register(nameof(CommandTarget), typeof(IInputElement), typeof(TableSizePicker));

    public static readonly RoutedEvent PickedEvent =
        EventManager.RegisterRoutedEvent(nameof(Picked), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TableSizePicker));

    private readonly UniformGrid _grid = new() { Rows = RichTable.MaxPick, Columns = RichTable.MaxPick, Background = Brushes.Transparent };
    private readonly TextBlock _label = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };

    public TableSizePicker()
    {
        for (var i = 0; i < RichTable.MaxPick * RichTable.MaxPick; i++)
            _grid.Children.Add(new Border
            {
                Width = CellSize, Height = CellSize, Margin = new Thickness(Gap / 2),
                BorderThickness = new Thickness(1)
            });

        Children.Add(_grid);
        Children.Add(_label);
        _grid.MouseMove += (_, e) => Highlight(SizeAt(e.GetPosition(_grid)));
        _grid.MouseLeftButtonUp += (_, e) => Pick(SizeAt(e.GetPosition(_grid)));
        Highlight((1, 1));
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public IInputElement? CommandTarget
    {
        get => (IInputElement?)GetValue(CommandTargetProperty);
        set => SetValue(CommandTargetProperty, value);
    }

    public event RoutedEventHandler Picked
    {
        add => AddHandler(PickedEvent, value);
        remove => RemoveHandler(PickedEvent, value);
    }

    /// 지금 칠한 크기.
    public (int Rows, int Columns) Current { get; private set; }

    /// 격자 안 좌표 → 행 · 열(1부터, 범위 안으로 자른다).
    public static (int Rows, int Columns) SizeAt(Point point)
    {
        const double step = CellSize + Gap;
        static int Clamp(double value) => Math.Clamp((int)(value / step) + 1, 1, RichTable.MaxPick);
        return (Clamp(point.Y), Clamp(point.X));
    }

    public void Highlight((int Rows, int Columns) size)
    {
        Current = size;
        for (var i = 0; i < _grid.Children.Count; i++)
        {
            var on = i / RichTable.MaxPick < size.Rows && i % RichTable.MaxPick < size.Columns;
            var cell = (Border)_grid.Children[i];
            cell.Background = on ? Lit : Idle;
            cell.BorderBrush = on ? LitLine : IdleLine;
        }
        _label.Text = $"{size.Rows} × {size.Columns}";
    }

    public void Pick((int Rows, int Columns) size)
    {
        var parameter = $"{size.Rows}x{size.Columns}";
        if (Command is RoutedCommand routed)
        {
            if (routed.CanExecute(parameter, CommandTarget)) routed.Execute(parameter, CommandTarget);
        }
        else if (Command?.CanExecute(parameter) == true)
        {
            Command.Execute(parameter);
        }

        Highlight((1, 1));
        RaiseEvent(new RoutedEventArgs(PickedEvent, this));
    }
}
