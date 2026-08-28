using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CheatPatcher.Gui;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _pnachPaths = new();
    private string? _inputPath;
    private bool _isIso;

    public MainWindow()
    {
        InitializeComponent();
        PnachList.ItemsSource = _pnachPaths;
        TryLoadWindowIcon();
    }

    private void TryLoadWindowIcon()
    {
        try
        {
            string[] candidatePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico"),
                Path.Combine(Directory.GetCurrentDirectory(), "icon.ico"),
                "icon.ico"
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    Icon = new WindowIcon(path);
                    return;
                }
            }

            var resourceUri = new Uri("avares://CheatPatcher.Gui/icon.ico");
            if (Avalonia.Platform.AssetLoader.Exists(resourceUri))
            {
                using var stream = Avalonia.Platform.AssetLoader.Open(resourceUri);
                Icon = new WindowIcon(stream);
            }
        }
        catch
        {
            // Fallback gracefully if icon loading fails or file format is unsupported on current OS
        }
    }

    // ============================================================
    // Input / output pickers
    // ============================================================
    private async void OnBrowseInput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select ELF or ISO",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ELF / ISO") { Patterns = new[] { "*.elf", "*.iso", "*.*" } },
            },
        });
        if (files.Count == 0) return;

        _inputPath = files[0].TryGetLocalPath();
        if (_inputPath is null) return;

        InputPathBox.Text = _inputPath;
        _isIso = Path.GetExtension(_inputPath).Equals(".iso", StringComparison.OrdinalIgnoreCase);

        ElfNameBox.IsVisible = _isIso;
        OutputLabel.Text = _isIso ? "2. Output folder (patched disc contents)" : "2. Output ELF path";
        OutputPathBox.Text = "";
    }

    private async void OnBrowseOutput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_inputPath is null)
        {
            AppendLog("Pick the input ELF/ISO first.\n");
            return;
        }

        if (_isIso)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select output folder for patched disc contents",
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;
            OutputPathBox.Text = folders[0].TryGetLocalPath();
        }
        else
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save patched ELF as",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_inputPath) + "_patched.elf",
                FileTypeChoices = new[] { new FilePickerFileType("ELF") { Patterns = new[] { "*.elf" } } },
            });
            if (file is null) return;
            OutputPathBox.Text = file.TryGetLocalPath();
        }
    }

    // ============================================================
    // Pnach list management
    // ============================================================
    private async void OnAddPnachFiles(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select .pnach file(s)",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("pnach") { Patterns = new[] { "*.pnach" } } },
        });
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (path is not null && !_pnachPaths.Contains(path)) _pnachPaths.Add(path);
        }
    }

    private async void OnAddPnachFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a folder of .pnach files",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (path is not null && !_pnachPaths.Contains(path)) _pnachPaths.Add(path);
    }

    private void OnRemovePnach(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (PnachList.SelectedItems is null) return;
        foreach (var item in PnachList.SelectedItems.Cast<string>().ToList())
            _pnachPaths.Remove(item);
    }

    // ============================================================
    // Patch
    // ============================================================
    private async void OnPatchClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_inputPath is null)
        {
            AppendLog("Pick an input ELF/ISO first.\n");
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            AppendLog("Pick an output path first.\n");
            return;
        }
        if (_isIso && string.IsNullOrWhiteSpace(ElfNameBox.Text))
        {
            AppendLog("Enter the ELF filename inside the ISO first.\n");
            return;
        }
        if (_pnachPaths.Count == 0)
        {
            AppendLog("Add at least one .pnach file or folder first.\n");
            return;
        }

        LogBox.Text = "";
        PatchButton.IsEnabled = false;
        StatusText.Text = "Patching...";

        string inputPath = _inputPath;
        string outputPath = OutputPathBox.Text!;
        string elfName = ElfNameBox.Text ?? "";
        string[] pnachArgs = _pnachPaths.ToArray();
        string mastercodeLine = MastercodeBox.Text ?? "";
        bool isIso = _isIso;

        // Route the console app's Console.WriteLine progress output into
        // the log box instead of duplicating its logging here.
        var originalOut = Console.Out;
        Console.SetOut(new TextBoxWriter(this));

        try
        {
            await Task.Run(() =>
            {
                var hook = CheatPatcher.Program.ParseHookConfig(string.IsNullOrWhiteSpace(mastercodeLine) ? null : mastercodeLine);

                if (isIso)
                    CheatPatcher.Program.RunIso(inputPath, outputPath, elfName, pnachArgs, hook);
                else
                    CheatPatcher.Program.RunElf(inputPath, outputPath, pnachArgs, hook);
            });

            StatusText.Text = "Done.";
        }
        catch (Exception ex)
        {
            AppendLog($"\nERROR: {ex.Message}\n");
            StatusText.Text = "Failed -- see log above.";
        }
        finally
        {
            Console.SetOut(originalOut);
            PatchButton.IsEnabled = true;
        }
    }

    private void AppendLog(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text = (LogBox.Text ?? "") + text;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        });
    }

    // Forwards Console.Write/WriteLine calls made by the patch engine
    // (CheatPatcher.Program) into this window's log box.
    private sealed class TextBoxWriter : TextWriter
    {
        private readonly MainWindow _owner;
        public TextBoxWriter(MainWindow owner) => _owner = owner;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => _owner.AppendLog(value.ToString());
        public override void Write(string? value) => _owner.AppendLog(value ?? "");
    }
}
