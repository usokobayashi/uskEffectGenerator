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

    public float InnerRadius;
    public float OuterRadius;
    public float CenterRadius;
    public float RadialSign;

    public float PolarTilingX;
    public float PolarTilingY;
    public float BlendWidth;
    public float BlendStrength;

    public uint BlendAxis;
    public uint LayerInvert;
    public uint LayerMask;
    public float Padding2;
}

/// <summary>画面に見せるレイヤーの状態 (FR-15)。リストの先頭が一番下。</summary>
public sealed record LayerState(int Id, string Name, bool Visible, float Opacity,
    LayerBlendMode Blend = LayerBlendMode.Normal, NoiseSettings? Noise = null, bool Invert = false, bool IsMask = false,
    float TilingX = 1f, float TilingY = 1f, float OffsetX = 0f, float OffsetY = 0f)
{
    public bool IsTiled => TilingX != 1f || TilingY != 1f || OffsetX != 0f || OffsetY != 0f;
}

/// <summary>
/// 編集レイヤーへのスタンプ、元の画像との合成、元に戻す (FR-14 / FR-15 / 要件 §3.4)。
/// 元のノイズや画像は変更しない。レイヤーは乗算済みアルファで、薄いスタンプが消えないよう 16bit float。
/// 合成は変わった範囲だけやり直す。描いていないレイヤーは GPU メモリを取らない。
/// 元に戻すは編集した範囲の前後だけを残すので、履歴の量はテクスチャ全体ではなく描いた範囲に比例する。
/// </summary>
public sealed unsafe class PaintRenderer : IDisposable
{
    private const int ThumbnailSize = 32;

    // 生成の変更は、この間隔より短ければ (スライダーの操作中なら) 1 回の「元に戻す」にまとめる
    private const long NoiseEditMergeMilliseconds = 500;

    private const Format PixelFormat = Format.R16G16B16A16_Float;
    private const int ComponentsPerPixel = 4;
    private const int BytesPerPixel = ComponentsPerPixel * sizeof(short);

    // スタンプの縁をなじませる分の余裕 [画素]
    private const double DabMargin = 3;

    // 履歴の上限。超えたら古いものから捨てる
    private const int MaxHistoryEntries = 200;
    private const long MaxHistoryBytes = 1L << 30;

    private sealed class LayerTexture : IDisposable
    {
        public ID3D11Texture2D? Texture;
        public ID3D11RenderTargetView? Target;
        public ID3D11ShaderResourceView? View;
        public int Width, Height;
        public bool HasContent;

        public void Release()
        {
            View?.Dispose();
            Target?.Dispose();
            Texture?.Dispose();
            View = null;
            Target = null;
            Texture = null;
        }

        public void Dispose() => Release();
    }

    private sealed record LayerEntry(LayerState State, LayerTexture Texture);

    private abstract class HistoryEntry : IDisposable
    {
        public virtual long Bytes => 0;
        public virtual void Dispose() { }
    }

    // 画素の変更。範囲の前後だけを残す
    private sealed class PixelEntry(LayerTexture layer, int left, int top, ID3D11Texture2D before, ID3D11Texture2D after,
        bool contentBefore, bool contentAfter, long bytes) : HistoryEntry
    {
        public LayerTexture Layer { get; } = layer;
        public int Left { get; } = left;
        public int Top { get; } = top;
        public ID3D11Texture2D Before { get; } = before;
        public ID3D11Texture2D After { get; } = after;
        public bool ContentBefore { get; } = contentBefore;
        public bool ContentAfter { get; } = contentAfter;
        public override long Bytes { get; } = bytes;

        public override void Dispose()
        {
            Before.Dispose();
            After.Dispose();
        }
    }

    // レイヤーの追加・削除・並び替え・表示・不透明度
    private sealed class StructureEntry(LayerEntry[] before, int activeBefore, LayerEntry[] after, int activeAfter, string? mergeKey) : HistoryEntry
    {
        public LayerEntry[] Before { get; } = before;
        public int ActiveBefore { get; } = activeBefore;
        public LayerEntry[] After { get; set; } = after;
        public int ActiveAfter { get; set; } = activeAfter;
        public string? MergeKey { get; } = mergeKey;
    }

    private readonly GpuDevice _gpu;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _clampSampler;
    private readonly ID3D11SamplerState _wrapSampler;
    private readonly ID3D11BlendState _overBlend;
    private readonly ID3D11BlendState _eraseBlend;
    private readonly ID3D11RasterizerState _scissorRaster;

    private ID3D11VertexShader? _fullVertexShader;
    private ID3D11VertexShader? _dabVertexShader;
    private ID3D11PixelShader? _brushShader;
    private ID3D11PixelShader? _blurShader;
    private ID3D11PixelShader? _polarBrushShader;
    private ID3D11PixelShader? _polarBlurShader;
    private ID3D11PixelShader? _sourceShader;
    private ID3D11PixelShader? _layerOverShader;
    private ID3D11PixelShader? _straightenShader;
    private ID3D11PixelShader? _layerBlendShader;
    private ID3D11PixelShader? _generatedShader;
    private ID3D11PixelShader? _layerTileShader;
    private ID3D11PixelShader? _thumbnailLayerShader;
    private ID3D11PixelShader? _thumbnailSourceShader;
    private ID3D11PixelShader? _thumbnailGeneratedShader;
    private ID3D11PixelShader? _resampleShader;
    private ID3D11PixelShader? _cursorShader;

    private LayerEntry[] _layers = [];
    private int _activeId;
    private int _nextId = 1;

