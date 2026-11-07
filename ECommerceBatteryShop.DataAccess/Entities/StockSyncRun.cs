namespace ECommerceBatteryShop.DataAccess.Entities;

/// <summary>One attempt to read one supplier's feed, kept for the admin panel and troubleshooting.</summary>
public class StockSyncRun
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public bool Succeeded { get; set; }
    public int FeedItemCount { get; set; }

    /// <summary>Linked SKUs found in the feed (their quantities were updated).</summary>
    public int MatchedCount { get; set; }

    /// <summary>Linked SKUs absent from the feed (their old quantities were kept).</summary>
    public int MissingCount { get; set; }
    public string? Error { get; set; }
}
