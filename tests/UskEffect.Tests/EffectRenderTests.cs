using System.Numerics;
using UskEffect.Graphics;
using UskEffect.Graphics.Effects;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UskEffect.Tests;

/// <summary>エフェクトを 64×64 のオフスクリーンに描き、BGRA で読み戻す。</summary>
public sealed unsafe class EffectRenderFixture : IDisposable
{
    public const int Size = 64;

    private readonly GpuDevice _gpu = new();
    private readonly NoiseGenerator _noise;
    private readonly EffectRenderer _renderer;
    private readonly ID3D11Texture2D _target;
    private readonly ID3D11RenderTargetView _targetView;
    private readonly ID3D11Texture2D _staging;
    private readonly Lock _sync = new();

    public EffectRenderFixture()
    {
        string shaders = Path.Combine(NoiseFixture.FindRepositoryRoot(), "Shaders");
        _noise = new NoiseGenerator(_gpu);
        _renderer = new EffectRenderer(_gpu);
        string? errors = _noise.LoadShader(Path.Combine(shaders, "Noise", "PeriodicNoise.hlsl"))
                         ?? _renderer.LoadShader(Path.Combine(shaders, "Effect.hlsl"));
        if (errors is not null)
            throw new InvalidOperationException(errors);

        // コントラスト 0・明るさ +0.5 で全画素 1.0 の一様なテクスチャにする
        _noise.Generate(new NoiseSettings { Resolution = 64, Contrast = 0f, Brightness = 0.5f });

        var desc = new Texture2DDescription
        {
            Width = Size,
            Height = Size,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        };
        _target = _gpu.Device.CreateTexture2D(desc);
        _targetView = _gpu.Device.CreateRenderTargetView(_target);
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        _staging = _gpu.Device.CreateTexture2D(desc);
    }

    /// <summary>描画して画素 (x, y) の RGB を 0..1 で返す。y は上から。</summary>
    public Func<int, int, Vector3> Render(EffectSettings effect, EffectFrame frame, PolarSettings? polar = null)
    {
        lock (_sync)
        {
            _renderer.Render(_targetView, Size, Size, _noise.ShaderResourceView, effect, polar ?? new PolarSettings(), frame);
            _gpu.Context.CopyResource(_staging, _target);

            var mapped = _gpu.Context.Map(_staging, 0, MapMode.Read);
            var bytes = new byte[Size * Size * 4];
            try
            {
                for (int y = 0; y < Size; y++)
                    new ReadOnlySpan<byte>((void*)(mapped.DataPointer + (nint)(y * mapped.RowPitch)), Size * 4)
                        .CopyTo(bytes.AsSpan(y * Size * 4));
            }
            finally
            {
                _gpu.Context.Unmap(_staging, 0);
            }

            return (x, y) =>
            {
                int i = (y * Size + x) * 4;
                return new Vector3(bytes[i + 2], bytes[i + 1], bytes[i]) / 255f;
            };
        }
    }

    public void Dispose()
    {
        _staging.Dispose();
        _targetView.Dispose();
        _target.Dispose();
        _renderer.Dispose();
        _noise.Dispose();
        _gpu.Dispose();
    }
}

public sealed class EffectRenderTests(EffectRenderFixture fixture) : IClassFixture<EffectRenderFixture>
{
    private const int Center = EffectRenderFixture.Size / 2;
    private const float Tolerance = 2.5f / 255f;

    private static readonly Vector3 Red = new(1, 0, 0);

    private static EffectSettings Plain(EffectBlend blend, EffectBackground background) => new()
    {
        Blend = blend,
        Background = background,
        Mask = EffectMask.None,
        DissolveEdgeWidth = 0f,
    };

    private static EffectFrame Frame(float alpha = 1f, float rotation = 0f, float scale = 1f, float dissolve = 0f, float edge = 0f) =>
        new(rotation * (MathF.PI / 180f), scale, EffectEvaluator.DissolveThreshold(dissolve, edge), alpha, Red, Vector2.Zero);

