using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using UskEffect.Graphics.Analysis;

namespace UskEffect.Graphics;

/// <summary>ソーステクスチャを矩形・極座標・シーム検証のいずれかで描画する。</summary>
public sealed class PreviewRenderer : IDisposable
{
    // キャンバスの周りの色。UI の Ue.Stage (#111417) と同じ値
    private static readonly Color4 StageColor = new(0x11 / 255f, 0x14 / 255f, 0x17 / 255f, 1f);

    private readonly GpuDevice _gpu;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _wrapSampler;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;

    public PreviewRenderer(GpuDevice gpu)
    {
        _gpu = gpu;
        _constantBuffer = gpu.Device.CreateBuffer(new BufferDescription(
            (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<PreviewConstants>(), BindFlags.ConstantBuffer));
        _wrapSampler = gpu.Device.CreateSamplerState(SamplerDescription.LinearWrap);
    }

    public string? LoadShader(string path)
    {
        var vs = ShaderCompiler.CompileFile(path, "VSMain", "vs_5_0");
        var ps = ShaderCompiler.CompileFile(path, "PSMain", "ps_5_0");
        if (!vs.Succeeded || !ps.Succeeded)
            return string.Join(Environment.NewLine, new[] { vs.Errors, ps.Errors }.Where(e => e is not null).Distinct());

        _vertexShader?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader = _gpu.Device.CreateVertexShader(vs.Bytecode!);
        _pixelShader = _gpu.Device.CreatePixelShader(ps.Bytecode!);
        return null;
    }

    public void Render(ID3D11RenderTargetView target, int width, int height, ID3D11ShaderResourceView? source,
        int sourceWidth, int sourceHeight, bool sourceIsColor,
        PreviewSettings settings, float timeSeconds, SeamAnalysis? seam, float contentMargin, CanvasView? view = null)
    {
        view ??= new CanvasView();
        var context = _gpu.Context;
        context.OMSetRenderTargets(target);
        context.ClearRenderTargetView(target, StageColor);

        if (_vertexShader is null || _pixelShader is null || source is null)
            return;

        var constants = new PreviewConstants
        {
            ViewportWidth = width,
            ViewportHeight = height,
            TextureWidth = sourceWidth,
            TextureHeight = sourceHeight,
            ViewMode = (uint)settings.Mode,
            Time = timeSeconds,
            ScrollSpeedU = settings.ScrollSpeedU,
            ScrollSpeedV = settings.ScrollSpeedV,
            InnerRadius = settings.Polar.InnerRadius,
            OuterRadius = settings.Polar.OuterRadius,
            RotationRadians = settings.Polar.RotationDegrees * (MathF.PI / 180f),
            PolarFlags = (settings.Polar.FlipAngular ? 1u : 0u) | (settings.Polar.FlipRadial ? 2u : 0u),
            // 解析前はヒートマップを出さない
            HeatmapMode = seam is null ? 0u : (uint)settings.Heatmap,
            MeanValueDiff = (float)(seam?.MeanValueDiff ?? 1.0),
            MeanGradientDiff = (float)(seam?.MeanGradientDiff ?? 1.0),
            HeatThreshold = Math.Max(1f, settings.HeatThreshold),
            StageR = StageColor.R,
            StageG = StageColor.G,
            StageB = StageColor.B,
            ContentMargin = contentMargin,
            ViewPanX = (float)view.PanX,
            ViewPanY = (float)view.PanY,
            ViewZoom = (float)view.Zoom,
            ViewRotation = (float)(view.RotationDegrees * Math.PI / 180),
            SourceIsColor = sourceIsColor ? 1u : 0u,
            TilingX = settings.TilingX,
            TilingY = settings.TilingY,
            OffsetX = settings.OffsetX,
            OffsetY = settings.OffsetY,
        };
        context.UpdateSubresource(constants, _constantBuffer);

        context.RSSetViewport(0, 0, width, height);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.PSSetShaderResource(0, source);
        context.PSSetSampler(0, _wrapSampler);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null!);
    }

    public void Dispose()
    {
        _vertexShader?.Dispose();
        _pixelShader?.Dispose();
        _wrapSampler.Dispose();
        _constantBuffer.Dispose();
    }
}
