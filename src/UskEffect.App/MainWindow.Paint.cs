using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UskEffect.App.Controls;
using UskEffect.App.Interop;
using UskEffect.App.Locale;
using UskEffect.Graphics;
using UskEffect.Graphics.Painting;

namespace UskEffect.App;

/// <summary>生成／ペイントの切り替えと、ブラシ・消しゴム・ぼかし (FR-14)。</summary>
public partial class MainWindow
{
    private const double BrushSizeStep = 1.25;

    private CanvasPainter? _painter;

    // レイヤーを開いている間のキャンバスのクリックは、閉じるだけで描かない
    private bool CanPaint => IsActiveLayerVisible && ActiveLayer is { Noise: null } && !IsPopupOpen;

    private bool IsPopupOpen => Rail.IsLayerOpen || NoiseTab.IsTypeMenuOpen || Strip.IsPopupOpen;

    private void ClosePopups()
    {
        Rail.CloseLayers();
        NoiseTab.CloseTypeMenu();
        Strip.ClosePopups();
    }

    // 閉じた後の入力欄にフォーカスが残っていても、入力中として扱わない
    private static bool IsEditingText(TopLevel top) =>
        top.FocusManager?.GetFocusedElement() is TextBox { IsEffectivelyVisible: true };

    private void OnAnyKeyDown(TopLevel top, KeyEventArgs e)
    {
        // 別の窓 (確認や書き出し) のキーは、その窓のもの
        if (e.Handled || _exportWindowOpen)
            return;

        bool editing = IsEditingText(top);
        if (e.Key == Key.Escape && !editing && IsPopupOpen)
        {
            ClosePopups();
            e.Handled = true;
            return;
        }
        if (editing)
            return;
        OnPaintKeyDown(e);
        if (!e.Handled)
            OnUndoKeyDown(e);
    }

    // 見えないレイヤーに描くと結果が分からないので描かせない
    private bool IsActiveLayerVisible =>
        _engine is null || _engine.Layers.FirstOrDefault(l => l.Id == _engine.ActiveLayerId)?.Visible != false;

    internal BrushSettings CurrentBrush => Strip.ReadBrush(Rail.Tool);

    private void SetUpPaint()
    {
        _painter = new CanvasPainter(Viewport, _engine!, () => CurrentBrush, () => CanPaint, MarkDirty);

        Rail.ToolChanged += () => Strip.Tool = Rail.Tool;
        Viewport.HostPointerDown += (button, _, _) =>
        {
            if (button == 0 && !Win32.IsSpaceDown() && !IsPopupOpen)
                ExplainWhyNotPainting();
            // キャンバスは別の窓なので、外をクリックしたことが Avalonia に届かない。描くかどうかの判定の後で閉じる
            if (IsPopupOpen)
            {
                ClosePopups();
                // ポップアップの入力欄に残ったフォーカスを外す。残るとキーが届かない
                Focus();
            }
        };
        // ポップアップにフォーカスがあると、キーは窓を通らない。中身でも受ける
        AddHandler(KeyDownEvent, (_, e) => OnAnyKeyDown(this, e), RoutingStrategies.Tunnel);
        foreach (Control popup in new Control[] { Strip.BrushPopup, Strip.PolarPopup, LayerTab, NoiseTab.TypeMenu })
            popup.AddHandler(KeyDownEvent, (_, e) => OnAnyKeyDown(TopLevel.GetTopLevel(popup) ?? this, e), RoutingStrategies.Tunnel);

        Top.UndoMenuItem.Click += (_, _) => UndoOrRedo(undo: true);
        Top.UndoButton.Click += (_, _) => UndoOrRedo(undo: true);
        Top.RedoMenuItem.Click += (_, _) => UndoOrRedo(undo: false);
        Top.RedoButton.Click += (_, _) => UndoOrRedo(undo: false);

        LayerTab.BlendChanged += (id, blend) => ChangeLayers(() => _engine!.SetLayerBlend(id, blend));
        LayerTab.LayerSelected += SelectLayer;
        LayerTab.VisibleToggled += (id, visible) => ChangeLayers(() => _engine!.SetLayerVisible(id, visible));
        LayerTab.OpacityChanged += (id, opacity) => ChangeLayers(() => _engine!.SetLayerOpacity(id, opacity));
        LayerTab.AddPaintRequested += () => ChangeLayers(() => _engine!.AddLayer());
        LayerTab.AddNoiseRequested += () =>
        {
            Rail.CloseLayers();
            NoiseTab.PickTypeForNewLayer(Rail.LayerButton, Avalonia.Controls.PlacementMode.RightEdgeAlignedBottom);
        };
        NoiseTab.NewLayerTypeChosen += AddNoiseLayer;
        LayerTab.AddImageRequested += async () =>
        {
            Rail.CloseLayers();
            await PickImageLayerAsync();
        };
        LayerTab.InvertToggled += (id, invert) => ChangeLayers(() => _engine!.SetLayerInvert(id, invert));
        LayerTab.MaskToggled += (id, isMask) => ChangeLayers(() => _engine!.SetLayerMask(id, isMask));
        LayerTab.RemoveRequested += id => ChangeLayers(() => _engine!.RemoveLayer(id));
        LayerTab.MoveRequested += (id, delta) => ChangeLayers(() => _engine!.MoveLayer(id, delta));
        _engine!.TransparentBase = true;
        RefreshLayers();

    }

    private void ChangeLayers(Action change, bool dirty = true)
    {
        change();
        RefreshLayers();
        if (dirty)
            MarkDirty();
    }

