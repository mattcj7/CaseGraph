using CaseGraph.App.Views.Dialogs;
using Microsoft.Win32;
using System.Windows;

namespace CaseGraph.App.Services;

public sealed class UserInteractionService : IUserInteractionService
{
    public string? PromptForCaseName()
    {
        var dialog = new CaseNameDialog
        {
            Owner = Application.Current.MainWindow
        };

        var result = dialog.ShowDialog();
        return result == true ? dialog.CaseName : null;
    }

    public IReadOnlyList<string> PickEvidenceFiles()
    {
        var fileDialog = new OpenFileDialog
        {
            Multiselect = true,
            CheckFileExists = true,
            Filter = "All files (*.*)|*.*"
        };

        var result = fileDialog.ShowDialog(Application.Current.MainWindow);
        if (result != true)
        {
            return Array.Empty<string>();
        }

        return fileDialog.FileNames;
    }

    public string? PickDebugBundleOutputPath(string defaultFileName)
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopPath))
        {
            desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var fileDialog = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".zip",
            Filter = "ZIP archive (*.zip)|*.zip|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(defaultFileName)
                ? "casegraph-debug.zip"
                : defaultFileName,
            InitialDirectory = desktopPath
        };

        var result = fileDialog.ShowDialog(Application.Current.MainWindow);
        return result == true ? fileDialog.FileName : null;
    }

    public void CopyToClipboard(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Clipboard.SetText(value);
    }
}
