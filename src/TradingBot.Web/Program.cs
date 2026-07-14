using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingBot.Web.Data;
using TradingBot.Web.Services;
using TradingBot.Web.Services.Backtesting;
using TradingBot.Web.Services.Diagnostics;
using TradingBot.Web.Services.Shared;

var builder = WebApplication.CreateBuilder(args);

// Load user-secrets regardless of environment. By default they only load in Development, but
// owlnest defaults to Production — and in real Production (Azure App Service) we'll use
// App Service Configuration which takes precedence anyway. `optional: true` so it doesn't fail
// when the secrets file is absent.
builder.Configuration.AddUserSecrets<Program>(optional: true);

// Bind to 5001 so the single-user TradingBot (port 5000) keeps running undisturbed.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5001");

// SQLite is the default so self-hosters do not need a paid managed database.
// — never appsettings.json, which is committed to git.
var connStr = BuildSqliteConnectionString(builder);
builder.Services.AddDbContext<OwlNestDbContext>(opts => opts.UseSqlite(connStr));

// ASP.NET Identity for auth.
builder.Services
    .AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.Password.RequireDigit = true;
        o.Password.RequireNonAlphanumeric = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireUppercase = false;
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<OwlNestDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/Login";
});

// External login providers. Google OAuth — credentials live in user-secrets / App Service config
// under Authentication:Google:ClientId and :ClientSecret. If not configured, the "Sign in with
// Google" button simply isn't rendered. To set up:
//   1. Create OAuth credentials at https://console.cloud.google.com/apis/credentials
//   2. Redirect URI: https://YOUR-DOMAIN/signin-google (for prod) + http://localhost:5001/signin-google (for dev)
//   3. dotnet user-secrets set "Authentication:Google:ClientId" "<your client id>"
//      dotnet user-secrets set "Authentication:Google:ClientSecret" "<your client secret>"
{
    var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
    var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
    if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
    {
        builder.Services.AddAuthentication()
            .AddGoogle(opts =>
            {
                opts.ClientId = googleClientId;
                opts.ClientSecret = googleClientSecret;
                opts.SignInScheme = Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme;
            });
    }
}

// Data protection — used by ApiKeyProtector. Persists keys to the local profile by default.
builder.Services.AddDataProtection();

builder.Services
    .AddRazorPages(opts =>
    {
        opts.Conventions.AuthorizePage("/Dashboard");
        opts.Conventions.AuthorizePage("/Settings");
        opts.Conventions.AuthorizePage("/Keys");
        opts.Conventions.AuthorizePage("/Reports");
        opts.Conventions.AuthorizePage("/Diagnostics");
        opts.Conventions.AllowAnonymousToPage("/Index");
        opts.Conventions.AllowAnonymousToPage("/Contact");
        opts.Conventions.AllowAnonymousToPage("/Terms");
    });

// Serialize enums as strings in API responses.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddSingleton<ApiKeyProtector>();

// Platform-shared API keys (Finnhub / Gemini / Grok). Bound from "ServerKeys" config section.
// Real values live in user-secrets (dev) or App Service Configuration (prod).
builder.Services.Configure<ServerKeys>(builder.Configuration.GetSection("ServerKeys"));

// Shared across all users — no per-user data inside.
builder.Services.AddSingleton<SecCikCache>();
builder.Services.AddSingleton<SecFilingsFeed>();
builder.Services.AddSingleton<ManifoldFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ManifoldFeed>());
builder.Services.AddSingleton<FedFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FedFeed>());

builder.Services.AddSingleton<UserBotHost>();
builder.Services.AddSingleton<SourceTester>();
builder.Services.AddScoped<BacktestReplayService>();
// Postmark notifier — used by the Contact form to email submissions to the owner. Silent no-op
// when ServerKeys:PostmarkServerToken isn't configured (local dev / pre-setup prod).
builder.Services.AddSingleton<ContactNotifier>();

var app = builder.Build();

