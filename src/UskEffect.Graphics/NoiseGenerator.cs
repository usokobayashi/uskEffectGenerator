using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UskEffect.Graphics;

/// <summary>Compute Shader で周期ノイズを R32_Float テクスチャへ生成する。</summary>
public sealed unsafe class NoiseGenerator : IDisposable
{
    private const int ThreadGroupSize = 8;

    private readonly GpuDevice _gpu;
    private readonly ID3D11Buffer _constantBuffer;
    private ID3D11ComputeShader? _shader;
    private ID3D11Texture2D? _texture;
    private ID3D11UnorderedAccessView? _uav;

    public ID3D11ShaderResourceView? ShaderResourceView { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public NoiseGenerator(GpuDevice gpu)
    {
        _gpu = gpu;
        _constantBuffer = gpu.Device.CreateBuffer(new BufferDescription(
            (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NoiseConstants>(), BindFlags.ConstantBuffer));
    }

    /// <summary>シェーダーを再コンパイルする。失敗時は直前のシェーダーを使い続け、エラー文字列を返す。</summary>
    public string? LoadShader(string path)
    {
        var compiled = ShaderCompiler.CompileFile(path, "CSMain", "cs_5_0");
        if (!compiled.Succeeded)
            return compiled.Errors;

        _shader?.Dispose();
        _shader = _gpu.Device.CreateComputeShader(compiled.Bytecode!);
        return null;
    }

    public void Generate(NoiseSettings settings)
    {
        if (_shader is null)
            return;

        EnsureTexture(settings.Resolution, settings.Resolution);

        var constants = new NoiseConstants
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Seed = settings.Seed,
            Octaves = (uint)Math.Clamp(settings.Octaves, 1, 8),
            Frequency = Math.Max(1, settings.Frequency),
            Persistence = settings.Persistence,
            SeamMode = (uint)settings.Seam,
            NoiseType = (uint)settings.Type,
            Lacunarity = Math.Clamp(settings.Lacunarity, 1, 8),
            FractalMode = (uint)settings.Fractal,
            WorleyMode = (uint)settings.Worley,
            WarpStrength = Math.Max(0f, settings.WarpStrength),
            Contrast = settings.Contrast,
            Brightness = settings.Brightness,
            Invert = settings.Invert ? 1u : 0u,
            StripeDirection = (uint)settings.StripeDirection,
            StripeWidth = settings.StripeWidth,
            StripeSoftness = settings.StripeSoftness,
        };

        var context = _gpu.Context;
        context.UpdateSubresource(constants, _constantBuffer);
        context.CSSetShader(_shader);
        context.CSSetConstantBuffer(0, _constantBuffer);
        context.CSSetUnorderedAccessView(0, _uav);
        context.Dispatch(
            (uint)((Width + ThreadGroupSize - 1) / ThreadGroupSize),
            (uint)((Height + ThreadGroupSize - 1) / ThreadGroupSize),
            1);
        // SRV として読む前に UAV を外す
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShader(null);
    }

    /// <summary>生成結果を CPU へ読み戻す (行優先、Width × Height)。テストと画像出力用で、GPU の完了を待つ。</summary>
    public float[] ReadPixels()
    {
        if (_texture is null)
            return [];

        using var staging = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        var context = _gpu.Context;
        context.CopyResource(staging, _texture);

        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            var pixels = new float[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                var row = new ReadOnlySpan<float>((void*)(mapped.DataPointer + (nint)(y * mapped.RowPitch)), Width);
                row.CopyTo(pixels.AsSpan(y * Width, Width));
            }
            return pixels;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private void EnsureTexture(int width, int height)
    {
        if (_texture is not null && width == Width && height == Height)
            return;

        ReleaseTexture();
        _texture = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        });
        _uav = _gpu.Device.CreateUnorderedAccessView(_texture);
        ShaderResourceView = _gpu.Device.CreateShaderResourceView(_texture);
        Width = width;
        Height = height;
    }

    private void ReleaseTexture()
    {
        ShaderResourceView?.Dispose();
        _uav?.Dispose();
        _texture?.Dispose();
        ShaderResourceView = null;
        _uav = null;
        _texture = null;
    }

    public void Dispose()
    {
        ReleaseTexture();
        _shader?.Dispose();
        _constantBuffer.Dispose();
    }
}
