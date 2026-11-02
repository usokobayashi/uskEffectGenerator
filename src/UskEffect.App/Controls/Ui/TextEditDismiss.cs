using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace UskEffect.App.Controls.Ui;

/// <summary>
/// 数値などの入力中に、入力欄の外をクリックしたら入力を終える。
/// フォーカスを持てない場所をクリックしてもフォーカスが移らないので、窓全体でクリックを見張る。
/// 子 HWND (キャンバス) のクリックは Avalonia を通らないので、EndActive を呼んでもらう。
/// </summary>
public static class TextEditDismiss
{
    private static Action? s_endActive;

    /// <summary>入力中のものがあれば確定して終える。</summary>
    public static void EndActive() => s_endActive?.Invoke();

    /// <summary>入力欄のフォーカスの間だけ見張る。end は入力を確定して終える処理。</summary>
    public static void Attach(TextBox box, Action end)
    {
        TopLevel? watched = null;

        void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Visual source && (ReferenceEquals(source, box) || box.IsVisualAncestorOf(source)))
                return;
            end();
        }

        void Stop()
        {
            watched?.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            watched = null;
            if (ReferenceEquals(s_endActive, end))
                s_endActive = null;
        }

        box.GotFocus += (_, _) =>
        {
            Stop();
            watched = TopLevel.GetTopLevel(box);
            watched?.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            s_endActive = end;
        };
        box.LostFocus += (_, _) => Stop();
        box.DetachedFromVisualTree += (_, _) => Stop();
    }
}
