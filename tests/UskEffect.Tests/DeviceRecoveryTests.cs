using System.Numerics;
using System.Runtime.InteropServices;
using UskEffect.Graphics;
using UskEffect.Graphics.Painting;

namespace UskEffect.Tests;

public sealed partial class DeviceRecoveryTests : IDisposable
{
    private static readonly BrushSettings Red = new()
    {
        Tool = PaintTool.Brush,
        Size = 16,
        Hardness = 1,
        Opacity = 1,
        Color = new Vector3(1, 0, 0),
    };

    private readonly RenderEngine _engine = new(Path.Combine(NoiseFixture.FindRepositoryRoot(), "Shaders"))
    {
        RecoveryRetryInterval = TimeSpan.Zero,
        VSync = false,
    };

    public void Dispose() => _engine.Dispose();

    private static (byte R, byte G, byte B, byte A) At(ImageData image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return (image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2], image.Rgba[i + 3]);
    }

    private static ImageData Blue(int size)
    {
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 2] = 255;
            rgba[i + 3] = 255;
        }
        return new ImageData(size, size, rgba);
    }

    [Fact]
    public void Recovery_RestoresTheImageAndTheBackedUpPaint_ButClearsHistory()
    {
        _engine.SetImageSource(Blue(64));
        _engine.Paint(Red, [new PaintDab(16, 16)]);
        _engine.EndStroke();
        _engine.BackupLayersNow();

        _engine.SimulateDeviceLoss();

        var image = _engine.ReadComposite()!;
        Assert.False(_engine.IsDeviceLost);
        Assert.Equal((255, 0, 0, 255), At(image, 16, 16));
        Assert.Equal((0, 0, 255, 255), At(image, 50, 50));
        Assert.False(_engine.CanUndo);
    }

    [Fact]
    public void Recovery_RegeneratesTheSameNoise()
    {
        _engine.Noise = new NoiseSettings { Resolution = 64 };
        var (before, _, _) = _engine.ReadNoisePixels();

        _engine.SimulateDeviceLoss();

        var (after, width, _) = _engine.ReadNoisePixels();
        Assert.Equal(64, width);
        Assert.Equal(before, after);
    }

    [Fact]
    public void WhileTheGpuCannotComeBack_SaveUsesTheBackup_AndExportRefuses_ThenItRecovers()
    {
        _engine.SetImageSource(Blue(64));
        _engine.Paint(Red, [new PaintDab(16, 16)]);
        _engine.EndStroke();
        _engine.BackupLayersNow();

        _engine.DeviceFactory = () => throw new InvalidOperationException("GPU はまだ使えない");
        _engine.SimulateDeviceLoss();

        Assert.False(_engine.RenderFrame(64, 64, out _));
        Assert.True(_engine.IsDeviceLost);
        Assert.Equal((255, 0, 0, 255), At(_engine.ReadLayers()![0].Image!, 16, 16));
        Assert.Throws<InvalidOperationException>(() => _engine.ReadComposite());

        // 操作は復帰するまで待っていて、失われない
        _engine.Paint(Red, [new PaintDab(48, 48)]);
        _engine.AddLayer();
        _engine.SetImageSource(Blue(32));
        _engine.SetImageSource(Blue(64));

        _engine.DeviceFactory = null;
        var image = _engine.ReadComposite()!;
        Assert.False(_engine.IsDeviceLost);
        Assert.Equal((255, 0, 0, 255), At(image, 16, 16));
        Assert.Equal((255, 0, 0, 255), At(image, 48, 48));
    }

    [Fact]
    public void Recovery_RecreatesTheSwapChain_AndKeepsPaintBackedUpByRealFrames()
    {
        IntPtr hwnd = CreateWindowExW(0, "STATIC", "", WsPopup, 0, 0, 64, 64, 0, 0, 0, 0);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        try
        {
            _engine.AttachSurface(hwnd, 64, 64);
            _engine.SetImageSource(Blue(64));
            Assert.True(_engine.RenderFrame(64, 64, out var first));
            Assert.Equal(0, first.DeviceRecoveries);

            _engine.Paint(Red, [new PaintDab(16, 16)]);
            _engine.EndStroke();

            // 操作が落ち着くと控えを取る
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromMilliseconds(400))
                _engine.RenderFrame(64, 64, out _);

            _engine.SimulateDeviceLoss();

            Assert.True(_engine.RenderFrame(64, 64, out var recovered));
            Assert.Equal(1, recovered.DeviceRecoveries);
            Assert.Equal((255, 0, 0, 255), At(_engine.ReadComposite()!, 16, 16));
            Assert.True(_engine.RenderFrame(64, 64, out _));
        }
        finally
        {
            _engine.DetachSurface();
            DestroyWindow(hwnd);
        }
    }

    [Fact]
    public void PolarDefault_IsADisc_WithNoHole()
    {
        Assert.Equal(0f, new PolarSettings().InnerRadius);
    }

    private const uint WsPopup = 0x80000000;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);
}
