using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace UskEffect.Graphics.Painting;

// cbuffer PaintParams (Shaders/Paint/Paint.hlsl) と同じレイアウト
[StructLayout(LayoutKind.Sequential)]
internal struct PaintConstants
{
    public float CanvasWidth;
    public float CanvasHeight;
    public float DabX;
    public float DabY;

    public float DabRadius;
    public float Hardness;
    public float Flow;
    public float BlurRadius;

    public float ColorR;
    public float ColorG;
    public float ColorB;
    public uint SourceIsColor;
}

/// <summary>
/// 編集レイヤーへのスタンプと、元の画像との合成 (FR-14)。
/// 元のノイズや画像は変更しない。レイヤーは乗算済みアルファで、薄いスタンプが消えないよう 16bit float。
/// 合成は変わった範囲だけやり直す。
/// </summary>
public sealed unsafe class PaintRenderer : IDisposable
{
    private const Format PixelFormat = Format.R16G16B16A16_Float;
    private const int ComponentsPerPixel = 4;

    private readonly GpuDevice _gpu;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _clampSampler;
    private readonly ID3D11BlendState _overBlend;
    private readonly ID3D11BlendState _eraseBlend;
    private readonly ID3D11RasterizerState _scissorRaster;

    private ID3D11VertexShader? _fullVertexShader;
    private ID3D11VertexShader? _dabVertexShader;
    private ID3D11PixelShader? _brushShader;
    private ID3D11PixelShader? _blurShader;
    private ID3D11PixelShader? _compositeShader;
    private ID3D11PixelShader? _resampleShader;
    private ID3D11PixelShader? _cursorShader;

    private ID3D11Texture2D? _layer;
    private ID3D11RenderTargetView? _layerTarget;
    private ID3D11ShaderResourceView? _layerView;
    private ID3D11Texture2D? _composite;
    private ID3D11RenderTargetView? _compositeTarget;

    // 合成をやり直す範囲。_dirtyRight <= _dirtyLeft なら無し
    private int _dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>レイヤーに何か描かれたか。消しゴムで消し切っても true のまま。</summary>
    public bool HasPaint { get; private set; }

    public ID3D11ShaderResourceView? CompositeView { get; private set; }

