using System.Numerics;
using UskEffect.Core.Export;
using UskEffect.Graphics;
using UskEffect.Graphics.Painting;

namespace UskEffect.Tests;

/// <summary>レイヤーの合成モードと生成。実際の GPU で画素を確かめる。</summary>
public sealed class LayerCompositionTests : IDisposable
{
    private const int Size = 64;

    private readonly RenderEngine _engine = new(Path.Combine(NoiseFixture.FindRepositoryRoot(), "Shaders"));

    public void Dispose() => _engine.Dispose();

    private static (int R, int G, int B, int A) At(ImageData image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return (image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2], image.Rgba[i + 3]);
    }

    private static ImageData Solid(byte r, byte g, byte b)
    {
        var rgba = new byte[Size * Size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = 255;
        }
        return new ImageData(Size, Size, rgba);
    }

    private static BrushSettings Brush(float r, float g, float b, float opacity = 1) => new()
    {
        Tool = PaintTool.Brush,
        Size = 16,
        Hardness = 1,
        Opacity = opacity,
        Color = new Vector3(r, g, b),
    };

    // 下が (200, 100, 50) の上に、(128, 128, 128) を合成モードで描いた結果
    [Theory]
    [InlineData(LayerBlendMode.Normal, 128, 128, 128)]
    [InlineData(LayerBlendMode.Multiply, 100, 50, 25)]
    [InlineData(LayerBlendMode.Add, 255, 228, 178)]
    [InlineData(LayerBlendMode.Subtract, 72, 0, 0)]
    [InlineData(LayerBlendMode.Screen, 228, 178, 153)]
    [InlineData(LayerBlendMode.Overlay, 200, 100, 50)]
    public void BlendMode_MixesWithTheLayerBelow(LayerBlendMode mode, int r, int g, int b)
    {
        _engine.SetImageSource(Solid(200, 100, 50));
        _engine.Paint(Brush(128 / 255f, 128 / 255f, 128 / 255f), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        _engine.SetLayerBlend(_engine.ActiveLayerId, mode);

        var image = _engine.ReadComposite()!;
        var (outR, outG, outB, outA) = At(image, 32, 32);
        Assert.InRange(outR, r - 3, r + 3);
        Assert.InRange(outG, g - 3, g + 3);
        Assert.InRange(outB, b - 3, b + 3);
        Assert.Equal(255, outA);

        // 描いていないところは下のまま
        Assert.Equal((200, 100, 50, 255), At(image, 2, 2));
    }

    [Fact]
    public void BlendMode_RespectsOpacity()
    {
        _engine.SetImageSource(Solid(200, 200, 200));
        _engine.Paint(Brush(0, 0, 0), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        _engine.SetLayerBlend(_engine.ActiveLayerId, LayerBlendMode.Multiply);
        _engine.SetLayerOpacity(_engine.ActiveLayerId, 0.5f);

        Assert.InRange(At(_engine.ReadComposite()!, 32, 32).R, 97, 103);
    }

    [Fact]
    public void BlendMode_CanBeUndone()
    {
        _engine.SetImageSource(Solid(200, 100, 50));
        _engine.Paint(Brush(0.5f, 0.5f, 0.5f), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        _engine.SetLayerBlend(_engine.ActiveLayerId, LayerBlendMode.Multiply);
        Assert.InRange(At(_engine.ReadComposite()!, 32, 32).R, 97, 103);

        _engine.Undo();
        Assert.Equal(LayerBlendMode.Normal, _engine.Layers[0].Blend);
        Assert.InRange(At(_engine.ReadComposite()!, 32, 32).R, 125, 131);
    }

    private NoiseSettings Circle(bool invert = false) => new()
    {
        Resolution = Size,
        Type = NoiseType.ShapeCircle,
        Invert = invert,
        Shape = new ShapeSettings { Size = 0.25f },
    };

    [Fact]
    public void GeneratedLayer_CoversTheBase_InNormalMode()
    {
        _engine.SetImageSource(Solid(200, 100, 50));
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 255, 255, 255), At(image, 32, 32));
        Assert.Equal((0, 0, 0, 255), At(image, 2, 2));
    }

    // 用途: ノイズの中心だけ、サークルで穴をあける
    [Fact]
    public void CircleLayer_PunchesAHoleInTheBaseNoise_AndExportsAsTransparent()
    {
        // コントラスト 0 だと値は 0.5 + 明るさ
        _engine.Noise = new NoiseSettings { Resolution = Size, Contrast = 0, Brightness = -0.2f };
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());
        _engine.SetLayerBlend(_engine.ActiveLayerId, LayerBlendMode.Subtract);

        var image = _engine.ReadComposite()!;
        Assert.Equal(0, At(image, 32, 32).R);
        Assert.InRange(At(image, 2, 2).R, 74, 80);

        var transparent = TextureExporter.BlackToAlpha(image.Rgba);
        Assert.Equal(0, transparent[(32 * Size + 32) * 4 + 3]);
        Assert.InRange(transparent[(2 * Size + 2) * 4 + 3], 74, 80);
    }

    [Fact]
    public void InvertedCircle_Multiplied_KeepsOnlyTheOutside()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size, Contrast = 0 };
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle(invert: true));
        _engine.SetLayerBlend(_engine.ActiveLayerId, LayerBlendMode.Multiply);

        var image = _engine.ReadComposite()!;
        Assert.Equal(0, At(image, 32, 32).R);
        Assert.InRange(At(image, 2, 2).R, 124, 131);
    }

    [Fact]
    public void BaseNoise_AndLayerGeneration_DoNotAffectEachOther()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size, Seed = 1 };
        var (before, _, _) = _engine.ReadNoisePixels();
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());

        var (after, _, _) = _engine.ReadNoisePixels();
        Assert.Equal(before, after);

        // ベースを変えても、レイヤーの生成はそのまま
        _engine.Noise = new NoiseSettings { Resolution = Size, Seed = 2 };
        Assert.Equal((255, 255, 255, 255), At(_engine.ReadComposite()!, 32, 32));
    }

    [Fact]
    public void GeneratedLayer_FollowsTheBaseSize_AndCanBeTurnedOff()
    {
        _engine.SetImageSource(Solid(10, 10, 10));
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle() with { Resolution = 256 });
        var image = _engine.ReadComposite()!;
        Assert.Equal(Size, image.Width);
        Assert.Equal((255, 255, 255, 255), At(image, 32, 32));

        // 間を空けた操作は、別の 1 回の「元に戻す」になる
        Thread.Sleep(700);

        // 生成をやめて描いた内容もなければ、合成するものがない
        _engine.SetLayerNoise(_engine.ActiveLayerId, null);
        Assert.Null(_engine.ReadComposite());

        _engine.Undo();
        Assert.Equal((255, 255, 255, 255), At(_engine.ReadComposite()!, 32, 32));
    }

    [Fact]
    public void QuickGenerationEdits_UndoTogether()
    {
        _engine.SetImageSource(Solid(10, 10, 10));
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle(invert: true));

        _engine.Undo();

        Assert.Null(_engine.Layers[0].Noise);
    }

    [Fact]
    public void PaintOnAGeneratedLayer_GoesOnTopOfIt()
    {
        _engine.SetImageSource(Solid(10, 10, 10));
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());
        _engine.Paint(Brush(1, 0, 0), [new PaintDab(32, 32)]);
        _engine.EndStroke();

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 32, 32));
        Assert.Equal((255, 255, 255, 255), At(image, 32, 16 + 4));
    }

    [Fact]
    public void Generation_AndBlend_SurviveADeviceLoss()
    {
        _engine.RecoveryRetryInterval = TimeSpan.Zero;
        _engine.Noise = new NoiseSettings { Resolution = Size, Contrast = 0, Brightness = 0.3f };
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());
        _engine.SetLayerBlend(_engine.ActiveLayerId, LayerBlendMode.Subtract);
        _engine.BackupLayersNow();

        _engine.SimulateDeviceLoss();

        Assert.Equal(0, At(_engine.ReadComposite()!, 32, 32).R);
        Assert.Equal(LayerBlendMode.Subtract, _engine.Layers[0].Blend);
    }

    [Fact]
    public void Thumbnails_ShowTheBaseAndEachLayer()
    {
        _engine.SetImageSource(Solid(200, 100, 50));
        _engine.Paint(Brush(1, 0, 0), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        int painted = _engine.ActiveLayerId;
        _engine.AddLayer();
        int generated = _engine.ActiveLayerId;
        _engine.SetLayerNoise(generated, Circle());
        _engine.AddLayer();
        int empty = _engine.ActiveLayerId;

        var thumbnails = _engine.RenderLayerThumbnails();

        Assert.Equal(4, thumbnails.Count);
        (int R, int G, int B, int A) Pixel(int id, double u, double v)
        {
            var t = thumbnails.Single(x => x.LayerId == id);
            int i = ((int)(v * t.Size) * t.Size + (int)(u * t.Size)) * 4;
            return (t.Rgba[i], t.Rgba[i + 1], t.Rgba[i + 2], t.Rgba[i + 3]);
        }

        Assert.Equal((200, 100, 50, 255), Pixel(LayerThumbnail.BaseLayerId, 0.5, 0.5));
        Assert.Equal((255, 0, 0, 255), Pixel(painted, 0.5, 0.5));
        Assert.Equal(0, Pixel(painted, 0.05, 0.05).A);
        Assert.Equal((255, 255, 255, 255), Pixel(generated, 0.5, 0.5));
        Assert.Equal((0, 0, 0, 255), Pixel(generated, 0.05, 0.05));
        Assert.Equal(0, Pixel(empty, 0.5, 0.5).A);
    }

    // 白い部分が下のレイヤーから消える
    [Fact]
    public void Mask_RemovesTheLayersBelow_WhereItIsWhite()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, new NoiseSettings { Resolution = Size, Contrast = 0 });
        _engine.AddLayer(Circle());
        _engine.SetLayerMask(_engine.ActiveLayerId, true);

        var image = _engine.ReadComposite()!;
        Assert.Equal(0, At(image, 32, 32).A);
        Assert.Equal(255, At(image, 2, 2).A);
        Assert.InRange(At(image, 2, 2).R, 124, 131);
    }

    [Fact]
    public void Mask_KeepsPartOfTheLayerBelow_WhereItIsGray()
    {
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, new NoiseSettings { Resolution = Size, Contrast = 0, Brightness = 0.5f });
        _engine.AddLayer(new NoiseSettings { Resolution = Size, Contrast = 0 });
        _engine.SetLayerMask(_engine.ActiveLayerId, true);

        var (r, _, _, a) = At(_engine.ReadComposite()!, 32, 32);
        Assert.Equal(255, r);
        Assert.InRange(a, 124, 131);
    }

    // 色のある絵をマスクにしたときは明るさで扱う。透明なところは消さない
    [Fact]
    public void PaintedMask_UsesBrightness_AndTransparentPartsKeepTheLayerBelow()
    {
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, new NoiseSettings { Resolution = Size, Contrast = 0, Brightness = 0.5f });
        _engine.AddLayer(null);
        _engine.Paint(Brush(1, 0, 0), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        _engine.SetLayerMask(_engine.ActiveLayerId, true);

        var image = _engine.ReadComposite()!;
        Assert.InRange(At(image, 32, 32).A, 176, 182); // 赤の明るさは 0.299
        Assert.Equal(255, At(image, 2, 2).A);
    }

    [Fact]
    public void Invert_FlipsTheLayersColor()
    {
        _engine.SetImageSource(Solid(10, 10, 10));
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());
        _engine.SetLayerInvert(_engine.ActiveLayerId, true);

        var image = _engine.ReadComposite()!;
        Assert.Equal((0, 0, 0, 255), At(image, 32, 32));
        Assert.Equal((255, 255, 255, 255), At(image, 2, 2));

        // 描いた内容は透明なところを反転しない
        _engine.AddLayer(null);
        _engine.Paint(Brush(1, 0, 0), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        _engine.SetLayerInvert(_engine.ActiveLayerId, true);
        image = _engine.ReadComposite()!;
        Assert.Equal((0, 255, 255, 255), At(image, 32, 32));
        Assert.Equal((255, 255, 255, 255), At(image, 2, 2));
    }

    [Fact]
    public void InvertedThumbnail_MatchesTheLayer()
    {
        _engine.SetImageSource(Solid(10, 10, 10));
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());
        _engine.SetLayerInvert(_engine.ActiveLayerId, true);
        var thumbnail = _engine.RenderLayerThumbnails().Single(t => t.LayerId == _engine.ActiveLayerId);
        int center = (thumbnail.Size / 2 * thumbnail.Size + thumbnail.Size / 2) * 4;
        Assert.Equal(0, thumbnail.Rgba[center]);
        Assert.Equal(255, thumbnail.Rgba[center + 3]);
    }

    [Fact]
    public void TransparentBase_StartsFromNothing()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        var image = _engine.ReadComposite()!;
        Assert.Equal((0, 0, 0, 0), At(image, 32, 32));

        _engine.Paint(Brush(1, 0, 0), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 32, 32));
        Assert.Equal(0, At(image, 2, 2).A);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, 0)]             // 黒は透明
    [InlineData(255, 255, 255, 255, 255, 255, 255)] // 白は不透明の白
    [InlineData(128, 128, 128, 255, 255, 255, 128)] // 灰は半透明の白
    [InlineData(200, 0, 0, 255, 0, 0, 200)]       // 色は保つ
    public void BlackToAlpha_OnTheComposite_KeepsColor(byte r, byte g, byte b, int outR, int outG, int outB, int outA)
    {
        _engine.SetImageSource(Solid(r, g, b));
        _engine.Output = CompositeOutput.BlackToAlpha;

        var (actualR, actualG, actualB, actualA) = At(_engine.ReadComposite()!, 32, 32);
        Assert.InRange(actualR, outR - 2, outR + 2);
        Assert.InRange(actualG, outG - 2, outG + 2);
        Assert.InRange(actualB, outB - 2, outB + 2);
        Assert.InRange(actualA, outA - 2, outA + 2);
    }

    // マスクで抜けた部分は、透過を入れても透明のまま。残りの部分だけが変換される
    [Fact]
    public void BlackToAlpha_MultipliesWithWhatTheMaskLeft()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, new NoiseSettings { Resolution = Size, Contrast = 0 });
        _engine.AddLayer(Circle());
        _engine.SetLayerMask(_engine.ActiveLayerId, true);
        _engine.Output = CompositeOutput.BlackToAlpha;

        var image = _engine.ReadComposite()!;
        Assert.Equal(0, At(image, 32, 32).A);
        Assert.InRange(At(image, 2, 2).A, 124, 131);
        Assert.Equal(255, At(image, 2, 2).R);
    }

    [Fact]
    public void AddLayer_WithNoise_IsOneUndoStep()
    {
        _engine.SetImageSource(Solid(10, 10, 10));
        _engine.AddLayer(Circle());
        Assert.Equal(2, _engine.Layers.Count);
        Assert.NotNull(_engine.Layers[^1].Noise);

        _engine.Undo();
        Assert.Single(_engine.Layers);
    }

    [Fact]
    public void CompositeLuminance_TreatsTransparentAsBlack()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());
        _engine.ReadComposite();

        var pixels = _engine.ReadCompositeLuminance();
        Assert.Equal(1f, pixels[32 * Size + 32], 2);
        Assert.Equal(0f, pixels[2 * Size + 2], 2);

        // マスクで抜けたところは黒
        _engine.SetLayerMask(_engine.ActiveLayerId, true);
        Assert.Equal(0f, _engine.ReadCompositeLuminance()[32 * Size + 32], 2);
    }

    private static ImageData Red(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 255;
            rgba[i + 3] = 255;
        }
        return new ImageData(width, height, rgba);
    }

    [Fact]
    public void FitInto_KeepsTheAspect_AndLeavesTheRestTransparent()
    {
        var fitted = Red(32, 16).FitInto(Size, Size);
        Assert.Equal(Size, fitted.Width);
        Assert.Equal((255, 0, 0, 255), At(fitted, 32, 32));
        Assert.Equal((255, 0, 0, 255), At(fitted, 1, 32));
        Assert.Equal(0, At(fitted, 32, 2).A);
        Assert.Equal(0, At(fitted, 32, 61).A);
    }

    [Fact]
    public void FitInto_SameSize_ReturnsTheSameImage()
    {
        var image = Red(Size, Size);
        Assert.Same(image, image.FitInto(Size, Size));
    }

    // 色のある画像をレイヤーにして、合成・マスク・透過に使う
    [Fact]
    public void ImageLayer_ComposesOverNoise_AndCanBePaintedOn()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, new NoiseSettings { Resolution = Size, Contrast = 0 });
        _engine.AddImageLayer(Red(Size, Size), "red.png");

        Assert.Equal("red.png", _engine.Layers[^1].Name);
        Assert.Equal((255, 0, 0, 255), At(_engine.ReadComposite()!, 32, 32));

        _engine.Paint(Brush(0, 1, 0), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        Assert.Equal((0, 255, 0, 255), At(_engine.ReadComposite()!, 32, 32));
        Assert.Equal((255, 0, 0, 255), At(_engine.ReadComposite()!, 2, 2));
    }

    [Fact]
    public void ColorImageAsMask_UsesItsBrightness()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, new NoiseSettings { Resolution = Size, Contrast = 0, Brightness = 0.5f });
        _engine.AddImageLayer(Red(Size, Size), "red.png");
        _engine.SetLayerMask(_engine.ActiveLayerId, true);

        // 赤の明るさは 0.299 なので、下のアルファが 7 割残る
        Assert.InRange(At(_engine.ReadComposite()!, 32, 32).A, 176, 182);
    }

    [Fact]
    public void ColorImage_WithBlackToAlpha_KeepsItsColor()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        var rgba = new byte[Size * Size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200;
            rgba[i + 3] = 255;
        }
        _engine.AddImageLayer(new ImageData(Size, Size, rgba), "a.png");
        _engine.Output = CompositeOutput.BlackToAlpha;

        var (r, g, b, a) = At(_engine.ReadComposite()!, 32, 32);
        Assert.InRange(r, 253, 255);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
        Assert.InRange(a, 198, 202);
    }

    [Fact]
    public void ImageLayer_CanBeUndone_AndSurvivesSaving()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.AddImageLayer(Red(Size, Size), "red.png");
        var saved = _engine.ReadLayers()!;
        Assert.Equal((255, 0, 0, 255), At(saved[^1].Image!, 32, 32));

        _engine.Undo();
        Assert.Single(_engine.Layers);

        _engine.SetLayers(saved);
        Assert.Equal((255, 0, 0, 255), At(_engine.ReadComposite()!, 32, 32));
    }

    [Fact]
    public void CanvasSize_CanBeNonSquare_AndNoiseLayersFollowIt()
    {
        _engine.TransparentBase = true;
        _engine.CanvasSize = new CanvasPixelSize(80, 40);
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle() with { Shape = new ShapeSettings { Size = 0.4f } });

        var image = _engine.ReadComposite()!;
        Assert.Equal(80, image.Width);
        Assert.Equal(40, image.Height);
        Assert.Equal((255, 255, 255, 255), At(image, 40, 20));

        _engine.CanvasSize = null;
        Assert.NotEqual(80, _engine.ReadComposite()!.Width);
    }

    [Fact]
    public void MaskOutput_IsGrayAndOpaque_TransparentBecomesBlack()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.Paint(Brush(1, 0, 0), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        _engine.Output = CompositeOutput.Mask;

        var image = _engine.ReadComposite()!;
        Assert.Equal((0, 0, 0, 255), At(image, 2, 2));
        var (r, g, b, a) = At(image, 32, 32);
        Assert.InRange(r, 74, 78); // 赤の明るさは 0.299
        Assert.Equal(r, g);
        Assert.Equal(r, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public void ColorOutput_KeepsColorAndAlpha()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.Paint(Brush(1, 0, 0), [new PaintDab(32, 32)]);
        _engine.EndStroke();
        _engine.Output = CompositeOutput.Color;

        var image = _engine.ReadComposite()!;
        Assert.Equal((255, 0, 0, 255), At(image, 32, 32));
        Assert.Equal(0, At(image, 2, 2).A);
    }

    [Fact]
    public void SolidLayer_HasItsColor_AndAThumbnailOfIt()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, new NoiseSettings
        {
            Resolution = Size,
            Type = NoiseType.Solid,
            Shape = new ShapeSettings { SolidR = 1f, SolidG = 0.5f, SolidB = 0f },
        });

        var image = _engine.ReadComposite()!;
        Assert.Equal(255, At(image, 2, 2).R);
        Assert.InRange(At(image, 2, 2).G, 126, 129);
        Assert.Equal(0, At(image, 2, 2).B);
        Assert.Equal(At(image, 2, 2), At(image, 60, 60));

        var thumbnail = _engine.RenderLayerThumbnails().Single(t => t.LayerId == _engine.ActiveLayerId);
        Assert.Equal(255, thumbnail.Rgba[0]);
        Assert.InRange(thumbnail.Rgba[1], 126, 129);
        Assert.Equal(0, thumbnail.Rgba[2]);
    }

    [Fact]
    public void LayerTiling_RepeatsOnlyThatLayer()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, new NoiseSettings { Resolution = Size, Contrast = 0 });
        _engine.AddLayer(Circle());
        _engine.SetLayerBlend(_engine.ActiveLayerId, LayerBlendMode.Add);

        var single = _engine.ReadComposite()!;
        Assert.Equal(255, At(single, 32, 32).R);
        Assert.InRange(At(single, 16, 16).R, 124, 131);

        _engine.SetLayerTexture(_engine.ActiveLayerId, 2, 2, 0, 0);

        var tiled = _engine.ReadComposite()!;
        // 円が 4 か所に出る。中心は円の外になり、下のレイヤーはそのまま
        Assert.Equal(255, At(tiled, 16, 16).R);
        Assert.Equal(255, At(tiled, 48, 48).R);
        Assert.InRange(At(tiled, 32, 32).R, 124, 131);

        _engine.Undo();
        Assert.Equal(255, At(_engine.ReadComposite()!, 32, 32).R);
    }

    [Fact]
    public void LayerOffset_MovesThatLayer_AndIsSaved()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.SetLayerNoise(_engine.ActiveLayerId, Circle());
        _engine.SetLayerTexture(_engine.ActiveLayerId, 1, 1, 0.5f, 0.5f);

        var image = _engine.ReadComposite()!;
        Assert.Equal(0, At(image, 32, 32).R);
        Assert.Equal(255, At(image, 2, 2).R);

        var saved = _engine.ReadLayers()!.Single();
        Assert.Equal(0.5f, saved.OffsetX);
        _engine.SetLayers([saved]);
        Assert.Equal(255, At(_engine.ReadComposite()!, 2, 2).R);
    }

    [Fact]
    public void PaintOnATiledLayer_LandsWhereTheCursorIs()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.SetLayerTexture(_engine.ActiveLayerId, 2, 2, 0, 0);
        // 余白 12 で 88px のキャンバスを 2 回繰り返す表示の、右下のタイルの中央
        var mapping = _engine.CreateCanvasMapping(112, 112)!;
        var (x, y) = mapping.ToStrokeSpace(12 + 88 * 0.75, 12 + 88 * 0.75);

        _engine.Paint(Brush(1, 0, 0), [mapping.ToDab(x, y, Brush(1, 0, 0))!.Value]);
        _engine.EndStroke();

        var image = _engine.ReadComposite()!;
        Assert.Equal(255, At(image, 48, 48).R);
        Assert.Equal(255, At(image, 16, 16).R); // 繰り返しなので同じ絵
        Assert.Equal(0, At(image, 32, 16).A);
    }

    [Fact]
    public void Tile4x4View_MapsTheCursorIntoTheRepeatedTexture()
    {
        _engine.Noise = new NoiseSettings { Resolution = Size };
        _engine.TransparentBase = true;
        _engine.Preview = new PreviewSettings { Mode = PreviewMode.Tile4x4 };
        _engine.ReadComposite();
        var mapping = _engine.CreateCanvasMapping(112, 112)!;

        // 表示の 1/8 の位置は、テクスチャの中央に当たる
        var (x, y) = mapping.ToStrokeSpace(12 + 88 / 8.0, 12 + 88 / 8.0);
        var dab = mapping.ToDab(x, y, Brush(1, 0, 0))!.Value;
        Assert.InRange(dab.X, 31, 33);
        Assert.InRange(dab.Y, 31, 33);
    }

    [Fact]
    public void FreshProject_WithoutChanges_HasNothingToSave()
    {
        Assert.Null(_engine.ReadLayers());
        _engine.SetLayerBlend(_engine.ActiveLayerId, LayerBlendMode.Add);
        Assert.NotNull(_engine.ReadLayers());
    }
}
