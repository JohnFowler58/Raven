using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Raven.Contracts.Services;
using Raven.Models;

namespace Raven.Services;

public class LocalSettingsService : ILocalSettingsService
{
    private const string _defaultApplicationDataFolder = "Raven/ApplicationData";
    private const string _defaultLocalSettingsFile = "LocalSettings.json";

    private readonly LocalSettingsOptions _options;

    private readonly string _localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private readonly string _applicationDataFolder;
    private readonly string _localsettingsFile;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private Dictionary<string, string> _settings;

    private bool _isInitialized;

    public LocalSettingsService(IOptions<LocalSettingsOptions> options)
    {
        _options = options.Value;

        _applicationDataFolder = Path.Combine(_localApplicationData, _options.ApplicationDataFolder ?? _defaultApplicationDataFolder);
        _localsettingsFile = _options.LocalSettingsFile ?? _defaultLocalSettingsFile;

        _settings = new Dictionary<string, string>();
    }

    private async Task InitializeCoreAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        var path = Path.Combine(_applicationDataFolder, _localsettingsFile);
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
            _settings = string.IsNullOrWhiteSpace(json)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }

        _isInitialized = true;
    }

    public async Task<T?> ReadSettingAsync<T>(string key)
    {
        await _syncLock.WaitAsync();
        try
        {
            await InitializeCoreAsync();

            if (_settings.TryGetValue(key, out var obj))
            {
                return JsonSerializer.Deserialize<T>(obj);
            }

            return default;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SaveSettingAsync<T>(string key, T value)
    {
        await _syncLock.WaitAsync();
        try
        {
            await InitializeCoreAsync();

            _settings[key] = JsonSerializer.Serialize(value);

            var path = Path.Combine(_applicationDataFolder, _localsettingsFile);
            await PersistSettingsCoreAsync(path);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task PersistSettingsCoreAsync(string path)
    {
        Directory.CreateDirectory(_applicationDataFolder);

        var serializedSettings = JsonSerializer.Serialize(_settings);
        var tempPath = $"{path}.tmp";

        await File.WriteAllTextAsync(tempPath, serializedSettings, new UTF8Encoding(false));
        File.Move(tempPath, path, true);
    }
}
