# Owlnest

Owlnest is an open-source, news-driven trading bot built with C# and .NET. It watches market news, scores headlines with AI, applies configurable safety gates, records every decision, and can route paper trades through a user's Alpaca account.

The main app is a multi-user ASP.NET Core Razor web app with dashboards, settings, encrypted API-key storage, diagnostics, and replay/backtest reports.

> Important: Owlnest is not financial advice. It can lose money. It defaults to Alpaca paper trading, but live trading is possible if explicitly enabled. Use paper mode until you understand the code, logs, and risks.
>
> Public signup and hosted demo access are closed. Run the open-source app yourself, or contact the maintainer if you are interested in a managed instance.
>
> See [docs/PUBLIC_RISK_REPORT.md](docs/PUBLIC_RISK_REPORT.md) before using or sharing the project publicly.
>
> For R&D evidence planning, see [docs/RND_ACTIVITY_REGISTER.md](docs/RND_ACTIVITY_REGISTER.md).

## Features

- Multi-user web dashboard with ASP.NET Core Identity.
- Alpaca paper/live trading integration.
- Stocks and crypto support. Crypto symbols use Alpaca format such as `BTC/USD`.
- News ingestion from Finnhub, Google News RSS, SEC EDGAR, insider transaction data, Fed feeds, and crypto news RSS.
- AI sentiment classification with Gemini by default.
- Optional Llama/Groq classifier support.
- Optional Grok and Claude verification gates.
- Configurable confidence, confirmation, position sizing, daily loss, trade-count, stop-loss, trailing-stop, take-profit, and max-hold controls.
- Dynamic ticker discovery from news buzz and optional Grok trending.
- Encrypted per-user Alpaca keys using ASP.NET Core Data Protection.
- Replay/backtest reports over stored decisions, orders, and price snapshots.
- Contact form and optional Postmark email notifications.

## Repository Layout

```text
src/
  TradingBot.Web/        Main ASP.NET Core Razor web app
    Data/                EF Core entities and Identity DbContext
    Pages/               Razor pages
    Services/            Bot host, broker/news/AI/replay services
    wwwroot/             CSS, JS, static assets
    Program.cs           App startup, DI, routes, DB bootstrap

  TradingBot/            Legacy single-user console/worker bot
    TradingWorker.cs     Console bot loop
    Services/            Broker, news, sentiment, risk, state services

TradingBot.slnx          Solution file
README.md                This file
```

## Requirements

Required:

- .NET 10 SDK.
- Alpaca account for paper trading keys.
- Finnhub API key.
- Google Gemini API key.

Optional:

- xAI Grok API key for Grok trending and second-opinion checks.
- Anthropic API key for Claude verification.
- Groq Cloud API key for Llama classifier mode.
- Google OAuth client ID/secret for Google login.
- Postmark account for contact-form email notifications.

## Safety Model

Owlnest has multiple layers of safety, but none removes market risk.

- Paper mode is the default.
- Live mode requires explicit user action on the API keys page.
- `TradingEnabled` must be on before orders are submitted.
- Position size is capped by `MaxPositionFraction`.
- Daily loss is capped by `MaxDailyLossFraction`.
- Max trades per day is capped by `MaxTradesPerDay`.
- Confirmation can require multiple matching signals before entry.
- Earnings blackout can block equity trades near scheduled earnings.
- Crypto skips equity-only gates such as regular market hours, SEC filings, Form 4, and earnings blackout.

Before using real money, run in paper mode for an extended period and review the decisions, orders, and replay reports.

## Quick Start: Web App

Clone and restore:

```powershell
git clone https://github.com/owlnest-trade/owlnest.git
cd owlnest
dotnet restore
dotnet build src\TradingBot.Web\TradingBot.Web.csproj
```

No database server is required. The web app uses SQLite by default and creates this local file on first run:

```text
src/TradingBot.Web/App_Data/owlnest.db
```

To store it somewhere else, set `OwlNest:SqlitePath` or `ConnectionStrings:OwlNest`.

Set required server-level API keys:

```powershell
dotnet user-secrets set "ServerKeys:Finnhub" "<your-finnhub-key>" --project src\TradingBot.Web\TradingBot.Web.csproj
dotnet user-secrets set "ServerKeys:Gemini" "<your-gemini-key>" --project src\TradingBot.Web\TradingBot.Web.csproj
```

