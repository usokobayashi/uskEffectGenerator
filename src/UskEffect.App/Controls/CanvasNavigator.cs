using UskEffect.App.Interop;
using UskEffect.Graphics;

namespace UskEffect.App.Controls;

/// <summary>
/// キャンバスのズーム・パン・回転 (要件 §6)。操作は CLIP STUDIO PAINT に合わせる。
/// ホイール: カーソル位置を中心にズーム / 中ボタンまたは Space + ドラッグ: 移動 /
/// Shift + Space + ドラッグ: 回転 / Ctrl + Space + ドラッグ: ズーム。
/// 子 HWND の上なので、マウスは D3DHost の通知で受ける。
/// </summary>
internal sealed class CanvasNavigator
{
    private const int LeftButton = 0;
    private const int MiddleButton = 1;
    private const double WheelStep = 1.2;
    private const double DragZoomSpeed = 0.01;

    private enum DragMode { None, Pan, Rotate, Zoom }

    private readonly D3DHost _host;
    private readonly Func<CanvasView> _get;
    private readonly Action<CanvasView> _set;
    private readonly Func<bool> _isEnabled;
    private DragMode _drag;
    private int _dragButton;
    private int _lastX, _lastY, _startX, _startY;

    public CanvasNavigator(D3DHost host, Func<CanvasView> get, Action<CanvasView> set, Func<bool> isEnabled)
    {
        _host = host;
        _get = get;
        _set = set;
        _isEnabled = isEnabled;
        host.HostPointerDown += OnDown;
        host.HostPointerMoved += OnMoved;
        host.HostPointerUp += OnUp;
        host.HostWheel += OnWheel;
    }

    private (double X, double Y) FromCenter(int x, int y)
    {
        var (width, height) = _host.PixelSize;
        return (x - width / 2.0, y - height / 2.0);
    }

    private void OnWheel(int delta, int x, int y)
    {
        if (!_isEnabled())
            return;
        var (cx, cy) = FromCenter(x, y);
        _set(_get().ZoomAt(cx, cy, Math.Pow(WheelStep, delta / 120.0)));
    }

    private void OnDown(int button, int x, int y)
    {
        if (!_isEnabled() || _drag != DragMode.None)
            return;

        bool space = Win32.IsSpaceDown();
        _drag = button == MiddleButton ? DragMode.Pan
            : button != LeftButton || !space ? DragMode.None
            : Win32.IsShiftDown() ? DragMode.Rotate
            : Win32.IsControlDown() ? DragMode.Zoom
            : DragMode.Pan;
        if (_drag == DragMode.None)
            return;

        _dragButton = button;
        _lastX = _startX = x;
        _lastY = _startY = y;
        Win32.BeginDrag(_host.Hwnd);
    }

    private void OnMoved(int x, int y)
    {
        if (_drag == DragMode.None)
            return;

        var view = _get();
        switch (_drag)
        {
            case DragMode.Pan:
                view = view.Panned(x - _lastX, y - _lastY);
                break;
            case DragMode.Rotate:
            {
                var (ax, ay) = FromCenter(_lastX, _lastY);
                var (bx, by) = FromCenter(x, y);
                view = view.Rotated((Math.Atan2(by, bx) - Math.Atan2(ay, ax)) * 180 / Math.PI);
                break;
            }
            case DragMode.Zoom:
            {
                var (cx, cy) = FromCenter(_startX, _startY);
                view = view.ZoomAt(cx, cy, Math.Exp((_lastY - y) * DragZoomSpeed));
                break;
            }
        }
        _set(view);
        _lastX = x;
        _lastY = y;
    }

    private void OnUp(int button, int x, int y)
    {
        if (_drag == DragMode.None || button != _dragButton)
            return;
        _drag = DragMode.None;
        Win32.EndDrag();
    }
}
