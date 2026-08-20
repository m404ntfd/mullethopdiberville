using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MulletHopKioskController;

internal enum SoftwareDownloadKind
{
    AllPrograms,
    WaiverKiosk,
    MulletHopPos
}

internal sealed record SoftwareReleaseAsset(
    string Name,
    Uri DownloadUri,
    long? Size,
    string ReleaseTag);

internal sealed record SoftwareDownloadProgress(long DownloadedBytes, long? TotalBytes);

internal static class SoftwareDownloadService
{
    private static readonly HttpClient Client = CreateClient();

    public static async Task<SoftwareReleaseAsset> FindLatestAssetAsync(
        SoftwareDownloadKind kind,
        CancellationToken cancellationToken)
    {
        var apiUri = BuildLatestReleaseApiUri(ControllerUpdater.ReleaseRepositoryUrl);
        using var response = await Client.GetAsync(apiUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub returned {(int)response.StatusCode} while checking the latest release.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var releaseTag = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? "latest"
            : "latest";
        if (!root.TryGetProperty("assets", out var assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The latest release does not list downloadable files.");
        }

        SoftwareReleaseAsset? bestAsset = null;
        var bestScore = -1;
        foreach (var item in assetsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameElement) ||
                !item.TryGetProperty("browser_download_url", out var urlElement))
            {
                continue;
            }

            var name = nameElement.GetString()?.Trim() ?? string.Empty;
            var url = urlElement.GetString()?.Trim() ?? string.Empty;
            var score = ScoreAsset(kind, name);
            if (score <= bestScore ||
                !Uri.TryCreate(url, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            long? size = null;
            if (item.TryGetProperty("size", out var sizeElement) &&
                sizeElement.TryGetInt64(out var parsedSize))
            {
                size = parsedSize;
            }

            bestScore = score;
            bestAsset = new SoftwareReleaseAsset(name, downloadUri, size, releaseTag);
        }

        if (bestAsset is null || bestScore < 0)
        {
            var product = kind switch
            {
                SoftwareDownloadKind.AllPrograms => "Mullet Hop All Programs installer",
                SoftwareDownloadKind.WaiverKiosk => "Waiver Kiosk installer",
                _ => "Mullet Hop POS package"
            };
            throw new InvalidOperationException(
                $"The latest release does not contain a {product}.");
        }

        return bestAsset;
    }

    public static async Task DownloadAsync(
        SoftwareReleaseAsset asset,
        string destinationPath,
        IProgress<SoftwareDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var partialPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await Client.GetAsync(
                asset.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            var buffer = new byte[81920];
            long downloadedBytes = 0;
            while (true)
            {
                var bytesRead = await input.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloadedBytes += bytesRead;
                progress.Report(new SoftwareDownloadProgress(downloadedBytes, totalBytes));
            }

            await output.FlushAsync(cancellationToken);
            output.Close();
            File.Move(partialPath, destinationPath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(partialPath))
                    File.Delete(partialPath);
            }
            catch
            {
                // A stale partial download is harmless and can be removed later.
            }
        }
    }

    private static int ScoreAsset(SoftwareDownloadKind kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return -1;

        if (kind == SoftwareDownloadKind.AllPrograms)
        {
            return string.Equals(
                name,
                "MulletHop-All-Programs-Installer.exe",
                StringComparison.OrdinalIgnoreCase)
                ? 500
                : -1;
        }

        if (kind == SoftwareDownloadKind.MulletHopPos)
        {
            if (name.StartsWith("Mullet-Hop-POS-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return 300;
            }

            if (name.Contains("POSController", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                return 200;
            }

            return -1;
        }

        if (!name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("KioskController", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("POSController", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (name.Contains("WaiverKiosk", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Waiver-Kiosk", StringComparison.OrdinalIgnoreCase))
        {
            return 300;
        }

        return 100;
    }

    private static Uri BuildLatestReleaseApiUri(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) ||
            !string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The controller release repository is not configured correctly.");
        }

        var segments = repositoryUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new InvalidOperationException("The controller release repository is not configured correctly.");

        var owner = Uri.EscapeDataString(segments[0]);
        var repository = Uri.EscapeDataString(segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1]);
        return new Uri($"https://api.github.com/repos/{owner}/{repository}/releases/latest");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "MulletHopKioskController",
            ControllerUpdater.CurrentVersion == "Unknown" ? "1.0" : ControllerUpdater.CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}

internal sealed class SoftwareDownloadsDialog : Form
{
    private readonly Button _allProgramsButton = new();
    private readonly Button _kioskButton = new();
    private readonly Button _posButton = new();
    private readonly Button _releasePageButton = new();
    private readonly Button _closeButton = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private CancellationTokenSource? _downloadCancellation;

    public SoftwareDownloadsDialog()
    {
        Text = "Software Downloads";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(730, 520);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(18),
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Download Mullet Hop software",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var introduction = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Choose an application below. The controller finds the newest published " +
                   "version and lets you choose where to save it.",
            ForeColor = Color.FromArgb(52, 65, 76),
            TextAlign = ContentAlignment.TopLeft
        };

        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(introduction, 0, 1);
        layout.Controls.Add(BuildDownloadRow(
            "All Mullet Hop Programs",
            "Downloads one installer that lets you select the Kiosk, Systems Controller, POS, or any combination.",
            _allProgramsButton,
            "Download All-Programs Installer",
            Color.FromArgb(105, 210, 236)), 0, 2);
        layout.Controls.Add(BuildDownloadRow(
            "Waiver Kiosk",
            "Downloads the current Windows Setup installer for a waiver kiosk computer.",
            _kioskButton,
            "Download Kiosk Installer",
            Color.FromArgb(118, 196, 66)), 0, 3);
        layout.Controls.Add(BuildDownloadRow(
            "Mullet Hop POS",
            "Downloads the complete POS package, including its installer and instructions.",
            _posButton,
            "Download POS Package",
            Color.FromArgb(245, 130, 32)), 0, 4);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Text = "Ready to download.";
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = Color.FromArgb(52, 65, 76);
        _statusLabel.AutoEllipsis = true;
        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Value = 0;
        layout.Controls.Add(_statusLabel, 0, 5);
        layout.Controls.Add(_progressBar, 0, 6);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            Margin = Padding.Empty
        };
        ConfigureFooterButton(_closeButton, "Close", Color.FromArgb(80, 92, 103), Color.White);
        ConfigureFooterButton(_releasePageButton, "Open Latest Release Page", Color.FromArgb(8, 119, 189), Color.White);
        footer.Controls.Add(_closeButton);
        footer.Controls.Add(_releasePageButton);
        layout.Controls.Add(footer, 0, 7);

        Controls.Add(layout);
        CancelButton = _closeButton;
        _allProgramsButton.Click += async (_, _) => await DownloadAsync(SoftwareDownloadKind.AllPrograms);
        _kioskButton.Click += async (_, _) => await DownloadAsync(SoftwareDownloadKind.WaiverKiosk);
        _posButton.Click += async (_, _) => await DownloadAsync(SoftwareDownloadKind.MulletHopPos);
        _releasePageButton.Click += (_, _) => OpenLatestReleasePage();
        _closeButton.Click += (_, _) =>
        {
            if (_downloadCancellation is not null)
            {
                _statusLabel.Text = "Canceling download…";
                _downloadCancellation.Cancel();
            }
            else
            {
                Close();
            }
        };
        FormClosing += (_, e) =>
        {
            if (_downloadCancellation is null)
                return;

            e.Cancel = true;
            _statusLabel.Text = "Canceling download…";
            _downloadCancellation.Cancel();
        };

        ControllerTheme.Apply(this);
    }

    private static Control BuildDownloadRow(
        string title,
        string description,
        Button button,
        string buttonText,
        Color buttonColor)
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = title,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Padding = new Padding(12, 20, 12, 10),
            Margin = new Padding(0, 3, 0, 3)
        };
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        var detail = new Label
        {
            Dock = DockStyle.Fill,
            Text = description,
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 0, 10, 0)
        };
        button.Dock = DockStyle.Fill;
        button.Text = buttonText;
        button.BackColor = buttonColor;
        button.ForeColor = Color.FromArgb(16, 24, 32);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        button.Margin = new Padding(4, 2, 0, 2);
        row.Controls.Add(detail, 0, 0);
        row.Controls.Add(button, 1, 0);
        group.Controls.Add(row);
        return group;
    }

    private static void ConfigureFooterButton(
        Button button,
        string text,
        Color backColor,
        Color foreColor)
    {
        button.Text = text;
        button.AutoSize = false;
        button.Size = new Size(text.StartsWith("Open", StringComparison.Ordinal) ? 210 : 110, 38);
        button.BackColor = backColor;
        button.ForeColor = foreColor;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        button.Margin = new Padding(8, 0, 0, 0);
    }

    private async Task DownloadAsync(SoftwareDownloadKind kind)
    {
        if (_downloadCancellation is not null)
            return;

        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        SetBusy(true);
        _progressBar.Style = ProgressBarStyle.Marquee;
        _statusLabel.Text = "Finding the newest published release…";

        try
        {
            var asset = await SoftwareDownloadService.FindLatestAssetAsync(kind, cancellation.Token);
            using var saveDialog = new SaveFileDialog
            {
                Title = kind switch
                {
                    SoftwareDownloadKind.AllPrograms => "Save Mullet Hop All Programs installer",
                    SoftwareDownloadKind.WaiverKiosk => "Save Waiver Kiosk installer",
                    _ => "Save Mullet Hop POS package"
                },
                FileName = asset.Name,
                Filter = asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? "ZIP package (*.zip)|*.zip|All files (*.*)|*.*"
                    : "Windows installer (*.exe)|*.exe|All files (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = true
            };
            var downloadsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (Directory.Exists(downloadsFolder))
                saveDialog.InitialDirectory = downloadsFolder;

            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
            _statusLabel.Text = $"Ready to save {asset.ReleaseTag}.";
            if (saveDialog.ShowDialog(this) != DialogResult.OK)
            {
                _statusLabel.Text = "Download canceled.";
                return;
            }

            _progressBar.Style = ProgressBarStyle.Marquee;
            _statusLabel.Text = $"Downloading {asset.Name}…";
            var progress = new Progress<SoftwareDownloadProgress>(UpdateProgress);
            await SoftwareDownloadService.DownloadAsync(
                asset,
                saveDialog.FileName,
                progress,
                cancellation.Token);
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 100;
            _statusLabel.Text = $"Saved {asset.Name}.";

            var openFolder = MessageBox.Show(
                this,
                $"{asset.Name} downloaded successfully.\n\nOpen its folder now?",
                "Download Complete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (openFolder == DialogResult.Yes)
                OpenDownloadedFileFolder(saveDialog.FileName);
        }
        catch (OperationCanceledException)
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
            _statusLabel.Text = "Download canceled.";
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Software download error: " + ex.GetType().Name + " - " + ex.Message);
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
            _statusLabel.Text = "Download failed. Check the internet connection and try again.";
            MessageBox.Show(
                this,
                "The software download could not be completed. Verify the internet connection " +
                "and try again, or use Open Latest Release Page.",
                "Download Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _downloadCancellation = null;
            SetBusy(false);
        }
    }

    private void UpdateProgress(SoftwareDownloadProgress progress)
    {
        if (progress.TotalBytes is not > 0)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            _statusLabel.Text = $"Downloaded {FormatBytes(progress.DownloadedBytes)}…";
            return;
        }

        var percent = (int)Math.Clamp(
            progress.DownloadedBytes * 100L / progress.TotalBytes.Value,
            0,
            100);
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = percent;
        _statusLabel.Text =
            $"Downloading… {percent}% ({FormatBytes(progress.DownloadedBytes)} of " +
            $"{FormatBytes(progress.TotalBytes.Value)})";
    }

    private void SetBusy(bool busy)
    {
        _allProgramsButton.Enabled = !busy;
        _kioskButton.Enabled = !busy;
        _posButton.Enabled = !busy;
        _releasePageButton.Enabled = !busy;
        _closeButton.Text = busy ? "Cancel Download" : "Close";
        _closeButton.Width = busy ? 150 : 110;
    }

    private void OpenLatestReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                ControllerUpdater.ReleaseRepositoryUrl.TrimEnd('/') + "/releases/latest")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Open release page error: " + ex.Message);
            MessageBox.Show(
                this,
                "Windows could not open the latest release page.",
                "Unable to Open Page",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static void OpenDownloadedFileFolder(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Open download folder error: " + ex.Message);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
