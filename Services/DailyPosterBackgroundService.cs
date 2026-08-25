namespace DailyPosterGenerator.Services;

/// <summary>
/// Automatically generates the next day's poster on a schedule (default: daily at 06:00 UTC),
/// so tomorrow's poster is ready in advance. Skips when a poster already exists for the day.
/// </summary>
public class DailyPosterBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailyPosterBackgroundService> _logger;

    public DailyPosterBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<DailyPosterBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var enabled = bool.Parse(await settings.GetAsync("scheduler.enabled", "true") ?? "true");
        if (!enabled)
        {
            _logger.LogInformation("Scheduler is disabled; background generation skipped.");
            return;
        }

        var configuredTime = TimeOnly.TryParse(await settings.GetAsync("scheduler.time", "06:00"), out var t) ? t : new TimeOnly(6, 0);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var runAt = DateTime.Now.Date.Add(configuredTime.ToTimeSpan());
                if (runAt <= DateTime.Now)
                {
                    runAt = runAt.AddDays(1);
                }

                _logger.LogInformation("Scheduler next run at {Next} (local)", runAt.ToString("yyyy-MM-dd HH:mm:ss"));

                // Poll until the scheduled time (robust against clock drift / long sleeps).
                while (!stoppingToken.IsCancellationRequested && DateTime.Now < runAt)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

        // Generate posters for tomorrow (local school time) so they are ready before the day begins.
        var targetDate = DateTime.Now.Date.AddDays(1);
        await GenerateIfNeededAsync(targetDate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler iteration failed.");
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
    }

    private async Task GenerateIfNeededAsync(DateTime date, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var generation = scope.ServiceProvider.GetRequiredService<IPosterGenerationService>();
        var events = scope.ServiceProvider.GetRequiredService<IEventService>();
        var log = scope.ServiceProvider.GetService<IActivityLog>();

        _logger.LogInformation("Scheduler generating next day's posters for {Date}.", date.ToString("yyyy-MM-dd"));
        log?.Add("scheduler", $"Generating posters for {date:dd MMM yyyy} (tomorrow).");

        var todaysEvents = await events.GetTodaysEventsAsync(date, ct);
        if (todaysEvents.Count == 0)
        {
            _logger.LogInformation("No events found for {Date}; skipping.", date.ToString("yyyy-MM-dd"));
            log?.Add("scheduler", $"No events found for {date:dd MMM yyyy}; nothing to generate.");
            return;
        }

        var generated = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var item in todaysEvents.Take(12))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (await generation.HasPosterForEventAsync(date, item.Text))
            {
                skipped++;
                continue;
            }

            var result = await generation.GenerateEventAsync(date, item, new GenerateOptions { Persist = true }, ct);
            if (result.Success && result.Poster is not null)
            {
                generated++;
                _logger.LogInformation("Scheduled poster generated for {Date} (#{Id}): {Title}", date.ToString("yyyy-MM-dd"), result.Poster.Id, item.Text);
            }
            else
            {
                failed++;
                _logger.LogWarning("Scheduled poster failed for {Date} ({Title}): {Error}", date.ToString("yyyy-MM-dd"), item.Text, result.Error);
                log?.Add("error", $"Failed poster for {date:dd MMM yyyy}: {item.Text} ({result.Error})");
            }
        }

        log?.Add("scheduler", $"Scheduled run for {date:dd MMM yyyy}: {generated} generated, {skipped} skipped, {failed} failed.");
    }
}
