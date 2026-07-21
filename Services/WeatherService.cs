using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace sidequest.backend.Services;

// One parsed provider day. Code is the SideQuest-owned condition (mapped
// from the WMO weather code here, server-side) — provider JSON never
// reaches the mobile app.
public class WeatherDay
{
    public DateOnly Date { get; set; }
    // False when the provider's response covers this date in `time` but
    // returned null for its core values (observed on the last day of a
    // forecast_days request) — the date is real, the forecast isn't. Every
    // other field is null in that case; never a fabricated zero.
    public bool IsForecastAvailable { get; set; }
    public string? Code { get; set; }
    public double? TempMinC { get; set; }
    public double? TempMaxC { get; set; }
    public int? PrecipitationProbability { get; set; }
    public double? UvIndexMax { get; set; }
}

public class WeatherForecast
{
    public string Timezone { get; set; } = "UTC";
    public DateTime FetchedAt { get; set; }
    public List<WeatherDay> Days { get; set; } = new();
}

public class WeatherServiceResult
{
    public WeatherForecast? Forecast { get; set; }
    public bool Stale { get; set; }
}

// Open-Meteo daily-forecast client with an in-memory cache. Single Railway
// instance (same assumption as the presence write throttle in Program.cs),
// so in-memory is the right level — no distributed cache.
//
// Cache design:
//  - key: coordinates rounded to 2 decimals (~1.1 km) — trips to the same
//    city share one entry and one provider call.
//  - fresh TTL 3h (Weather:CacheMinutes), stale entries served up to 24h
//    (Weather:StaleHours) when the provider fails, flagged Stale.
//  - concurrent requests for the same key share one in-flight provider
//    call (Lazy<Task> dedupe).
//  - provider timeout 5s; HTTP 429 pauses ALL provider calls for 10
//    minutes (stale cache keeps serving); malformed JSON = failure.
public class WeatherService
{
    public const string Attribution = "Weather data by Open-Meteo.com (CC BY 4.0)";
    // Open-Meteo's real daily forecast horizon: today + 15 more days.
    public const int ForecastDays = 16;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<WeatherService> _logger;

    private static readonly ConcurrentDictionary<string, WeatherForecast> _cache = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<WeatherForecast?>>> _inFlight = new();
    private static DateTime _pausedUntil = DateTime.MinValue;