// Create the database tables on first run. If OWLNEST_RESET_DB=true is set, drop and recreate
// the schema first — used during development when we add/remove columns. SAFE on Azure SQL only
// when the DB is empty; protect it behind the env var so a misconfigured prod restart can't wipe data.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var reset = string.Equals(builder.Configuration["OwlNest:ResetDb"]
                                  ?? Environment.GetEnvironmentVariable("OWLNEST_RESET_DB"),
                                  "true", StringComparison.OrdinalIgnoreCase);
        if (reset)
        {
            await db.Database.EnsureDeletedAsync();
            Console.WriteLine("OWLNEST_RESET_DB=true - deleted the owlnest database, will recreate");
        }
        if (string.Equals(builder.Configuration["OwlNest:LegacyBootstrapNote"], "true", StringComparison.OrdinalIgnoreCase))
        {
            // Drop ALL owlnest-managed tables including the Identity tables (AspNetUsers etc.)
            // because we sometimes add columns to AppUser (e.g. AcceptedTermsAtUtc) which only
            // get applied if the table is recreated. Order matters because of FK constraints —
            // child tables first.
            Console.WriteLine("⚠️  OWLNEST_RESET_DB=true — dropped all owlnest tables (including Identity), will recreate");
        }
        await db.Database.EnsureCreatedAsync();
        await ApplySqliteAdditiveSchemaAsync(db, logger);

        // ── Additive schema migrations ──
        // EF Core's EnsureCreated only creates missing tables; it does NOT add new columns
        // to existing tables. So every time we add a property to UserSettings/AppUser we must
        // also add an idempotent ALTER TABLE here. Each block is safe to run every startup —
        // it only fires if the column is missing. Cheaper than EF migrations for this app size.
        if (string.Equals(builder.Configuration["OwlNest:LegacyMigrationNote"], "true", StringComparison.OrdinalIgnoreCase))
        try
        {
            logger.LogInformation("Database additive migrations applied");
        }
        catch (Exception migEx)
        {
            logger.LogError(migEx, "Additive migration script failed (will continue — some pages may error)");
        }

        logger.LogInformation("Database ready");
    }
    catch (Exception ex)
    {
        // DON'T crash the process if the DB isn't reachable. Log loudly and let the app boot
        // so the landing page still serves and users see something useful instead of Azure's
        // "no content yet" placeholder. Any DB-touching request (Dashboard, /api/me/*, etc.)
        // will then return a clear 500 with the SqlException details rather than a silent crash.
        logger.LogCritical(ex, "DATABASE STARTUP FAILED — site will load but DB-backed pages will error");
        Console.Error.WriteLine("⚠️  Database startup failed: " + ex.Message);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
//  Bot auto-resume
//  ────────────────────────────────────────────────────────────────────────────────
//  Every App Service restart (deploy, env-var change, scale event, idle recycle)
//  used to drop all running bots — the user had to manually click Start again.
//  Now, after the app is fully built we look up every AppUser with BotRunning=true
//  and call host.StartAsync for them. Idempotent: if a user wasn't really running
//  (missing keys, etc.) StartAsync returns an error string and we log + continue.
//
//  Done in its own scope so DI resolution works against the finished service tree.
//  Runs BEFORE app.Run() so bots spin up while the request pipeline starts accepting
//  traffic — first user request might race with bot startup but that's fine; the
//  dashboard polls every 3 seconds.
// ────────────────────────────────────────────────────────────────────────────────
using (var resumeScope = app.Services.CreateScope())
{
    var resumeLogger = resumeScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = resumeScope.ServiceProvider.GetRequiredService<OwlNestDbContext>();
        var host = resumeScope.ServiceProvider.GetRequiredService<UserBotHost>();
        var runningUserIds = await db.Users
            .Where(u => u.BotRunning)
            .Select(u => u.Id)
            .ToListAsync();
        if (runningUserIds.Count == 0)
        {
            resumeLogger.LogInformation("Bot auto-resume: no users had BotRunning=true, nothing to do");
        }
        else
        {
            resumeLogger.LogInformation("Bot auto-resume: starting {Count} bot(s) that were running before last restart", runningUserIds.Count);
            foreach (var uid in runningUserIds)
            {
                try
                {
                    var result = await host.StartAsync(uid, CancellationToken.None);
                    resumeLogger.LogInformation("Bot auto-resume for {Uid}: {Result}", uid, result);
                }
                catch (Exception ex)
                {
                    resumeLogger.LogWarning(ex, "Bot auto-resume failed for {Uid}", uid);
                }
            }
        }
    }
    catch (Exception ex)
    {
        // Never let auto-resume failure crash startup.
        resumeLogger.LogError(ex, "Bot auto-resume block threw — manual Start will be needed");
    }
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// ============================================================================================
//  Per-user dashboard API — all scoped to the logged-in user
// ============================================================================================
// NOTE: every read endpoint pipes its lookup ID through DemoMode.EffectiveUserIdAsync.
// Demo mode is disabled for the public release, so the helper now returns the authenticated user's own id.
// Status/Watchlist/Trending are in-memory host snapshots keyed by UserId.

app.MapGet("/api/me/status", async (HttpContext ctx, UserManager<AppUser> users, UserBotHost host, OwlNestDbContext db) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    var settings = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == uid)
                   ?? new UserSettings();
    // Pull paper/live flag straight from the user's keys bag (works even if bot isn't running yet)
    var keys = await db.UserApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.UserId == uid);
    var liveMode = host.IsLiveMode(uid) || (keys is not null && !keys.AlpacaPaperMode);
    return Results.Json(new
    {
        running = host.IsRunning(uid),
        liveMode,
        statusLine = host.StatusLine(uid),
        tradingEnabled = settings.TradingEnabled,
        regularHoursOnly = settings.RegularHoursOnly,
        minConfidence = settings.MinConfidence,
        maxPositionFraction = settings.MaxPositionFraction,
        maxDailyLossFraction = settings.MaxDailyLossFraction,
        maxTradesPerDay = settings.MaxTradesPerDay,
        stopLossType = settings.StopLossType,
        stopLossPercent = settings.StopLossPercent,
        trailingStopPercent = settings.TrailingStopPercent,
        trailingStopActivationPercent = settings.TrailingStopActivationPercent,
        maxHoldDays = settings.MaxHoldDays,
        llmProvider = settings.LlmProvider,
        universe = settings.Universe(),
        tier = settings.Tier,
        demo = DemoMode.IsDemo(ctx.User),
    });
}).RequireAuthorization();