    public PaintRenderer(GpuDevice gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<PaintConstants>(), BindFlags.ConstantBuffer));
        _clampSampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
        _overBlend = device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
        _eraseBlend = device.CreateBlendState(new BlendDescription(Blend.Zero, Blend.InverseSourceAlpha, Blend.Zero, Blend.InverseSourceAlpha));
        _scissorRaster = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { ScissorEnable = true });
    }

    public string? LoadShader(string path)
    {
        var fullVs = ShaderCompiler.CompileFile(path, "VSFull", "vs_5_0");
        var dabVs = ShaderCompiler.CompileFile(path, "VSDab", "vs_5_0");
        var brush = ShaderCompiler.CompileFile(path, "PSBrush", "ps_5_0");
        var blur = ShaderCompiler.CompileFile(path, "PSBlur", "ps_5_0");
        var composite = ShaderCompiler.CompileFile(path, "PSComposite", "ps_5_0");
        var resample = ShaderCompiler.CompileFile(path, "PSResample", "ps_5_0");
        var cursor = ShaderCompiler.CompileFile(path, "PSCursor", "ps_5_0");

        var all = new[] { fullVs, dabVs, brush, blur, composite, resample, cursor };
        if (all.Any(r => !r.Succeeded))
            return string.Join(Environment.NewLine, all.Select(r => r.Errors).Where(e => e is not null).Distinct());

        DisposeShaders();
        var device = _gpu.Device;
        _fullVertexShader = device.CreateVertexShader(fullVs.Bytecode!);
        _dabVertexShader = device.CreateVertexShader(dabVs.Bytecode!);
        _brushShader = device.CreatePixelShader(brush.Bytecode!);
        _blurShader = device.CreatePixelShader(blur.Bytecode!);
        _compositeShader = device.CreatePixelShader(composite.Bytecode!);
        _resampleShader = device.CreatePixelShader(resample.Bytecode!);
        _cursorShader = device.CreatePixelShader(cursor.Bytecode!);
        return null;
    }

    /// <summary>レイヤーを元の画像と同じ大きさにする。描いた内容は拡大縮小して引き継ぐ。</summary>
    public void EnsureSize(int width, int height)
    {
        if (_layer is not null && width == Width && height == Height)
            return;

        var (oldLayer, oldTarget, oldView) = (_layer, _layerTarget, _layerView);
        ReleaseComposite();
        (_layer, _layerTarget, _layerView) = CreateLayerTextures(width, height, null);
        _composite = _gpu.Device.CreateTexture2D(TextureDescription(width, height));
        _compositeTarget = _gpu.Device.CreateRenderTargetView(_composite);
        CompositeView = _gpu.Device.CreateShaderResourceView(_composite);
        Width = width;
        Height = height;

        var context = _gpu.Context;
        context.ClearRenderTargetView(_layerTarget!, new Color4(0, 0, 0, 0));
        if (oldView is not null && HasPaint && _fullVertexShader is not null && _resampleShader is not null)
        {
            SetCommonState(null);
            context.OMSetRenderTargets(_layerTarget!);
            context.RSSetViewport(0, 0, width, height);
            context.VSSetShader(_fullVertexShader);
            context.PSSetShader(_resampleShader);
            context.PSSetShaderResource(1, oldView);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.Draw(3, 0);
            ResetState();
        }
        oldView?.Dispose();
        oldTarget?.Dispose();
        oldLayer?.Dispose();
        MarkAllDirty();
    }

    /// <summary>元の画像が変わったので、次の合成で全体をやり直す。</summary>
    public void MarkAllDirty()
    {
        _dirtyLeft = 0;
        _dirtyTop = 0;
        _dirtyRight = Width;
        _dirtyBottom = Height;
    }

    public void Clear()
    {
        HasPaint = false;
        if (_layerTarget is not null)
            _gpu.Context.ClearRenderTargetView(_layerTarget, new Color4(0, 0, 0, 0));
        MarkAllDirty();
    }

    /// <summary>保存された画像 (乗算なし) をレイヤーにする。次の EnsureSize で元の画像の大きさに合わせる。</summary>
    public void SetLayerImage(ImageData image)
    {
        var data = new Half[image.Width * image.Height * ComponentsPerPixel];
        var rgba = image.Rgba;
        for (int i = 0; i < rgba.Length; i += ComponentsPerPixel)
        {
            float a = rgba[i + 3] / 255f;
            data[i] = (Half)(rgba[i] / 255f * a);
            data[i + 1] = (Half)(rgba[i + 1] / 255f * a);
            data[i + 2] = (Half)(rgba[i + 2] / 255f * a);
            data[i + 3] = (Half)a;
        }

        ReleaseComposite();
        ReleaseLayer();
        fixed (Half* pointer = data)
        {
            (_layer, _layerTarget, _layerView) = CreateLayerTextures(image.Width, image.Height,
                new SubresourceData((nint)pointer, (uint)(image.Width * ComponentsPerPixel * sizeof(Half))));
        }
        _composite = _gpu.Device.CreateTexture2D(TextureDescription(image.Width, image.Height));
        _compositeTarget = _gpu.Device.CreateRenderTargetView(_composite);
        CompositeView = _gpu.Device.CreateShaderResourceView(_composite);
        Width = image.Width;
        Height = image.Height;
        HasPaint = true;
        MarkAllDirty();
    }

    /// <summary>スタンプを順に置く。ぼかしは直前のスタンプの結果を読むので、1 つごとに合成し直す。</summary>
    public void Stamp(BrushSettings brush, IReadOnlyList<PaintDab> dabs, ID3D11ShaderResourceView source, bool sourceIsColor)
    {
        if (_layerTarget is null || _dabVertexShader is null || _brushShader is null || _blurShader is null || dabs.Count == 0)
            return;

        var context = _gpu.Context;
        bool blur = brush.Tool == PaintTool.Blur;
        float radius = (float)(brush.Size / 2);
        foreach (var dab in dabs)
        {
            if (blur)
                UpdateComposite(source, sourceIsColor);

            var constants = new PaintConstants
            {
                CanvasWidth = Width,
                CanvasHeight = Height,
                DabX = (float)dab.X,
                DabY = (float)dab.Y,
                DabRadius = radius,
                Hardness = (float)Math.Clamp(brush.Hardness, 0, 1),
                Flow = (float)brush.Flow,
                BlurRadius = Math.Max(1f, radius * 0.5f),
                ColorR = brush.Color.X,
                ColorG = brush.Color.Y,
                ColorB = brush.Color.Z,
                SourceIsColor = sourceIsColor ? 1u : 0u,
            };
            context.UpdateSubresource(constants, _constantBuffer);

            SetCommonState(brush.Tool == PaintTool.Eraser ? _eraseBlend : _overBlend);
            context.OMSetRenderTargets(_layerTarget);
            context.RSSetViewport(0, 0, Width, Height);
            context.VSSetShader(_dabVertexShader);
            context.VSSetConstantBuffer(0, _constantBuffer);
            context.PSSetShader(blur ? _blurShader : _brushShader);
            if (blur)
                context.PSSetShaderResource(2, CompositeView!);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            context.Draw(4, 0);
            ResetState();

            AddDirty(dab.X, dab.Y, radius + 3);
        }
        HasPaint = true;
    }

    /// <summary>変わった範囲の合成をやり直す。</summary>
    public void UpdateComposite(ID3D11ShaderResourceView source, bool sourceIsColor)
    {
        if (_compositeTarget is null || _fullVertexShader is null || _compositeShader is null || _dirtyRight <= _dirtyLeft || _dirtyBottom <= _dirtyTop)
            return;

        var constants = new PaintConstants
        {
            CanvasWidth = Width,
            CanvasHeight = Height,
            SourceIsColor = sourceIsColor ? 1u : 0u,
        };
        var context = _gpu.Context;
        context.UpdateSubresource(constants, _constantBuffer);

        SetCommonState(null);
        context.OMSetRenderTargets(_compositeTarget);
        context.RSSetViewport(0, 0, Width, Height);
        context.RSSetState(_scissorRaster);
        context.RSSetScissorRect(_dirtyLeft, _dirtyTop, _dirtyRight - _dirtyLeft, _dirtyBottom - _dirtyTop);
        context.VSSetShader(_fullVertexShader);
        context.PSSetShader(_compositeShader);
        context.PSSetShaderResource(0, source);
        context.PSSetShaderResource(1, _layerView!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.Draw(3, 0);
        ResetState();

        _dirtyRight = _dirtyLeft;
        _dirtyBottom = _dirtyTop;
    }

    /// <summary>ブラシの大きさを示す輪を描く。座標は画面の画素。</summary>
    public void DrawCursor(ID3D11RenderTargetView target, int width, int height, float x, float y, float radius)
    {
        if (_dabVertexShader is null || _cursorShader is null)
            return;

        var constants = new PaintConstants
        {
            CanvasWidth = width,
            CanvasHeight = height,
            DabX = x,
            DabY = y,
            DabRadius = radius,
        };
        var context = _gpu.Context;
        context.UpdateSubresource(constants, _constantBuffer);

        SetCommonState(_overBlend);
        context.OMSetRenderTargets(target);
        context.RSSetViewport(0, 0, width, height);
        context.VSSetShader(_dabVertexShader);
        context.VSSetConstantBuffer(0, _constantBuffer);
        context.PSSetShader(_cursorShader);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.Draw(4, 0);
        ResetState();
    }

    /// <summary>合成結果を読み戻す (乗算なし)。</summary>
    public ImageData? ReadComposite() => _composite is null ? null : ReadBack(_composite, premultiplied: false);

    /// <summary>レイヤーを読み戻す (乗算なしへ戻す)。保存用。</summary>
    public ImageData? ReadLayer() => _layer is null || !HasPaint ? null : ReadBack(_layer, premultiplied: true);

    private ImageData ReadBack(ID3D11Texture2D texture, bool premultiplied)
    {
        var description = TextureDescription(Width, Height);
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _gpu.Device.CreateTexture2D(description);
        var context = _gpu.Context;
        context.CopyResource(staging, texture);

        var rgba = new byte[Width * Height * ComponentsPerPixel];
        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                var row = new ReadOnlySpan<Half>((void*)(mapped.DataPointer + (nint)(y * mapped.RowPitch)), Width * ComponentsPerPixel);
                int output = y * Width * ComponentsPerPixel;
                for (int x = 0; x < Width * ComponentsPerPixel; x += ComponentsPerPixel, output += ComponentsPerPixel)
                {
                    float a = (float)row[x + 3];
                    float divide = premultiplied && a > 0 ? 1f / a : 1f;
                    rgba[output] = ToByte((float)row[x] * divide);
                    rgba[output + 1] = ToByte((float)row[x + 1] * divide);
                    rgba[output + 2] = ToByte((float)row[x + 2] * divide);
                    rgba[output + 3] = ToByte(a);
                }
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }
        return new ImageData(Width, Height, rgba);
    }

    private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    private void AddDirty(double x, double y, double radius)
    {
        int left = Math.Clamp((int)Math.Floor(x - radius), 0, Width);
        int top = Math.Clamp((int)Math.Floor(y - radius), 0, Height);
        int right = Math.Clamp((int)Math.Ceiling(x + radius), 0, Width);
        int bottom = Math.Clamp((int)Math.Ceiling(y + radius), 0, Height);
        if (_dirtyRight <= _dirtyLeft || _dirtyBottom <= _dirtyTop)
        {
            (_dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom) = (left, top, right, bottom);
            return;
        }
        _dirtyLeft = Math.Min(_dirtyLeft, left);
        _dirtyTop = Math.Min(_dirtyTop, top);
        _dirtyRight = Math.Max(_dirtyRight, right);
        _dirtyBottom = Math.Max(_dirtyBottom, bottom);
    }

    private void SetCommonState(ID3D11BlendState? blend)
    {
        var context = _gpu.Context;
        context.OMSetBlendState(blend);
        context.IASetInputLayout(null);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.VSSetConstantBuffer(0, _constantBuffer);
        context.PSSetSampler(0, _clampSampler);
    }

    // 他の描画に影響を残さない。読み込みと書き込みが同時にならないようテクスチャも外す
    private void ResetState()
    {
        var context = _gpu.Context;
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        context.PSSetShaderResource(0, null!);
        context.PSSetShaderResource(1, null!);
        context.PSSetShaderResource(2, null!);
        context.OMSetBlendState(null);
        context.RSSetState(null);
    }

    private static Texture2DDescription TextureDescription(int width, int height) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = PixelFormat,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
    };

    private (ID3D11Texture2D, ID3D11RenderTargetView, ID3D11ShaderResourceView) CreateLayerTextures(int width, int height, SubresourceData? initial)
    {
        var device = _gpu.Device;
        var description = TextureDescription(width, height);
        var texture = initial is { } data ? device.CreateTexture2D(description, data) : device.CreateTexture2D(description);
        return (texture, device.CreateRenderTargetView(texture), device.CreateShaderResourceView(texture));
    }

    private void ReleaseLayer()
    {
        _layerView?.Dispose();
        _layerTarget?.Dispose();
        _layer?.Dispose();
        _layerView = null;
        _layerTarget = null;
        _layer = null;
    }

    private void ReleaseComposite()
    {
        CompositeView?.Dispose();
        _compositeTarget?.Dispose();
        _composite?.Dispose();
        CompositeView = null;
        _compositeTarget = null;
        _composite = null;
    }

    private void DisposeShaders()
    {
        _fullVertexShader?.Dispose();
        _dabVertexShader?.Dispose();
        _brushShader?.Dispose();
        _blurShader?.Dispose();
        _compositeShader?.Dispose();
        _resampleShader?.Dispose();
        _cursorShader?.Dispose();
    }

    public void Dispose()
    {
        ReleaseComposite();
        ReleaseLayer();
        DisposeShaders();
        _scissorRaster.Dispose();
        _eraseBlend.Dispose();
        _overBlend.Dispose();
        _clampSampler.Dispose();
        _constantBuffer.Dispose();
    }
}
