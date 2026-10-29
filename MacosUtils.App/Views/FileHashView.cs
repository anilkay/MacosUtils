using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MacosUtils.Core.Hashing;

namespace MacosUtils.App.Views;

/// <summary>Computes MD5 / SHA-1 / SHA-256 / SHA-512 of a file in one pass and checks a pasted hash against them.</summary>
public sealed class FileHashView : UtilityView
{
    private static readonly (string Name, HashAlgorithmName Algorithm)[] Algorithms =
    [
        ("MD5", HashAlgorithmName.MD5),
        ("SHA-1", HashAlgorithmName.SHA1),
        ("SHA-256", HashAlgorithmName.SHA256),
        ("SHA-512", HashAlgorithmName.SHA512),
    ];

    private readonly TextBox _fileBox = new() { Width = 420, PlaceholderText = "Select a file... (or drop one here)" };
    private readonly Dictionary<HashAlgorithmName, CheckBox> _checks = [];
    private readonly Dictionary<HashAlgorithmName, TextBox> _results = [];
    private readonly Button _computeButton = AccentButton("Compute Hashes");
    private readonly TextBox _verifyBox = new() { Width = 520, PlaceholderText = "Paste a hash here to verify..." };
    private readonly TextBlock _verifyResult = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly TextBlock _status = StatusLine();
    private IReadOnlyDictionary<HashAlgorithmName, string>? _hashes;
    private CancellationTokenSource? _cts;

    public FileHashView()
    {
        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) =>
        {
            if (await PickFileAsync("Choose a file to hash") is { } file)
                _fileBox.Text = file;
        };
        _computeButton.Click += async (_, _) => await ComputeAsync();
        _verifyBox.TextChanged += (_, _) => UpdateVerify();

        var checks = new List<Control> { Label("Algorithms:") };
        var results = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), Margin = new Avalonia.Thickness(0, 4, 0, 8) };
        var row = 0;
        foreach (var (name, algorithm) in Algorithms)
        {
            var check = new CheckBox { Content = name, IsChecked = algorithm != HashAlgorithmName.SHA512 };
            _checks[algorithm] = check;
            checks.Add(check);

            results.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Label(name);
            var box = new TextBox { IsReadOnly = true, Margin = new Avalonia.Thickness(0, 2) };
            box.Classes.Add("mono");
            _results[algorithm] = box;
            Grid.SetRow(label, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            results.Children.Add(label);
            results.Children.Add(box);
            row++;
        }

        var top = new StackPanel
        {
            Children =
            {
                Toolbar(Label("File:"), _fileBox, browse),
                Toolbar([.. checks, _computeButton]),
                results,
                Toolbar(Label("Verify:"), _verifyBox, _verifyResult),
            },
        };
        DockPanel.SetDock(top, Dock.Top);
        Content = new DockPanel { Children = { top, _status, new Panel() } };

        // Finder drag and drop: drop a file anywhere on the screen.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles()?.FirstOrDefault() is { } dropped && dropped.TryGetLocalPath() is { } path)
                _fileBox.Text = path;
        });
    }

    private async Task ComputeAsync()
    {
        var path = _fileBox.Text?.Trim() ?? "";
        if (!File.Exists(path))
        {
            await Dialogs.ShowAsync(this, "File Hash", "Select an existing file first.");
            return;
        }
        var selected = _checks.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToList();
        if (selected.Count == 0)
        {
            await Dialogs.ShowAsync(this, "File Hash", "Select at least one hash algorithm.");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _computeButton.IsEnabled = false;
        _computeButton.Content = "Computing...";
        foreach (var box in _results.Values)
            box.Text = "";
        _status.Text = $"Hashing {Path.GetFileName(path)} ({FormatBytes(new FileInfo(path).Length)})...";
        try
        {
            _hashes = await FileHasher.ComputeHashesAsync(path, selected, _cts.Token);
            foreach (var (algorithm, hash) in _hashes)
                _results[algorithm].Text = hash;
            _status.Text = $"Done: {path}";
            UpdateVerify();
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowAsync(this, "File Hash", $"Failed to hash file: {ex.Message}");
            _status.Text = "";
        }
        finally
        {
            _computeButton.IsEnabled = true;
            _computeButton.Content = "Compute Hashes";
        }
    }

    private void UpdateVerify()
    {
        var candidate = _verifyBox.Text ?? "";
        _verifyResult.Classes.Set("success", false);
        _verifyResult.Classes.Set("error", false);
        if (candidate.Trim().Length == 0 || _hashes is null)
        {
            _verifyResult.Text = "";
            return;
        }
        var match = FileHasher.FindMatch(_hashes, candidate);
        var name = match is { } m ? Algorithms.First(a => a.Algorithm == m).Name : null;
        _verifyResult.Text = name is not null ? $"MATCH ({name})" : "NO MATCH with any computed hash.";
        _verifyResult.Classes.Set(name is not null ? "success" : "error", true);
    }
}
