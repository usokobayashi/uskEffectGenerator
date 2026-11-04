using UskEffect.App.Interop;
using UskEffect.Graphics;
using UskEffect.Graphics.Painting;

namespace UskEffect.App.Controls;

/// <summary>
/// キャンバス上の左ボタンのドラッグをストロークにする (FR-14)。Space を押している間は CanvasNavigator に任せる。
/// 子 HWND の上なので、マウスは D3DHost の通知で受ける。
/// </summary>
internal sealed class CanvasPainter
{
    private const int LeftButton = 0;

    private readonly D3DHost _host;
    private readonly RenderEngine _engine;
    private readonly Func<BrushSettings> _brush;
    private readonly Func<bool> _isEnabled;
    private readonly Action _strokeFinished;

    private BrushSettings? _strokeBrush;
    private ICanvasMapping? _mapping;
    private StrokeInterpolator? _interpolator;
    private readonly List<(double X, double Y)> _points = [];

    public CanvasPainter(D3DHost host, RenderEngine engine, Func<BrushSettings> brush, Func<bool> isEnabled, Action strokeFinished)
    {
        _host = host;
        _engine = engine;
        _brush = brush;
        _isEnabled = isEnabled;
        _strokeFinished = strokeFinished;
        host.HostPointerDown += OnDown;
        host.HostPointerMoved += OnMoved;
        host.HostPointerUp += OnUp;
        host.HostPointerExited += HideCursor;
    }

    public void HideCursor()
    {
        if (_strokeBrush is null)
            _engine.Cursor = null;
    }

    private void OnDown(int button, int x, int y)
    {
        if (button != LeftButton || _strokeBrush is not null || !_isEnabled() || Win32.IsSpaceDown())
            return;

        var (width, height) = _host.PixelSize;
        if (_engine.CreateCanvasMapping(width, height) is not { } mapping)
            return;

        var brush = _brush();
        var (strokeX, strokeY) = mapping.ToStrokeSpace(x, y);
        if (!mapping.CanStartAt(strokeX, strokeY))
            return;

        _strokeBrush = brush;
        _mapping = mapping;
        _interpolator = new StrokeInterpolator(brush.Spacing);
        Win32.BeginCapture(_host.Hwnd);

        _points.Clear();
        _points.Add(_interpolator.Begin(strokeX, strokeY));
        Submit();
    }

    private void OnMoved(int x, int y)
    {
        if (_strokeBrush is null)
        {
            _engine.Cursor = _isEnabled() && !Win32.IsSpaceDown() ? new PaintCursor(x, y, _brush().Size) : null;
            return;
        }

        // ボタンを離した通知を取りこぼしていたら、ここで終える
        if (!Win32.IsLeftButtonDown())
        {
            EndStroke();
            return;
        }

        _engine.Cursor = new PaintCursor(x, y, _strokeBrush.Size);
        var (strokeX, strokeY) = _mapping!.ToStrokeSpace(x, y);
        _points.Clear();
        _interpolator!.MoveTo(strokeX, strokeY, _points);
        Submit();
    }

    private void OnUp(int button, int x, int y)
    {
        if (button == LeftButton && _strokeBrush is not null)
            EndStroke();
    }

    private void Submit()
    {
        var mapping = _mapping!;
        var brush = _strokeBrush!;
        var dabs = _points.Select(p => mapping.ToDab(p.X, p.Y, brush)).OfType<PaintDab>().ToArray();
        _engine.Paint(brush, dabs, mapping.Polar);
    }

    private void EndStroke()
    {
        _strokeBrush = null;
        _mapping = null;
        _interpolator = null;
        Win32.EndCapture();
        _strokeFinished();
    }
}
