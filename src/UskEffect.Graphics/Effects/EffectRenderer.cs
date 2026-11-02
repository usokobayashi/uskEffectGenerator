using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace UskEffect.Graphics.Effects;

/// <summary>エフェクトプレビューを描く。背景を不透明で描いたあと、エフェクトを Alpha / Additive で合成する。</summary>
public sealed class EffectRenderer : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _wrapSampler;

    // シェーダーは乗算済みアルファを出力する
    private readonly ID3D11BlendState _alphaBlend;
    private readonly ID3D11BlendState _additiveBlend;

    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _backgroundShader;
    private ID3D11PixelShader? _effectShader;

    public EffectRenderer(GpuDevice gpu)
    {
        _gpu = gpu;
        _constantBuffer = gpu.Device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<EffectConstants>(), BindFlags.ConstantBuffer));
        _wrapSampler = gpu.Device.CreateSamplerState(SamplerDescription.LinearWrap);
        _alphaBlend = gpu.Device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha));
        _additiveBlend = gpu.Device.CreateBlendState(new BlendDescription(Blend.One, Blend.One));
    }

    public string? LoadShader(string path)
    {
        var vs = ShaderCompiler.CompileFile(path, "VSMain", "vs_5_0");
        var bg = ShaderCompiler.CompileFile(path, "PSBackground", "ps_5_0");
        var fx = ShaderCompiler.CompileFile(path, "PSEffect", "ps_5_0");
        if (!vs.Succeeded || !bg.Succeeded || !fx.Succeeded)
            return string.Join(Environment.NewLine, new[] { vs.Errors, bg.Errors, fx.Errors }.Where(e => e is not null).Distinct());

        _vertexShader?.Dispose();
        _backgroundShader?.Dispose();
        _effectShader?.Dispose();
        _vertexShader = _gpu.Device.CreateVertexShader(vs.Bytecode!);
        _backgroundShader = _gpu.Device.CreatePixelShader(bg.Bytecode!);
        _effectShader = _gpu.Device.CreatePixelShader(fx.Bytecode!);
        return null;
    }

    public void Render(ID3D11RenderTargetView target, int width, int height, ID3D11ShaderResourceView? source,
        EffectSettings effect, PolarSettings polar, EffectFrame frame)
    {
        var context = _gpu.Context;
        context.OMSetRenderTargets(target);
        context.ClearRenderTargetView(target, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));

        if (_vertexShader is null || _backgroundShader is null || _effectShader is null)
            return;

        uint flags = (effect.Mapping == EffectMapping.Polar ? 1u : 0u)
                   | (effect.TextureAsAlpha ? 2u : 0u)
                   | (effect.Mask == EffectMask.SoftCircle ? 4u : 0u);
        var constants = new EffectConstants
        {
            ViewportWidth = width,
            ViewportHeight = height,
            Rotation = frame.RotationRadians,
            Scale = frame.Scale,
            ScrollOffset = frame.ScrollOffset,
            DissolveThreshold = frame.DissolveThreshold,
            DissolveEdge = Math.Max(0f, effect.DissolveEdgeWidth),
            Color = frame.Color,
            Alpha = frame.Alpha,
            EdgeColor = effect.DissolveEdgeColor,
            Flags = flags,
            InnerRadius = polar.InnerRadius,
            OuterRadius = polar.OuterRadius,
            PolarRotation = polar.RotationDegrees * (MathF.PI / 180f),
            PolarFlags = (polar.FlipAngular ? 1u : 0u) | (polar.FlipRadial ? 2u : 0u),
            BackgroundMode = (uint)effect.Background,
            DissolveSoftness = EffectEvaluator.DissolveSoftness,
        };
        context.UpdateSubresource(constants, _constantBuffer);

        context.RSSetViewport(0, 0, width, height);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.PSSetSampler(0, _wrapSampler);

        // 背景 (不透明)
        context.OMSetBlendState(null);
        context.PSSetShader(_backgroundShader);
        context.Draw(3, 0);

        if (source is null)
            return;

        // エフェクト
        context.OMSetBlendState(effect.Blend == EffectBlend.Additive ? _additiveBlend : _alphaBlend);
        context.PSSetShader(_effectShader);
        context.PSSetShaderResource(0, source);
        context.Draw(3, 0);

        context.PSSetShaderResource(0, null!);
        context.OMSetBlendState(null);
    }

    public void Dispose()
    {
        _vertexShader?.Dispose();
        _backgroundShader?.Dispose();
        _effectShader?.Dispose();
        _alphaBlend.Dispose();
        _additiveBlend.Dispose();
        _wrapSampler.Dispose();
        _constantBuffer.Dispose();
    }

    // cbuffer EffectParams (Shaders/Effect.hlsl) と同じレイアウト
    [StructLayout(LayoutKind.Sequential)]
    private struct EffectConstants
    {
        public float ViewportWidth;
        public float ViewportHeight;
        public float Rotation;
        public float Scale;

        public Vector2 ScrollOffset;
        public float DissolveThreshold;
        public float DissolveEdge;

        public Vector3 Color;
        public float Alpha;

        public Vector3 EdgeColor;
        public uint Flags;

        public float InnerRadius;
        public float OuterRadius;
        public float PolarRotation;
        public uint PolarFlags;

        public uint BackgroundMode;
        public float DissolveSoftness;
        public Vector2 Padding;
    }
}
