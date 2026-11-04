using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace TodoSaaS.Api.Services;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task<long> GetVersionAsync(string versionKey, CancellationToken cancellationToken = default);
    Task InvalidateVersionAsync(string versionKey, CancellationToken cancellationToken = default);
}

public class DistributedCacheService : ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedCacheService> _logger;

    public DistributedCacheService(IDistributedCache cache, ILogger<DistributedCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await _cache.GetAsync(key, cancellationToken);
            if (bytes == null || bytes.Length == 0) return default;
            return JsonSerializer.Deserialize<T>(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve key '{Key}' from distributed cache", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = absoluteExpiration ?? TimeSpan.FromMinutes(5)
            };
            await _cache.SetAsync(key, bytes, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set key '{Key}' in distributed cache", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove key '{Key}' from distributed cache", key);
        }
    }

    public async Task<long> GetVersionAsync(string versionKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var str = await _cache.GetStringAsync(versionKey, cancellationToken);
            if (long.TryParse(str, out var v)) return v;
            return 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    public async Task InvalidateVersionAsync(string versionKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var current = await GetVersionAsync(versionKey, cancellationToken);
            await _cache.SetStringAsync(versionKey, (current + 1).ToString(), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30)
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate cache version for '{Key}'", versionKey);
        }
    }
}
