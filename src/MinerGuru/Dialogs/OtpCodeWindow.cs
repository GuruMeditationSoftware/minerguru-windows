using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using MinerGuru.Controls;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

/// <summary>
/// Asks for the one-time code. A small window of its own, not a ContentDialog: writes often start from
/// an open dialog, and a window holds only one of those at a time.
/// </summary>
public sealed partial class OtpCodeWindow : Window
{
    private const int LogicalWidth = 400;
    private const int LogicalHeight = 270;

    private readonly TaskCompletionSource<string?> _answer = new();
    private readonly TextBox _code = new() { MaxLength = 6, PlaceholderText = "123456" };

    private OtpCodeWindow(string minerName, string? problem, ElementTheme theme)
    {
        Title = "MinerGuru";
        SystemBackdrop = new MicaBackdrop();
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.Number));
        _code.InputScope = scope;
        _code.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) Finish(_code.Text);
            else if (e.Key == VirtualKey.Escape) Finish(null);
        };

        var title = new TextBlock
        {
            Text = L($"Einmalcode für {minerName}", $"One-time code for {minerName}"),
            FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        var text = new TextBlock
        {
            Text = problem ?? L("Dieser Miner schützt seine Einstellungen mit einem Einmalcode. Gib den 6-stelligen Code aus deiner Authenticator-App ein. Die Sitzung gilt bis zu 24 Stunden.",
                                "This miner protects its settings with a one-time code. Enter the 6-digit code from your authenticator app. The session lasts up to 24 hours."),
            TextWrapping = TextWrapping.Wrap,
        };
        var cancel = PageParts.TextButton("\uE711", L("Abbrechen", "Cancel"), () => Finish(null));
        var confirm = PageParts.TextButton("\uE73E", L("Bestätigen", "Confirm"), () => Finish(_code.Text), accent: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var body = new StackPanel { Spacing = 14, Padding = new Thickness(24), RequestedTheme = theme };
        body.Children.Add(title);
        body.Children.Add(text);
        body.Children.Add(_code);
        body.Children.Add(buttons);
        body.Loaded += (_, _) => _code.Focus(FocusState.Programmatic);
        Content = body;

        AppWindow.TitleBar.PreferredTheme = theme == ElementTheme.Light ? TitleBarTheme.Light : TitleBarTheme.Dark;
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(LogicalWidth * scale), (int)(LogicalHeight * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
        Closed += (_, _) => _answer.TrySetResult(null);
    }

    public static Task<string?> AskAsync(string minerName, string? problem, ElementTheme theme)
    {
        var window = new OtpCodeWindow(minerName, problem, theme);
        window.Activate();
        return window._answer.Task;
    }

    private void Finish(string? answer)
    {
        _answer.TrySetResult(answer);
        Close();
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
