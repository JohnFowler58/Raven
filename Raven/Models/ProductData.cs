using StoreListings.Library;

namespace Raven.Models;

/// <summary>
/// Unified product data populated from either DCATPackage (packaged) or StoreEdgeFDPage (unpackaged).
/// Replaces StoreEdgeFDProduct in the UI and download layers.
/// </summary>
public class ProductData
{
    public required string ProductId { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string PublisherName { get; set; }
    public required Image Logo { get; set; }
    public required List<Image> Screenshots { get; set; }
    public required double Rating { get; set; }
    public required long RatingCount { get; set; }
    public required InstallerType InstallerType { get; set; }
    public string? RevisionId { get; set; }
    public string? PackageFamilyName { get; set; }
    public string? Version { get; set; }
    public long? Size { get; set; }
    public bool IsBundle { get; set; }

    public static ProductData FromDCAT(DCATPackage dcat)
    {
        return new ProductData
        {
            ProductId = dcat.ProductId,
            Title = dcat.Title,
            Description = dcat.Description,
            PublisherName = dcat.PublisherName,
            Logo = dcat.Logo,
            Screenshots = dcat.Screenshots,
            Rating = dcat.Rating,
            RatingCount = dcat.RatingCount,
            InstallerType = InstallerType.Packaged,
            RevisionId = dcat.RevisionId,
            PackageFamilyName = dcat.PackageFamilyName,
            Version = dcat.AppVersion?.ToString(),
            Size = dcat.Size,
            IsBundle = dcat.IsBundle,
        };
    }

    public static ProductData FromStoreEdgeFDPage(StoreEdgeFDPage page)
    {
        return new ProductData
        {
            ProductId = page.ProductId,
            Title = page.Title,
            Description = page.Description,
            PublisherName = page.PublisherName,
            Logo = page.Logo,
            Screenshots = page.Screenshots,
            Rating = page.Rating,
            RatingCount = page.RatingCount,
            InstallerType = page.InstallerType,
            RevisionId = page.LastUpdateDate?.ToString("o"),
            PackageFamilyName = page.PackageFamilyName,
            Version = page.Version,
            Size = page.Size,
            IsBundle = false,
        };
    }
}
