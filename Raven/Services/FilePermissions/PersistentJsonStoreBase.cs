using System.Text;
using System.Text.Json;

namespace Raven.Services.FilePermissions;

public abstract class PersistentJsonStoreBase<TCache>
{
    private const string BackupSuffix = ".backup";
    private const string CorruptedSuffix = ".corrupted";

    private readonly string _filePath;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private TCache? _cache;
    private bool _isInitialized;

    protected PersistentJsonStoreBase(string filePath)
    {
        _filePath = filePath;
    }

    protected async Task<TCache> LoadCacheAsync()
    {
        await _syncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return CloneCache(_cache!);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    protected async Task SaveCacheAsync(TCache cache)
    {
        await _syncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            _cache = CloneCache(cache);
            await PersistAsync().ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    protected async Task UpdateCacheAsync(Action<TCache> update)
    {
        await _syncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            update(_cache!);
            await PersistAsync().ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    protected abstract TCache CreateEmptyCache();

    protected abstract TCache CloneCache(TCache cache);

    protected abstract TCache? DeserializeCache(string json);

    protected abstract string SerializeCache(TCache cache);

    protected virtual Encoding TextEncoding => Encoding.UTF8;

    private async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _cache = await LoadCoreAsync().ConfigureAwait(false);
        _isInitialized = true;
    }

    private async Task<TCache> LoadCoreAsync()
    {
        if (!File.Exists(_filePath))
        {
            return CreateEmptyCache();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, TextEncoding).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return CreateEmptyCache();
            }

            return DeserializeCache(json) ?? CreateEmptyCache();
        }
        catch
        {
            return await RecoverFromCorruptionAsync().ConfigureAwait(false);
        }
    }

    private async Task<TCache> RecoverFromCorruptionAsync()
    {
        var backupPath = $"{_filePath}{BackupSuffix}";

        if (File.Exists(backupPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(backupPath, TextEncoding).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var recovered = DeserializeCache(json);
                    if (recovered is not null)
                    {
                        OverwriteCorruptedFile();
                        return recovered;
                    }
                }
            }
            catch
            {
                // Backup is also corrupted.
            }
        }

        OverwriteCorruptedFile();
        return CreateEmptyCache();
    }

    private void OverwriteCorruptedFile()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var corruptedPath = $"{_filePath}{CorruptedSuffix}";
            File.Move(_filePath, corruptedPath, true);
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    private async Task PersistAsync()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var serialized = SerializeCache(_cache!);
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp"
        );

        try
        {
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, $"{_filePath}{BackupSuffix}", true);
            }

            await File.WriteAllTextAsync(tempPath, serialized, new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(tempPath, _filePath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }
}
