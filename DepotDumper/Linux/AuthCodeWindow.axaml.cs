using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DepotDumper.Linux;

/// <summary>Modal prompt for a Steam Guard (authenticator app or email) code. Mirrors the Windows AuthCodeWindow.</summary>
public partial class AuthCodeWindow : Window
{
    public string? Code { get; private set; }

    public AuthCodeWindow() => InitializeComponent();

    public AuthCodeWindow(AuthPromptKind kind, string? email, bool previousCodeWasIncorrect) : this()
    {
        MessageText.Text = kind == AuthPromptKind.EmailCode
            ? $"Steam emailed a code to {email}. Enter it below."
            : "Enter the current code from your Steam Mobile App authenticator.";
        ErrorText.IsVisible = previousCodeWasIncorrect;
        Opened += (_, _) => CodeBox.Focus();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text?.Trim();
        if (string.IsNullOrEmpty(code)) return;
        Code = code;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