app.MapGet("/api/me/positions", async (HttpContext ctx, UserManager<AppUser> users, OwlNestDbContext db) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    var rows = await db.UserPositions.AsNoTracking()
        .Where(p => p.UserId == uid)
        .OrderBy(p => p.Ticker)
        .ToListAsync();
    return Results.Json(rows);
}).RequireAuthorization();

app.MapGet("/api/me/orders", async (HttpContext ctx, UserManager<AppUser> users, OwlNestDbContext db, int? limit) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    var n = Math.Clamp(limit ?? 50, 1, 200);
    var rows = await db.UserOrders.AsNoTracking()
        .Where(o => o.UserId == uid)
        .OrderByDescending(o => o.Id)
        .Take(n)
        .ToListAsync();
    return Results.Json(rows);
}).RequireAuthorization();

app.MapGet("/api/me/decisions", async (HttpContext ctx, UserManager<AppUser> users, OwlNestDbContext db, int? limit) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    var n = Math.Clamp(limit ?? 100, 1, 500);
    var rows = await db.UserDecisions.AsNoTracking()
        .Where(d => d.UserId == uid)
        .OrderByDescending(d => d.Id)
        .Take(n)
        .ToListAsync();
    return Results.Json(rows);
}).RequireAuthorization();

app.MapGet("/api/me/equity", async (HttpContext ctx, UserManager<AppUser> users, OwlNestDbContext db, int? hours) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    // SQLite can't ORDER BY DateTimeOffset, so we sort by Id (auto-increment = chronological).
    var rows = await db.UserEquitySnapshots.AsNoTracking()
        .Where(e => e.UserId == uid)
        .OrderByDescending(e => e.Id)
        .Take(Math.Clamp(hours ?? 168, 1, 720))
        .ToListAsync();
    rows.Reverse();   // chronological ascending for chart
    return Results.Json(rows);
}).RequireAuthorization();

