using System.Text.Json;

namespace Raven.Services.FilePermissions;

public sealed class PersistentFileStore : PersistentJsonStoreBase<Dictionary<string, string>>, IPersistentFileStore
{
    public PersistentFileStore(string filePath)
        : base(filePath)
    {
    }

    public async Task<T?> ReadAsync<T>(string key)
    {
        var cache = await LoadCacheAsync();
        return cache.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;
    }

    public async Task WriteAsync<T>(string key, T value)
    {
        var serialized = JsonSerializer.Serialize(value);
        await UpdateCacheAsync(cache => cache[key] = serialized);
    }

    public async Task<IDictionary<string, string>> LoadAllAsync()
    {
        var cache = await LoadCacheAsync();
        return new Dictionary<string, string>(cache);
    }

    public Task SaveAllAsync(IDictionary<string, string> data) =>
        SaveCacheAsync(new Dictionary<string, string>(data));

    protected override Dictionary<string, string> CreateEmptyCache() => [];

    protected override Dictionary<string, string> CloneCache(Dictionary<string, string> cache) => new(cache);

    protected override Dictionary<string, string>? DeserializeCache(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json);

    protected override string SerializeCache(Dictionary<string, string> cache) =>
        JsonSerializer.Serialize(cache);
}
