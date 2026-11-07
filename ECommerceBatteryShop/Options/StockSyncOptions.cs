namespace ECommerceBatteryShop.Options
{
    public sealed class StockSyncOptions
    {
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// TR times to run at. Empty means every 4 hours (see StockSyncScheduler). Kept empty here on purpose:
        /// the config binder appends configured array items to a non-empty default instead of replacing it.
        /// </summary>
        public string[] RunTimesLocal { get; set; } = Array.Empty<string>();

        /// <summary>Feed URLs by supplier code. They carry access keys, so they live in .env, never in the database.</summary>
        public Dictionary<string, StockFeedSource> Feeds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class StockFeedSource
    {
        public string Url { get; set; } = string.Empty;
    }
}
