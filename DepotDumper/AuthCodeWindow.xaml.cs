#nullable enable
using System.Windows;

namespace DepotDumper.GUI;

/// <summary>Modal prompt for a Steam Guard (authenticator app or email) code.</summary>
public partial class AuthCodeWindow : Window
{
    public string Code { get; private set; } = string.Empty;

    public AuthCodeWindow(AuthPromptKind kind, string? email, bool previousCodeWasIncorrect)
    {
        InitializeComponent();

        MessageText.Text = kind == AuthPromptKind.EmailCode
            ? $"Steam emailed a code to {email}. Enter it below."
            : "Enter the current code from your Steam Mobile App authenticator.";
        ErrorText.Visibility = previousCodeWasIncorrect ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => CodeBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0)
            return;

        Code = code;
        DialogResult = true;
    }
}
