namespace ECommerceBatteryShop.DataAccess.Entities;

/// <summary>An external stock source (another shop's product feed).</summary>
public class Supplier
{
    public int Id { get; set; }

    /// <summary>Stable key, also used to look up the feed URL in config (StockSync:Feeds:{Code}:Url).</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Which parser reads this feed, e.g. "akakce-xml".</summary>
    public string FeedFormat { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    /// <summary>A feed with fewer items than this is treated as broken and the run is aborted.</summary>
    public int MinExpectedItems { get; set; }

    /// <summary>Quantities not confirmed by the feed within this window no longer count toward stock.</summary>
    public int StaleAfterHours { get; set; } = 48;

    public ICollection<ProductSupplierLink> ProductLinks { get; set; } = new List<ProductSupplierLink>();
}