Set optional server-level API keys:

```powershell
dotnet user-secrets set "ServerKeys:Grok" "<your-xai-grok-key>" --project src\TradingBot.Web\TradingBot.Web.csproj
dotnet user-secrets set "ServerKeys:Anthropic" "<your-anthropic-key>" --project src\TradingBot.Web\TradingBot.Web.csproj
dotnet user-secrets set "ServerKeys:Llama" "<your-groq-cloud-key>" --project src\TradingBot.Web\TradingBot.Web.csproj
```

Run the web app:

```powershell
dotnet run --project src\TradingBot.Web\TradingBot.Web.csproj
```

By default the app listens on:

```text
http://localhost:5001
```

The app creates database tables on first run. It also runs an idempotent additive schema script for columns/tables added after initial setup.

## Account Creation

The hosted public UI does not offer open signup or demo login. Existing accounts can still log in.

Self-hosters control their own instance. For local/private deployments, account creation remains invite-code based unless you change the auth flow.

## Creating an Invite Code For Self-Hosting

Until there is an admin UI, create invite codes directly in your SQLite database.

Example:

```sql
INSERT INTO InviteCodes (Code, CreatedAtUtc, Note, RestrictedToEmail, UsedAtUtc, UsedByUserId)
VALUES ('DEV-INVITE-CHANGE-ME', datetime('now'), 'Local development invite', NULL, NULL, NULL);
```

Use a long, unguessable code for public deployments.

## First User Setup For Self-Hosting

After account creation:

1. Open the API keys page.
2. Add Alpaca paper-trading key ID and secret.
3. Keep mode set to Paper.
4. Open Settings.
5. Confirm `Trading enabled` is off while testing.
6. Configure stock universe and crypto universe.
7. Run diagnostics.
8. Start the bot from the dashboard.
9. Review decisions and reports before enabling real order submission.

Per-user Alpaca keys are encrypted before storage. The server-level keys above are used by the platform for shared news and AI services.

## Core Configuration

Most user-facing settings are stored per user in the database and edited from the Settings page.

Important defaults:

| Setting | Default | Purpose |
| --- | --- | --- |
| `TradingEnabled` | `false` | Master switch for submitting orders. |
| `RegularHoursOnly` | `true` | Blocks equity entries outside regular US market hours. Crypto still runs 24/7. |
| `UniverseCsv` | major stocks and ETFs | Equity symbols to watch. |
| `CryptoUniverseCsv` | `BTC/USD,ETH/USD,SOL/USD` | Crypto symbols to watch. Empty disables crypto. |
| `MinConfidence` | `0.85` | Minimum AI confidence for entries. |
| `RequiredSignalCount` | `2` | Number of matching bullish signals required before entry. |
| `ConfirmationWindowMinutes` | `120` | Window for matching signals. |
| `MaxPositionFraction` | `0.025` | Max fraction of equity per position. |
| `MaxDailyLossFraction` | `0.02` | Daily loss kill switch. |
| `MaxTradesPerDay` | `15` | Daily order cap. |
| `StopLossType` | `Both` | Hard stop plus trailing stop. |
| `TrailingStopPercent` | `0.015` | Trailing stop pullback amount. |
| `TrailingStopActivationPercent` | `0.03` | Gain required before trailing stop activates. |
| `BearishNewsExitsEnabled` | `true` | Allows bearish news to close held positions. |

## Server Configuration Reference

Use .NET user-secrets for local development and environment variables or your host's configuration system in production.

Required:

```text
ServerKeys:Finnhub
ServerKeys:Gemini
```

Optional:

```text
ServerKeys:Grok
ServerKeys:Anthropic
ServerKeys:Llama
ServerKeys:LlamaModel
ServerKeys:GeminiModel
ServerKeys:AnthropicModel
ServerKeys:PostmarkServerToken
ServerKeys:PostmarkFromEmail
ServerKeys:PostmarkToEmail
Authentication:Google:ClientId
Authentication:Google:ClientSecret
ConnectionStrings:OwlNest
OwlNest:SqlitePath
OwlNest:ResetDb
OWLNEST_RESET_DB
```

Do not put real secrets in `appsettings.json`.

