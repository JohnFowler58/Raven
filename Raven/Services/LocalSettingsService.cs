using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Raven.Contracts.Services;
using Raven.Models;
using Raven.Services.FilePermissions;

namespace Raven.Services;

public class LocalSettingsService : ILocalSettingsService
{
    private const string _defaultApplicationDataFolder = "Raven/ApplicationData";
    private const string _defaultLocalSettingsFile = "LocalSettings.json";

    private readonly IPersistentFileStore _fileStore;

    public LocalSettingsService(IOptions<LocalSettingsOptions> options)
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var applicationDataFolder = Path.Combine(
            localApplicationData, 
            options.Value.ApplicationDataFolder ?? _defaultApplicationDataFolder);
        var settingsFile = options.Value.LocalSettingsFile ?? _defaultLocalSettingsFile;
        var settingsPath = Path.Combine(applicationDataFolder, settingsFile);

        _fileStore = new PersistentFileStore(settingsPath);
    }

    public Task<T?> ReadSettingAsync<T>(string key)
    {
        return _fileStore.ReadAsync<T>(key);
    }

    public Task SaveSettingAsync<T>(string key, T value)
    {
        return _fileStore.WriteAsync(key, value);
    }
}
