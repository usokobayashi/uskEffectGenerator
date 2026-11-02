using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UskEffect.Graphics;

/// <summary>子 HWND に結び付いたフリップモデルのスワップチェーン。</summary>
public sealed class SwapChainTarget : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11RenderTargetView RenderTargetView => _renderTargetView!;

    public SwapChainTarget(GpuDevice gpu, IntPtr hwnd, int width, int height)
    {
        _gpu = gpu;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore,
        };
        _swapChain = gpu.Factory.CreateSwapChainForHwnd(gpu.Device, hwnd, desc);
        gpu.Factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        CreateRenderTargetView();
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
            return;

        _gpu.Context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _gpu.Context.Flush();

        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        Width = width;
        Height = height;
        CreateRenderTargetView();
    }

    public void Present(bool vsync) => _swapChain.Present(vsync ? 1u : 0u, PresentFlags.None);

    private void CreateRenderTargetView()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _gpu.Device.CreateRenderTargetView(backBuffer);
    }

    public void Dispose()
    {
        _renderTargetView?.Dispose();
        _swapChain.Dispose();
    }
}
