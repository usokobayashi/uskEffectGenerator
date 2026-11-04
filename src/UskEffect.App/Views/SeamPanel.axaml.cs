using Avalonia.Controls;
using UskEffect.App.Locale;
using UskEffect.Graphics;
using UskEffect.Graphics.Analysis;
using UskEffect.Graphics.Painting;

namespace UskEffect.App.Views;

public partial class SeamPanel : UserControl
{
    public event Action? Changed;

    /// <summary>シーム修正の操作 (FR-13)。</summary>
    public event Action? ManualFixRequested;
    public event Action? BlendStarted;
    public event Action? BlendChanged;
    public event Action? BlendApplied;
    public event Action? BlendCancelled;

    public SeamPanel()
    {
        InitializeComponent();
        HeatmapSegment.SelectionChanged += (_, _) => Changed?.Invoke();
        ThresholdField.ValueChanged += (_, _) => Changed?.Invoke();

        ManualFixButton.Click += (_, _) => ManualFixRequested?.Invoke();
        BlendButton.Click += (_, _) =>
        {
            ShowBlendControls(true);
            BlendStarted?.Invoke();
        };
        BlendAxisSegment.SelectionChanged += (_, _) => RaiseBlendChanged();
        BlendWidthField.ValueChanged += (_, _) => RaiseBlendChanged();
        BlendStrengthField.ValueChanged += (_, _) => RaiseBlendChanged();
        BlendApplyButton.Click += (_, _) => BlendApplied?.Invoke();
        BlendCancelButton.Click += (_, _) => BlendCancelled?.Invoke();

        ShowAnalysis(null, pending: true);
    }

    public void ShowBlendControls(bool visible)
    {
        BlendControls.IsVisible = visible;
        FixButtons.IsVisible = !visible;
    }

    public SeamBlendSettings ReadBlend() => new(
        BlendAxisSegment.SelectedIndex switch { 0 => SeamMode.X, 1 => SeamMode.Y, _ => SeamMode.XY },
        BlendWidthField.Value,
        BlendStrengthField.Value);

    private void RaiseBlendChanged()
    {
        if (BlendControls.IsVisible)
            BlendChanged?.Invoke();
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