## Database Notes

The web app uses EF Core with SQLite by default.

- `EnsureCreated()` creates the initial schema.
- `Program.cs` contains idempotent SQLite schema catch-ups for later columns/tables.
- There are no formal EF migrations yet.
- The replay/backtest harness reads stored `UserDecisions`, `UserOrders`, gate calls, watchlist events, and price snapshots.

For larger production instances, consider moving old raw prompt/response/history data to cheaper archive storage and keeping only recent replay data in SQLite.

## Deployment Notes

Typical self-hosted deployment:

1. Create an app host or VPS with persistent disk storage.
2. Configure required `ServerKeys:*`.
3. Optionally configure `OwlNest:SqlitePath` to point at a persistent volume.
4. Configure Google OAuth and Postmark only if needed.
5. Deploy the web app.
6. Open the site once so the database bootstrap runs.
7. Insert an invite code.
8. Create the first account.
9. Add Alpaca paper keys from the UI.

Operational notes:

- App restart auto-resumes users marked as bot-running.
- Stop running bots before risky config/deployment changes.
- Persist ASP.NET Core Data Protection keys for serious production or multi-instance deployments. If the key ring is lost, stored encrypted Alpaca keys may no longer decrypt.
- Do not set `OWLNEST_RESET_DB=true` in production unless you intend to drop all app tables.

## Legacy Single-User Console Bot

The older single-user worker still exists under `src/TradingBot`.

Run it with:

```powershell
dotnet run --project src\TradingBot\TradingBot.csproj
```

Set secrets for the console project:

```powershell
dotnet user-secrets set "Alpaca:KeyId" "<your-alpaca-paper-key-id>" --project src\TradingBot\TradingBot.csproj
dotnet user-secrets set "Alpaca:SecretKey" "<your-alpaca-paper-secret>" --project src\TradingBot\TradingBot.csproj
dotnet user-secrets set "Finnhub:ApiKey" "<your-finnhub-key>" --project src\TradingBot\TradingBot.csproj
dotnet user-secrets set "Gemini:ApiKey" "<your-gemini-key>" --project src\TradingBot\TradingBot.csproj
```

Use the web app for new development unless you specifically need the console worker.

## Replay and Backtesting

The Reports page includes a replay backtest. It compares actual submitted orders against shadow portfolios such as:

- baseline sentiment strategy
- no confirmation gate
- no Grok gate
- no Claude gate
- macro-tagged decisions

The replay is only as good as the stored data. Keep decision/order/price history if you want meaningful reports.

## Development Workflow

Build:

```powershell
dotnet build
```

Run web app:

```powershell
dotnet run --project src\TradingBot.Web\TradingBot.Web.csproj
```

Common checks:

```powershell
dotnet build src\TradingBot.Web\TradingBot.Web.csproj
dotnet build src\TradingBot\TradingBot.csproj
```

## Open Source Readiness Checklist

Before making the repository public, review this list:

- Add a real license file, such as MIT or Apache-2.0. Without a license, the code is not practically open source.
- Remove committed local databases or runtime artifacts from git history if they contain private data.
- Audit publish profiles and deployment files for subscription IDs, resource names, or secrets.
- Rotate any key that was ever committed, pasted into logs, or stored in a tracked file.
- Confirm `.gitignore` excludes local DB files, logs, build output, user files, and secrets.
- Add `CONTRIBUTING.md` if external contributions are expected.
- Add `SECURITY.md` with a private security contact if the repo will be public.
- Add screenshots only if they do not reveal account IDs, keys, emails, or trading history.
- Review [docs/PUBLIC_RISK_REPORT.md](docs/PUBLIC_RISK_REPORT.md) and the hosted Terms page before announcing the project.

## Security

- Never commit API keys.
- Never commit production connection strings.
- Keep Alpaca in paper mode during testing.
- Treat all trading logs and decision data as sensitive.
- Review contact messages and user emails before sharing database exports.
- Use separate API keys for development and production.

## Contributing

Issues and pull requests are welcome once the repository is public. Keep changes small and focused. For trading logic changes, include the reasoning, expected behavior, and any replay/backtest evidence when possible.

## License

No open-source license has been selected yet. Choose and add a `LICENSE` file before announcing the repository as open source.
