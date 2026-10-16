using Artemis.App.ViewModels;
using Artemis.Core.Playsets;
using Microsoft.Win32;

namespace Artemis.App.Views;

public sealed class Dialogs : IDialogs
{
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void Error(string message) => DialogWindow.Message(message);

    public string? Prompt(string title, string initial) => DialogWindow.Prompt(title, initial);

    public bool Confirm(string message, string yes = "Yes") => DialogWindow.Ask(message, yes);

    public (string Name, bool Option)? PromptWithOption(string title, string initial, string optionText) => DialogWindow.PromptWithOption(title, initial, optionText);

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string fileName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public IReadOnlyList<ExternalPlayset>? PickPlaysets(string title, string body, string ok, IReadOnlyList<ExternalPlayset> found, Func<ExternalPlayset, bool> preselect) =>
        ImportWindow.Pick(title, body, ok, found, preselect);
}
