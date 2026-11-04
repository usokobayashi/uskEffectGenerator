using System.Numerics;
using UskEffect.Core.Project;
using UskEffect.Graphics;
using UskEffect.Graphics.Painting;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UskEffect.Tests;

public sealed class StrokeTests
{
    [Fact]
    public void Interpolator_PlacesDabsAtEvenSpacing_AcrossMoves()
    {
        var interpolator = new StrokeInterpolator(10);
        var dabs = new List<(double X, double Y)> { interpolator.Begin(0, 0) };
        interpolator.MoveTo(7, 0, dabs);
        interpolator.MoveTo(25, 0, dabs);

        Assert.Equal([0d, 10d, 20d], dabs.Select(d => d.X));
    }

    [Fact]
    public void Interpolator_NoMovement_AddsNothing()
    {
        var interpolator = new StrokeInterpolator(4);
        var dabs = new List<(double X, double Y)> { interpolator.Begin(3, 3) };
        interpolator.MoveTo(3, 3, dabs);
        Assert.Single(dabs);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.0, 0.0)]
    public void Flow_AtOpacityLimits(double opacity, double flow) =>
        Assert.Equal(flow, new BrushSettings { Opacity = opacity }.Flow, 6);

    [Fact]
    public void Flow_OverlappingDabsReachOpacity()
    {
        var brush = new BrushSettings { Size = 100, Opacity = 0.5 };
        double overlaps = brush.Size / brush.Spacing;
        double reached = 1 - Math.Pow(1 - brush.Flow, overlaps);
        Assert.Equal(0.5, reached, 6);
    }
}

public sealed class CanvasMappingTests
{
    private static CanvasMapping Map(CanvasView? view = null, double tiling = 1, double offset = 0) =>
        new(1024, 1024, 512, 512, 12, view ?? new CanvasView(), tiling, tiling, offset, offset);

    [Fact]
    public void Center_MapsToTextureCenter()
    {
        var (x, y) = Map().ScreenToTexture(512, 512);
        Assert.Equal(256, x, 6);
        Assert.Equal(256, y, 6);
    }

