using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Serilog;
using TradingBot;
using TradingBot.Options;
using TradingBot.Services.Broker;
using TradingBot.Services.Dashboard;
using TradingBot.Services.Discovery;
using TradingBot.Services.Macro;
using TradingBot.Services.News;
using TradingBot.Services.Risk;
using TradingBot.Services.Sentiment;
using TradingBot.Services.State;

var builder = WebApplication.CreateBuilder(args);

// Default to localhost:5000 so the dashboard isn't exposed to the LAN out of the box.
// Override on cloud hosts with: dotnet run -- --urls=http://0.0.0.0:5000
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5000");

// User-secrets are picked up automatically in Development. Force-add for any environment so
// dotnet run from a non-Development shell still finds the keys.
builder.Configuration.AddUserSecrets<Program>(optional: true);

// --- Logging via Serilog ---
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

// --- Options binding ---
builder.Services.AddOptions<AlpacaOptions>()
    .Bind(builder.Configuration.GetSection(AlpacaOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.KeyId),    "Alpaca:KeyId is missing (set via dotnet user-secrets)")
    .Validate(o => !string.IsNullOrWhiteSpace(o.SecretKey),"Alpaca:SecretKey is missing (set via dotnet user-secrets)")
    .ValidateOnStart();

builder.Services.AddOptions<FinnhubOptions>()
    .Bind(builder.Configuration.GetSection(FinnhubOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Finnhub:ApiKey is missing (set via dotnet user-secrets)")
    .ValidateOnStart();

// Anthropic key is optional — only required when Llm:Provider = "Anthropic". Failing on missing
// key would prevent the bot from starting on a Gemini-only config.
builder.Services.AddOptions<AnthropicOptions>()
    .Bind(builder.Configuration.GetSection(AnthropicOptions.SectionName));

builder.Services.AddOptions<LlmOptions>()
    .Bind(builder.Configuration.GetSection(LlmOptions.SectionName));

builder.Services.AddOptions<GeminiOptions>()
    .Bind(builder.Configuration.GetSection(GeminiOptions.SectionName));

builder.Services.AddOptions<GrokOptions>()
    .Bind(builder.Configuration.GetSection(GrokOptions.SectionName));

builder.Services.AddOptions<ExitOptions>()
    .Bind(builder.Configuration.GetSection(ExitOptions.SectionName));

builder.Services.AddOptions<EntryOptions>()
    .Bind(builder.Configuration.GetSection(EntryOptions.SectionName));

builder.Services.AddOptions<TradingOptions>()
    .Bind(builder.Configuration.GetSection(TradingOptions.SectionName))
    .Validate(o => o.Universe is { Length: > 0 }, "Trading:Universe must contain at least one ticker")
    .Validate(o => o.MinConfidence is >= 0 and <= 1, "Trading:MinConfidence must be in [0,1]")
    .Validate(o => o.MaxPositionFraction is > 0 and <= 1, "Trading:MaxPositionFraction must be in (0,1]")
    .Validate(o => o.MaxDailyLossFraction is > 0 and <= 1, "Trading:MaxDailyLossFraction must be in (0,1]")
    .ValidateOnStart();

// SEC EDGAR is permissive — if ContactEmail is missing the provider just self-disables (doesn't crash startup).
builder.Services.AddOptions<SecEdgarOptions>()
    .Bind(builder.Configuration.GetSection(SecEdgarOptions.SectionName));

builder.Services.AddOptions<DiscoveryOptions>()
    .Bind(builder.Configuration.GetSection(DiscoveryOptions.SectionName))
    .Validate(o => o.BuzzThreshold >= 1, "Discovery:BuzzThreshold must be >= 1")
    .Validate(o => o.BuzzWindowMinutes >= 1, "Discovery:BuzzWindowMinutes must be >= 1")
    .Validate(o => o.WatchlistTtlHours >= 1, "Discovery:WatchlistTtlHours must be >= 1")
    .Validate(o => o.MaxWatchlistSize >= 1, "Discovery:MaxWatchlistSize must be >= 1")
    .ValidateOnStart();

builder.Services.AddOptions<MacroOptions>()
    .Bind(builder.Configuration.GetSection(MacroOptions.SectionName));
builder.Services.AddOptions<PolymarketOptions>()
    .Bind(builder.Configuration.GetSection(PolymarketOptions.SectionName));
builder.Services.AddOptions<ManifoldOptions>()
    .Bind(builder.Configuration.GetSection(ManifoldOptions.SectionName));

// --- Typed HttpClients with retries ---
var retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()                                 // 5xx + network failures
    .OrResult(r => (int)r.StatusCode == 429)                    // rate limited
    .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt)));

