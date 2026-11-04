using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UskEffect.App.Locale;

namespace UskEffect.App.Controls;

public enum UnsavedChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Avalonia には標準のメッセージボックスがないので、最小限のモーダルダイアログを用意する。</summary>
public static class Dialogs
{
    public static Task ShowMessageAsync(Window owner, string title, string message) =>
        ShowAsync(owner, title, message, dismissed: false, (Loc.T("S.Dialog.Ok"), true));

    /// <summary>未保存の変更を保存するか確認する。ウィンドウを閉じた場合はキャンセル扱い。</summary>
    public static Task<UnsavedChoice> AskSaveChangesAsync(Window owner, string documentName) =>
        ShowAsync(owner, Loc.T("S.Dialog.UnsavedTitle"), Loc.F("S.Dialog.UnsavedBody", documentName), dismissed: UnsavedChoice.Cancel,
            (Loc.T("S.Dialog.Save"), UnsavedChoice.Save), (Loc.T("S.Dialog.Discard"), UnsavedChoice.Discard), (Loc.T("S.Dialog.Cancel"), UnsavedChoice.Cancel));

    /// <summary>取り消せない操作の確認。実行するなら true。ウィンドウを閉じた場合はキャンセル扱い。</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string action) =>
        ShowAsync(owner, title, message, dismissed: false, (action, true), (Loc.T("S.Dialog.Cancel"), false));

    /// <summary>ボタンを押さずにウィンドウを閉じたときは dismissed を返す。</summary>
    private static async Task<T> ShowAsync<T>(Window owner, string title, string message, T dismissed, params (string Label, T Value)[] buttons)
    {
        T result = dismissed;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 360,
            MaxWidth = 640,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var (label, value) in buttons)
        {
            var button = new Button { Content = label };
            button.Classes.Add("btn");
            button.Click += (_, _) =>
            {
                result = value;
                dialog.Close();
            };
            buttonRow.Children.Add(button);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttonRow,
            },
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
