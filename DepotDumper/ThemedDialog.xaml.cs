#nullable enable
using System.Windows;
using System.Windows.Controls;

namespace DepotDumper.GUI;

public enum DialogChoice { Primary, Secondary, Cancel }

/// <summary>The app's own message box: themed, with buttons that say what they do.</summary>
public partial class ThemedDialog : Window
{
    public DialogChoice Choice { get; private set; } = DialogChoice.Cancel;

    public ThemedDialog(string title, string message, MessageBoxImage icon, string primary, string? secondary, string? cancel, bool dangerPrimary)
    {
        InitializeComponent();
        WindowTheme.Follow(this);

        Title = "Depot Dumper GUI";
        TitleText.Text = title;
        MessageText.Text = message;

        var (glyph, brush) = icon switch
        {
            MessageBoxImage.Error => ("", "DangerBrush"),
            MessageBoxImage.Warning => ("", "WarnBrush"),
            MessageBoxImage.Question => ("", "AccentBrush"),
            MessageBoxImage.Information => ("", "AccentBrush"),
            _ => ("", "AccentBrush"),
        };
        IconText.Text = glyph;
        IconText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        IconText.Visibility = glyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        Button Make(string text, DialogChoice choice, bool isDefault, bool isCancel, string? style)
        {
            var b = new Button { Content = text, Padding = new Thickness(22, 9, 22, 9), MinWidth = 88, Margin = new Thickness(10, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            if (style != null) b.SetResourceReference(StyleProperty, style);
            b.Click += (_, _) => { Choice = choice; DialogResult = choice != DialogChoice.Cancel; };
            return b;
        }

        // left to right: Cancel, Secondary, Primary. Esc picks the least destructive button that exists.
        if (cancel != null) ButtonRow.Children.Add(Make(cancel, DialogChoice.Cancel, false, true, null));
        if (secondary != null) ButtonRow.Children.Add(Make(secondary, DialogChoice.Secondary, false, cancel == null, null));
        ButtonRow.Children.Add(Make(primary, DialogChoice.Primary, true, cancel == null && secondary == null, dangerPrimary ? "DangerButton" : "PrimaryButton"));
    }
}

public static class Dialogs
{
    /// <summary>Shows the dialog and returns which button was pressed.</summary>
    public static DialogChoice Choose(Window? owner, string title, string message, string primary, string? secondary = null, string? cancel = null,
        MessageBoxImage icon = MessageBoxImage.Question, bool dangerPrimary = false)
    {
        var dialog = new ThemedDialog(title, message, icon, primary, secondary, cancel, dangerPrimary);
        if (owner != null && owner.IsLoaded) dialog.Owner = owner; else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Choice;
    }

    /// <summary>Drop-in replacement for MessageBox.Show(owner, text, caption, buttons, icon).</summary>
    public static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                return Choose(owner, title, message, "Yes", "No", null, icon) == DialogChoice.Primary ? MessageBoxResult.Yes : MessageBoxResult.No;
            case MessageBoxButton.YesNoCancel:
                return Choose(owner, title, message, "Yes", "No", "Cancel", icon) switch { DialogChoice.Primary => MessageBoxResult.Yes, DialogChoice.Secondary => MessageBoxResult.No, _ => MessageBoxResult.Cancel };
            case MessageBoxButton.OKCancel:
                return Choose(owner, title, message, "OK", null, "Cancel", icon) == DialogChoice.Primary ? MessageBoxResult.OK : MessageBoxResult.Cancel;
            default:
                Choose(owner, title, message, "OK", null, null, icon);
                return MessageBoxResult.OK;
        }
    }
}
