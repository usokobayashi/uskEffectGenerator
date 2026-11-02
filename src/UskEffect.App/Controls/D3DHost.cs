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

    void IHostWindowInput.OnClicked() => HostClicked?.Invoke();
    void IHostWindowInput.OnPointerEntered() => HostPointerEntered?.Invoke();
    void IHostWindowInput.OnPointerExited() => HostPointerExited?.Invoke();

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
