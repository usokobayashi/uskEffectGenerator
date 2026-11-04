using Avalonia.Controls;
using UskEffect.Graphics.Painting;

namespace UskEffect.App.Views;

public partial class ToolRail : UserControl
{
    private PaintTool _tool = PaintTool.Brush;

    /// <summary>道具を選び直したときに発生する。</summary>
    public event Action? ToolChanged;

    public ToolRail()
    {
        InitializeComponent();
        BrushToolButton.Click += (_, _) => Tool = PaintTool.Brush;
        EraserToolButton.Click += (_, _) => Tool = PaintTool.Eraser;
        BlurToolButton.Click += (_, _) => Tool = PaintTool.Blur;
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

    private void ShowTool()
    {
        BrushToolButton.Classes.Set("selected", _tool == PaintTool.Brush);
        EraserToolButton.Classes.Set("selected", _tool == PaintTool.Eraser);
        BlurToolButton.Classes.Set("selected", _tool == PaintTool.Blur);
    }
}