// Each concrete news provider gets its own typed HttpClient. INewsProvider itself resolves to
// CompositeNewsProvider, which fans out to all sources in parallel.
builder.Services.AddHttpClient<FinnhubNewsProvider>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<FinnhubOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(15);
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddHttpClient<SecEdgarNewsProvider>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<SecEdgarOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(20);
    var contact = string.IsNullOrWhiteSpace(opts.ContactEmail) ? "unconfigured@example.com" : opts.ContactEmail;
    http.DefaultRequestHeaders.UserAgent.ParseAdd($"TradingBot/1.0 ({contact})");
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddSingleton<CikCache>();
builder.Services.AddSingleton<INewsProvider, CompositeNewsProvider>();

// Market-wide discovery feed (separate typed client so it doesn't share rate-limit state with per-ticker).
builder.Services.AddHttpClient<FinnhubMarketNewsProvider>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<FinnhubOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(15);
})
.AddPolicyHandler(retryPolicy);

// Earnings calendar — separate typed client to keep rate-limit accounting clean.
builder.Services.AddHttpClient<EarningsCalendar>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<FinnhubOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(20);
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddSingleton<BuzzTracker>();
builder.Services.AddSingleton<WatchlistManager>();

// Macro pipeline (independent of trading loop). Source is selectable via Macro:Source config.
builder.Services.AddHttpClient<PolymarketMacroProvider>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<PolymarketOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(20);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingBot/1.0");
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddHttpClient<ManifoldMacroProvider>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<ManifoldOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(20);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingBot/1.0");
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
.AddPolicyHandler(retryPolicy);

