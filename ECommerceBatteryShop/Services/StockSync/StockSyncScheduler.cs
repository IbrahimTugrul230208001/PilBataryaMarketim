 using ECommerceBatteryShop.Options;
using Microsoft.Extensions.Options;

namespace ECommerceBatteryShop.Services.StockSync;

/// <summary>Runs the supplier stock sync at the configured Turkey-local times (default every 4 hours).</summary>
public sealed class StockSyncScheduler : BackgroundService
{
    // 6 runs a day
    private static readonly TimeSpan[] DefaultTimes = Enumerable.Range(0, 6).Select(i => TimeSpan.FromHours(i * 4)).ToArray();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<StockSyncOptions> _opt;
    private readonly ILogger<StockSyncScheduler> _log;
    private readonly TimeZoneInfo _tz;

    public StockSyncScheduler(IServiceScopeFactory scopeFactory, IOptionsMonitor<StockSyncOptions> opt,
        ILogger<StockSyncScheduler> log)
    {
        _scopeFactory = scopeFactory; _opt = opt; _log = log;
        _tz = TryTz("Europe/Istanbul") ?? TryTz("Turkey Standard Time") ?? TimeZoneInfo.Local;
        static TimeZoneInfo? TryTz(string id) { try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return null; } }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = NextDelay();
            _log.LogInformation("Stock sync scheduled in {Delay}", delay);
            try { await Task.Delay(delay, ct); } catch (TaskCanceledException) { break; }

            if (!_opt.CurrentValue.Enabled)
            {
                _log.LogInformation("Stock sync is disabled (StockSync__Enabled=false); skipping this run");
                continue;
            }

            await TryRun(ct);
        }
    }

    private async Task TryRun(CancellationToken ct)
    {
        try
        {
            // The sync uses the scoped DbContext, so each run gets its own scope
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IStockSyncService>();
            var result = await sync.SyncAllAsync(ct);
            _log.LogInformation("Stock sync finished: {Ok}/{Total} suppliers succeeded, {Products} products recalculated",
                result.Suppliers.Count(s => s.Succeeded), result.Suppliers.Count, result.ProductsRecalculated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stock sync tick failed");
        }
    }

    private TimeSpan NextDelay()
    {
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz).DateTime;
        var times = (_opt.CurrentValue.RunTimesLocal ?? Array.Empty<string>())
            .Select(t => TimeSpan.TryParse(t, out var ts) ? ts : (TimeSpan?)null)
            .Where(ts => ts.HasValue).Select(ts => ts!.Value)
            .OrderBy(ts => ts)
            .ToArray();

        if (times.Length == 0) times = DefaultTimes;

        foreach (var t in times)
        {
            var next = nowLocal.Date + t;
            if (next > nowLocal) return next - nowLocal;
        }
        return (nowLocal.Date.AddDays(1) + times[0]) - nowLocal;
    }
}
