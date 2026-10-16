using System.Windows;
using System.Windows.Input;
using Artemis.Core.Playsets;
using Artemis.Core.Text;

namespace Artemis.App.Views;

/// <summary>Lists playsets found in other tools, each with a tick box. Used for the first-run welcome and for "Import playsets".</summary>
public partial class ImportWindow : Window
{
    public sealed class Choice(ExternalPlayset playset, bool isChecked)
    {
        public ExternalPlayset Playset { get; } = playset;
        public bool IsChecked { get; set; } = isChecked;
        public string Title => Playset.Name;
        public string Detail => Loc.T(Playset.Mods.Count == 1 ? "win.import_detail_one" : "win.import_detail_many", ("source", Playset.Source), ("count", Playset.Mods.Count));
    }

    private readonly List<Choice> _choices;

    private ImportWindow(string title, string body, string ok, List<Choice> choices)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsVisible: true } main ? main : null;
        if (Owner is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TitleText.Text = title;
        BodyText.Text = body;
        OkButton.Content = ok;
        Choices.ItemsSource = _choices = choices;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase == false) DragMove();
    }

    /// <summary>The ticked playsets, or null if the user skipped.</summary>
    public static IReadOnlyList<ExternalPlayset>? Pick(string title, string body, string ok, IReadOnlyList<ExternalPlayset> found, Func<ExternalPlayset, bool> preselect)
    {
        var window = new ImportWindow(title, body, ok, found.Select(p => new Choice(p, preselect(p))).ToList());
        return window.ShowDialog() == true ? window._choices.Where(c => c.IsChecked).Select(c => c.Playset).ToList() : null;
    }
}