    // 履歴が参照しているものも含めた、すべてのレイヤーのテクスチャ
    private readonly HashSet<LayerTexture> _textures = [];
    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];

    // 合成の途中 (乗算済み) と結果 (乗算なし)
    private ID3D11Texture2D? _accumulated;
    private ID3D11RenderTargetView? _accumulatedTarget;
    private ID3D11ShaderResourceView? _accumulatedView;

    // 生成した内容の上に描いた内容を重ねた、1 枚分のレイヤー
    private ID3D11Texture2D? _resolved;
    private ID3D11RenderTargetView? _resolvedTarget;
    private ID3D11ShaderResourceView? _resolvedView;

    // レイヤーを繰り返した結果
    private ID3D11Texture2D? _tiled;
    private ID3D11RenderTargetView? _tiledTarget;
    private ID3D11ShaderResourceView? _tiledView;

    // 合成モードで下のレイヤーを読むための写し
    private ID3D11Texture2D? _belowCopy;
    private ID3D11ShaderResourceView? _belowCopyView;

    // レイヤーの縮小画像
    private ID3D11Texture2D? _thumbnail;
    private ID3D11RenderTargetView? _thumbnailTarget;
    private ID3D11Texture2D? _thumbnailStaging;

    // レイヤーごとの生成結果。レイヤーの番号で持つ
    private sealed class GeneratedLayer(NoiseGenerator generator) : IDisposable
    {
        public NoiseGenerator Generator { get; } = generator;
        public NoiseSettings? Settings;
        public int Width, Height;

        public void Dispose() => Generator.Dispose();
    }

    private readonly Dictionary<int, GeneratedLayer> _generated = [];
    private readonly NoiseGenerator? _noiseShaderOwner;
    private bool _transparentBase;
    private CompositeOutput _output;
    private System.Numerics.Vector3 _generatedTint = System.Numerics.Vector3.One;
    private long _lastNoiseEdit;
    private int _noiseEditGroup;
    private ID3D11Texture2D? _composite;
    private ID3D11RenderTargetView? _compositeTarget;

    // 編集を始める前の、選択中のレイヤーの写し。履歴の「前」と、自動ブレンドの取り消しに使う
    private ID3D11Texture2D? _shadow;
    private LayerTexture? _shadowOwner;
    private bool _shadowHadContent;

    // 合成をやり直す範囲と、履歴に入れる範囲。Right <= Left なら無し
    private int _dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom;
    private int _editLeft, _editTop, _editRight, _editBottom;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>どれかのレイヤーに描かれたか、生成があるか。消しゴムで消し切っても true のまま。</summary>
    public bool HasPaint => _layers.Any(l => l.Texture.HasContent || l.State.Noise is not null);

    public ID3D11ShaderResourceView? CompositeView { get; private set; }

    public IReadOnlyList<LayerState> Layers { get; private set; } = [];
    public int ActiveLayerId => _activeId;
    public bool CanUndo => _undo.Count > 0 || HasEdit;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>まだ何もしていない最初の状態か。保存するものがない。</summary>
    public bool IsPristine => !HasPaint && _layers.Length == 1 && _undo.Count == 0 && _redo.Count == 0 && _layers[0].State is { Visible: true, Opacity: 1f, Blend: LayerBlendMode.Normal, Noise: null, Invert: false, IsMask: false, IsTiled: false };

    private bool HasEdit => _editRight > _editLeft && _editBottom > _editTop;
    private LayerEntry Active => _layers.First(l => l.State.Id == _activeId);

    /// <param name="noiseShaderOwner">レイヤーの生成に使うシェーダーを持つ生成器。null なら生成しない。</param>
    public PaintRenderer(GpuDevice gpu, NoiseGenerator? noiseShaderOwner = null)
    {
        _gpu = gpu;
        _noiseShaderOwner = noiseShaderOwner;
        var device = gpu.Device;
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<PaintConstants>(), BindFlags.ConstantBuffer));
        _clampSampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
        _wrapSampler = device.CreateSamplerState(SamplerDescription.LinearWrap);
        _overBlend = device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
        _eraseBlend = device.CreateBlendState(new BlendDescription(Blend.Zero, Blend.InverseSourceAlpha, Blend.Zero, Blend.InverseSourceAlpha));
        _scissorRaster = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { ScissorEnable = true });
        ResetToSingleLayer();
    }

    public string? LoadShader(string path)
    {
        var fullVs = ShaderCompiler.CompileFile(path, "VSFull", "vs_5_0");
        var dabVs = ShaderCompiler.CompileFile(path, "VSDab", "vs_5_0");
        var brush = ShaderCompiler.CompileFile(path, "PSBrush", "ps_5_0");
        var blur = ShaderCompiler.CompileFile(path, "PSBlur", "ps_5_0");
        var polarBrush = ShaderCompiler.CompileFile(path, "PSPolarBrush", "ps_5_0");
        var polarBlur = ShaderCompiler.CompileFile(path, "PSPolarBlur", "ps_5_0");
        var source = ShaderCompiler.CompileFile(path, "PSSourcePremultiplied", "ps_5_0");
        var layerOver = ShaderCompiler.CompileFile(path, "PSLayerOver", "ps_5_0");
        var straighten = ShaderCompiler.CompileFile(path, "PSStraighten", "ps_5_0");
        var resample = ShaderCompiler.CompileFile(path, "PSResample", "ps_5_0");
        var cursor = ShaderCompiler.CompileFile(path, "PSCursor", "ps_5_0");
        var layerBlend = ShaderCompiler.CompileFile(path, "PSLayerBlend", "ps_5_0");
        var generated = ShaderCompiler.CompileFile(path, "PSGeneratedLayer", "ps_5_0");
        var layerTile = ShaderCompiler.CompileFile(path, "PSLayerTile", "ps_5_0");
        var thumbnailLayer = ShaderCompiler.CompileFile(path, "PSThumbnailLayer", "ps_5_0");
        var thumbnailSource = ShaderCompiler.CompileFile(path, "PSThumbnailSource", "ps_5_0");
        var thumbnailGenerated = ShaderCompiler.CompileFile(path, "PSThumbnailGenerated", "ps_5_0");

        var all = new[] { fullVs, dabVs, brush, blur, polarBrush, polarBlur, source, layerOver, straighten, resample, cursor, layerBlend, generated, layerTile, thumbnailLayer, thumbnailSource, thumbnailGenerated };
        if (all.Any(r => !r.Succeeded))
            return string.Join(Environment.NewLine, all.Select(r => r.Errors).Where(e => e is not null).Distinct());

        DisposeShaders();
        var device = _gpu.Device;
        _fullVertexShader = device.CreateVertexShader(fullVs.Bytecode!);
        _dabVertexShader = device.CreateVertexShader(dabVs.Bytecode!);
        _brushShader = device.CreatePixelShader(brush.Bytecode!);
        _blurShader = device.CreatePixelShader(blur.Bytecode!);
        _polarBrushShader = device.CreatePixelShader(polarBrush.Bytecode!);
        _polarBlurShader = device.CreatePixelShader(polarBlur.Bytecode!);
        _sourceShader = device.CreatePixelShader(source.Bytecode!);
        _layerOverShader = device.CreatePixelShader(layerOver.Bytecode!);
        _straightenShader = device.CreatePixelShader(straighten.Bytecode!);
        _resampleShader = device.CreatePixelShader(resample.Bytecode!);
        _cursorShader = device.CreatePixelShader(cursor.Bytecode!);
        _layerBlendShader = device.CreatePixelShader(layerBlend.Bytecode!);
        _generatedShader = device.CreatePixelShader(generated.Bytecode!);
        _layerTileShader = device.CreatePixelShader(layerTile.Bytecode!);
        _thumbnailLayerShader = device.CreatePixelShader(thumbnailLayer.Bytecode!);
        _thumbnailSourceShader = device.CreatePixelShader(thumbnailSource.Bytecode!);
        _thumbnailGeneratedShader = device.CreatePixelShader(thumbnailGenerated.Bytecode!);
        return null;
    }

    /// <summary>
    /// レイヤーを元の画像と同じ大きさにする。描いた内容は拡大縮小して引き継ぐ。
    /// 履歴の範囲が合わなくなるので、大きさが変わると元に戻せなくなる。
    /// </summary>
    public void EnsureSize(int width, int height)
    {
        if (_composite is not null && width == Width && height == Height)
            return;

        // 最初の割り当てでは、先に行ったレイヤーの操作の履歴を残す
        if (Width != 0 && (width != Width || height != Height))
        {
            ClearHistory();
            ReleaseShadow();
            ClearEdit();
        }
        ReleaseComposite();
        Width = width;
        Height = height;

        var device = _gpu.Device;
        _accumulated = device.CreateTexture2D(TextureDescription(width, height));
        _accumulatedTarget = device.CreateRenderTargetView(_accumulated);
        _accumulatedView = device.CreateShaderResourceView(_accumulated);
        _composite = device.CreateTexture2D(TextureDescription(width, height));
        _compositeTarget = device.CreateRenderTargetView(_composite);
        CompositeView = device.CreateShaderResourceView(_composite);

        foreach (var entry in _layers)
            Resize(entry.Texture);
        MarkAllDirty();
    }

    private void Resize(LayerTexture layer)
    {
        if (layer.Texture is null || (layer.Width == Width && layer.Height == Height))
            return;
        if (!layer.HasContent || _fullVertexShader is null || _resampleShader is null)
        {
            layer.Release();
            return;
        }

        var (oldTexture, oldTarget, oldView) = (layer.Texture, layer.Target, layer.View);
        layer.Texture = null;
        Allocate(layer);

        var context = _gpu.Context;
        SetCommonState(null);
        context.OMSetRenderTargets(layer.Target!);
        context.RSSetViewport(0, 0, Width, Height);
        context.VSSetShader(_fullVertexShader);
        context.PSSetShader(_resampleShader);
        context.PSSetShaderResource(1, oldView!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.Draw(3, 0);
        ResetState();

        oldView?.Dispose();
        oldTarget?.Dispose();
        oldTexture?.Dispose();
    }

    private void Allocate(LayerTexture layer)
    {
        if (layer.Texture is not null)
            return;
        var device = _gpu.Device;
        layer.Texture = device.CreateTexture2D(TextureDescription(Width, Height));
        layer.Target = device.CreateRenderTargetView(layer.Texture);
        layer.View = device.CreateShaderResourceView(layer.Texture);
        layer.Width = Width;
        layer.Height = Height;
        _gpu.Context.ClearRenderTargetView(layer.Target, new Color4(0, 0, 0, 0));
    }

    /// <summary>元の画像が変わったので、次の合成で全体をやり直す。</summary>
    public void MarkAllDirty()
    {
        _dirtyLeft = 0;
        _dirtyTop = 0;
        _dirtyRight = Width;
        _dirtyBottom = Height;
    }

    /// <summary>選択中のレイヤーを消す。元に戻せる。</summary>
    public void ClearActiveLayer()
    {
        var layer = Active.Texture;
        if (!layer.HasContent)
            return;
        CommitEdit();
        BeginEdit();
        _gpu.Context.ClearRenderTargetView(layer.Target!, new Color4(0, 0, 0, 0));
        layer.HasContent = false;
        AddEditRect(0, 0, Width, Height);
        CommitEdit();
    }

    /// <summary>保存されていたレイヤーに置き換える。null か空なら最初の状態へ戻す。履歴は消える。</summary>
    public void SetLayers(IReadOnlyList<LayerData>? layers)
    {
        ClearEdit();
        _layers = [];
        ClearHistory();
        ReleaseShadow();
        if (layers is null || layers.Count == 0)
        {
            ResetToSingleLayer();
            MarkAllDirty();
            return;
        }

        _nextId = 1;
        _layers = layers.Select(data =>
        {
            var texture = data.Image is { } image ? Upload(image) : new LayerTexture();
            _textures.Add(texture);
            return new LayerEntry(new LayerState(_nextId++, data.Name, data.Visible, Math.Clamp(data.Opacity, 0f, 1f), data.Blend, data.Noise, data.Invert, data.IsMask, data.TilingX, data.TilingY, data.OffsetX, data.OffsetY), texture);
        }).ToArray();
        _activeId = _layers[^1].State.Id;
        CollectTextures();
        PublishLayers();

        // 次の EnsureSize で元の画像の大きさへ揃える
        ReleaseComposite();
    }

    /// <summary>保存用に読み戻す (乗算なしへ戻す)。何も描いていないレイヤーの Image は null。</summary>
    public IReadOnlyList<LayerData> ReadLayers() => _layers.Select(entry => new LayerData
    {
        Name = entry.State.Name,
        Visible = entry.State.Visible,
        Opacity = entry.State.Opacity,
        Blend = entry.State.Blend,
        Noise = entry.State.Noise,
        Invert = entry.State.Invert,
        IsMask = entry.State.IsMask,
        TilingX = entry.State.TilingX,
        TilingY = entry.State.TilingY,
        OffsetX = entry.State.OffsetX,
        OffsetY = entry.State.OffsetY,
        Image = entry.Texture is { HasContent: true, Texture: { } texture } ? ReadBack(texture, entry.Texture.Width, entry.Texture.Height, premultiplied: true) : null,
    }).ToArray();

    private LayerTexture Upload(ImageData image)
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

        var device = _gpu.Device;
        var layer = new LayerTexture { Width = image.Width, Height = image.Height, HasContent = true };
        fixed (Half* pointer = data)
        {
            layer.Texture = device.CreateTexture2D(TextureDescription(image.Width, image.Height),
                new SubresourceData((nint)pointer, (uint)(image.Width * BytesPerPixel)));
        }
        layer.Target = device.CreateRenderTargetView(layer.Texture);
        layer.View = device.CreateShaderResourceView(layer.Texture);
        return layer;
    }

    private void ResetToSingleLayer()
    {
        var texture = new LayerTexture();
        _textures.Add(texture);
        _nextId = 1;
        _layers = [new LayerEntry(NewState(), texture)];
        _activeId = _layers[0].State.Id;
        CollectTextures();
        PublishLayers();
    }

    private LayerState NewState()
    {
        int id = _nextId++;
        return new LayerState(id, $"ペイント {id}", true, 1f);
    }

    private void PublishLayers() => Layers = _layers.Select(l => l.State).ToArray();

    // --- レイヤーの操作。どれも元に戻せる ---

    /// <param name="noise">ノイズのレイヤーにする設定。null ならペイントのレイヤー。</param>
    public void AddLayer(NoiseSettings? noise = null)
    {
        var texture = new LayerTexture();
        _textures.Add(texture);
        var state = NewState();
        if (noise is not null)
            state = state with { Noise = noise, Name = $"ノイズ {state.Id}" };
        var entry = new LayerEntry(state, texture);
        int index = Array.FindIndex(_layers, l => l.State.Id == _activeId) + 1;
        ChangeStructure([.. _layers[..index], entry, .. _layers[index..]], entry.State.Id);
    }

    /// <summary>画像をレイヤーとして追加する。描き足したり、マスクや合成モードに使ったりできる。</summary>
    public void AddImageLayer(ImageData image, string name)
    {
        var texture = Upload(image);
        _textures.Add(texture);
        var entry = new LayerEntry(NewState() with { Name = name }, texture);
        int index = Array.FindIndex(_layers, l => l.State.Id == _activeId) + 1;
        ChangeStructure([.. _layers[..index], entry, .. _layers[index..]], entry.State.Id);
    }

    public void RemoveLayer(int id)
    {
        int index = Array.FindIndex(_layers, l => l.State.Id == id);
        if (index < 0 || _layers.Length <= 1)
            return;
        LayerEntry[] after = [.. _layers[..index], .. _layers[(index + 1)..]];
        int active = id == _activeId ? after[Math.Max(index - 1, 0)].State.Id : _activeId;
        ChangeStructure(after, active);
    }

    /// <summary>delta が正なら上へ動かす。</summary>
    public void MoveLayer(int id, int delta)
    {
        int index = Array.FindIndex(_layers, l => l.State.Id == id);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _layers.Length)
            return;
        var after = (LayerEntry[])_layers.Clone();
        (after[index], after[target]) = (after[target], after[index]);
        ChangeStructure(after, _activeId);
    }

    public void SetLayerVisible(int id, bool visible) =>
        UpdateState(id, s => s with { Visible = visible }, mergeKey: null);

    /// <summary>スライダーの操作中は 1 回の変更としてまとめる。</summary>
    public void SetLayerOpacity(int id, float opacity) =>
        UpdateState(id, s => s with { Opacity = Math.Clamp(opacity, 0f, 1f) }, mergeKey: $"opacity:{id}");

    public void SetLayerInvert(int id, bool invert) =>
        UpdateState(id, s => s with { Invert = invert }, mergeKey: null);

    public void SetLayerMask(int id, bool isMask) =>
        UpdateState(id, s => s with { IsMask = isMask }, mergeKey: null);

    /// <summary>レイヤーの繰り返しを変える。スライダーの操作中は 1 回の変更としてまとめる。</summary>
    public void SetLayerTexture(int id, float tilingX, float tilingY, float offsetX, float offsetY) =>
        UpdateState(id, s => s with { TilingX = tilingX, TilingY = tilingY, OffsetX = offsetX, OffsetY = offsetY }, mergeKey: $"texture:{id}");

    public void SetLayerBlend(int id, LayerBlendMode blend) =>
        UpdateState(id, s => s with { Blend = blend }, mergeKey: null);

    /// <summary>レイヤーの生成を変える。null で生成をやめる。スライダーの操作中は 1 回の変更としてまとめる。</summary>
    public void SetLayerNoise(int id, NoiseSettings? noise)
    {
        long now = Environment.TickCount64;
        if (now - _lastNoiseEdit > NoiseEditMergeMilliseconds)
            _noiseEditGroup++;
        _lastNoiseEdit = now;
        UpdateState(id, s => s with { Noise = noise }, mergeKey: $"noise:{id}:{_noiseEditGroup}");
    }

    /// <summary>シェーダーを読み直したので、生成をやり直す。</summary>
    public void InvalidateGenerated()
    {
        foreach (var generated in _generated.Values)
            generated.Dispose();
        _generated.Clear();
        MarkAllDirty();
    }

    public void SetActiveLayer(int id)
    {
        if (id == _activeId || _layers.All(l => l.State.Id != id))
            return;
        CommitEdit();
        _activeId = id;
    }

    private void UpdateState(int id, Func<LayerState, LayerState> update, string? mergeKey)
    {
        var after = _layers.Select(l => l.State.Id == id ? l with { State = update(l.State) } : l).ToArray();
        if (after.SequenceEqual(_layers))
            return;
        ChangeStructure(after, _activeId, mergeKey);
    }

    private void ChangeStructure(LayerEntry[] after, int activeAfter, string? mergeKey = null)
    {
        CommitEdit();
        if (mergeKey is not null && _redo.Count == 0 && _undo.LastOrDefault() is StructureEntry last && last.MergeKey == mergeKey)
        {
            last.After = after;
            last.ActiveAfter = activeAfter;
        }
        else
        {
            PushUndo(new StructureEntry(_layers, _activeId, after, activeAfter, mergeKey));
        }
        _layers = after;
        _activeId = activeAfter;
        PublishLayers();
        MarkAllDirty();
    }

    // --- 元に戻す ---

    /// <summary>編集をひと区切りにして履歴へ入れる。ストロークの終わりに呼ぶ。</summary>
    public void CommitEdit()
    {
        if (!HasEdit || _shadow is null || _shadowOwner?.Texture is null)
        {
            ClearEdit();
            return;
        }

        int width = _editRight - _editLeft, height = _editBottom - _editTop;
        var layer = _shadowOwner;
        var before = CreateRegion(width, height);
        var after = CreateRegion(width, height);
        var context = _gpu.Context;
        CopyRegion(before, 0, 0, _shadow, _editLeft, _editTop, width, height);
        CopyRegion(after, 0, 0, layer.Texture!, _editLeft, _editTop, width, height);
        CopyRegion(_shadow, _editLeft, _editTop, layer.Texture!, _editLeft, _editTop, width, height);

        PushUndo(new PixelEntry(layer, _editLeft, _editTop, before, after, _shadowHadContent, layer.HasContent, (long)width * height * BytesPerPixel * 2));
        _shadowHadContent = layer.HasContent;
        ClearEdit();
    }

    public bool Undo()
    {
        CommitEdit();
        if (_undo.Count == 0)
            return false;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        Apply(entry, undo: true);
        return true;
    }

    public bool Redo()
    {
        CommitEdit();
        if (_redo.Count == 0)
            return false;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        Apply(entry, undo: false);
        return true;
    }

    private void Apply(HistoryEntry entry, bool undo)
    {
        switch (entry)
        {
            case PixelEntry pixels:
            {
                var description = (undo ? pixels.Before : pixels.After).Description;
                int width = (int)description.Width, height = (int)description.Height;
                if (pixels.Layer.Texture is null)
                    Allocate(pixels.Layer);
                CopyRegion(pixels.Layer.Texture!, pixels.Left, pixels.Top, undo ? pixels.Before : pixels.After, 0, 0, width, height);
                pixels.Layer.HasContent = undo ? pixels.ContentBefore : pixels.ContentAfter;
                if (ReferenceEquals(_shadowOwner, pixels.Layer))
                {
                    CopyRegion(_shadow!, pixels.Left, pixels.Top, undo ? pixels.Before : pixels.After, 0, 0, width, height);
                    _shadowHadContent = pixels.Layer.HasContent;
                }
                break;
            }
            case StructureEntry structure:
                _layers = undo ? structure.Before : structure.After;
                _activeId = undo ? structure.ActiveBefore : structure.ActiveAfter;
                PublishLayers();
                break;
        }
        MarkAllDirty();
    }

    private void PushUndo(HistoryEntry entry)
    {
        foreach (var redo in _redo)
            redo.Dispose();
        _redo.Clear();
        _undo.Add(entry);

        long bytes = _undo.Sum(e => e.Bytes);
        while (_undo.Count > 1 && (_undo.Count > MaxHistoryEntries || bytes > MaxHistoryBytes))
        {
            bytes -= _undo[0].Bytes;
            _undo[0].Dispose();
            _undo.RemoveAt(0);
        }
        CollectTextures();
    }

    private void ClearHistory()
    {
        foreach (var entry in _undo.Concat(_redo))
            entry.Dispose();
        _undo.Clear();
        _redo.Clear();
        CollectTextures();
    }

    // どこからも参照されなくなったレイヤーのテクスチャを捨てる
    private void CollectTextures()
    {
        var used = new HashSet<LayerTexture>(_layers.Select(l => l.Texture));
        foreach (var entry in _undo.Concat(_redo))
        {
            switch (entry)
            {
                case PixelEntry pixels:
                    used.Add(pixels.Layer);
                    break;
                case StructureEntry structure:
                    used.UnionWith(structure.Before.Select(l => l.Texture));
                    used.UnionWith(structure.After.Select(l => l.Texture));
                    break;
            }
        }
        foreach (var texture in _textures.Where(t => !used.Contains(t)).ToArray())
        {
            if (ReferenceEquals(_shadowOwner, texture))
                _shadowOwner = null;
            texture.Dispose();
            _textures.Remove(texture);
        }
    }

    private ID3D11Texture2D CreateRegion(int width, int height)
    {
        var description = TextureDescription(width, height);
        description.BindFlags = BindFlags.ShaderResource;
        return _gpu.Device.CreateTexture2D(description);
    }

    private void CopyRegion(ID3D11Texture2D target, int targetX, int targetY, ID3D11Texture2D source, int sourceX, int sourceY, int width, int height) =>
        _gpu.Context.CopySubresourceRegion(target, 0, (uint)targetX, (uint)targetY, 0, source, 0,
            new Box(sourceX, sourceY, 0, sourceX + width, sourceY + height, 1));

    // 選択中のレイヤーを描ける状態にし、編集前の写しを揃える
    private void BeginEdit()
    {
        var layer = Active.Texture;
        Allocate(layer);
        _shadow ??= _gpu.Device.CreateTexture2D(TextureDescription(Width, Height));
        if (!ReferenceEquals(_shadowOwner, layer))
        {
            _gpu.Context.CopyResource(_shadow, layer.Texture!);
            _shadowOwner = layer;
            _shadowHadContent = layer.HasContent;
        }
    }

    private void ClearEdit()
    {
        _editLeft = 0;
        _editTop = 0;
        _editRight = 0;
        _editBottom = 0;
    }

    private void AddEditRect(int left, int top, int right, int bottom)
    {
        AddDirtyRect(left, top, right, bottom);
        if (!HasEdit)
        {
            (_editLeft, _editTop, _editRight, _editBottom) = (left, top, right, bottom);
            return;
        }
        _editLeft = Math.Min(_editLeft, left);
        _editTop = Math.Min(_editTop, top);
        _editRight = Math.Max(_editRight, right);
        _editBottom = Math.Max(_editBottom, bottom);
    }

    // --- スタンプ ---

    /// <summary>スタンプを順に置く。ぼかしは直前のスタンプの結果を読むので、1 つごとに合成し直す。</summary>
    public void Stamp(BrushSettings brush, IReadOnlyList<PaintDab> dabs, PolarStamp? polar, ID3D11ShaderResourceView source, bool sourceIsColor)
    {
        if (dabs.Count == 0 || _composite is null)
            return;
        if (polar is not null)
        {
            StampPolar(brush, dabs, polar, source, sourceIsColor);
            return;
        }
        if (_dabVertexShader is null || _brushShader is null || _blurShader is null)
            return;

        BeginEdit();
        var layer = Active.Texture;
        layer.HasContent = true; // ぼかしの途中の合成にも、このレイヤーを入れる
        var context = _gpu.Context;
        bool blur = brush.Tool == PaintTool.Blur;
        foreach (var center in dabs)
        {
            double radius = brush.Size / 2 * brush.SizeAt(center.Pressure);
            foreach (var dab in WrappedCopies(center, radius, brush.Wrap))
            {
                if (blur)
                    UpdateComposite(source, sourceIsColor);

                var constants = new PaintConstants
                {
                    CanvasWidth = Width,
                    CanvasHeight = Height,
                    DabX = (float)dab.X,
                    DabY = (float)dab.Y,
                    DabRadius = (float)radius,
                    Hardness = (float)Math.Clamp(brush.Hardness, 0, 1),
                    Flow = (float)brush.FlowAt(center.Pressure),
                    BlurRadius = (float)Math.Max(1, radius * 0.5),
                    ColorR = brush.Color.X,
                    ColorG = brush.Color.Y,
                    ColorB = brush.Color.Z,
                    SourceIsColor = sourceIsColor ? 1u : 0u,
                };
                context.UpdateSubresource(constants, _constantBuffer);

                SetCommonState(brush.Tool == PaintTool.Eraser ? _eraseBlend : _overBlend, brush.Wrap ? _wrapSampler : _clampSampler);
                context.OMSetRenderTargets(layer.Target!);
                context.RSSetViewport(0, 0, Width, Height);
                context.VSSetShader(_dabVertexShader);
                context.VSSetConstantBuffer(0, _constantBuffer);
                context.PSSetShader(blur ? _blurShader : _brushShader);
                if (blur)
                    context.PSSetShaderResource(2, CompositeView!);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                context.Draw(4, 0);
                ResetState();

                double reach = radius + DabMargin;
                AddEditRect(
                    Math.Clamp((int)Math.Floor(dab.X - reach), 0, Width),
                    Math.Clamp((int)Math.Floor(dab.Y - reach), 0, Height),
                    Math.Clamp((int)Math.Ceiling(dab.X + reach), 0, Width),
                    Math.Clamp((int)Math.Ceiling(dab.Y + reach), 0, Height));
            }
        }
    }

    // 端をまたぐスタンプは、反対側にも同じものを置く
    private IEnumerable<PaintDab> WrappedCopies(PaintDab center, double radius, bool wrap)
    {
        yield return center;
        if (!wrap)
            yield break;

        double reach = radius + DabMargin;
        foreach (int dy in new[] { 0, -Height, Height })
        foreach (int dx in new[] { 0, -Width, Width })
        {
            if (dx == 0 && dy == 0)
                continue;
            double x = center.X + dx, y = center.Y + dy;
            if (x + reach > 0 && x - reach < Width && y + reach > 0 && y - reach < Height)
                yield return center with { X = x, Y = y };
        }
    }

    private void StampPolar(BrushSettings brush, IReadOnlyList<PaintDab> dabs, PolarStamp polar, ID3D11ShaderResourceView source, bool sourceIsColor)
    {
        if (_fullVertexShader is null || _polarBrushShader is null || _polarBlurShader is null)
            return;

        BeginEdit();
        var layer = Active.Texture;
        layer.HasContent = true; // ぼかしの途中の合成にも、このレイヤーを入れる
        var context = _gpu.Context;
        bool blur = brush.Tool == PaintTool.Blur;
        foreach (var dab in dabs)
        {
            if (blur)
                UpdateComposite(source, sourceIsColor);

            double radius = brush.Size / polar.ContentSize * brush.SizeAt(dab.Pressure); // 外接円を 1 とした比率
            var constants = new PaintConstants
            {
                CanvasWidth = Width,
                CanvasHeight = Height,
                DabX = (float)dab.X,
                DabY = (float)dab.Y,
                DabRadius = (float)radius,
                Hardness = (float)Math.Clamp(brush.Hardness, 0, 1),
                Flow = (float)brush.FlowAt(dab.Pressure),
                BlurRadius = (float)Math.Max(2 / polar.ContentSize, radius * 0.5),
                ColorR = brush.Color.X,
                ColorG = brush.Color.Y,
                ColorB = brush.Color.Z,
                SourceIsColor = sourceIsColor ? 1u : 0u,
                InnerRadius = (float)polar.InnerRadius,
                OuterRadius = (float)polar.OuterRadius,
                CenterRadius = (float)dab.PolarRadius,
                RadialSign = polar.FlipRadial ? -1f : 1f,
                PolarTilingX = (float)polar.TilingX,
                PolarTilingY = (float)polar.TilingY,
            };
            context.UpdateSubresource(constants, _constantBuffer);

            SetCommonState(brush.Tool == PaintTool.Eraser ? _eraseBlend : _overBlend, _wrapSampler);
            context.OMSetRenderTargets(layer.Target!);
            context.RSSetViewport(0, 0, Width, Height);
            context.RSSetState(_scissorRaster);
            context.VSSetShader(_fullVertexShader);
            context.PSSetShader(blur ? _polarBlurShader : _polarBrushShader);
            if (blur)
                context.PSSetShaderResource(2, CompositeView!);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            // 影響する範囲だけ描く。0°/360° をまたぐと範囲が分かれる
            var (halfWidth, halfHeight) = PolarDabExtent(dab, radius, polar);
            foreach (var (left, right) in WrappedRanges(dab.X, halfWidth, Width))
            foreach (var (top, bottom) in WrappedRanges(dab.Y, halfHeight, Height))
            {
                context.RSSetScissorRect(left, top, right - left, bottom - top);
                context.Draw(3, 0);
                AddEditRect(left, top, right, bottom);
            }
            ResetState();
        }
    }

    // スタンプが届くテクスチャ上の範囲 (片側の画素数)
    private (double HalfWidth, double HalfHeight) PolarDabExtent(PaintDab dab, double radius, PolarStamp polar)
    {
        double angle = dab.PolarRadius > radius ? Math.Asin(radius / dab.PolarRadius) : Math.PI;
        double halfWidth = angle / (2 * Math.PI) * polar.TilingX * Width + DabMargin;
        double halfHeight = radius / Math.Max(polar.OuterRadius - polar.InnerRadius, 1e-5) * polar.TilingY * Height + DabMargin;
        return (halfWidth, halfHeight);
    }

    private static IEnumerable<(int Start, int End)> WrappedRanges(double center, double half, int size)
    {
        if (half * 2 >= size)
        {
            yield return (0, size);
            yield break;
        }

        int start = (int)Math.Floor(center - half);
        int end = (int)Math.Ceiling(center + half);
        if (start < 0)
        {
            yield return (0, end);
            yield return (start + size, size);
        }
        else if (end > size)
        {
            yield return (start, size);
            yield return (0, end - size);
        }
        else
        {
            yield return (start, end);
        }
    }

    // --- 継ぎ目の自動ブレンド ---

    // --- 合成 ---

    /// <summary>変わった範囲の合成をやり直す。表示しているレイヤーを下から順に重ねる。</summary>
    /// <param name="transparentBase">一番下を透明から始める。false なら source の上に重ねる。</param>
    /// <param name="output">最後に変換する形 (プレビューと書き出しの切り替え)。</param>
    public void UpdateComposite(ID3D11ShaderResourceView source, bool sourceIsColor, bool transparentBase = false, CompositeOutput output = CompositeOutput.Color)
    {
        _transparentBase = transparentBase;
        _output = output;
        if (_compositeTarget is null || _accumulatedTarget is null || _fullVertexShader is null
            || _sourceShader is null || _layerOverShader is null || _straightenShader is null
            || _dirtyRight <= _dirtyLeft || _dirtyBottom <= _dirtyTop)
            return;

        // 繰り返すと、変わった範囲の外にも同じ絵が出る
        if (_layers.Any(l => l.State.IsTiled))
            MarkAllDirty();

        DrawCompositePass(_accumulatedTarget, _sourceShader, null, source, null, 1f, sourceIsColor);
        foreach (var entry in _layers)
        {
            if (!entry.State.Visible || entry.State.Opacity <= 0f || ResolveLayer(entry) is not { } resolved)
                continue;
            var view = entry.State.IsTiled && _layerTileShader is not null ? TileLayer(resolved, entry.State) : resolved;

            if (entry.State is { Blend: LayerBlendMode.Normal, Invert: false, IsMask: false } || _layerBlendShader is null)
                DrawCompositePass(_accumulatedTarget, _layerOverShader, _overBlend, null, view, entry.State.Opacity, sourceIsColor);
            else
                DrawBlendPass(view, entry.State);
        }
        PruneGenerated();
        DrawCompositePass(_compositeTarget, _straightenShader, null, null, _accumulatedView, 1f, sourceIsColor);

        _dirtyRight = _dirtyLeft;
        _dirtyBottom = _dirtyTop;
    }

    // 生成した内容と描いた内容をまとめて 1 枚にする。何もなければ null
    private ID3D11ShaderResourceView? ResolveLayer(LayerEntry entry)
    {
        var paint = entry.Texture is { HasContent: true, View: { } view } ? view : null;
        if (GeneratedView(entry.State) is not { } generated || _generatedShader is null)
            return paint;

        if (_resolved is null)
        {
            var device = _gpu.Device;
            _resolved = device.CreateTexture2D(TextureDescription(Width, Height));
            _resolvedTarget = device.CreateRenderTargetView(_resolved);
            _resolvedView = device.CreateShaderResourceView(_resolved);
        }
        _generatedTint = TintOf(entry.State);
        DrawCompositePass(_resolvedTarget!, _generatedShader, null, generated, null, 1f, false);
        _generatedTint = System.Numerics.Vector3.One;
        if (paint is not null)
            DrawCompositePass(_resolvedTarget!, _layerOverShader!, _overBlend, null, paint, 1f, false);
        return _resolvedView;
    }

    // 単色のレイヤーは白で生成して、ここで色を付ける
    private static System.Numerics.Vector3 TintOf(LayerState state) =>
        state.Noise is { Type: NoiseType.Solid } noise
            ? new System.Numerics.Vector3(noise.Shape.SolidR, noise.Shape.SolidG, noise.Shape.SolidB)
            : System.Numerics.Vector3.One;

    private ID3D11ShaderResourceView TileLayer(ID3D11ShaderResourceView layer, LayerState state)
    {
        var device = _gpu.Device;
        if (_tiled is null)
        {
            _tiled = device.CreateTexture2D(TextureDescription(Width, Height));
            _tiledTarget = device.CreateRenderTargetView(_tiled);
            _tiledView = device.CreateShaderResourceView(_tiled);
        }
        var constants = new PaintConstants
        {
            CanvasWidth = Width,
            CanvasHeight = Height,
            DabX = state.OffsetX,
            DabY = state.OffsetY,
            PolarTilingX = state.TilingX,
            PolarTilingY = state.TilingY,
        };
        var context = _gpu.Context;
        context.UpdateSubresource(constants, _constantBuffer);

        SetCommonState(null, _wrapSampler);
        context.OMSetRenderTargets(_tiledTarget!);
        context.RSSetViewport(0, 0, Width, Height);
        context.VSSetShader(_fullVertexShader);
        context.PSSetShader(_layerTileShader);
        context.PSSetShaderResource(1, layer);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.Draw(3, 0);
        ResetState();
        return _tiledView!;
    }

    private ID3D11ShaderResourceView? GeneratedView(LayerState state)
    {
        if (state.Noise is not { } settings || _noiseShaderOwner is null || Width <= 0 || Height <= 0)
            return null;

        if (!_generated.TryGetValue(state.Id, out var generated))
        {
            generated = new GeneratedLayer(new NoiseGenerator(_gpu, _noiseShaderOwner));
            _generated[state.Id] = generated;
        }
        if (generated.Settings != settings || generated.Width != Width || generated.Height != Height)
        {
            generated.Generator.Generate(settings, Width, Height);
            generated.Settings = settings;
            generated.Width = Width;
            generated.Height = Height;
        }
        return generated.Generator.ShaderResourceView;
    }

    // 消したレイヤーの生成結果を捨てる。元に戻したら作り直す
    private void PruneGenerated()
    {
        foreach (int id in _generated.Keys.Where(id => _layers.All(l => l.State.Id != id)).ToArray())
        {
            _generated[id].Dispose();
            _generated.Remove(id);
        }
    }

    private void DrawBlendPass(ID3D11ShaderResourceView layer, LayerState state)
    {
        var device = _gpu.Device;
        if (_belowCopy is null)
        {
            _belowCopy = device.CreateTexture2D(TextureDescription(Width, Height));
            _belowCopyView = device.CreateShaderResourceView(_belowCopy);
        }
        var context = _gpu.Context;
        context.CopyResource(_belowCopy, _accumulated!);

        var constants = new PaintConstants
        {
            CanvasWidth = Width,
            CanvasHeight = Height,
            Flow = state.Opacity,
            BlendAxis = (uint)state.Blend,
            LayerInvert = state.Invert ? 1u : 0u,
            LayerMask = state.IsMask ? 1u : 0u,
        };
        context.UpdateSubresource(constants, _constantBuffer);

        SetCommonState(null);
        context.OMSetRenderTargets(_accumulatedTarget!);
        context.RSSetViewport(0, 0, Width, Height);
        context.RSSetState(_scissorRaster);
        context.RSSetScissorRect(_dirtyLeft, _dirtyTop, _dirtyRight - _dirtyLeft, _dirtyBottom - _dirtyTop);
        context.VSSetShader(_fullVertexShader);
        context.PSSetShader(_layerBlendShader);
        context.PSSetShaderResource(1, layer);
        context.PSSetShaderResource(2, _belowCopyView!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.Draw(3, 0);
        ResetState();
    }

    /// <summary>ベースと各レイヤーの縮小画像を作る。読み戻しは小さいので、操作が落ち着くたびに呼んでよい。</summary>
    public IReadOnlyList<LayerThumbnail> RenderThumbnails(ID3D11ShaderResourceView source, bool sourceIsColor)
    {
        if (_fullVertexShader is null || _thumbnailSourceShader is null || _thumbnailGeneratedShader is null || _thumbnailLayerShader is null)
            return [];

        if (_thumbnail is null)
        {
            var description = new Texture2DDescription
            {
                Width = ThumbnailSize,
                Height = ThumbnailSize,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            };
            _thumbnail = _gpu.Device.CreateTexture2D(description);
            _thumbnailTarget = _gpu.Device.CreateRenderTargetView(_thumbnail);
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            _thumbnailStaging = _gpu.Device.CreateTexture2D(description);
        }

        var result = new List<LayerThumbnail>(_layers.Length + 1);
        BeginThumbnail();
        DrawThumbnailPass(_thumbnailSourceShader, null, source, null, sourceIsColor, null);
        result.Add(ReadThumbnail(LayerThumbnail.BaseLayerId));

        foreach (var entry in _layers)
        {
            BeginThumbnail();
            if (GeneratedView(entry.State) is { } generated)
                DrawThumbnailPass(_thumbnailGeneratedShader, null, generated, null, false, entry.State);
            if (entry.Texture is { HasContent: true, View: { } view })
                DrawThumbnailPass(_thumbnailLayerShader, _overBlend, null, view, false, entry.State);
            var thumbnail = ReadThumbnail(entry.State.Id);
            if (entry.State.Invert)
            {
                // 乗算済みアルファなので、アルファから引く
                var rgba = thumbnail.Rgba;
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    rgba[i] = (byte)(rgba[i + 3] - rgba[i]);
                    rgba[i + 1] = (byte)(rgba[i + 3] - rgba[i + 1]);
                    rgba[i + 2] = (byte)(rgba[i + 3] - rgba[i + 2]);
                }
            }
            result.Add(thumbnail);
        }
        return result;
    }

    private void BeginThumbnail() => _gpu.Context.ClearRenderTargetView(_thumbnailTarget!, new Color4(0, 0, 0, 0));

    private void DrawThumbnailPass(ID3D11PixelShader shader, ID3D11BlendState? blend,
        ID3D11ShaderResourceView? source, ID3D11ShaderResourceView? layer, bool sourceIsColor, LayerState? state)
    {
        var tint = state is null ? System.Numerics.Vector3.One : TintOf(state);
        var constants = new PaintConstants
        {
            CanvasWidth = ThumbnailSize,
            CanvasHeight = ThumbnailSize,
            SourceIsColor = sourceIsColor ? 1u : 0u,
            DabX = state?.OffsetX ?? 0f,
            DabY = state?.OffsetY ?? 0f,
            PolarTilingX = state?.TilingX ?? 1f,
            PolarTilingY = state?.TilingY ?? 1f,
            ColorR = tint.X,
            ColorG = tint.Y,
            ColorB = tint.Z,
        };
        var context = _gpu.Context;
        context.UpdateSubresource(constants, _constantBuffer);

        SetCommonState(blend, _wrapSampler);
        context.OMSetRenderTargets(_thumbnailTarget!);
        context.RSSetViewport(0, 0, ThumbnailSize, ThumbnailSize);
        context.VSSetShader(_fullVertexShader);
        context.PSSetShader(shader);
        if (source is not null)
            context.PSSetShaderResource(0, source);
        if (layer is not null)
            context.PSSetShaderResource(1, layer);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.Draw(3, 0);
        ResetState();
    }

    private LayerThumbnail ReadThumbnail(int id)
    {
        var context = _gpu.Context;
        context.CopyResource(_thumbnailStaging!, _thumbnail!);
        var rgba = new byte[ThumbnailSize * ThumbnailSize * 4];
        var mapped = context.Map(_thumbnailStaging!, 0, MapMode.Read);
        try
        {
            for (int y = 0; y < ThumbnailSize; y++)
                System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), rgba, y * ThumbnailSize * 4, ThumbnailSize * 4);
        }
        finally
        {
            context.Unmap(_thumbnailStaging!, 0);
        }
        return new LayerThumbnail(id, ThumbnailSize, rgba);
    }

    private void DrawCompositePass(ID3D11RenderTargetView target, ID3D11PixelShader shader, ID3D11BlendState? blend,
        ID3D11ShaderResourceView? source, ID3D11ShaderResourceView? layer, float opacity, bool sourceIsColor)
    {
        var constants = new PaintConstants
        {
            CanvasWidth = Width,
            CanvasHeight = Height,
            Flow = opacity,
            SourceIsColor = _transparentBase && shader == _sourceShader ? 2u : sourceIsColor ? 1u : 0u,
            BlendAxis = shader == _straightenShader ? (uint)_output : 0u,
            ColorR = _generatedTint.X,
            ColorG = _generatedTint.Y,
            ColorB = _generatedTint.Z,
        };
        var context = _gpu.Context;
        context.UpdateSubresource(constants, _constantBuffer);

        SetCommonState(blend);
        context.OMSetRenderTargets(target);
        context.RSSetViewport(0, 0, Width, Height);
        context.RSSetState(_scissorRaster);
        context.RSSetScissorRect(_dirtyLeft, _dirtyTop, _dirtyRight - _dirtyLeft, _dirtyBottom - _dirtyTop);
        context.VSSetShader(_fullVertexShader);
        context.PSSetShader(shader);
        if (source is not null)
            context.PSSetShaderResource(0, source);
        if (layer is not null)
            context.PSSetShaderResource(1, layer);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.Draw(3, 0);
        ResetState();
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

    /// <summary>合成結果の明るさを読み戻す (透明は黒)。16bit の書き出し用で、ReadComposite より階調が細かい。</summary>
    public float[] ReadCompositeLuminance()
    {
        if (_composite is null)
            return [];
        var description = TextureDescription(Width, Height);
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _gpu.Device.CreateTexture2D(description);
        var context = _gpu.Context;
        context.CopyResource(staging, _composite);
        var result = new float[Width * Height];
        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                var row = new ReadOnlySpan<Half>((void*)(mapped.DataPointer + (nint)(y * mapped.RowPitch)), Width * ComponentsPerPixel);
                for (int x = 0; x < Width; x++)
                {
                    int i = x * ComponentsPerPixel;
                    result[y * Width + x] = (0.299f * (float)row[i] + 0.587f * (float)row[i + 1] + 0.114f * (float)row[i + 2]) * (float)row[i + 3];
                }
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }
        return result;
    }

    /// <summary>合成結果を読み戻す (乗算なし)。</summary>
    public ImageData? ReadComposite() => _composite is null ? null : ReadBack(_composite, Width, Height, premultiplied: false);

    private ImageData ReadBack(ID3D11Texture2D texture, int width, int height, bool premultiplied)
    {
        var description = TextureDescription(width, height);
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _gpu.Device.CreateTexture2D(description);
        var context = _gpu.Context;
        context.CopyResource(staging, texture);

        var rgba = new byte[width * height * ComponentsPerPixel];
        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            for (int y = 0; y < height; y++)
            {
                var row = new ReadOnlySpan<Half>((void*)(mapped.DataPointer + (nint)(y * mapped.RowPitch)), width * ComponentsPerPixel);
                int output = y * width * ComponentsPerPixel;
                for (int x = 0; x < width * ComponentsPerPixel; x += ComponentsPerPixel, output += ComponentsPerPixel)
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
        return new ImageData(width, height, rgba);
    }

    private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    private void AddDirtyRect(int left, int top, int right, int bottom)
    {
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

    private void SetCommonState(ID3D11BlendState? blend, ID3D11SamplerState? sampler = null)
    {
        var context = _gpu.Context;
        context.OMSetBlendState(blend);
        context.IASetInputLayout(null);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.VSSetConstantBuffer(0, _constantBuffer);
        context.PSSetSampler(0, sampler ?? _clampSampler);
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

    private void ReleaseShadow()
    {
        _shadow?.Dispose();
        _shadow = null;
        _shadowOwner = null;
    }

    private void ReleaseComposite()
    {
        CompositeView?.Dispose();
        _compositeTarget?.Dispose();
        _composite?.Dispose();
        _accumulatedView?.Dispose();
        _accumulatedTarget?.Dispose();
        _accumulated?.Dispose();
        _tiledView?.Dispose();
        _tiledTarget?.Dispose();
        _tiled?.Dispose();
        _tiledView = null;
        _tiledTarget = null;
        _tiled = null;
        _resolvedView?.Dispose();
        _resolvedTarget?.Dispose();
        _resolved?.Dispose();
        _belowCopyView?.Dispose();
        _belowCopy?.Dispose();
        _resolvedView = null;
        _resolvedTarget = null;
        _resolved = null;
        _belowCopyView = null;
        _belowCopy = null;
        CompositeView = null;
        _compositeTarget = null;
        _composite = null;
        _accumulatedView = null;
        _accumulatedTarget = null;
        _accumulated = null;
    }

    private void DisposeShaders()
    {
        _fullVertexShader?.Dispose();
        _dabVertexShader?.Dispose();
        _brushShader?.Dispose();
        _blurShader?.Dispose();
        _polarBrushShader?.Dispose();
        _polarBlurShader?.Dispose();
        _sourceShader?.Dispose();
        _layerOverShader?.Dispose();
        _straightenShader?.Dispose();
        _layerBlendShader?.Dispose();
        _generatedShader?.Dispose();
        _layerTileShader?.Dispose();
        _thumbnailLayerShader?.Dispose();
        _thumbnailSourceShader?.Dispose();
        _thumbnailGeneratedShader?.Dispose();
        _resampleShader?.Dispose();
        _cursorShader?.Dispose();
    }

    public void Dispose()
    {
        _layers = [];
        foreach (var entry in _undo.Concat(_redo))
            entry.Dispose();
        _undo.Clear();
        _redo.Clear();
        foreach (var texture in _textures)
            texture.Dispose();
        _textures.Clear();
        ReleaseShadow();
        ReleaseComposite();
        foreach (var generated in _generated.Values)
            generated.Dispose();
        _generated.Clear();
        _thumbnailTarget?.Dispose();
        _thumbnail?.Dispose();
        _thumbnailStaging?.Dispose();
        DisposeShaders();
        _scissorRaster.Dispose();
        _eraseBlend.Dispose();
        _overBlend.Dispose();
        _wrapSampler.Dispose();
        _clampSampler.Dispose();
        _constantBuffer.Dispose();
    }
}
