using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UskEffect.App.Controls.Ui;

/// <summary>透明を表す市松模様。キャンバスのプレビューと同じ色。</summary>
public sealed class CheckerBackdrop : Control
{
    private const double Cell = 8;
    private static readonly IBrush Dark = new SolidColorBrush(Color.FromRgb(26, 26, 26));
    private static readonly IBrush Light = new SolidColorBrush(Color.FromRgb(32, 32, 32));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Dark, new Rect(Bounds.Size));
        for (int y = 0; y * Cell < Bounds.Height; y++)
        {
            for (int x = y % 2 == 0 ? 1 : 0; x * Cell < Bounds.Width; x += 2)
                context.FillRectangle(Light, new Rect(x * Cell, y * Cell, Cell, Cell).Intersect(new Rect(Bounds.Size)));
        }
    }
}
