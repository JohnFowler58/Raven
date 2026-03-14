using StoreListings.Library;

namespace Raven.Events;

public class FiltersChangedEventArgs : EventArgs
{
    public MediaTypeSearch MediaType { get; }
    public PriceType PriceType { get; }

    public FiltersChangedEventArgs(MediaTypeSearch mediaType, PriceType priceType)
    {
        MediaType = mediaType;
        PriceType = priceType;
    }
}