// Macro: shared across all users. Read-only — no auth needed for the snapshot itself but
// keep it authorized so it doesn't leak to anonymous scrapers.
app.MapGet("/api/me/macro", (UserManager<AppUser> users, HttpContext ctx, ManifoldFeed feed) =>
{
    if (users.GetUserId(ctx.User) is null) return Results.Unauthorized();
    var s = feed.Latest;
    return Results.Json(new { atUtc = s.AtUtc, markets = s.Markets });
}).RequireAuthorization();

// Per-user dynamic watchlist (in-memory in UserBotInstance — empty if bot not running).
app.MapGet("/api/me/watchlist", async (UserManager<AppUser> users, HttpContext ctx, UserBotHost host, OwlNestDbContext db) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    return Results.Json(host.Watchlist(uid));
}).RequireAuthorization();

// Per-user last Grok trending result.
app.MapGet("/api/me/trending", async (UserManager<AppUser> users, HttpContext ctx, UserBotHost host, OwlNestDbContext db) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    return Results.Json(host.Trending(uid));
}).RequireAuthorization();

// Per-user watchlist promotion audit. One row per time a ticker was newly added to the dynamic
// watchlist (by buzz discovery or Grok trending), with the market price at promotion time.
// Lets you reconstruct "what was X trading at when the bot first noticed it, vs when it bought".
app.MapGet("/api/me/watchlist-events", async (HttpContext ctx, UserManager<AppUser> users, OwlNestDbContext db, int? limit) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    var n = Math.Clamp(limit ?? 50, 1, 200);
    var rows = await db.UserWatchlistEvents.AsNoTracking()
        .Where(e => e.UserId == uid)
        .OrderByDescending(e => e.Id)
        .Take(n)
        .ToListAsync();
    return Results.Json(rows);
}).RequireAuthorization();

// Per-user Grok/Claude gate-call audit log. One row per call we made to a verification gate,
// including the prompt we sent + the raw model response + verdict + latency. Useful for
// answering "why did Grok veto?" without replaying expensive API calls.
app.MapGet("/api/me/gate-calls", async (HttpContext ctx, UserManager<AppUser> users, OwlNestDbContext db, int? limit, string? gate) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    var n = Math.Clamp(limit ?? 50, 1, 200);
    var query = db.UserGateCalls.AsNoTracking().Where(g => g.UserId == uid);
    if (!string.IsNullOrEmpty(gate))
        query = query.Where(g => g.Gate == gate);
    var rows = await query
        .OrderByDescending(g => g.Id)
        .Take(n)
        .ToListAsync();
    return Results.Json(rows);
}).RequireAuthorization();

// Server-side replay/backtest over stored decisions, submitted orders, and saved price snapshots.
// This intentionally uses a fixed notional per trade so gate variants are comparable even if
// the real account size changed while the bot was running.
app.MapGet("/api/me/backtest", async (
    HttpContext ctx,
    UserManager<AppUser> users,
    OwlNestDbContext db,
    BacktestReplayService replay,
    int? limit,
    decimal? initialCapital,
    decimal? tradeNotional,
    CancellationToken ct) =>
{
    var uid = await DemoMode.EffectiveUserIdAsync(ctx.User, users, db);
    if (uid is null) return Results.Unauthorized();
    var report = await replay.ReplayAsync(
        uid,
        limit ?? 2_000,
        initialCapital ?? 10_000m,
        tradeNotional ?? 1_000m,
        ct);
    return Results.Json(report);
}).RequireAuthorization();

// Diagnostics: live-test all 8 news sources for a given ticker. Used by /Diagnostics page.
app.MapGet("/api/diagnostics/sources", async (HttpContext ctx, UserManager<AppUser> users,
    SourceTester tester, string? ticker, CancellationToken ct) =>
{
    if (users.GetUserId(ctx.User) is null) return Results.Unauthorized();
    var t = (ticker ?? "AAPL").Trim().ToUpperInvariant();
    if (t.Length is < 1 or > 6 || !t.All(c => char.IsLetter(c) || c is '.' or '-')) t = "AAPL";
    var results = await tester.TestAllAsync(t, ct);
    return Results.Json(results);
}).RequireAuthorization();

