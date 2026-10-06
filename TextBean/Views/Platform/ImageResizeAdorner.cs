using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TextBean.Views.Platform;

/// <summary>
/// 고른 그림의 네 모서리 손잡이 (D-142). 비율은 늘 유지한다 — 폭만 바꾸고 높이는 Stretch=Uniform 이 따라온다. 변 손잡이는 없다.
/// 끄는 동안은 폭만 바꿔 미리 보이고, 놓으면 원래 폭으로 돌린 뒤 RichImage.Resize 로 갈아 끼운다 —
/// 폭만 바꾼 것은 실행취소 · 수정됨에 잡히지 않는다 [실측].
/// </summary>
public sealed class ImageResizeAdorner : Adorner
{
    private const double HandleSize = 9;

    private static readonly Brush Line = Frozen(Color.FromRgb(0x18, 0x5F, 0xA5));
    private static readonly Pen Outline = FrozenPen();

    private readonly VisualCollection _visuals;
    private readonly Thumb[] _handles;
    private readonly Image _image;
    private readonly InlineUIContainer _container;
    private readonly RichTextBox _box;
    private double _startWidth;
    private double _startHeight;
    private Point _startPointer;

    public ImageResizeAdorner(Image image, InlineUIContainer container, RichTextBox box) : base(image)
    {
        _image = image;
        _container = container;
        _box = box;
        _visuals = new VisualCollection(this);
        Pointer = () => Mouse.GetPosition(_box);

        // 왼쪽 위 · 오른쪽 위 · 왼쪽 아래 · 오른쪽 아래. 끄는 방향 부호(가로, 세로)
        _handles =
        [
            Handle(-1, -1, Cursors.SizeNWSE), Handle(+1, -1, Cursors.SizeNESW),
            Handle(-1, +1, Cursors.SizeNESW), Handle(+1, +1, Cursors.SizeNWSE)
        ];
    }

    public IReadOnlyList<Thumb> Handles => _handles;

    /// 마우스 위치(본문 기준). 시험은 바꿔 끼운다 — 실제 마우스를 쓰지 않는다.
    public Func<Point> Pointer { get; set; }

    private Thumb Handle(int sx, int sy, Cursor cursor)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.BackgroundProperty, Brushes.White);
        face.SetValue(Border.BorderBrushProperty, Line);
        face.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        var thumb = new Thumb { Width = HandleSize, Height = HandleSize, Cursor = cursor, Template = new ControlTemplate(typeof(Thumb)) { VisualTree = face } };
        thumb.DragStarted += (_, _) =>
        {
            _startWidth = ImageWidth();
            _startHeight = _image.ActualHeight > 0 ? _image.ActualHeight : _startWidth;
            _startPointer = Pointer();
        };
        thumb.DragDelta += (_, _) => Preview(sx, sy);
        thumb.DragCompleted += (_, e) => Commit(e.Canceled);
        _visuals.Add(thumb);
        return thumb;
    }

    private double ImageWidth() => double.IsNaN(_image.Width) ? _image.ActualWidth : _image.Width;

    /// <summary>
    /// 끌기 시작 위치에서 지금까지의 마우스 이동을 그 모서리의 대각선 방향으로 투영해 폭을 정한다 (D-147).
    /// Thumb 이 주는 이동량은 손잡이 기준이라, 그림이 커지며 손잡이가 따라 움직이면 기준이 흔들려
    /// 커졌다 줄었다 했다 [사용자 실기]. 가로 · 세로 중 큰 쪽을 고르면 축이 바뀌며 튀었다.
    /// </summary>
    private void Preview(int sx, int sy)
    {
        var now = Pointer();
        var dx = now.X - _startPointer.X;
        var dy = now.Y - _startPointer.Y;

        // 대각선 d = (sx·폭, sy·높이). 이동 p 의 d 방향 비율 k = p·d / d·d → 새 폭 = 시작 폭 × (1 + k)
        var dX = sx * _startWidth;
        var dY = sy * _startHeight;
        var k = (dx * dX + dy * dY) / Math.Max(dX * dX + dY * dY, 1);

        _image.Width = Math.Clamp(_startWidth * (1 + k), RichImage.MinWidth, Math.Max(RichImage.MinWidth, RichImage.AvailableWidth(_box)));
    }

    private void Commit(bool canceled)
    {
        var final = ImageWidth();
        _image.Width = _startWidth;
        if (canceled || Math.Abs(final - _startWidth) < 1) return;

        RichImage.Resize(_box, _container, final);      // 고른 것이 새 그림으로 바뀌어 손잡이도 새로 붙는다
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = AdornedElement.RenderSize;
        var half = HandleSize / 2;
        Point[] corners = [new(0, 0), new(size.Width, 0), new(0, size.Height), new(size.Width, size.Height)];
        for (var i = 0; i < _handles.Length; i++)
            _handles[i].Arrange(new Rect(corners[i].X - half, corners[i].Y - half, HandleSize, HandleSize));
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
        => drawingContext.DrawRectangle(null, Outline, new Rect(AdornedElement.RenderSize));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen()
    {
        var pen = new Pen(Frozen(Color.FromRgb(0x18, 0x5F, 0xA5)), 1);
        pen.Freeze();
        return pen;
    }
}
