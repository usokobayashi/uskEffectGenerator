using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UskEffect.Graphics;

/// <summary>D3D11 デバイスと即時コンテキスト、DXGI ファクトリをまとめて保持する。</summary>
public sealed class GpuDevice : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIFactory2 Factory { get; }
    public string AdapterName { get; }
    public FeatureLevel FeatureLevel { get; }

    public GpuDevice(bool enableDebugLayer = false)
    {
        var flags = DeviceCreationFlags.BgraSupport;
        if (enableDebugLayer)
            flags |= DeviceCreationFlags.Debug;

        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, FeatureLevels, out ID3D11Device device, out ID3D11DeviceContext context);
        if (result.Failure && enableDebugLayer)
        {
            // Graphics Tools 未インストール環境ではデバッグレイヤーなしで再試行
            result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, FeatureLevels, out device, out context);
        }
        if (result.Failure)
        {
            // ハードウェア GPU が使えない環境 (リモートデスクトップ・CI) ではソフトウェアラスタライザで動かす
            result = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, FeatureLevels, out device, out context);
        }
        result.CheckError();

        Device = device!;
        Context = context!;
        FeatureLevel = Device.FeatureLevel;

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        dxgiDevice.GetAdapter(out var adapter).CheckError();
        using (adapter)
        {
            AdapterName = adapter.Description.Description;
            Factory = adapter.GetParent<IDXGIFactory2>();
        }
    }

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        Factory.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
