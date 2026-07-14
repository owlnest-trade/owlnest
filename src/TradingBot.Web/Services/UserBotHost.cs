using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingBot.Web.Data;
using TradingBot.Web.Services.Shared;
using TradingBot.Web.Services.UserBot;

namespace TradingBot.Web.Services;

/// <summary>
/// Tracks every currently-running per-user bot instance. Start/Stop are idempotent and safe to call
/// from request handlers. The instance itself runs on a background Task; the host just holds the
/// references so we can stop/dispose them cleanly.
///
/// As of v6, owlnest is platform-shared for API keys: Finnhub, Gemini, and Grok credentials come
/// from server config (ServerKeys section), NOT from the user's keys bag. The user only provides
/// their own Alpaca paper-trading credentials.
/// </summary>
public sealed class UserBotHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, UserBotInstance> _instances = new();
    private readonly IServiceProvider _sp;
    private readonly ApiKeyProtector _protector;
    private readonly SecFilingsFeed _sec;
    private readonly ManifoldFeed _macro;
    private readonly FedFeed _fed;
    private readonly ServerKeys _serverKeys;
    private readonly ILogger<UserBotHost> _log;

    public UserBotHost(IServiceProvider sp, ApiKeyProtector protector,
        SecFilingsFeed sec, ManifoldFeed macro, FedFeed fed,
        IOptions<ServerKeys> serverKeys, ILogger<UserBotHost> log)
    {
        _sp = sp;
        _protector = protector;
        _sec = sec;
        _macro = macro;
        _fed = fed;
        _serverKeys = serverKeys.Value;
        _log = log;
    }

    public bool IsRunning(string userId) => _instances.ContainsKey(userId);

    public string? StatusLine(string userId) =>
        _instances.TryGetValue(userId, out var inst) ? inst.LastStatusLine : null;

    public IReadOnlyList<WatchEntry> Watchlist(string userId) =>
        _instances.TryGetValue(userId, out var inst) ? inst.Watchlist : Array.Empty<WatchEntry>();

    public IReadOnlyList<TrendingTicker> Trending(string userId) =>
        _instances.TryGetValue(userId, out var inst) ? inst.LastTrending : Array.Empty<TrendingTicker>();

    /// <summary>True if the user's bot is currently running against Alpaca LIVE (real money).</summary>
    public bool IsLiveMode(string userId) =>
        _instances.TryGetValue(userId, out var inst) && inst.IsLiveMode;

    public async Task<string> StartAsync(string userId, CancellationToken ct)
    {
        if (_instances.ContainsKey(userId)) return "Already running.";

        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();

        var keys = await db.UserApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.UserId == userId, ct);
        var settings = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (keys is null || settings is null) return "Save your Alpaca keys + settings first.";

        // ── Per-user keys (only Alpaca now) ──
        var alpacaKey = _protector.Unprotect(keys.AlpacaKeyIdEncrypted);
        var alpacaSecret = _protector.Unprotect(keys.AlpacaSecretKeyEncrypted);
        if (string.IsNullOrWhiteSpace(alpacaKey) || string.IsNullOrWhiteSpace(alpacaSecret))
            return "Add your Alpaca paper-trading keys on the API keys page first.";

        // ── Platform-shared keys (server config) ──
        if (!_serverKeys.IsFinnhubReady) return "Platform error: Finnhub key not configured on server. Contact owner.";
        if (!_serverKeys.IsGeminiReady)  return "Platform error: Gemini key not configured on server. Contact owner.";

        // Grok is optional — only required if user is on Plus+ AND wants trending discovery or 2nd-opinion
        var tier = TierPolicy.Normalize(settings.Tier);
        var needGrok = (TierPolicy.AllowsGrokTrending(tier) && settings.UseGrokTrending)
                    || (TierPolicy.AllowsGrokConfirmation(tier) && settings.GrokConfirmationEnabled);
        if (needGrok && !_serverKeys.IsGrokReady)
            _log.LogInformation("User {U} on tier {T} enabled Grok but server has no Grok key — feature silently disabled", userId, tier);

        // Pick the sentiment classifier provider based on what the user selected in Settings.
        // Fall back to Gemini if their chosen provider's server key isn't configured.
        var requested = (settings.LlmProvider ?? "Gemini").Trim();
        var effectiveProvider = requested.ToLowerInvariant() switch
        {
            "anthropic" => _serverKeys.IsAnthropicReady ? "Anthropic" : "Gemini",
            "llama"     => _serverKeys.IsLlamaReady     ? "Llama"     : "Gemini",
            _           => "Gemini",
        };
        if (!effectiveProvider.Equals(requested, StringComparison.OrdinalIgnoreCase))
            _log.LogWarning("User {U} chose LLM {Chosen} but server lacks the key — falling back to {Effective}",
                userId, requested, effectiveProvider);

        // User can override the Llama model name in their settings (e.g. switch from 8B to 70B
        // for higher quality at higher cost); empty falls back to whatever the server default is.
        var llamaModel = string.IsNullOrWhiteSpace(settings.LlamaModel)
            ? _serverKeys.LlamaModel
            : settings.LlamaModel;

        var instance = new UserBotInstance(
            userId, settings,
            alpacaKey, alpacaSecret, keys.AlpacaPaperMode,
            _serverKeys.Finnhub,
            effectiveProvider,
            _serverKeys.Gemini,
            _serverKeys.IsAnthropicReady ? _serverKeys.Anthropic : null,
            _serverKeys.IsGrokReady      ? _serverKeys.Grok      : null,
            _serverKeys.IsLlamaReady     ? _serverKeys.Llama     : null,
            _serverKeys.GeminiModel, _serverKeys.AnthropicModel, llamaModel,
            _sec, _macro, _fed,
            _sp, _log);
        if (!_instances.TryAdd(userId, instance))
        {
            await instance.DisposeAsync();
            return "Already running.";
        }
        instance.Start();

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.BotRunning = true;
        user.BotStartedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Started bot for user {U} on tier {Tier}", userId, tier);
        return "Started.";
    }

    public async Task<string> StopAsync(string userId, CancellationToken ct)
    {
        if (!_instances.TryRemove(userId, out var inst)) return "Not running.";
        await inst.DisposeAsync();

        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.BotRunning = false;
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Stopped bot for user {U}", userId);
        return "Stopped.";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, inst) in _instances) await inst.DisposeAsync();
        _instances.Clear();
    }
}
