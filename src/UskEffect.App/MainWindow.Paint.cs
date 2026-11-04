using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UskEffect.App.Controls;
using UskEffect.App.Locale;
using UskEffect.Graphics;
using UskEffect.Graphics.Painting;

namespace UskEffect.App;

/// <summary>生成／ペイントの切り替えと、ブラシ・消しゴム・ぼかし (FR-14)。</summary>
public partial class MainWindow
{
    private const double BrushSizeStep = 1.25;

    private CanvasPainter? _painter;
    private bool _seamBlendPreviewing;

    private bool IsPaintMode => Top.ModeSegment.SelectedIndex == 1;

    // エフェクトの表示は動いていて画素と対応しない。自動ブレンドの確認中は描くと取り消せなくなる
    private bool CanPaint => IsPaintMode && Strip.Mode != PreviewMode.Effect && !_seamBlendPreviewing && IsActiveLayerVisible;

    // 見えないレイヤーに描くと結果が分からないので描かせない
    private bool IsActiveLayerVisible =>
        _engine is null || _engine.Layers.FirstOrDefault(l => l.Id == _engine.ActiveLayerId)?.Visible != false;

    internal BrushSettings CurrentBrush => Strip.ReadBrush(Rail.Tool);

    private void SetUpPaint()
    {
        _painter = new CanvasPainter(Viewport, _engine!, () => CurrentBrush, () => CanPaint, MarkDirty);

        Top.ModeSegment.SelectionChanged += (_, _) => ApplyPaintMode();
        Rail.ToolChanged += () => Strip.Tool = Rail.Tool;
        Strip.ClearPaintRequested += ClearActiveLayer;
        Strip.Changed += ShowPaintAvailability;
        AddHandler(KeyDownEvent, OnPaintKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnUndoKeyDown, RoutingStrategies.Tunnel);

        Top.UndoMenuItem.Click += (_, _) => UndoOrRedo(undo: true);
        Top.UndoButton.Click += (_, _) => UndoOrRedo(undo: true);
        Top.RedoMenuItem.Click += (_, _) => UndoOrRedo(undo: false);
        Top.RedoButton.Click += (_, _) => UndoOrRedo(undo: false);

        LayerTab.LayerSelected += id => ChangeLayers(() => _engine!.SetActiveLayer(id), dirty: false);
        LayerTab.VisibleToggled += (id, visible) => ChangeLayers(() => _engine!.SetLayerVisible(id, visible));
        LayerTab.OpacityChanged += (id, opacity) => ChangeLayers(() => _engine!.SetLayerOpacity(id, opacity));
        LayerTab.AddRequested += () => ChangeLayers(() => _engine!.AddLayer());
        LayerTab.RemoveRequested += id => ChangeLayers(() => _engine!.RemoveLayer(id));
        LayerTab.MoveRequested += (id, delta) => ChangeLayers(() => _engine!.MoveLayer(id, delta));
        RefreshLayers();

        SeamTab.ManualFixRequested += StartManualSeamFix;
        SeamTab.BlendStarted += StartSeamBlend;
        SeamTab.BlendChanged += () => _engine?.PreviewSeamBlend(SeamTab.ReadBlend());
        SeamTab.BlendApplied += () => EndSeamBlend(apply: true);
        SeamTab.BlendCancelled += () => EndSeamBlend(apply: false);
        Closing += (_, _) =>
        {
            if (_seamBlendPreviewing)
                EndSeamBlend(apply: false);
        };

        ApplyPaintMode();
    }

    private void ChangeLayers(Action change, bool dirty = true)
    {
        change();
        RefreshLayers();
        if (dirty)
            MarkDirty();
        ShowPaintAvailability();
    }

    private void RefreshLayers()
    {
        if (_engine is null)
            return;
        LayerTab.Show(_engine.Layers, _engine.ActiveLayerId);
        RefreshUndoButtons();
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
        if (_engine is null || _seamBlendPreviewing)
            return;
        if (undo)
            _engine.Undo();
        else
            _engine.Redo();
        RefreshLayers();
        MarkDirty();
    }

    // メニューの HotKey にすると、数値の入力中の Ctrl+Z まで奪うのでここで受ける
    private void OnUndoKeyDown(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        bool undo = e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control;
        bool redo = (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control)
            || (e.Key == Key.Z && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift));
        if (!undo && !redo)
            return;
        UndoOrRedo(undo);
        e.Handled = true;
    }

    // 継ぎ目を中央に寄せた表示のまま描くと、端をまたいで手で直せる
    private void StartManualSeamFix()
    {
        Top.ModeSegment.SelectedIndex = 1;
        Strip.Mode = PreviewMode.SeamCheck;
        Status.ShowMessage(Loc.T("S.Seam.ManualHint"));
    }

    // 確認中はプレビューが保存されないよう、ファイル操作と元の画像の変更を止める
    private void StartSeamBlend()
    {
        _seamBlendPreviewing = true;
        Top.IsEnabled = false;
        NoiseTab.IsEnabled = false;
        LayerTab.IsEnabled = false;
        Strip.Mode = PreviewMode.SeamCheck;
        _engine?.Cursor = null;
        _engine?.PreviewSeamBlend(SeamTab.ReadBlend());
    }

    private void EndSeamBlend(bool apply)
    {
        _seamBlendPreviewing = false;
        Top.IsEnabled = true;
        NoiseTab.IsEnabled = true;
        LayerTab.IsEnabled = true;
        SeamTab.ShowBlendControls(false);
        if (apply)
        {
            _engine?.CommitSeamBlend();
            MarkDirty();
        }
        else
        {
            _engine?.CancelSeamBlend();
        }
    }

    private void ApplyPaintMode()
    {
        bool paint = IsPaintMode;
        Rail.IsPaintMode = paint;
        Strip.ShowBrushSettings = paint;
        if (paint && Strip.Mode == PreviewMode.Effect)
            Strip.Mode = PreviewMode.Rect;
        else if (!paint)
            _painter?.HideCursor();
        if (!paint && _engine is not null)
            _engine.Cursor = null;
    }

    private void ShowPaintAvailability()
    {
        if (!IsPaintMode)
            return;
        string? message = Strip.Mode == PreviewMode.Effect ? "S.Paint.NotInEffect" : !IsActiveLayerVisible ? "S.Paint.LayerHidden" : null;
        if (message is null)
            return;
        Status.ShowMessage(Loc.T(message));
        if (_engine is not null)
            _engine.Cursor = null;
    }

    private void ClearActiveLayer()
    {
        _engine?.ClearActiveLayer();
        MarkDirty();
    }

    // 操作の参考は CLIP STUDIO PAINT。B ブラシ、E 消しゴム、J ぼかし (色混ぜ)、[ ] で大きさ
    private void OnPaintKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsPaintMode || e.KeyModifiers != KeyModifiers.None || FocusManager?.GetFocusedElement() is TextBox)
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
