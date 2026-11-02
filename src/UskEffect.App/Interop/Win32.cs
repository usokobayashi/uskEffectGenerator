using System.Runtime.InteropServices;

namespace UskEffect.App.Interop;

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
    private const int IDC_ARROW = 32512;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ネイティブ側から呼ばれるので GC されないよう保持する
    private static readonly WndProcDelegate s_wndProc = HostWndProc;
    private static bool s_classRegistered;

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
