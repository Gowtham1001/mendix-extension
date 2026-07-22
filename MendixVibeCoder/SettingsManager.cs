using System.Text.Json;

namespace MendixVibeCoder;

public class VibeCoderSettings
{
    public string OpenRouterApiKey { get; set; } = "";
    public string ModelId { get; set; } = "openrouter/free";
    public string MxcliPath { get; set; } = "mxcli";
    public bool AutoSync { get; set; } = true;
    public bool AutoFetchContext { get; set; } = true;
    public int SyncDelayMs { get; set; } = 800;
    public bool AutoExecuteMdl { get; set; } = true;
    public int MaxHistoryTokens { get; set; } = 120000;
    public int MaxOutputTokens { get; set; } = 8192;
    public bool UseMcp { get; set; } = true;
    public int McpPort { get; set; } = 7782;
    public string McpDialAddress { get; set; } = "127.0.0.1";
}

public class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private VibeCoderSettings _settings;

    public SettingsManager()
    {
        var extensionDir = AppDomain.CurrentDomain.BaseDirectory;
        _settingsPath = Path.Combine(extensionDir, "settings.json");
        _settings = Load();
    }

    public VibeCoderSettings Get() => _settings;

    public VibeCoderSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<VibeCoderSettings>(json, JsonOptions) ?? new VibeCoderSettings();
            }
            else
            {
                _settings = new VibeCoderSettings();
                Save();
            }
        }
        catch
        {
            _settings = new VibeCoderSettings();
        }
        return _settings;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
        }
    }

    public void Update(Action<VibeCoderSettings> updater)
    {
        updater(_settings);
        Save();
    }
}
