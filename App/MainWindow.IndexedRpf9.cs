using Microsoft.Win32;
using RpfPreview.Core;
using System.IO;
using System.Windows;

namespace RpfPreview.App;

public partial class MainWindow
{
    private static string DecoderConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rpf9-explorer", "rpf9-decoder.json");
    private Rpf9IndexProvider? _indexProvider;

    private void LoadIndexedDecoder()
    {
        if (!File.Exists(DecoderConfigurationPath)) return;
        try { _indexProvider = Rpf9IndexProvider.Load(DecoderConfigurationPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { StatusText.Text = "Local RPF9 decoder unavailable: " + ex.Message; }
    }

    private void ConfigureIndexedDecoder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose a local RPF9 decoder configuration you trust",
            Filter = "Decoder configuration (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var provider = Rpf9IndexProvider.Load(dialog.FileName);
            string target = DecoderConfigurationPath;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!Path.GetFullPath(dialog.FileName).Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                string pending = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(dialog.FileName, pending); File.Move(pending, target, true); }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
            _indexProvider = provider;
            StatusText.Text = "Local RPF9 decoder enabled. Reopen an archive to use its index.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { StatusText.Text = "Could not configure the local decoder: " + ex.Message; }
    }

    private void DisableIndexedDecoder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            File.Delete(DecoderConfigurationPath);
            _indexProvider = null;
            StatusText.Text = "Recovery scans enabled for the next archive you open.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusText.Text = "Could not update decoder settings: " + ex.Message; }
    }

    private async Task<bool> ChangeIndexedArchiveAsync(ArchiveDocument source, int[] trail, bool goToParent = false)
    {
        if (_busy || !source.HasDecodedRpf9Index || trail.Length > 16) return false;
        _previewCancellation?.Cancel();
        CancelThumbnails();
        ClosePlayer();
        _workCancellation = new CancellationTokenSource();
        var token = _workCancellation.Token;
        SetBusy(true, true);
        WorkProgress.IsIndeterminate = true;
        StatusText.Text = "Opening indexed archive…";
        try
        {
            var destination = await Task.Run(() =>
            {
                var document = goToParent ? source.ParentArchive ?? source : source;
                foreach (int slot in trail)
                {
                    if (slot < 0 || slot >= document.Entries.Count) throw new InvalidDataException("The nested archive slot is no longer available.");
                    document = ArchiveReader.OpenNestedRpf9(document, document.Entries[slot], token);
                }
                return document;
            }, token);
            var documents = _documents.Values.Select(d => d.SourcePath.Equals(destination.SourcePath, StringComparison.OrdinalIgnoreCase) ? destination : d).ToArray();
            var index = await Task.Run(() => AssetWorkspaceIndex.Build(documents, cancellationToken: token), token);
            token.ThrowIfCancellationRequested();
            _documents[destination.SourcePath] = destination;
            _assetIndex = index;
            _allRows = index.Assets.Select(a => new FileRow(a.Entry) { Asset = a }).ToArray();
            _scopeSource = destination;
            _scopeContainer = null;
            _selectedFolder = null;
            _shownReplacementTarget = null;
            _loading = true;
            try
            {
                FilesGrid.SelectedItem = null;
                SearchBox.Clear();
                AssetTypeFilter.SelectedIndex = 0;
                ThumbnailsCheckBox.IsChecked = false;
                ResetAssetPreview();
                SetActiveDocument(destination);
                BuildLoadedTextureLookup();
                BuildWorkspaceTree();
            }
            finally { _loading = false; }
            RefreshReplacementAnnotations();
            RefreshFiles();
            ResetPreview();
            UpdateWorkspaceHeader();
            StatusText.Text = $"{destination.Entries.Count:N0} indexed members · header signature verified"
                + (index.Warnings.Count == 0 ? "" : $" · {index.Warnings.Count:N0} preview notices");
            return true;
        }
        catch (OperationCanceledException) { StatusText.Text = "Archive opening canceled."; return false; }
        catch (Exception ex) { StatusText.Text = "Could not open nested archive: " + ex.Message; return false; }
        finally { _workCancellation.Dispose(); _workCancellation = null; SetBusy(false); }
    }
}
