using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MulletHopKioskController;

internal sealed class RemoteAccessSettings
{
    public bool Enabled { get; set; }
    public bool IsRemoteMachine { get; set; }
    public string RelayUrl { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;

    public RemoteAccessSettings Clone() => new()
    {
        Enabled = Enabled,
        IsRemoteMachine = IsRemoteMachine,
        RelayUrl = RelayUrl,
        LocationId = LocationId,
        AccessKey = AccessKey
    };
}

internal static class RemoteAccessSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string SettingsPath = Path.Combine(
        ControllerLog.DataDirectory, "remote-access.json");

    public static RemoteAccessSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new RemoteAccessSettings();
            return JsonSerializer.Deserialize<RemoteAccessSettings>(
                       File.ReadAllText(SettingsPath), JsonOptions)
                   ?? new RemoteAccessSettings();
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Remote settings read error: " + ex.Message);
            return new RemoteAccessSettings();
        }
    }

    public static void Save(RemoteAccessSettings settings)
    {
        Directory.CreateDirectory(ControllerLog.DataDirectory);
        var path = SettingsPath + ".new";
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(path, SettingsPath, true);
    }

    public static string CreateSetupCode(RemoteAccessSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static RemoteAccessSettings ParseSetupCode(string value)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
        return JsonSerializer.Deserialize<RemoteAccessSettings>(json, JsonOptions)
               ?? throw new InvalidOperationException("The setup code is empty.");
    }
}

internal sealed class CloudCommand
{
    public string StationId { get; set; } = string.Empty;
    public KioskCommand Command { get; set; } = new();
}

internal sealed class CloudSyncRequest
{
    public string Role { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
    public List<ManagedKiosk> Kiosks { get; set; } = [];
    public List<CloudCommand> Commands { get; set; } = [];
    public AdvertisementSyncPackage? Advertisements { get; set; }
    public DateTime? AdvertisementUpdatedUtc { get; set; }
}

internal sealed class CloudSyncResponse
{
    public List<ManagedKiosk> Kiosks { get; set; } = [];
    public List<CloudCommand> Commands { get; set; } = [];
    public AdvertisementSyncPackage? Advertisements { get; set; }
    public DateTime? AdvertisementUpdatedUtc { get; set; }
}

internal sealed class CloudSyncService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ControllerState _state;
    private readonly RemoteAccessSettings _settings;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public event Action<string, bool>? StatusChanged;

    public CloudSyncService(ControllerState state, RemoteAccessSettings settings)
    {
        _state = state;
        _settings = settings.Clone();
    }

    public void Start()
    {
        if (!_settings.Enabled || _worker is not null) return;
        Validate(_settings);
        _worker = Task.Run(SynchronizeLoopAsync);
    }

    public static async Task TestConnectionAsync(RemoteAccessSettings settings)
    {
        Validate(settings);
        using var client = CreateClient(settings);
        using var response = await client.GetAsync(BuildUrl(settings, "api/health"));
        response.EnsureSuccessStatusCode();
    }