app.Run();

static string BuildSqliteConnectionString(WebApplicationBuilder builder)
{
    var configured = builder.Configuration.GetConnectionString("OwlNest");
    if (LooksLikeSqliteConnectionString(configured))
    {
        return configured!;
    }

    var configuredPath = builder.Configuration["OwlNest:SqlitePath"];
    var dbPath = string.IsNullOrWhiteSpace(configuredPath)
        ? DefaultSqlitePath(builder)
        : Environment.ExpandEnvironmentVariables(configuredPath);

    if (!Path.IsPathRooted(dbPath))
    {
        dbPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, dbPath));
    }

    var directory = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    return new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
}

static string DefaultSqlitePath(WebApplicationBuilder builder)
{
    var home = Environment.GetEnvironmentVariable("HOME");
    var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
    if (!string.IsNullOrWhiteSpace(home) && !string.IsNullOrWhiteSpace(siteName))
    {
        return Path.Combine(home, "data", "owlnest", "owlnest.db");
    }

    return Path.Combine(builder.Environment.ContentRootPath, "App_Data", "owlnest.db");
}

static bool LooksLikeSqliteConnectionString(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return false;
    }

    try
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return !string.IsNullOrWhiteSpace(builder.DataSource);
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static async Task ApplySqliteAdditiveSchemaAsync(OwlNestDbContext db, ILogger logger)
{
    if (!db.Database.IsSqlite())
    {
        return;
    }

    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await AddColumnIfMissingAsync(db, "UserSettings", "Tier", "TEXT NOT NULL DEFAULT 'Free'");
    await AddColumnIfMissingAsync(db, "UserSettings", "GrokConfirmationEnabled", "INTEGER NOT NULL DEFAULT 0");
    await AddColumnIfMissingAsync(db, "UserSettings", "ClaudeConfirmationEnabled", "INTEGER NOT NULL DEFAULT 0");
    await AddColumnIfMissingAsync(db, "UserSettings", "BlacklistedTickersCsv", "TEXT NOT NULL DEFAULT ''");
    await AddColumnIfMissingAsync(db, "UserSettings", "BlockedKeywordsCsv", "TEXT NOT NULL DEFAULT ''");
    await AddColumnIfMissingAsync(db, "UserSettings", "BoostKeywordsCsv", "TEXT NOT NULL DEFAULT ''");
    await AddColumnIfMissingAsync(db, "UserSettings", "NoTradeMinutesAfterOpen", "INTEGER NOT NULL DEFAULT 0");
    await AddColumnIfMissingAsync(db, "UserSettings", "NoTradeMinutesBeforeClose", "INTEGER NOT NULL DEFAULT 0");
    await AddColumnIfMissingAsync(db, "UserSettings", "MinHoldMinutes", "INTEGER NOT NULL DEFAULT 0");
    await AddColumnIfMissingAsync(db, "UserSettings", "UseInsiderTransactions", "INTEGER NOT NULL DEFAULT 1");
    await AddColumnIfMissingAsync(db, "UserSettings", "UseGoogleNews", "INTEGER NOT NULL DEFAULT 1");
    await AddColumnIfMissingAsync(db, "UserSettings", "UseFomcMacro", "INTEGER NOT NULL DEFAULT 1");
    await AddColumnIfMissingAsync(db, "UserSettings", "CryptoUniverseCsv", "TEXT NOT NULL DEFAULT 'BTC/USD,ETH/USD,SOL/USD'");
    await AddColumnIfMissingAsync(db, "UserSettings", "LlamaModel", "TEXT NOT NULL DEFAULT 'llama-3.3-70b-versatile'");
    await AddColumnIfMissingAsync(db, "UserSettings", "ClaudeAdvisorMode", "INTEGER NOT NULL DEFAULT 0");
    await AddColumnIfMissingAsync(db, "UserDecisions", "PriceUsd", "TEXT NULL");
    await AddColumnIfMissingAsync(db, "UserOrders", "PriceAtSubmitUsd", "TEXT NULL");
    await AddColumnIfMissingAsync(db, "AspNetUsers", "AcceptedTermsAtUtc", "TEXT NULL");
    await AddColumnIfMissingAsync(db, "AspNetUsers", "AcceptedTermsVersion", "TEXT NOT NULL DEFAULT ''");

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "UserWatchlistEvents" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_UserWatchlistEvents" PRIMARY KEY AUTOINCREMENT,
            "UserId" TEXT NOT NULL,
            "AtUtc" TEXT NOT NULL,
            "Ticker" TEXT NOT NULL,
            "Source" TEXT NOT NULL,
            "BuzzScore" INTEGER NOT NULL,
            "Reason" TEXT NULL,
            "PriceUsd" TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_UserWatchlistEvents_UserId_Id" ON "UserWatchlistEvents" ("UserId", "Id");

        CREATE TABLE IF NOT EXISTS "InviteCodes" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_InviteCodes" PRIMARY KEY AUTOINCREMENT,
            "Code" TEXT NOT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "Note" TEXT NOT NULL,
            "RestrictedToEmail" TEXT NULL,
            "UsedAtUtc" TEXT NULL,
            "UsedByUserId" TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_InviteCodes_Code" ON "InviteCodes" ("Code");
        CREATE INDEX IF NOT EXISTS "IX_InviteCodes_UsedAtUtc" ON "InviteCodes" ("UsedAtUtc");

        CREATE TABLE IF NOT EXISTS "UserGateCalls" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_UserGateCalls" PRIMARY KEY AUTOINCREMENT,
            "UserId" TEXT NOT NULL,
            "AtUtc" TEXT NOT NULL,
            "Gate" TEXT NOT NULL,
            "ModelName" TEXT NOT NULL,
            "Ticker" TEXT NOT NULL,
            "Source" TEXT NOT NULL,
            "Headline" TEXT NOT NULL,
            "Prompt" TEXT NOT NULL,
            "RawResponse" TEXT NOT NULL,
            "Verdict" TEXT NOT NULL,
            "Reason" TEXT NOT NULL,
            "LatencyMs" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_UserGateCalls_UserId_Id" ON "UserGateCalls" ("UserId", "Id");
        """);

    if (await TableExistsAsync(db, "UserSettings"))
    {
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "UserSettings" SET "LlamaModel"='llama-3.3-70b-versatile'
                WHERE "LlamaModel" IN ('llama-3.1-8b-instant', 'openai/gpt-oss-20b', 'meta-llama/llama-4-scout-17b-16e-instruct');
            UPDATE "UserSettings" SET "MinConfidence"=0.85
                WHERE "MinConfidence"=0.80;
            UPDATE "UserSettings" SET "BearishNewsMinConfidence"=0.80
                WHERE "BearishNewsMinConfidence"=0.75;
            """);
    }

    logger.LogInformation("SQLite database schema ready");
}

