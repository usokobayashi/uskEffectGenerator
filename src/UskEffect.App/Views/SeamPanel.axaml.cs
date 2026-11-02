using Avalonia.Controls;
using UskEffect.App.Locale;
using UskEffect.Graphics;
using UskEffect.Graphics.Analysis;

namespace UskEffect.App.Views;

public partial class SeamPanel : UserControl
{
    public event Action? Changed;

    public SeamPanel()
    {
        InitializeComponent();
        HeatmapSegment.SelectionChanged += (_, _) => Changed?.Invoke();
        ThresholdField.ValueChanged += (_, _) => Changed?.Invoke();
        ShowAnalysis(null, pending: true);
    }

    public HeatmapMode Heatmap
    {
        get => (HeatmapMode)Math.Max(0, HeatmapSegment.SelectedIndex);
        set => HeatmapSegment.SelectedIndex = (int)value;
    }

    public float Threshold
    {
        get => (float)ThresholdField.Value;
        set => ThresholdField.SetValueExpanding(value);
    }

    public void ShowAnalysis(SeamAnalysis? seam, bool pending)
    {
        if (seam is null)
        {
            VerdictText.Text = Loc.T("S.Seam.Analyzing");
            VerdictText.Classes.Set("ok", false);
            VerdictText.Classes.Set("bad", false);
            DetailText.Text = "";
            return;
        }

        bool seamless = seam.Horizontal.IsSeamless && seam.Vertical.IsSeamless;
        VerdictText.Text = $"{(seamless ? "✓" : "!")} {Loc.T(seamless ? "S.Seam.None" : "S.Seam.Found")}{(pending ? Loc.T("S.Seam.Updating") : "")}";
        VerdictText.Classes.Set("ok", seamless);
        VerdictText.Classes.Set("bad", !seamless);
        DetailText.Text = Loc.F("S.Seam.Detail",
            seam.Horizontal.ValueRatio, seam.Horizontal.GradientRatio,
            seam.Vertical.ValueRatio, seam.Vertical.GradientRatio,
            SeamReport.SeamlessValueLimit, SeamReport.SeamlessGradientLimit);
    }
}
