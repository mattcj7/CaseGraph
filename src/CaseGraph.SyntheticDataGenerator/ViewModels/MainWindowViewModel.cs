using CaseGraph.SyntheticDataGenerator.Models;
using CaseGraph.SyntheticDataGenerator.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CaseGraph.SyntheticDataGenerator.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly SyntheticDatasetGenerator _generator = new();
    private readonly StringBuilder _statusBuilder = new();
    private IRelayCommand? _browseOutputFolderCommand;
    private IAsyncRelayCommand? _generateCommand;

    [ObservableProperty]
    private int seed = 424242;

    [ObservableProperty]
    private int datasetCount = 1;

    [ObservableProperty]
    private int personCount = 6;

    [ObservableProperty]
    private int approximateMessageCount = 120;

    [ObservableProperty]
    private int approximateLocationCount = 48;

    [ObservableProperty]
    private string selectedProfile = GeneratorOptions.OffenseWindowProfile;

    [ObservableProperty]
    private string outputFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "CaseGraphSyntheticData"
    );

    [ObservableProperty]
    private string statusText = "Choose options, select an output folder, and generate fictional synthetic datasets.";

    [ObservableProperty]
    private bool isGenerating;

    public IReadOnlyList<string> Profiles { get; } = GeneratorOptions.SupportedProfiles;

    public IRelayCommand BrowseOutputFolderCommand => _browseOutputFolderCommand ??= new RelayCommand(BrowseOutputFolder);

    public IAsyncRelayCommand GenerateCommand => _generateCommand ??= new AsyncRelayCommand(GenerateAsync, CanGenerate);

    private bool CanGenerate()
    {
        return !IsGenerating;
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the folder where synthetic datasets should be generated.",
            SelectedPath = string.IsNullOrWhiteSpace(OutputFolder)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : OutputFolder,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            OutputFolder = dialog.SelectedPath;
            AppendStatus($"Output folder set to: {OutputFolder}");
        }
    }

    private async Task GenerateAsync()
    {
        IsGenerating = true;
        GenerateCommand.NotifyCanExecuteChanged();
        StatusText = string.Empty;
        _statusBuilder.Clear();

        try
        {
            var options = new GeneratorOptions
            {
                Seed = Seed,
                DatasetCount = DatasetCount,
                PersonCount = PersonCount,
                ApproximateMessageCount = ApproximateMessageCount,
                ApproximateLocationCount = ApproximateLocationCount,
                Profile = SelectedProfile,
                OutputFolder = OutputFolder
            };

            options.Validate();
            AppendStatus("Starting synthetic dataset generation...");

            var progress = new Progress<string>(AppendStatus);
            var manifests = await _generator.GenerateAsync(options, progress, CancellationToken.None);
            AppendStatus(
                $"Completed. Generated {manifests.Count} dataset(s) in {OutputFolder} using seed {Seed} and profile {SelectedProfile}."
            );
        }
        catch (Exception ex)
        {
            AppendStatus($"Generation failed: {ex.Message}");
        }
        finally
        {
            IsGenerating = false;
            GenerateCommand.NotifyCanExecuteChanged();
        }
    }

    private void AppendStatus(string message)
    {
        if (_statusBuilder.Length > 0)
        {
            _statusBuilder.AppendLine();
        }

        _statusBuilder.Append($"{DateTime.Now:HH:mm:ss}  {message}");
        StatusText = _statusBuilder.ToString();
    }
}