    private void RefreshLayers()
    {
        if (_engine is null)
            return;
        LayerTab.Show(_engine.Layers, _engine.ActiveLayerId);
        ShowCurrentLayerOnRail();
        SyncNoiseTarget();
        ApplyPaintAvailability();
        RefreshUndoButtons();
    }

    private LayerState? ActiveLayer => _engine?.Layers.FirstOrDefault(l => l.Id == _engine.ActiveLayerId);

    private void ShowCurrentLayerOnRail()
    {
        if (_engine is null)
            return;
        var layers = _engine.Layers;
        int active = layers.ToList().FindIndex(l => l.Id == _engine.ActiveLayerId);
        if (active >= 0)
            Rail.ShowCurrentLayer((active + 1).ToString(), layers[active].Name, layers[active].Visible, LayerTab.ThumbnailOf(layers[active].Id));
    }

    private void SelectLayer(int id) => ChangeLayers(() => _engine!.SetActiveLayer(id), dirty: false);

    // 選んだレイヤーの設定をノイズの欄へ出す。元に戻したときにも追いかける
    private void SyncNoiseTarget()
    {
        if (ActiveLayer is not { } layer)
            return;
        bool same = _shownNoiseTarget == layer.Id && NoiseTab.IsNoiseLayer == layer.Noise is not null
            && (layer.Noise is null || NoiseTab.Read() == layer.Noise with { Resolution = NoiseTab.Resolution })
            && NoiseTab.ReadTexture() == (layer.TilingX, layer.TilingY, layer.OffsetX, layer.OffsetY);
        if (same)
            return;
        _shownNoiseTarget = layer.Id;
        NoiseTab.ShowLayer(layer.Name, layer.Noise, layer.TilingX, layer.TilingY, layer.OffsetX, layer.OffsetY);
    }

    private void OnNoiseTabChanged()
    {
        ApplySettings();
        if (_engine is null || _suppressApply || !NoiseTab.IsNoiseLayer)
            return;
        ChangeLayers(() => _engine.SetLayerNoise(_engine.ActiveLayerId, NoiseTab.Read()));
    }

    // 始めたばかりなら、最初のレイヤーにする
    internal void AddNoiseLayer(NoiseType type)
    {
        var noise = new NoiseSettings { Resolution = NoiseTab.Resolution, Type = type };
        if (_engine?.IsPristine == true)
        {
            ChangeLayers(() => _engine.SetLayers([new LayerData { Name = Loc.T("S.Layer.FirstNoiseName"), Noise = noise }]), dirty: false);
            return;
        }
        ChangeLayers(() => _engine!.AddLayer(noise));
    }

    private void ShowStartTypeMenu()
    {
        if (_engine?.IsPristine == true && !_closed)
            NoiseTab.PickTypeForNewLayer(Viewport, Avalonia.Controls.PlacementMode.Center);
    }

    private void OnLayerTextureChanged()
    {
        if (_engine is null || _suppressApply)
            return;
        var (tilingX, tilingY, offsetX, offsetY) = NoiseTab.ReadTexture();
        ChangeLayers(() => _engine.SetLayerTexture(_engine.ActiveLayerId, tilingX, tilingY, offsetX, offsetY));
    }


    private void RefreshUndoButtons()
    {
        if (_engine is null)
            return;
        Top.UndoButton.IsEnabled = Top.UndoMenuItem.IsEnabled = _engine.CanUndo;
        Top.RedoButton.IsEnabled = Top.RedoMenuItem.IsEnabled = _engine.CanRedo;
    }

    private void UndoOrRedo(bool undo)
    {
        if (_engine is null)
            return;
        if (undo)
            _engine.Undo();
        else
            _engine.Redo();
        RefreshLayers();
        MarkDirty();
    }

    // メニューの HotKey にすると、数値の入力中の Ctrl+Z まで奪うのでここで受ける
    private void OnUndoKeyDown(KeyEventArgs e)
    {
        bool undo = e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control;
        bool redo = (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control)
            || (e.Key == Key.Z && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift));
        if (!undo && !redo)
            return;
        UndoOrRedo(undo);
        e.Handled = true;
    }

    // 描けるのはペイントのレイヤーだけ。道具とブラシの欄もそれに合わせる
    private void ApplyPaintAvailability()
    {
        bool paint = ActiveLayer is { Noise: null };
        Rail.IsPaintMode = paint;
        Strip.ShowBrushSettings = paint;
        if (!paint)
        {
            _painter?.HideCursor();
            _engine?.Cursor = null;
        }
    }

    private string? PaintBlockedMessage =>
        ActiveLayer is { Noise: not null } ? "S.Paint.NoiseLayer"
        : !IsActiveLayerVisible ? "S.Paint.LayerHidden"
        : null;

    // キャンバスを押しても描けないときは、何も起きないままにせず理由を出す
    internal void ExplainWhyNotPainting()
    {
        if (PaintBlockedMessage is { } message)
            Status.ShowMessage(Loc.T(message));
    }

    // 操作の参考は CLIP STUDIO PAINT。B ブラシ、E 消しゴム、J ぼかし (色混ぜ)、[ ] で大きさ
    private void OnPaintKeyDown(KeyEventArgs e)
    {
        if (ActiveLayer is not { Noise: null } || e.KeyModifiers != KeyModifiers.None)
            return;

        switch (e.Key)
        {
            case Key.B:
                Rail.Tool = PaintTool.Brush;
                break;
            case Key.E:
                Rail.Tool = PaintTool.Eraser;
                break;
            case Key.J:
                Rail.Tool = PaintTool.Blur;
                break;
            case Key.OemOpenBrackets:
                Strip.BrushSize /= BrushSizeStep;
                break;
            case Key.OemCloseBrackets:
                Strip.BrushSize *= BrushSizeStep;
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
