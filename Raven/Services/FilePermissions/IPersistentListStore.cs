namespace Raven.Services.FilePermissions;

/// <summary>
/// Robust file persistence for list-based JSON data storage.
/// </summary>
public interface IPersistentListStore<T>
{
    /// <summary>
    /// Loads entire list from persistent storage.
    /// </summary>
    Task<List<T>> LoadAsync();

    /// <summary>
    /// Saves entire list to persistent storage.
    /// </summary>
    Task SaveAsync(List<T> items);
}
