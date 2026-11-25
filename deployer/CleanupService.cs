namespace Deployer;

public sealed class CleanupService(SiteStore store, DeployerOptions options, ILogger<CleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var removed = await store.CleanupAsync(TimeSpan.FromDays(options.TtlDays));
                if (removed > 0) log.LogInformation("Cleanup removed {Count} expired sites", removed);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Cleanup failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