    [Fact]
    public void Corner_MapsToTextureCorner_InsideMargin()
    {
        var (x, y) = Map().ScreenToTexture(12, 12);
        Assert.Equal(0, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void ZoomAndPan_FollowTheView()
    {
        var view = new CanvasView { Zoom = 2, PanX = 100 };
        var (x, _) = Map(view).ScreenToTexture(612, 512);
        Assert.Equal(256, x, 6);
    }

    [Fact]
    public void Rotation_FollowsTheView()
    {
        // 時計回りに 90° 回すと、内容の右は画面の下へ行く
        var (x, y) = Map(new CanvasView { RotationDegrees = 90 }).ScreenToTexture(512, 512 + 100);
        Assert.Equal(256 + 100 * 512.0 / 1000, x, 6);
        Assert.Equal(256, y, 6);
    }

    [Fact]
    public void Tiling_RepeatsTheTexture_AndStaysUnwrappedUntilAsked()
    {
        var mapping = Map(tiling: 2);
        var (x, y) = mapping.ScreenToTexture(1012, 512);
        Assert.Equal(1024, x, 6);
        Assert.Equal(512, y, 6);
        Assert.Equal(new PaintDab(0, 0), mapping.Wrap(x, y));
    }

    [Fact]
    public void Outside_IsNotOnTheCanvas()
    {
        var mapping = Map();
        var (x, y) = mapping.ScreenToTexture(2, 512);
        Assert.False(mapping.IsInsideCanvas(x, y));
        var (cx, cy) = mapping.ScreenToTexture(512, 512);
        Assert.True(mapping.IsInsideCanvas(cx, cy));
    }

    [Fact]
    public void Wrap_NegativeValuesComeBackInside() =>
        Assert.Equal(new PaintDab(502, 2), Map().Wrap(-10, 514));

    private static PolarCanvasMapping Polar(PolarSettings settings, CanvasView? view = null, double tiling = 1) =>
        new(1024, 1024, 512, 512, 12, view ?? new CanvasView(), settings, tiling, tiling, 0, 0);

    [Theory]
    [InlineData(0.3f, 1f, 0f, false, false)]
    [InlineData(0.3f, 1f, 45f, false, false)]
    [InlineData(0.2f, 0.9f, 90f, true, false)]
    [InlineData(0.2f, 0.9f, 10f, false, true)]
    [InlineData(0f, 1f, 200f, true, true)]
    public void Polar_MatchesPolarMapping(float inner, float outer, float rotation, bool flipAngular, bool flipRadial)
    {
        var settings = new PolarSettings(inner, outer, rotation, flipAngular, flipRadial);
        var mapping = Polar(settings);
        foreach (var uv in new[] { new Vector2(0.1f, 0.2f), new Vector2(0.5f, 0.5f), new Vector2(0.9f, 0.8f) })
        {
            // キャンバス → 画面 (外接円の半径は 500 画素、y 下向き)
            var canvas = PolarMapping.UvToCanvas(uv, settings);
            var (strokeX, strokeY) = mapping.ToStrokeSpace(512 + canvas.X * 500, 512 - canvas.Y * 500);
            var dab = mapping.ToDab(strokeX, strokeY, new BrushSettings())!.Value;
            Assert.Equal(uv.X * 512, dab.X, 1);
            Assert.Equal(uv.Y * 512, dab.Y, 1);
            Assert.Equal(canvas.Length(), dab.PolarRadius, 3);
        }
    }

    [Fact]
    public void Polar_OutsideTheRing_CannotStart_ButABrushOverlappingItStillPaints()
    {
        var mapping = Polar(new PolarSettings(0.5f, 0.8f));
        var inRing = mapping.ToStrokeSpace(512 + 300, 512);
        Assert.True(mapping.CanStartAt(inRing.X, inRing.Y));

        // 半径 0.84。リングの外だが、比率 0.1 のブラシは外径に届く
        var near = mapping.ToStrokeSpace(512 + 420, 512);
        Assert.False(mapping.CanStartAt(near.X, near.Y));
        Assert.NotNull(mapping.ToDab(near.X, near.Y, new BrushSettings { Size = 51.2 }));

        var far = mapping.ToStrokeSpace(512 + 490, 512);
        Assert.Null(mapping.ToDab(far.X, far.Y, new BrushSettings { Size = 8 }));
    }

    [Fact]
    public void Polar_Stroke_InterpolatesOnTheCanvas_AcrossThe360DegreeSeam()
    {
        // +x 軸 (u = 0) を上から下へまたぐ。テクスチャ座標で補間すると反対側を通ってしまう
        var mapping = Polar(new PolarSettings(0.3f, 1f));
        var brush = new BrushSettings { Size = 16 };
        var interpolator = new StrokeInterpolator(brush.Spacing);
        var start = mapping.ToStrokeSpace(512 + 300, 512 - 20);
        var end = mapping.ToStrokeSpace(512 + 300, 512 + 20);
        var points = new List<(double X, double Y)> { interpolator.Begin(start.X, start.Y) };
        interpolator.MoveTo(end.X, end.Y, points);

        var dabs = points.Select(p => mapping.ToDab(p.X, p.Y, brush)!.Value).ToArray();
        Assert.All(dabs, d => Assert.True(d.X < 25 || d.X > 512 - 25));
        Assert.Contains(dabs, d => d.X < 25);
        Assert.Contains(dabs, d => d.X > 512 - 25);
    }

    [Fact]
    public void SeamView_OffsetsByHalfATile()
    {
        var mapping = new CanvasMapping(1024, 1024, 512, 512, 12, new CanvasView(), 1, 1, 0.5, 0.5);
        var (x, y) = mapping.ToStrokeSpace(512, 512);
        Assert.Equal(new PaintDab(0, 0), mapping.ToDab(x, y, new BrushSettings()));
    }
}

public sealed unsafe class PaintCursorTests
{
    [Fact]
    public void Cursor_DrawsARing_AndLeavesTheCenterAlone()
    {
        const int size = 64;
        using var gpu = new GpuDevice();
        using var paint = new PaintRenderer(gpu);
        Assert.Null(paint.LoadShader(Path.Combine(NoiseFixture.FindRepositoryRoot(), "Shaders", "Paint", "Paint.hlsl")));

        var description = new Texture2DDescription
        {
            Width = size,
            Height = size,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        };
        using var target = gpu.Device.CreateTexture2D(description);
        using var view = gpu.Device.CreateRenderTargetView(target);
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = gpu.Device.CreateTexture2D(description);

        gpu.Context.ClearRenderTargetView(view, new Vortice.Mathematics.Color4(0.5f, 0.5f, 0.5f, 1f));
        paint.DrawCursor(view, size, size, 32, 32, 20);
        gpu.Context.CopyResource(staging, target);

        var mapped = gpu.Context.Map(staging, 0, MapMode.Read);
        try
        {
            byte Blue(int x, int y) => ((byte*)mapped.DataPointer)[y * mapped.RowPitch + x * 4];
            Assert.Equal(255, Blue(52, 32));
            Assert.InRange(Blue(32, 32), 126, 129);
            Assert.InRange(Blue(2, 2), 126, 129);
        }
        finally
        {
            gpu.Context.Unmap(staging, 0);
        }
    }
}

/// <summary>GPU で実際に描いて読み戻す。GPU がなければ WARP で動く。</summary>
public sealed class PaintRenderTests : IDisposable
{
    private readonly RenderEngine _engine = new(Path.Combine(NoiseFixture.FindRepositoryRoot(), "Shaders")) { VSync = false };

    public void Dispose() => _engine.Dispose();

    private static (byte R, byte G, byte B, byte A) At(ImageData image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return (image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2], image.Rgba[i + 3]);
    }

    // 左半分が黒、右半分が白の不透明な画像
    private static ImageData Split(int size)
    {
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                byte v = x < size / 2 ? (byte)0 : (byte)255;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = v;
                rgba[i + 3] = 255;
            }
        }
        return new ImageData(size, size, rgba);
    }

    private static readonly BrushSettings Red = new()
    {
        Tool = PaintTool.Brush,
        Size = 16,
        Hardness = 1,
        Opacity = 1,
        Color = new Vector3(1, 0, 0),
    };

    [Fact]
    public void NoPaint_ReadsNothing_AndStaysOffTheGpu()
    {
        Assert.Null(_engine.ReadComposite());
        Assert.Null(_engine.ReadPaintLayer());
    }

    [Fact]
    public void Brush_PaintsInsideTheDab_AndLeavesTheRestAlone()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red, [new PaintDab(16, 16)]);

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 16, 16));
        Assert.Equal((0, 0, 0, 255), At(image, 16, 40));
        Assert.Equal((255, 255, 255, 255), At(image, 60, 60));
    }

    [Fact]
    public void Brush_OnNoise_KeepsTheNoiseGray()
    {
        _engine.Noise = new NoiseSettings { Resolution = 64 };
        _engine.Paint(Red, [new PaintDab(32, 32)]);

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 32, 32));
        var (r, g, b, a) = At(image, 2, 2);
        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public void LowOpacity_Accumulates_WithoutReachingFullColor()
    {
        _engine.SetImageSource(Split(64));
        var brush = Red with { Opacity = 0.5, Size = 20 };
        var dabs = Enumerable.Range(0, 11).Select(i => new PaintDab(10 + i * brush.Spacing, 32)).ToArray();
        _engine.Paint(brush, dabs);

        // 黒の上に赤。スタンプが重なった中心で濃さが 0.5 前後になる
        var (r, g, _, _) = At(_engine.ReadComposite()!, 20, 32);
        Assert.InRange(r, 100, 170);
        Assert.Equal(0, g);
    }

    [Fact]
    public void Eraser_BringsBackTheSource_ButOnlyInsideTheDab()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Size = 40 }, [new PaintDab(32, 32)]);
        _engine.Paint(Red with { Tool = PaintTool.Eraser, Size = 8 }, [new PaintDab(44, 32)]);

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 255, 255, 255), At(image, 44, 32));
        Assert.Equal((255, 0, 0, 255), At(image, 20, 32));
    }

    [Fact]
    public void Blur_SoftensTheEdge_OnlyWhereBrushed()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Tool = PaintTool.Blur, Size = 24, Hardness = 1 }, [new PaintDab(32, 16)]);

        var image = _engine.ReadComposite()!;
        var (left, _, _, alpha) = At(image, 31, 16);
        var (right, _, _, _) = At(image, 32, 16);
        Assert.InRange(left, 40, 215);
        Assert.InRange(right, 40, 215);
        Assert.Equal(255, alpha);

        // ブラシの外の境界はくっきりしたまま
        Assert.Equal(0, At(image, 31, 56).R);
        Assert.Equal(255, At(image, 32, 56).R);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red, [new PaintDab(16, 16)]);
        _engine.ClearPaint();

        Assert.Null(_engine.ReadComposite());
        Assert.Null(_engine.ReadPaintLayer());
    }

    [Fact]
    public void Layer_SurvivesASourceSizeChange()
    {
        _engine.Noise = new NoiseSettings { Resolution = 64 };
        _engine.Paint(Red with { Size = 16 }, [new PaintDab(32, 32)]);
        Assert.Equal((255, 0, 0, 255), At(_engine.ReadComposite()!, 32, 32));

        _engine.Noise = new NoiseSettings { Resolution = 128 };
        var image = _engine.ReadComposite()!;
        Assert.Equal(128, image.Width);
        Assert.Equal((255, 0, 0, 255), At(image, 64, 64));
    }

    [Fact]
    public void SwitchingSource_RecomposesUnderTheSamePaint()
    {
        _engine.Noise = new NoiseSettings { Resolution = 64 };
        _engine.Paint(Red, [new PaintDab(16, 16)]);
        _engine.ReadComposite();

        _engine.SetImageSource(Split(64));
        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 16, 16));
        Assert.Equal((255, 255, 255, 255), At(image, 60, 60));
    }

    [Fact]
    public void Wrap_ContinuesOnTheOppositeEdge()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Size = 16 }, [new PaintDab(2, 62)]);

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 2, 62));
        Assert.Equal((255, 0, 0, 255), At(image, 60, 62));
        Assert.Equal((255, 0, 0, 255), At(image, 2, 2));
        Assert.Equal((255, 0, 0, 255), At(image, 60, 2));
        Assert.Equal(0, At(image, 20, 32).R);
    }

    [Fact]
    public void WrapOff_StopsAtTheEdge()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Size = 16, Wrap = false }, [new PaintDab(2, 32)]);

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 2, 32));
        Assert.Equal((255, 255, 255, 255), At(image, 60, 32));
    }

    [Fact]
    public void Blur_WithWrap_ReadsAcrossTheEdge()
    {
        // 左端は黒で、向こう側の右端は白。またいで読めば左端の列が明るくなる
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Tool = PaintTool.Blur, Size = 24 }, [new PaintDab(0, 32)]);
        Assert.InRange(At(_engine.ReadComposite()!, 0, 32).R, 40, 215);
    }

    [Fact]
    public void Blur_WithoutWrap_StaysOnItsSide()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Tool = PaintTool.Blur, Size = 24, Wrap = false }, [new PaintDab(0, 32)]);
        Assert.Equal(0, At(_engine.ReadComposite()!, 0, 32).R);
    }

    // リング全体が見える極座標。64×64 なのでブラシの比率は Size / 64
    private static readonly PolarStamp Ring = new(0.25, 0.75, false, 1, 1, 64);

    [Fact]
    public void Polar_KeepsTheCircle_OnTheCanvas()
    {
        _engine.SetImageSource(Split(64));
        // 半径 0.5 (v = 0.5) に、比率 0.25 のブラシ。u = 0.25 → 64 × 0.25 = 16
        _engine.Paint(Red with { Size = 16 }, [new PaintDab(16, 32, 0.5)], Ring);

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 16, 32));

        // 内径側へ 30 画素は半径 0.234 離れた点で、ブラシの半径 0.25 の内側
        Assert.Equal((255, 0, 0, 255), At(image, 16, 32 - 30));

        // 同じ半径の円周上では、角度 ±29° (弦の長さ 0.25) がブラシの端。テクスチャでは 0.08 で約 5 画素
        Assert.Equal((255, 0, 0, 255), At(image, 16 + 4, 32));
        Assert.Equal(0, At(image, 16 + 7, 32).G);
        Assert.NotEqual(255, At(image, 16 + 7, 32).R);
    }

    [Fact]
    public void Polar_CrossesThe360DegreeSeam_WithoutABreak()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Size = 16 }, [new PaintDab(0, 32, 0.5)], Ring);

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 1, 32));
        Assert.Equal((255, 0, 0, 255), At(image, 62, 32));
        Assert.Equal(0, At(image, 32, 32).R == 255 && At(image, 32, 32).G == 0 ? 1 : 0);
    }

    [Fact]
    public void Polar_MasksOutsideTheRing()
    {
        _engine.SetImageSource(Split(64));

        // 外径のすぐ外の中心。v の折り返しで反対の端 (内径側) に描いてはいけない
        _engine.Paint(Red with { Size = 8 }, [new PaintDab(16, 2, 0.8)], Ring);
        var image = _engine.ReadComposite()!;
        Assert.Equal(0, At(image, 16, 60).R == 255 && At(image, 16, 60).G == 0 ? 1 : 0);
    }

    [Fact]
    public void Polar_DiscCenter_FillsEveryAngle()
    {
        _engine.SetImageSource(Split(64));
        var disc = new PolarStamp(0, 1, false, 1, 1, 64);
        _engine.Paint(Red with { Size = 8 }, [new PaintDab(0, 0, 0)], disc);

        // 中心では角度が不定なので、v の小さい行はどの角度も塗られる
        var image = _engine.ReadComposite()!;
        foreach (int x in new[] { 0, 16, 32, 48, 63 })
            Assert.Equal((255, 0, 0, 255), At(image, x, 2));
    }

    [Fact]
    public void Polar_Blur_ReadsAcrossThe360DegreeSeam()
    {
        // u = 0 の境界は黒と白の継ぎ目。またいで読めば両側が混ざる
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Tool = PaintTool.Blur, Size = 32 }, [new PaintDab(0, 32, 0.5)], Ring);

        var image = _engine.ReadComposite()!;
        Assert.InRange(At(image, 0, 32).R, 30, 225);
        Assert.InRange(At(image, 63, 32).R, 30, 225);
    }

    [Fact]
    public void Polar_Eraser_RemovesOnlyTheBrushedArea()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Size = 64 }, [new PaintDab(16, 32, 0.5)], Ring);
        _engine.Paint(Red with { Tool = PaintTool.Eraser, Size = 8 }, [new PaintDab(16, 32, 0.5)], Ring);

        var image = _engine.ReadComposite()!;
        Assert.Equal((0, 0, 0, 255), At(image, 16, 32));
        Assert.Equal((255, 0, 0, 255), At(image, 16, 56));
    }

    private static double SeamStep(ImageData image, int y)
    {
        int a = At(image, 0, y).R, b = At(image, image.Width - 1, y).R;
        return Math.Abs(a - b);
    }

    [Fact]
    public void SeamBlend_ClosesTheSeam_AndCancelRestoresIt()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red with { Size = 8 }, [new PaintDab(32, 8)]);
        var before = _engine.ReadComposite()!;
        Assert.Equal(255, SeamStep(before, 32));

        _engine.PreviewSeamBlend(new SeamBlendSettings(SeamMode.X, 0.25, 1));
        var blended = _engine.ReadComposite()!;
        Assert.True(SeamStep(blended, 32) < 16);
        Assert.Equal(At(before, 24, 32), At(blended, 24, 32));

        // 設定を変えると、残しておいた状態から作り直す
        _engine.PreviewSeamBlend(new SeamBlendSettings(SeamMode.X, 0.25, 0));
        Assert.Equal(255, SeamStep(_engine.ReadComposite()!, 32));

        _engine.PreviewSeamBlend(new SeamBlendSettings(SeamMode.X, 0.25, 1));
        _engine.CancelSeamBlend();
        Assert.Equal(before.Rgba, _engine.ReadComposite()!.Rgba);
    }

    [Fact]
    public void SeamBlend_Commit_Stays_AndCanBeSaved()
    {
        _engine.SetImageSource(Split(64));
        _engine.PreviewSeamBlend(new SeamBlendSettings(SeamMode.X, 0.25, 1));
        _engine.CommitSeamBlend();
        _engine.CancelSeamBlend();

        Assert.True(SeamStep(_engine.ReadComposite()!, 32) < 16);
        Assert.NotNull(_engine.ReadPaintLayer());
    }

    [Fact]
    public void SeamBlend_Cancel_WithNothingPainted_ReadsAsUnpainted()
    {
        _engine.SetImageSource(Split(64));
        _engine.PreviewSeamBlend(new SeamBlendSettings());
        _engine.CancelSeamBlend();
        Assert.Null(_engine.ReadComposite());
    }

    [Fact]
    public void Layer_RoundTripsThroughAProjectFile()
    {
        _engine.SetImageSource(Split(64));
        _engine.Paint(Red, [new PaintDab(16, 16)]);
        var layer = _engine.ReadPaintLayer()!;
        Assert.Equal((255, 0, 0, 255), At(layer, 16, 16));
        Assert.Equal(0, At(layer, 60, 60).A);

        string path = Path.Combine(Path.GetTempPath(), $"uskfx-paint-{Guid.NewGuid():N}.uskfx");
        try
        {
            ProjectFile.Save(path, new ProjectDocument { PaintLayer = layer });
            var loaded = ProjectFile.Load(path);
            Assert.Null(loaded.Image);

            using var other = new RenderEngine(Path.Combine(NoiseFixture.FindRepositoryRoot(), "Shaders"));
            other.SetImageSource(Split(64));
            other.SetPaintLayer(loaded.PaintLayer);
            var image = other.ReadComposite()!;
            Assert.Equal((255, 0, 0, 255), At(image, 16, 16));
            Assert.Equal((255, 255, 255, 255), At(image, 60, 60));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