// Resolve the active provider based on Macro:Source config.
builder.Services.AddSingleton<IMacroProvider>(sp =>
{
    var src = sp.GetRequiredService<IOptions<MacroOptions>>().Value.Source ?? "Manifold";
    return src.Equals("Polymarket", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<PolymarketMacroProvider>()
        : sp.GetRequiredService<ManifoldMacroProvider>();
});
builder.Services.AddSingleton<MacroStore>();

// --- LLM providers: both are registered as typed clients; ISentimentAnalyzer + ITickerExtractor
// resolve to the active one based on Llm:Provider config. Inactive provider stays available as
// a runtime fallback if you flip the config later.

builder.Services.AddHttpClient<ClaudeSentimentAnalyzer>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(30);
    if (!string.IsNullOrWhiteSpace(opts.ApiKey))
    {
        http.DefaultRequestHeaders.Add("x-api-key", opts.ApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", opts.ApiVersion);
    }
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddHttpClient<ClaudeTickerExtractor>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(45);          // batch call, allow longer
    if (!string.IsNullOrWhiteSpace(opts.ApiKey))
    {
        http.DefaultRequestHeaders.Add("x-api-key", opts.ApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", opts.ApiVersion);
        http.DefaultRequestHeaders.Add("anthropic-beta", "prompt-caching-2024-07-31");
    }
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddHttpClient<GeminiSentimentAnalyzer>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(30);
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddHttpClient<GeminiTickerExtractor>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(45);
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddSingleton<ISentimentAnalyzer>(sp =>
{
    var provider = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Provider ?? "Gemini";
    return provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<ClaudeSentimentAnalyzer>()
        : sp.GetRequiredService<GeminiSentimentAnalyzer>();
});

builder.Services.AddSingleton<ITickerExtractor>(sp =>
{
    var provider = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Provider ?? "Gemini";
    return provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<ClaudeTickerExtractor>()
        : sp.GetRequiredService<GeminiTickerExtractor>();
});

// Grok (xAI) trending discovery — independent of sentiment provider. OpenAI-compatible API.
builder.Services.AddHttpClient<GrokTrendingProvider>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<GrokOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(45);          // X-search can take a while
    if (!string.IsNullOrWhiteSpace(opts.ApiKey))
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddSingleton<TrendingTickerStore>();

// --- Domain services ---
builder.Services.AddSingleton<IBroker, AlpacaBroker>();
builder.Services.AddSingleton<RiskManager>();
builder.Services.AddSingleton<PositionOpenTimeTracker>();
builder.Services.AddSingleton<PositionPeakTracker>();
builder.Services.AddSingleton<PositionExitManager>();
builder.Services.AddSingleton<ActionableSignalTracker>();
builder.Services.AddSingleton<ProcessedNewsStore>();
builder.Services.AddSingleton<DecisionHistoryService>();

// Serialize enums (e.g. DecisionOutcome) as strings so the dashboard JS can filter on names like "NoTradeGate".
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// --- Hosted workers — all run in parallel with the web server ---
builder.Services.AddHostedService<TradingWorker>();
builder.Services.AddHostedService<MacroPollWorker>();
builder.Services.AddHostedService<GrokTrendingWorker>();
builder.Services.AddHostedService<EarningsCalendarPoller>();

// ============================================================================================
//  HTTP pipeline
// ============================================================================================
var app = builder.Build();

// Serve wwwroot/index.html at "/" and any other static asset under wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- Dashboard JSON API ----------
app.MapGet("/api/status", (
    IOptions<TradingOptions> trading,
    IOptions<AlpacaOptions> alpaca,
    IOptions<AnthropicOptions> anthropic,
    IOptions<GeminiOptions> gemini,
    IOptions<LlmOptions> llm,
    DecisionHistoryService history) =>
{
    var t = trading.Value;
    var provider = llm.Value.Provider ?? "Gemini";
    var activeModel = provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
        ? anthropic.Value.Model
        : gemini.Value.SentimentModel;
    return Results.Json(new
    {
        startedAtUtc = history.StartedAt,
        lastTickUtc = history.LastTickAt,
        marketOpen = history.MarketOpen,
        paperMode = alpaca.Value.UsePaperTrading,
        tradingEnabled = t.TradingEnabled,
        pollIntervalSeconds = t.PollIntervalSeconds,
        minConfidence = t.MinConfidence,
        maxPositionFraction = t.MaxPositionFraction,
        maxDailyLossFraction = t.MaxDailyLossFraction,
        maxTradesPerDay = t.MaxTradesPerDay,
        tradesToday = history.TradesToday,
        regularHoursOnly = t.RegularHoursOnly,
        universe = t.Universe,
        llmProvider = provider,
        model = activeModel
    });
});

app.MapGet("/api/account", (DecisionHistoryService history) =>
{
    var a = history.LatestAccount;
    if (a is null) return Results.Json(null);
    return Results.Json(new
    {
        equity = a.Equity,
        cash = a.Cash,
        buyingPower = a.BuyingPower,
        sessionOpenEquity = a.LastEquityAtSessionOpen,
        dayPnL = a.LastEquityAtSessionOpen > 0 ? (a.Equity - a.LastEquityAtSessionOpen) : 0m,
        dayPnLPct = a.LastEquityAtSessionOpen > 0 ? (a.Equity - a.LastEquityAtSessionOpen) / a.LastEquityAtSessionOpen : 0m
    });
});

app.MapGet("/api/positions", (DecisionHistoryService history) =>
    Results.Json(history.LatestPositions));

app.MapGet("/api/exits", (
    DecisionHistoryService history,
    PositionExitManager exits,
    IOptions<ExitOptions> opts) =>
{
    var o = opts.Value;
    return Results.Json(new
    {
        enabled = o.Enabled,
        stopLossPercent = o.StopLossPercent,
        takeProfitPercent = o.TakeProfitPercent,
        maxHoldDays = o.MaxHoldDays,
        positions = exits.DistanceSnapshot(history.LatestPositions, DateTimeOffset.UtcNow)
    });
});

app.MapGet("/api/orders", async (IBroker broker, CancellationToken ct, int? limit) =>
{
    var orders = await broker.ListRecentOrdersAsync(Math.Clamp(limit ?? 50, 1, 200), ct);
    return Results.Json(orders);
});

app.MapGet("/api/decisions", (DecisionHistoryService history, int? limit) =>
    Results.Json(history.Recent(Math.Clamp(limit ?? 100, 1, 500))));

app.MapGet("/api/watchlist", (WatchlistManager wl, BuzzTracker buzz, IOptions<DiscoveryOptions> opts) =>
{
    var o = opts.Value;
    return Results.Json(new
    {
        enabled = o.Enabled,
        buzzThreshold = o.BuzzThreshold,
        buzzWindowMinutes = o.BuzzWindowMinutes,
        watchlistTtlHours = o.WatchlistTtlHours,
        maxWatchlistSize = o.MaxWatchlistSize,
        entries = wl.ActiveEntries(),
        topBuzz = buzz.Snapshot(15).Select(x => new { ticker = x.Ticker, score = x.Score })
    });
});

app.MapGet("/api/macro", (MacroStore store, IOptions<MacroOptions> opts, IMacroProvider provider) =>
{
    var o = opts.Value;
    var snap = store.Latest;
    return Results.Json(new
    {
        enabled = o.Enabled,
        source = provider.SourceName,
        pollIntervalSeconds = o.PollIntervalSeconds,
        lastUpdatedUtc = snap.At == DateTimeOffset.MinValue ? (DateTimeOffset?)null : snap.At,
        markets = snap.Markets
    });
});

app.MapGet("/api/trending", (TrendingTickerStore store, IOptions<GrokOptions> opts) =>
{
    var o = opts.Value;
    var snap = store.Latest;
    return Results.Json(new
    {
        enabled = o.Enabled,
        model = o.Model,
        pollIntervalSeconds = o.PollIntervalSeconds,
        lastUpdatedUtc = snap.At == DateTimeOffset.MinValue ? (DateTimeOffset?)null : snap.At,
        tickers = snap.Tickers
    });
});

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