    private static void AssertColor(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) <= Tolerance * 1.8f, $"期待 {expected}、実際 {actual}");
    }

    [Fact]
    public void Additive_OnBlack_ShowsColorAtCenter_AndBackgroundOutside()
    {
        var pixel = fixture.Render(Plain(EffectBlend.Additive, EffectBackground.Black), Frame());

        AssertColor(Red, pixel(Center, Center));
        AssertColor(Vector3.Zero, pixel(0, 0));
    }

    [Fact]
    public void AlphaAndAdditive_DifferOnGray()
    {
        // 灰 (0.5) の上に半透明 (0.5) の赤
        var alpha = fixture.Render(Plain(EffectBlend.Alpha, EffectBackground.Gray), Frame(alpha: 0.5f))(Center, Center);
        var additive = fixture.Render(Plain(EffectBlend.Additive, EffectBackground.Gray), Frame(alpha: 0.5f))(Center, Center);

        // Alpha: 0.5 × 赤 + 0.5 × 灰 / Additive: 灰 + 0.5 × 赤
        AssertColor(new Vector3(0.75f, 0.25f, 0.25f), alpha);
        AssertColor(new Vector3(1f, 0.5f, 0.5f), additive);
    }

    [Fact]
    public void FullDissolve_ShowsOnlyBackground()
    {
        var pixel = fixture.Render(Plain(EffectBlend.Additive, EffectBackground.Black), Frame(dissolve: 1f));

        AssertColor(Vector3.Zero, pixel(Center, Center));
    }

    [Fact]
    public void DissolveEdge_MixesEdgeColor()
    {
        // テクスチャ値 1.0 がちょうど発光帯に入るディゾルブ量
        var effect = Plain(EffectBlend.Additive, EffectBackground.Black) with
        {
            DissolveEdgeWidth = 0.1f,
            DissolveEdgeColor = new Vector3(0, 1, 0),
        };
        var frame = Frame() with { DissolveThreshold = 0.95f };

        var color = fixture.Render(effect, frame)(Center, Center);

        // 発光帯の中 (n - しきい値 = 0.05 で帯の半分) なので赤と緑が半々
        AssertColor(new Vector3(0.5f, 0.5f, 0f), color);
    }

    [Fact]
    public void Scale_ShrinksSprite()
    {
        // 中心から半径の 75% の位置: 等倍なら内側、0.5 倍なら外側
        int x = Center + (int)(0.75f * EffectRenderFixture.Size * 0.45f);
        var effect = Plain(EffectBlend.Additive, EffectBackground.Black);

        AssertColor(Red, fixture.Render(effect, Frame(scale: 1f))(x, Center));
        AssertColor(Vector3.Zero, fixture.Render(effect, Frame(scale: 0.5f))(x, Center));
    }

    [Fact]
    public void Rotation_TurnsSpriteCorner()
    {
        // 正方形の右上の角付近 (p ≈ (0.85, 0.85)) は 45° 回すと正方形の外に出る
        const int x = 56, y = 7;
        var effect = Plain(EffectBlend.Additive, EffectBackground.Black);

        AssertColor(Red, fixture.Render(effect, Frame(rotation: 0f))(x, y));
        AssertColor(Vector3.Zero, fixture.Render(effect, Frame(rotation: 45f))(x, y));
    }

    [Fact]
    public void PolarMapping_LeavesInnerHoleEmpty()
    {
        var effect = Plain(EffectBlend.Additive, EffectBackground.Black) with { Mapping = EffectMapping.Polar };
        var pixel = fixture.Render(effect, Frame(), new PolarSettings(0.5f, 1f));

        AssertColor(Vector3.Zero, pixel(Center, Center));
        AssertColor(Red, pixel(Center + (int)(0.75f * EffectRenderFixture.Size * 0.45f), Center));
    }
}
