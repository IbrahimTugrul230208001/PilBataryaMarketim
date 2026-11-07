namespace ECommerceBatteryShop.Services.StockSync;

public sealed record FeedStockItem(string Sku, int Quantity);

/// <summary>Reads one feed format into SKU/quantity pairs. Add one implementation per new format.</summary>
public interface IStockFeedParser
{
    /// <summary>Matches <c>Supplier.FeedFormat</c>.</summary>
    string Format { get; }

    Task<IReadOnlyList<FeedStockItem>> ParseAsync(Stream feed, CancellationToken ct = default);
}
