using DailyPosterGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Services;

public interface ISettingsService
{
    Task<string?> GetAsync(string key, string? defaultValue = null);
    Task SetAsync(string key, string? value);
    Task RemoveAsync(string key);
    Task<Dictionary<string, string>> GetAllAsync();
}

public class SettingsService : ISettingsService
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly IConfiguration _config;

    public SettingsService(IDbContextFactory<DailyPosterDbContext> dbFactory, IConfiguration config)
    {
        _dbFactory = dbFactory;
        _config = config;
    }

    public async Task<string?> GetAsync(string key, string? defaultValue = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        if (row is not null)
        {
            return string.IsNullOrWhiteSpace(row.Value) ? defaultValue : row.Value;
        }

        return _config[$"AppSettings:{key}"] ?? defaultValue;
    }

    public async Task SetAsync(string key, string? value)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null)
        {
            db.SystemSettings.Add(new Models.SystemSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is not null)
        {
            db.SystemSettings.Remove(row);
            await db.SaveChangesAsync();
        }
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SystemSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty);
    }
}
