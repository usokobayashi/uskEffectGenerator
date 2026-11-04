using System.Runtime.InteropServices;

namespace UskEffect.App.Interop;

/// <summary>子 HWND 上のマウス操作の通知先。</summary>
internal interface IHostWindowInput
{
    void OnPointerEntered();
    void OnPointerExited();
    void OnClicked();
    void OnPointerDown(int button, int x, int y);
    void OnPointerMoved(int x, int y);
    void OnPointerUp(int button, int x, int y);
    void OnWheel(int delta, int x, int y);
}

/// <summary>D3D11 スワップチェーンを載せる子ウィンドウ用の最小限の Win32 API。</summary>
internal static class Win32
{
    private const string HostClassName = "UskEffectD3DHost";

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint CS_OWNDC = 0x0020;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const int VK_LBUTTON = 0x01;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_SPACE = 0x20;
    private const int IDC_SIZEALL = 32646;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint TME_LEAVE = 0x00000002;
    private const int IDC_ARROW = 32512;
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ネイティブ側から呼ばれるので GC されないよう保持する
    private static readonly WndProcDelegate s_wndProc = HostWndProc;
    private static bool s_classRegistered;

    // 子 HWND 上のマウス入力は Avalonia に届かないので、ここで受けて通知する
    private static readonly Dictionary<IntPtr, IHostWindowInput> s_inputs = [];
    private static readonly HashSet<IntPtr> s_hovered = [];

    public static void RegisterInput(IntPtr hwnd, IHostWindowInput input) => s_inputs[hwnd] = input;

    public static void UnregisterInput(IntPtr hwnd)
    {
        s_inputs.Remove(hwnd);
        s_hovered.Remove(hwnd);
    }

    public static bool IsSpaceDown() => GetKeyState(VK_SPACE) < 0;
    public static bool IsShiftDown() => GetKeyState(VK_SHIFT) < 0;
    public static bool IsControlDown() => GetKeyState(VK_CONTROL) < 0;
    public static bool IsLeftButtonDown() => GetKeyState(VK_LBUTTON) < 0;

    /// <summary>ストロークの間、窓の外でもマウスを受け取る。カーソルの形は変えない。</summary>
    public static void BeginCapture(IntPtr hwnd) => SetCapture(hwnd);

    public static void EndCapture() => ReleaseCapture();

    /// <summary>ドラッグの間、窓の外でもマウスを受け取る。</summary>
    public static void BeginDrag(IntPtr hwnd)
    {
        SetCapture(hwnd);
        SetCursor(LoadCursor(IntPtr.Zero, IDC_SIZEALL));
    }

    public static void EndDrag()
    {
        ReleaseCapture();
        SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
    }

    private static (int X, int Y) PointFrom(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        return ((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    /// <summary>OS のアニメーション効果の設定。取得できないときは有効として扱う。</summary>
    public static bool AreClientAnimationsEnabled() =>
        !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool enabled, 0) || enabled;

    public static IntPtr CreateHostWindow(IntPtr parent)
    {
        IntPtr instance = GetModuleHandle(null);
        if (!s_classRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_OWNDC,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = instance,
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                lpszClassName = HostClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            s_classRegistered = true;
        }

        IntPtr hwnd = CreateWindowEx(0, HostClassName, null,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1, parent, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return hwnd;
    }

    public static (int Width, int Height) GetClientSize(IntPtr hwnd)
    {
        return GetClientRect(hwnd, out var rect) ? (rect.Right - rect.Left, rect.Bottom - rect.Top) : (0, 0);
    }

    private static IntPtr HostWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return 1; // 背景はスワップチェーンが描くのでちらつき防止に消去しない
            case WM_PAINT:
                ValidateRect(hwnd, IntPtr.Zero);
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                if (s_inputs.TryGetValue(hwnd, out var moved))
                {
                    if (s_hovered.Add(hwnd))
                    {
                        var track = new TRACKMOUSEEVENT
                        {
                            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                            dwFlags = TME_LEAVE,
                            hwndTrack = hwnd,
                        };
                        TrackMouseEvent(ref track);
                        moved.OnPointerEntered();
                    }
                    var (mx, my) = PointFrom(lParam);
                    moved.OnPointerMoved(mx, my);
                }
                return IntPtr.Zero;
            case WM_LBUTTONDOWN or WM_MBUTTONDOWN:
                if (s_inputs.TryGetValue(hwnd, out var pressed))
                {
                    var (dx, dy) = PointFrom(lParam);
                    pressed.OnPointerDown(msg == WM_LBUTTONDOWN ? 0 : 1, dx, dy);
                }
                return IntPtr.Zero;
            case WM_MBUTTONUP:
                if (s_inputs.TryGetValue(hwnd, out var middleUp))
                {
                    var (ux, uy) = PointFrom(lParam);
                    middleUp.OnPointerUp(1, ux, uy);
                }
                return IntPtr.Zero;
            case WM_MOUSEWHEEL:
                if (s_inputs.TryGetValue(hwnd, out var wheel))
                {
                    // ホイールの座標は画面座標で届く
                    var (sx, sy) = PointFrom(lParam);
                    var point = new POINT { X = sx, Y = sy };
                    ScreenToClient(hwnd, ref point);
                    wheel.OnWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF), point.X, point.Y);
                }
                return IntPtr.Zero;
            case WM_MOUSELEAVE:
                if (s_inputs.TryGetValue(hwnd, out var left) && s_hovered.Remove(hwnd))
                    left.OnPointerExited();
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (s_inputs.TryGetValue(hwnd, out var clicked))
                {
                    var (lx, ly) = PointFrom(lParam);
                    clicked.OnPointerUp(0, lx, ly);
                    clicked.OnClicked();
                }
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int cursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
