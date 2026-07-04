namespace BookingsSearch.Services;

public sealed class IcsRefreshService(
    IcsBookingsService calendar,
    IConfiguration config,
    ILogger<IcsRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);

        var interval = TimeSpan.FromSeconds(
            config.GetValue("Bookings:RefreshIntervalSeconds", 30));

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
            await RefreshAsync(ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try   { await calendar.RefreshAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Background ICS refresh failed"); }
    }
}
