using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UskEffect.App.Locale;
using UskEffect.Graphics.Painting;

namespace UskEffect.App.Views;

public partial class ToolRail : UserControl
{
    private const double HiddenLayerOpacity = 0.35;

    private PaintTool _tool = PaintTool.Brush;

    /// <summary>道具を選び直したときに発生する。</summary>
    public event Action? ToolChanged;

    public ToolRail()
    {
        InitializeComponent();
        BrushToolButton.Click += (_, _) => Tool = PaintTool.Brush;
        EraserToolButton.Click += (_, _) => Tool = PaintTool.Eraser;
        BlurToolButton.Click += (_, _) => Tool = PaintTool.Blur;
        LayerButton.Flyout!.Opened += (_, _) => LayerButton.Classes.Set("selected", true);
        LayerButton.Flyout.Closed += (_, _) =>
        {
            LayerButton.Classes.Set("selected", false);
            LayerPopup.HideAddChoice();
        };
        // レイヤーの中にフォーカスがあるときは、キーが窓まで届かない
        LayerPopup.AddHandler(KeyDownEvent, (_, e) => CloseLayersOnEscape(e), RoutingStrategies.Tunnel);
        ShowTool();
    }

    public PaintTool Tool
    {
        get => _tool;
        set
        {
            if (value == _tool)
                return;
            _tool = value;
            ShowTool();
            ToolChanged?.Invoke();
        }
    }

    public bool IsPaintMode
    {
        set
        {
            foreach (var button in new[] { BrushToolButton, EraserToolButton, BlurToolButton })
                button.IsEnabled = value;
        }
    }

    /// <summary>今のレイヤーを出す。label は番号、thumbnail は縮小画像。</summary>
    public void ShowCurrentLayer(string label, string name, bool visible, Avalonia.Media.Imaging.Bitmap? thumbnail)
    {
        LayerNumberText.Text = label;
        LayerThumb.Source = thumbnail;
        LayerThumb.IsVisible = thumbnail is not null;
        LayerIcon.IsVisible = thumbnail is null;
        LayerButton.Opacity = visible ? 1 : HiddenLayerOpacity;
        ToolTip.SetTip(LayerButton, Loc.F("S.Layer.Current", name));
    }

    public bool IsLayerOpen => LayerButton.Flyout!.IsOpen;

    public void CloseLayers() => LayerButton.Flyout!.Hide();

    /// <summary>Esc でレイヤーを閉じる。数値の入力中は、まず入力の取り消しに使う。</summary>
    public void CloseLayersOnEscape(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsLayerOpen
            || TopLevel.GetTopLevel(LayerPopup)?.FocusManager?.GetFocusedElement() is TextBox)
            return;
        CloseLayers();
        e.Handled = true;
    }

    private void ShowTool()
    {
        BrushToolButton.Classes.Set("selected", _tool == PaintTool.Brush);
        EraserToolButton.Classes.Set("selected", _tool == PaintTool.Eraser);
        BlurToolButton.Classes.Set("selected", _tool == PaintTool.Blur);
    }
}