static async Task AddColumnIfMissingAsync(OwlNestDbContext db, string table, string column, string definition)
{
    if (!IsSafeSqliteIdentifier(table) || !IsSafeSqliteIdentifier(column))
    {
        throw new InvalidOperationException("Unsafe SQLite identifier in schema migration.");
    }

    if (!await TableExistsAsync(db, table) || await ColumnExistsAsync(db, table, column))
    {
        return;
    }

    var sql = "ALTER TABLE " + SqliteIdent(table) + " ADD COLUMN " + SqliteIdent(column) + " " + definition + ";";
    await db.Database.ExecuteSqlRawAsync(sql);
}

static async Task<bool> TableExistsAsync(OwlNestDbContext db, string table)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        await connection.OpenAsync();
    }

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=$name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }
    finally
    {
        if (shouldClose)
        {
            await connection.CloseAsync();
        }
    }
}

static async Task<bool> ColumnExistsAsync(OwlNestDbContext db, string table, string column)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        await connection.OpenAsync();
    }

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({SqliteIdent(table)});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
    finally
    {
        if (shouldClose)
        {
            await connection.CloseAsync();
        }
    }
}

static string SqliteIdent(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

static bool IsSafeSqliteIdentifier(string identifier) =>
    !string.IsNullOrWhiteSpace(identifier) && identifier.All(c => char.IsLetterOrDigit(c) || c == '_');
