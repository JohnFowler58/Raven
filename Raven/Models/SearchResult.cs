using StoreListings.Library;

namespace Raven.Models;

// Models/SearchResult.cs
public class SearchResult
{
    public bool IsSuccess { get; set; }
    public List<Card> Cards { get; set; } = new();
    public Exception? Exception { get; set; }

    // For C# 9.0+ pattern matching support
    public void Deconstruct(out bool isSuccess, out List<Card> cards, out Exception? exception)
    {
        isSuccess = IsSuccess;
        cards = Cards;
        exception = Exception;
    }
}

// Optional base class for operation results
public class OperationResult<T>
{
    public bool IsSuccess { get; set; }
    public T? Value { get; set; }
    public Exception? Exception { get; set; }
}

// If you need a generic version for other operations
public class ProductResult : OperationResult<Card>
{
    // Add product-specific properties if needed
}
