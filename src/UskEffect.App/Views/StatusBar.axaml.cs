using Avalonia.Controls;
using Avalonia.Threading;
using UskEffect.App.Locale;
using UskEffect.Graphics;

namespace UskEffect.App.Views;

public partial class StatusBar : UserControl
{
    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _messageTimer = new();
    private string? _shownErrors;
    private bool _vsync = true;

    /// <summary>VSync を切り替えたときに発生する。VSync はプロジェクトに含めない表示設定。</summary>
    public event Action<bool>? VSyncChanged;

    public StatusBar()
    {
        InitializeComponent();

        _messageTimer.Interval = MessageDuration;
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            MessageText.Text = "";
        };
        VSyncButton.Click += (_, _) =>
        {
            _vsync = !_vsync;
            VSyncButton.Content = Loc.T(_vsync ? "S.Status.VSyncOn" : "S.Status.VSyncOff");
            VSyncChanged?.Invoke(_vsync);
        };
    }

    /// <summary>しばらく表示して消える知らせ。fps の更新で上書きされない欄に出す。</summary>
    public void ShowMessage(string message)
    {
        MessageText.Text = message;
        ToolTip.SetTip(MessageText, message);
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    public void ShowFailure(string message) => GpuText.Text = message;

    public void ShowStats(RenderStats stats, double fps)
    {
        GpuText.Text = stats.AdapterName;
        ToolTip.SetTip(GpuText, $"{stats.AdapterName}\n{Loc.F("S.Status.Viewport", stats.ViewportWidth, stats.ViewportHeight)}");
        ResolutionText.Text = $"{stats.TextureWidth}×{stats.TextureHeight}";
        NoiseTimeText.Text = $"Noise {stats.ComputeMilliseconds:0.00} ms";
        FrameTimeText.Text = $"Frame {stats.FrameMilliseconds:0.00} ms";
        FpsText.Text = $"{fps:0} fps";

        if (stats.ShaderErrors != _shownErrors)
        {
            _shownErrors = stats.ShaderErrors;
            ShaderErrorText.Text = _shownErrors;
            ShaderErrorButton.IsVisible = _shownErrors is not null;
        }
    }
}
