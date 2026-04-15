using System.Text.Json;

namespace Raven.Services.FilePermissions;

public sealed class PersistentListStore<T> : PersistentJsonStoreBase<List<T>>, IPersistentListStore<T>
{
    public PersistentListStore(string filePath)
        : base(filePath)
    {
    }

    public Task<List<T>> LoadAsync() => LoadCacheAsync();

    public Task SaveAsync(List<T> items) => SaveCacheAsync(items);

    protected override List<T> CreateEmptyCache() => [];

    protected override List<T> CloneCache(List<T> cache) => new(cache);

    protected override List<T>? DeserializeCache(string json) =>
        JsonSerializer.Deserialize<List<T>>(json);

    protected override string SerializeCache(List<T> cache)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(cache, options);
    }
}