    private async Task SynchronizeLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await SynchronizeOnceAsync(_stopping.Token);
                StatusChanged?.Invoke(
                    _settings.IsRemoteMachine ? "● REMOTE CLOUD CONNECTED" : "● LOCAL + CLOUD CONNECTED",
                    true);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ControllerLog.Write("Cloud synchronization error: " + ex.Message);
                StatusChanged?.Invoke("● CLOUD OFFLINE — RETRYING", false);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), _stopping.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SynchronizeOnceAsync(CancellationToken cancellationToken)
    {
        var request = new CloudSyncRequest
        {
            Role = _settings.IsRemoteMachine ? "remote" : "local",
            LocationId = _settings.LocationId,
            Kiosks = _settings.IsRemoteMachine ? [] : _state.Snapshot().Select(k => k.Clone()).ToList(),
            Commands = _settings.IsRemoteMachine ? _state.PendingCloudCommands().ToList() : [],
            AdvertisementUpdatedUtc = _state.AdvertisementUpdatedUtc
        };

        if (!string.IsNullOrWhiteSpace(_state.AdvertisementRevision))
            request.Advertisements = _state.CreateAdvertisementSyncPackage();

        using var client = CreateClient(_settings);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var response = await client.PostAsync(
            BuildUrl(_settings, "api/sync"),
            new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<CloudSyncResponse>(body, JsonOptions)
                     ?? throw new InvalidOperationException("The relay returned an empty response.");

        if (_settings.IsRemoteMachine)
        {
            _state.AcknowledgeCloudCommands(request.Commands);
            _state.ApplyCloudKioskSnapshot(result.Kiosks);
        }
        else
        {
            foreach (var command in result.Commands)
                _state.QueueCloudCommand(command);
        }

        if (result.Advertisements is not null &&
            result.AdvertisementUpdatedUtc.HasValue &&
            (!_state.AdvertisementUpdatedUtc.HasValue ||
             result.AdvertisementUpdatedUtc.Value > _state.AdvertisementUpdatedUtc.Value.AddSeconds(1)))
        {
            _state.ApplyCloudAdvertisements(result.Advertisements, result.AdvertisementUpdatedUtc.Value);
        }
    }

    private static HttpClient CreateClient(RemoteAccessSettings settings)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.AccessKey.Trim());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MulletHopKioskController/1.4");
        return client;
    }

    private static string BuildUrl(RemoteAccessSettings settings, string path) =>
        settings.RelayUrl.Trim().TrimEnd('/') + "/" + path.TrimStart('/');

    internal static void Validate(RemoteAccessSettings settings)
    {
        if (!Uri.TryCreate(settings.RelayUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Enter a valid HTTPS Cloudflare relay URL.");
        if (string.IsNullOrWhiteSpace(settings.LocationId) || settings.LocationId.Length > 80)
            throw new InvalidOperationException("Enter the location ID created during Cloudflare setup.");
        if (string.IsNullOrWhiteSpace(settings.AccessKey) || settings.AccessKey.Length < 24)
            throw new InvalidOperationException("Enter the cloud access key created during Cloudflare setup.");
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _httpClient.Dispose();
        _stopping.Dispose();
    }
}

internal sealed class RemoteAccessSettingsDialog : Form
{
    private readonly CheckBox _enabled = new();
    private readonly CheckBox _remote = new();
    private readonly TextBox _url = new();
    private readonly TextBox _location = new();
    private readonly TextBox _key = new();
    public RemoteAccessSettings Settings { get; private set; }

    public RemoteAccessSettingsDialog(RemoteAccessSettings settings)
    {
        Settings = settings.Clone();
        Text = "Remote Access Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(650, 510);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10);

        var title = new Label { Text = "REMOTE ACCESS", Font = new Font("Segoe UI", 19, FontStyle.Bold), ForeColor = Color.FromArgb(117, 68, 154), Bounds = new Rectangle(24, 18, 450, 38) };
        var note = new Label { Text = "The on-site controller talks directly to kiosks. A remote controller uses the secure Cloudflare relay. No router ports are opened.", Bounds = new Rectangle(25, 58, 600, 48), ForeColor = Color.FromArgb(52, 65, 76) };
        _enabled.Text = "Enable secure cloud synchronization";
        _enabled.Bounds = new Rectangle(28, 112, 360, 30);
        _enabled.Checked = settings.Enabled;
        _remote.Text = "This is a remote machine";
        _remote.Bounds = new Rectangle(50, 148, 330, 30);
        _remote.Checked = settings.IsRemoteMachine;
        AddField("Cloudflare relay URL", _url, 195, settings.RelayUrl);
        AddField("Location ID", _location, 260, settings.LocationId);
        AddField("Cloud access key", _key, 325, settings.AccessKey);
        _key.UseSystemPasswordChar = true;
        var view = Button("View Key", 500, 349, 118, Color.FromArgb(255, 217, 188));
        view.Click += (_, _) => { _key.UseSystemPasswordChar = !_key.UseSystemPasswordChar; view.Text = _key.UseSystemPasswordChar ? "View Key" : "Hide Key"; };
        var test = Button("Test Connection", 25, 420, 150, Color.FromArgb(105, 210, 236));
        test.Click += async (_, _) => await TestAsync(test);
        var copy = Button("Copy Setup Code", 185, 420, 145, Color.FromArgb(118, 196, 66));
        copy.Click += (_, _) => { Clipboard.SetText(RemoteAccessSettingsStore.CreateSetupCode(ReadSettings())); MessageBox.Show(this, "Setup code copied. Paste it into the other controller's Remote Access settings.", Text); };
        var paste = Button("Paste Setup Code", 340, 420, 145, Color.FromArgb(255, 217, 188));
        paste.Click += (_, _) => PasteSetupCode();
        var save = Button("Save and Restart", 495, 420, 130, Color.FromArgb(117, 68, 154));
        save.ForeColor = Color.White;
        save.Click += (_, _) => SaveAndClose();
        Controls.AddRange([title, note, _enabled, _remote, view, test, copy, paste, save]);
    }

    private void AddField(string label, TextBox box, int y, string value)
    {
        Controls.Add(new Label { Text = label, Bounds = new Rectangle(25, y, 600, 24), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
        box.Text = value;
        box.Bounds = new Rectangle(25, y + 25, 460, 30);
        Controls.Add(box);
    }

    private static Button Button(string text, int x, int y, int width, Color color) => new() { Text = text, Bounds = new Rectangle(x, y, width, 42), BackColor = color, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };

    private RemoteAccessSettings ReadSettings() => new()
    {
        Enabled = _enabled.Checked,
        IsRemoteMachine = _remote.Checked,
        RelayUrl = _url.Text.Trim(),
        LocationId = _location.Text.Trim(),
        AccessKey = _key.Text.Trim()
    };

    private async Task TestAsync(Button button)
    {
        button.Enabled = false;
        try
        {
            await CloudSyncService.TestConnectionAsync(ReadSettings());
            MessageBox.Show(this, "The secure Cloudflare relay connection is working.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The relay connection failed.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { button.Enabled = true; }
    }

    private void PasteSetupCode()
    {
        try
        {
            var imported = RemoteAccessSettingsStore.ParseSetupCode(Clipboard.GetText());
            _enabled.Checked = imported.Enabled;
            _remote.Checked = imported.IsRemoteMachine;
            _url.Text = imported.RelayUrl;
            _location.Text = imported.LocationId;
            _key.Text = imported.AccessKey;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The clipboard does not contain a valid setup code.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveAndClose()
    {
        try
        {
            Settings = ReadSettings();
            if (Settings.Enabled)
                CloudSyncService.Validate(Settings);
            RemoteAccessSettingsStore.Save(Settings);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
