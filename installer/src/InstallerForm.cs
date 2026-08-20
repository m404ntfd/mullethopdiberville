using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MulletHopInstaller;

internal sealed class InstallerForm : Form
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/m404ntfd/mullethopdiberville/releases/latest";
    private readonly CheckBox _kiosk = new() { Text = "Mullet Hop Waiver Kiosk", Checked = true };
    private readonly CheckBox _controller = new() { Text = "Mullet Hop Systems Controller", Checked = true };
    private readonly CheckBox _pos = new() { Text = "Mullet Hop POS", Checked = true };
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _install = new();

    public InstallerForm()
    {
        Text = "Mullet Hop All Programs Installer";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 510);
        MinimumSize = new Size(620, 480);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);
        var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (icon is not null) Icon = icon;

        var title = new Label
        {
            Text = "MULLET HOP SOFTWARE INSTALLER",
            Dock = DockStyle.Top,
            Height = 66,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154)
        };
        var intro = new Label
        {
            Text = "Choose any combination. This installer downloads the matching packages from " +
                   "the latest mullethopdiberville release, then installs them one at a time.",
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(24, 4, 24, 6),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(52, 65, 76)
        };
        var choices = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 190,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(28, 10, 28, 10)
        };
        ConfigureChoice(_kiosk,
            "Full-screen waiver station with schedules, advertisements, screensaver, and closed video.");
        ConfigureChoice(_controller,
            "Local Systems Controller for kiosks, POS workstations, settings, updates, and failover.");
        ConfigureChoice(_pos,
            "Front-desk LilyPad POS shell with Firefox and four-kiosk controls.");
        choices.Controls.Add(_kiosk, 0, 0);
        choices.Controls.Add(_controller, 0, 1);
        choices.Controls.Add(_pos, 0, 2);

        _status.Dock = DockStyle.Top;
        _status.Height = 44;
        _status.Padding = new Padding(28, 4, 28, 4);
        _status.Text = "Ready to install the selected applications.";
        _status.ForeColor = Color.FromArgb(52, 65, 76);
        _progress.Dock = DockStyle.Top;
        _progress.Height = 20;
        _progress.Margin = new Padding(28, 0, 28, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 72,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(20, 14, 20, 12),
            WrapContents = false
        };
        _install.Text = "Install Selected";
        _install.Width = 160;
        _install.Height = 42;
        _install.BackColor = Color.FromArgb(118, 196, 66);
        _install.FlatStyle = FlatStyle.Flat;
        _install.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        var close = new Button
        {
            Text = "Close",
            Width = 110,
            Height = 42,
            BackColor = Color.FromArgb(235, 238, 241),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        var selectAll = new Button
        {
            Text = "Select All",
            Width = 110,
            Height = 42,
            BackColor = Color.FromArgb(105, 210, 236),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        actions.Controls.Add(_install);
        actions.Controls.Add(close);
        actions.Controls.Add(selectAll);

        Controls.Add(actions);
        Controls.Add(_progress);
        Controls.Add(_status);
        Controls.Add(choices);
        Controls.Add(intro);
        Controls.Add(title);
        _install.Click += async (_, _) => await InstallSelectedAsync();
        close.Click += (_, _) => Close();
        selectAll.Click += (_, _) => _kiosk.Checked = _controller.Checked = _pos.Checked = true;
    }

    public static bool SmokeTest()
    {
        const string sample = """
            {"assets":[
              {"name":"MulletHop-All-Programs-Installer.exe","browser_download_url":"https://example.test/all.exe"},
              {"name":"Mullet-Hop-Systems-Controller-1.15.0.zip","browser_download_url":"https://example.test/controller.zip"},
              {"name":"Mullet-Hop-POS-1.7.2.zip","browser_download_url":"https://example.test/pos.zip"},
              {"name":"MulletHop.WaiverKiosk-Setup.exe","browser_download_url":"https://example.test/kiosk.exe"}
            ]}
            """;
        var release = JsonSerializer.Deserialize<LatestRelease>(sample,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return release?.Assets.Count == 4 &&
               release.Assets.All(asset =>
                   !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl));
    }

    private static void ConfigureChoice(CheckBox choice, string description)
    {
        choice.Dock = DockStyle.Fill;
        choice.AutoSize = false;
        choice.Padding = new Padding(14, 2, 12, 2);
        choice.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        choice.ForeColor = Color.FromArgb(8, 119, 189);
        choice.Text += Environment.NewLine + "     " + description;
    }

    private async Task InstallSelectedAsync()
    {
        var selected = new List<InstallableApplication>();
        if (_controller.Checked)
            selected.Add(new("Systems Controller", "Mullet-Hop-Systems-Controller-", true));
        if (_pos.Checked)
            selected.Add(new("Mullet Hop POS", "Mullet-Hop-POS-", true));
        // Install the full-screen kiosk last so it cannot cover setup prompts for
        // the controller or POS packages selected in the same run.
        if (_kiosk.Checked)
            selected.Add(new("Waiver Kiosk", "MulletHop.WaiverKiosk", false));
        if (selected.Count == 0)
        {
            _status.Text = "Select at least one application.";
            return;
        }

        SetBusy(true);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(), "MulletHopInstaller", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            _progress.Minimum = 0;
            _progress.Maximum = selected.Count;
            _progress.Value = 0;
            var release = await LoadLatestReleaseAsync();
            foreach (var application in selected)
            {
                _status.Text = $"Preparing {application.DisplayName}…";
                var package = await ResolvePackageAsync(application, release, temporaryRoot);
                _status.Text = $"Installing {application.DisplayName}…";
                var exitCode = application.IsZip
                    ? await RunPackagedInstallerAsync(package, application, temporaryRoot)
                    : await RunProcessAsync(package, string.Empty);
                if (exitCode != 0)
                    throw new InvalidOperationException(
                        $"{application.DisplayName} installer ended with exit code {exitCode}.");
                _progress.Value++;
            }
            _status.Text = "All selected Mullet Hop applications were installed successfully.";
            MessageBox.Show(this, _status.Text, "Installation Complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Installation stopped: " + ex.Message;
            MessageBox.Show(this, _status.Text, "Mullet Hop Installer",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, true); }
            catch (Exception) { }
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _install.Enabled = !busy;
        _kiosk.Enabled = !busy;
        _controller.Enabled = !busy;
        _pos.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private static async Task<LatestRelease> LoadLatestReleaseAsync()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(LatestReleaseApi);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var release = await JsonSerializer.DeserializeAsync<LatestRelease>(stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return release ?? throw new InvalidDataException("The latest release response was empty.");
    }

    private static async Task<string> ResolvePackageAsync(
        InstallableApplication application,
        LatestRelease release,
        string temporaryRoot)
    {
        var local = Directory.EnumerateFiles(AppContext.BaseDirectory)
            .FirstOrDefault(path => Matches(Path.GetFileName(path), application));
        if (local is not null)
            return local;
        var asset = release.Assets.FirstOrDefault(item => Matches(item.Name, application))
                    ?? throw new FileNotFoundException(
                        $"The latest release does not contain the {application.DisplayName} installer.");
        var destination = Path.Combine(temporaryRoot, Path.GetFileName(asset.Name));
        using var client = CreateClient();
        using var response = await client.GetAsync(
            asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(destination);
        await input.CopyToAsync(output);
        return destination;
    }

    private static bool Matches(string name, InstallableApplication application) =>
        application.IsZip
            ? name.StartsWith(application.AssetName, StringComparison.OrdinalIgnoreCase) &&
              name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            : name.StartsWith(application.AssetName, StringComparison.OrdinalIgnoreCase) &&
              name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase);

    private static async Task<int> RunPackagedInstallerAsync(
        string package,
        InstallableApplication application,
        string temporaryRoot)
    {
        var folder = Path.Combine(temporaryRoot,
            application.DisplayName.Replace(" ", string.Empty, StringComparison.Ordinal));
        ZipFile.ExtractToDirectory(package, folder, true);
        var scriptName = application.DisplayName == "Systems Controller"
            ? "Install-Kiosk-Controller.ps1"
            : "Install-Mullet-Hop-POS.ps1";
        var script = Directory.EnumerateFiles(folder, scriptName, SearchOption.AllDirectories)
            .FirstOrDefault() ?? throw new FileNotFoundException(
                $"{scriptName} is missing from the downloaded package.");
        return await RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"");
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments)
    {
        var workingDirectory = Path.GetDirectoryName(fileName);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? AppContext.BaseDirectory
                : workingDirectory
        }) ?? throw new InvalidOperationException("Windows could not start the installer.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MulletHopInstaller", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed record InstallableApplication(
        string DisplayName,
        string AssetName,
        bool IsZip);

    private sealed class LatestRelease
    {
        public List<ReleaseAsset> Assets { get; set; } = [];
    }

    private sealed class ReleaseAsset
    {
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
