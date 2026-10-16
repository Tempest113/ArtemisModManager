using System.Windows;
using System.Windows.Input;
using Artemis.Core.Text;

namespace Artemis.App.Views;

/// <summary>One small themed dialog for messages, yes/no questions and asking for a name.</summary>
public partial class DialogWindow : Window
{
    private DialogWindow(string title, string body, string ok, bool cancel, string? input)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsVisible: true } main ? main : null;
        if (Owner is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TitleText.Text = title;
        BodyText.Text = body;
        BodyText.Visibility = body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        OkButton.Content = ok;
        CancelButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        if (input is not null)
        {
            Input.Visibility = Visibility.Visible;
            Input.Text = input;
            Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
        }
    }

    public string Value => Input.Text;

    public bool Option => OptionBox.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox) DragMove();
    }

    public static void Message(string text) => new DialogWindow("Artemis", text, Loc.T("win.ok"), cancel: false, input: null).ShowDialog();

    public static bool Ask(string text, string yes = "Yes") =>
        new DialogWindow("Artemis", text, yes, cancel: true, input: null).ShowDialog() == true;

    /// <summary>A name to type and one box to tick. Null when cancelled or the name is empty.</summary>
    public static (string Name, bool Option)? PromptWithOption(string title, string initial, string optionText)
    {
        var window = new DialogWindow(title, "", Loc.T("win.ok"), cancel: true, input: initial);
        window.OptionBox.Content = optionText;
        window.OptionBox.Visibility = Visibility.Visible;
        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.Value) ? (window.Value.Trim(), window.Option) : null;
    }

    public static string? Prompt(string title, string initial)
    {
        var window = new DialogWindow(title, "", Loc.T("win.ok"), cancel: true, input: initial);
        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.Value) ? window.Value.Trim() : null;
    }
}
