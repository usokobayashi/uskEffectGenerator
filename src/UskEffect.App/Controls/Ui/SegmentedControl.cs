using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Transformation;

namespace UskEffect.App.Controls.Ui;

/// <summary>
/// 滑るボタン群。選んだ面が選択肢の間を滑って移る。
/// ListBox を継承するのでキーボード操作・選択処理・アクセシビリティは標準のものを使う。
/// </summary>
[TemplatePart("PART_Thumb", typeof(Border))]
public sealed class SegmentedControl : ListBox
{
    private Border? _thumb;
    private Rect _thumbRect;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _thumb = e.NameScope.Find<Border>("PART_Thumb");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LayoutUpdated -= OnLayoutUpdated;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedIndexProperty)
            PlaceThumb(animate: true);
    }

    // 窓の大きさが変わったときや最初の配置では滑らせない
    private void OnLayoutUpdated(object? sender, EventArgs e) => PlaceThumb(animate: false);

    private void PlaceThumb(bool animate)
    {
        if (_thumb is null || ContainerFromIndex(SelectedIndex) is not { } container || container.Bounds == _thumbRect)
            return;

        _thumbRect = container.Bounds;
        _thumb.Width = _thumbRect.Width;
        _thumb.Height = _thumbRect.Height;
        _thumb.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        // Transitions を外した設定は ClearValue でテーマのものに戻す
        if (!animate)
            _thumb.Transitions = null;
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(_thumbRect.X, _thumbRect.Y);
        _thumb.RenderTransform = builder.Build();
        if (!animate)
            _thumb.ClearValue(TransitionsProperty);
    }
}

/// <summary>
/// 選択肢の並び。全部を同じ幅にできればそろえ、足りなければ文字の幅に合わせて配分して文字を切らない。
/// </summary>
public sealed class SegmentPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        double widest = 0, height = 0;
        foreach (var child in children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            widest = Math.Max(widest, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        // 文字に合わせた幅を求める。親が広げる配置 (Grid の * 列など) なら Arrange で広がる
        return new Size(Math.Min(widest * children.Count, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        int count = children.Count;
        if (count == 0)
            return finalSize;

        double widest = children.Max(c => c.DesiredSize.Width);
        double total = children.Sum(c => c.DesiredSize.Width);
        double extra = (finalSize.Width - total) / count;
        bool uniform = widest * count <= finalSize.Width;

        double x = 0;
        foreach (var child in children)
        {
            double width = uniform ? finalSize.Width / count : child.DesiredSize.Width + extra;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}

/// <summary>スイッチ。ToggleButton の見た目だけを変える。</summary>
public sealed class SwitchButton : ToggleButton;

/// <summary>折りたたみ見出し。IsChecked が開いている状態。</summary>
public sealed class SectionHeader : ToggleButton;
