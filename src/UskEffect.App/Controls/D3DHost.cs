using Avalonia.Controls;
using Avalonia.Platform;
using UskEffect.App.Interop;

namespace UskEffect.App.Controls;

/// <summary>
/// Avalonia のレイアウト内に子 HWND を埋め込み、D3D11 スワップチェーンの描画先にする。
/// HWND は Avalonia の描画より手前に表示されるため、この領域の上に Avalonia のコントロールは重ねられない。
/// </summary>
public sealed class D3DHost : NativeControlHost, IHostWindowInput
{
    public IntPtr Hwnd { get; private set; }

    public event Action<IntPtr>? HwndCreated;
    public event Action? HwndDestroying;

    /// <summary>子 HWND 上のマウス操作。Avalonia のポインターイベントは届かないのでこちらを使う。</summary>
    public event Action? HostClicked;
    public event Action? HostPointerEntered;
    public event Action? HostPointerExited;

    /// <summary>引数は (ボタン: 0 左 / 1 中, x, y)。座標は HWND の画素。</summary>
    public event Action<int, int, int>? HostPointerDown;
    public event Action<int, int>? HostPointerMoved;
    public event Action<int, int, int>? HostPointerUp;

    /// <summary>引数は (回転量: 1 ノッチ 120, x, y)。</summary>
    public event Action<int, int, int>? HostWheel;

    public bool IsPointerOverHost { get; private set; }

    /// <summary>ペンで押している間の筆圧 (0..1)。マウスなら null。</summary>
    public double? PenPressure { get; private set; }

    void IHostWindowInput.OnPenPressure(double? pressure) => PenPressure = pressure;

    void IHostWindowInput.OnPointerDown(int button, int x, int y) => HostPointerDown?.Invoke(button, x, y);
    void IHostWindowInput.OnPointerMoved(int x, int y) => HostPointerMoved?.Invoke(x, y);
    void IHostWindowInput.OnPointerUp(int button, int x, int y) => HostPointerUp?.Invoke(button, x, y);
    void IHostWindowInput.OnWheel(int delta, int x, int y) => HostWheel?.Invoke(delta, x, y);

    void IHostWindowInput.OnClicked() => HostClicked?.Invoke();
    void IHostWindowInput.OnPointerEntered()
    {
        IsPointerOverHost = true;
        HostPointerEntered?.Invoke();
    }

    void IHostWindowInput.OnPointerExited()
    {
        IsPointerOverHost = false;
        HostPointerExited?.Invoke();
    }

    public (int Width, int Height) PixelSize => Hwnd == IntPtr.Zero ? (0, 0) : Win32.GetClientSize(Hwnd);

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
            return base.CreateNativeControlCore(parent);

        Hwnd = Win32.CreateHostWindow(parent.Handle);
        Win32.RegisterInput(Hwnd, this);
        HwndCreated?.Invoke(Hwnd);
        return new PlatformHandle(Hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (!OperatingSystem.IsWindows())
        {
            base.DestroyNativeControlCore(control);
            return;
        }

        HwndDestroying?.Invoke();
        Win32.UnregisterInput(control.Handle);
        Win32.DestroyWindow(control.Handle);
        Hwnd = IntPtr.Zero;
    }
}