    public WeatherService(HttpClient httpClient, IConfiguration config, ILogger<WeatherService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    private TimeSpan FreshTtl => TimeSpan.FromMinutes(_config.GetValue("Weather:CacheMinutes", 180));
    private TimeSpan StaleTtl => TimeSpan.FromHours(_config.GetValue("Weather:StaleHours", 24));
    private TimeSpan Timeout => TimeSpan.FromSeconds(_config.GetValue("Weather:TimeoutSeconds", 5));
    private string BaseUrl => _config["Weather:BaseUrl"] ?? "https://api.open-meteo.com";

    public async Task<WeatherServiceResult> GetForecastAsync(double latitude, double longitude, CancellationToken ct)
    {
        var key = CacheKey(latitude, longitude);
        var now = DateTime.UtcNow;

        if (_cache.TryGetValue(key, out var cached) && now - cached.FetchedAt < FreshTtl)
            return new WeatherServiceResult { Forecast = cached, Stale = false };

        WeatherForecast? fresh = null;
        if (now >= _pausedUntil)
        {
            // Lazy<Task> so N concurrent requests for the same coordinates
            // produce exactly one provider call.
            var lazy = _inFlight.GetOrAdd(key, k => new Lazy<Task<WeatherForecast?>>(() => FetchFromProviderAsync(k, latitude, longitude)));
            try
            {
                fresh = await lazy.Value.WaitAsync(ct);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }

        if (fresh != null)
        {
            _cache[key] = fresh;
            return new WeatherServiceResult { Forecast = fresh, Stale = false };
        }

        // Provider failed/paused — serve the stale entry if it's still
        // within the stale window; weather must degrade, never break.
        if (cached != null && now - cached.FetchedAt < StaleTtl)
            return new WeatherServiceResult { Forecast = cached, Stale = true };

        return new WeatherServiceResult { Forecast = null, Stale = false };
    }

    private static string CacheKey(double latitude, double longitude)
        => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(latitude, 2)}:{Math.Round(longitude, 2)}");

    private async Task<WeatherForecast?> FetchFromProviderAsync(string key, double latitude, double longitude)
    {
        try
        {
            var lat = latitude.ToString(CultureInfo.InvariantCulture);
            var lon = longitude.ToString(CultureInfo.InvariantCulture);
            var url = $"{BaseUrl}/v1/forecast?latitude={lat}&longitude={lon}"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,uv_index_max"
                + $"&timezone=auto&forecast_days={ForecastDays}";

            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var response = await _httpClient.GetAsync(url, timeoutCts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _pausedUntil = DateTime.UtcNow.AddMinutes(10);
                _logger.LogWarning("[WEATHER] provider returned 429 — pausing provider calls for 10 minutes");
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[WEATHER] provider returned {Status} for {Key}", (int)response.StatusCode, key);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            return ParseForecast(doc.RootElement);
        }
        catch (Exception ex)
        {
            // Timeout, network failure, malformed JSON — all equal: no fresh
            // data. The caller falls back to stale cache or reports
            // unavailable; a weather failure never surfaces as an exception.
            _logger.LogWarning("[WEATHER] provider fetch failed for {Key}: {Message}", key, ex.Message);
            return null;
        }
    }

    private static WeatherForecast? ParseForecast(JsonElement root)
    {
        if (!root.TryGetProperty("daily", out var daily)) return null;
        if (!daily.TryGetProperty("time", out var times) || times.ValueKind != JsonValueKind.Array) return null;

        var codes = GetArray(daily, "weather_code");
        var maxes = GetArray(daily, "temperature_2m_max");
        var mins = GetArray(daily, "temperature_2m_min");
        var precip = GetArray(daily, "precipitation_probability_max");
        var uv = GetArray(daily, "uv_index_max");

        var forecast = new WeatherForecast
        {
            Timezone = root.TryGetProperty("timezone", out var tz) ? tz.GetString() ?? "UTC" : "UTC",
            FetchedAt = DateTime.UtcNow,
        };

        var count = times.GetArrayLength();
        for (var i = 0; i < count; i++)
        {
            var dateRaw = times[i].GetString();
            if (dateRaw == null || !DateOnly.TryParse(dateRaw, CultureInfo.InvariantCulture, out var date)) continue;

            // `time` is always the full requested length, but Open-Meteo can
            // return null in the value arrays for a date it doesn't have real
            // model data for yet (reliably observed on the LAST day of a
            // forecast_days request). Code/max/min missing means there is no
            // real forecast for this date — never substitute zero for it.
            var isAvailable = IsNumberAt(codes, i) && IsNumberAt(maxes, i) && IsNumberAt(mins, i);

            forecast.Days.Add(new WeatherDay
            {
                Date = date,
                IsForecastAvailable = isAvailable,
                Code = isAvailable ? MapWmoCode(GetIntAt(codes, i)) : null,
                TempMaxC = isAvailable ? Math.Round(GetDoubleAt(maxes, i), 1) : null,
                TempMinC = isAvailable ? Math.Round(GetDoubleAt(mins, i), 1) : null,
                PrecipitationProbability = isAvailable && IsNumberAt(precip, i) ? Math.Clamp(GetIntAt(precip, i), 0, 100) : null,
                UvIndexMax = isAvailable && IsNumberAt(uv, i) ? Math.Round(GetDoubleAt(uv, i), 1) : null,
            });
        }

        return forecast.Days.Count > 0 ? forecast : null;
    }

    private static JsonElement? GetArray(JsonElement daily, string name)
        => daily.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array ? el : null;

    private static bool IsNumberAt(JsonElement? array, int index)
        => array is { } arr && index < arr.GetArrayLength() && arr[index].ValueKind == JsonValueKind.Number;

    private static double GetDoubleAt(JsonElement? array, int index)
    {
        if (array is not { } arr || index >= arr.GetArrayLength()) return 0;
        var el = arr[index];
        return el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0;
    }

    private static int GetIntAt(JsonElement? array, int index)
        => (int)Math.Round(GetDoubleAt(array, index));

    // WMO weather interpretation codes → the SideQuest-owned condition set.
    // Anything unknown lands on the neutral "cloudy" rather than guessing.
    private static string MapWmoCode(int code) => code switch
    {
        0 or 1 => "clear",
        2 => "partly_cloudy",
        3 => "cloudy",
        45 or 48 => "fog",
        51 or 53 or 55 or 56 or 57 => "rain",   // drizzle family
        61 or 63 or 66 or 80 or 81 => "rain",
        65 or 67 or 82 => "heavy_rain",
        71 or 73 or 75 or 77 or 85 or 86 => "snow",
        95 or 96 or 99 => "thunderstorm",
        _ => "cloudy",
    };
}
