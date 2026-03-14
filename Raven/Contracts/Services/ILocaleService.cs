using StoreListings.Library;

namespace Raven.Contracts.Services;

public interface ILocaleService
{
    Market Market { get; }

    Lang Language { get; }

    event EventHandler? LocaleChanged;

    Task InitializeAsync();

    Task SetMarketAsync(Market market);

    Task SetLanguageAsync(Lang language);

    Task ResetToDefaultAsync();
}
