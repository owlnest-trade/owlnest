# Owlnest R&D Activity Register

Snapshot date: 2026-06-28

This register is a working evidence document for explaining the experimental work behind Owlnest. It is not tax, legal, or accounting advice. Before lodging an Australian R&D Tax Incentive claim, have an R&D adviser or tax professional review the activity descriptions, dates, costs, and evidence.

## R&D Position

Owlnest should not be described as merely "using AI for trading". The stronger R&D position is:

> We experimentally developed a modular trading decision engine to test whether noisy news, AI classification, independent verification gates, macro context, risk controls, broker execution constraints, and replay/shadow-portfolio analysis could be combined into measurable automated trading decisions.

The technical uncertainty was not whether an AI API or broker API could be called. The uncertainty was whether the interaction of the modules could produce useful, explainable, and measurable trade decisions without causing excessive false positives, missed trades, duplicated orders, blocked crypto execution, or unsafe broker behaviour.

## Experiment Method

For each experiment, keep evidence showing:

- Hypothesis stated before or during the work.
- Code snapshot or commit.
- Input data used, such as news, decisions, orders, price snapshots, or gate calls.
- Observation from logs, replay output, or reports.
- Evaluation against baseline or shadow portfolio.
- Conclusion: keep, change, disable, tune, or retest.

## Task Register

| ID | Task | Est. hrs | R&D role | Description | Code snapshot / evidence |
| --- | ---: | ---: | --- | --- | --- |
| T1 | News ingestion, 6 sources | 60 | Supporting | Built a multi-source news ingestion layer to create the experimental input stream for AI and rules. | `src/TradingBot/Services/News/CompositeNewsProvider.cs`, `FinnhubNewsProvider.cs`, `SecEdgarNewsProvider.cs`, `src/TradingBot.Web/Services/UserBot/UserGoogleNewsFeed.cs`, `UserCryptoNewsFeed.cs`, `UserInsiderFeed.cs`, `src/TradingBot.Web/Services/Shared/FedFeed.cs` |
| T2 | AI sentiment classification | 50 | Core | Tested whether AI could classify news into actionable bullish, bearish, or neutral trading signals with confidence and reasoning. | `src/TradingBot.Web/Services/UserBotInstance.cs`, `src/TradingBot/Services/Sentiment/SentimentPrompts.cs`, `GeminiSentimentAnalyzer.cs`, `ClaudeSentimentAnalyzer.cs` |
| T3 | Modular decision-gate pipeline | 80 | Core | Created sequential gates for sentiment, confirmation, earnings/no-trade windows, Grok, Claude, price, sizing, and broker submission. | `src/TradingBot.Web/Services/UserBotInstance.cs`, `src/TradingBot.Web/Data/UserDecision.cs`, `src/TradingBot.Web/Data/UserSettings.cs` |
| T4 | Confirmation-window signal tracker | 25 | Core | Tested whether requiring multiple matching signals inside a time window reduces false positives or over-blocks trades. | `src/TradingBot.Web/Services/UserBot/UserSignalTracker.cs`, `src/TradingBot/Services/Risk/ActionableSignalTracker.cs` |
| T5 | Grok gate | 30 | Core | Tested independent Grok verification as a second-opinion gate before order submission. | `src/TradingBot.Web/Services/UserBot/UserGrokConfirmation.cs`, `src/TradingBot.Web/Data/UserGateCall.cs` |
| T6 | Claude gate | 30 | Core | Tested Claude verification and advisor/shadow mode to measure whether Claude vetoes helped or hurt trade outcomes. | `src/TradingBot.Web/Services/UserBot/UserClaudeVerification.cs`, `src/TradingBot.Web/Data/UserSettings.cs`, `src/TradingBot.Web/Data/UserGateCall.cs` |
| T7 | Earnings blackout and no-trade window | 20 | Supporting / Core depending on experiment | Added time-based and event-based trade blockers, then tested whether they reduced unsafe entries or blocked valid trades. | `src/TradingBot.Web/Services/UserBot/UserEarningsCalendar.cs`, `src/TradingBot/Services/Risk/EarningsCalendar.cs`, `src/TradingBot.Web/Services/UserBotInstance.cs` |
| T8 | Risk manager | 55 | Core | Developed pre-trade risk gates for trading enabled, confidence, price sanity, daily loss cap, daily trade cap, sell eligibility, and position sizing. | `src/TradingBot/Services/Risk/RiskManager.cs`, `src/TradingBot.Web/Services/UserBotInstance.cs`, `src/TradingBot.Web/Data/UserSettings.cs` |
| T9 | Macro provider and tagging | 40 | Core | Tested whether macro prediction-market and central-bank context could explain or filter trading signals. | `src/TradingBot/Services/Macro/`, `src/TradingBot.Web/Services/Shared/ManifoldFeed.cs`, `src/TradingBot.Web/Services/Shared/FedFeed.cs`, `src/TradingBot.Web/Services/Backtesting/BacktestReplayService.cs` |
| T10 | Dynamic ticker discovery | 35 | Supporting / Core depending on experiment | Developed buzz and AI-driven ticker discovery to test whether the bot could expand beyond a fixed watchlist without degrading signal quality. | `src/TradingBot/Services/Discovery/`, `src/TradingBot.Web/Services/UserBot/UserDiscovery.cs`, `src/TradingBot.Web/Data/UserWatchlistEvent.cs` |
| T11 | Alpaca broker execution | 45 | Supporting | Integrated broker execution and order persistence so experimental decisions could be compared with actual order outcomes. | `src/TradingBot/Services/Broker/AlpacaBroker.cs`, `src/TradingBot/Services/Broker/IBroker.cs`, `src/TradingBot.Web/Services/UserBotInstance.cs`, `src/TradingBot.Web/Data/UserOrder.cs` |
| T12 | Crypto asset eligibility and execution | 40 | Core | Tested separate crypto eligibility and execution logic because crypto differs from equities in trading hours, symbol shape, fractional sizing, and order duration. | `src/TradingBot.Web/Services/UserBotInstance.cs`, `src/TradingBot.Web/Services/UserBot/UserCryptoNewsFeed.cs`, `src/TradingBot.Web/Services/TierPolicy.cs` |
| T13 | Persistence layer | 35 | Supporting | Persisted decisions, orders, gate calls, equity snapshots, settings, and watchlist events so experiments were auditable and replayable. | `src/TradingBot.Web/Data/OwlNestDbContext.cs`, `UserDecision.cs`, `UserOrder.cs`, `UserGateCall.cs`, `UserEquitySnapshot.cs`, `UserWatchlistEvent.cs` |
| T14 | Replay/backtest harness | 50 | Core | Replayed stored decisions, orders, and price snapshots to compare actual outcomes against counterfactual strategy variants. | `src/TradingBot.Web/Services/Backtesting/BacktestReplayService.cs`, `src/TradingBot.Web/Pages/Reports.cshtml.cs`, `src/TradingBot.Web/Pages/Reports.cshtml` |
| T15 | Shadow-portfolio attribution | 40 | Core | Created actual, baseline, no-confirmation, no-Grok, no-Claude, and macro shadow portfolios to measure which filters helped or harmed P&L. | `src/TradingBot.Web/Services/Backtesting/BacktestReplayService.cs` |
| T16 | Multi-user web app and infrastructure | 90 | Supporting | Built user isolation, settings, dashboards, API-key encryption, diagnostics, and reports to run and observe experiments safely per user. | `src/TradingBot.Web/Program.cs`, `src/TradingBot.Web/Services/UserBotHost.cs`, `src/TradingBot.Web/Services/UserBotInstance.cs`, `src/TradingBot.Web/Pages/` |
| T17 | Experiment design and hypothesis register | 20 | Core evidence | Converted engineering tasks into testable hypotheses, observations, and conclusions. | `docs/RND_ACTIVITY_REGISTER.md`, replay reports, commit notes, issue notes |
| T18 | Price snapshot and market-state capture | 35 | Core evidence | Stored prices at decision, order submission, watchlist promotion, and equity snapshot times to make replay measurable. | `src/TradingBot.Web/Data/UserDecision.cs`, `UserOrder.cs`, `UserWatchlistEvent.cs`, `UserEquitySnapshot.cs`, `src/TradingBot.Web/Services/UserBotInstance.cs` |
| T19 | Decision audit logging and explainability | 40 | Core evidence | Recorded pass/fail outcomes and reasons for each decision gate so blocked trades could be attributed to a specific module. | `src/TradingBot.Web/Data/UserDecision.cs`, `src/TradingBot.Web/Data/UserGateCall.cs`, `src/TradingBot.Web/Services/UserBotInstance.cs` |
| T20 | Gate threshold tuning experiments | 35 | Core | Tested how confidence, signal count, confirmation window, daily loss, trade count, and no-trade windows affect missed trades and false positives. | `src/TradingBot.Web/Data/UserSettings.cs`, `src/TradingBot.Web/Pages/Settings.cshtml`, `src/TradingBot.Web/Services/Backtesting/BacktestReplayService.cs` |
| T21 | P&L evaluation metrics and reports | 35 | Core evidence | Reported trade count, P&L, delta versus actual, fill rate, and slippage so each gate could be evaluated. | `src/TradingBot.Web/Services/Backtesting/BacktestReplayService.cs`, `src/TradingBot.Web/Pages/Reports.cshtml` |
| T22 | Order reconciliation and broker-state validation | 30 | Supporting | Synced broker order state back into stored orders to compare intended orders with fills, cancellations, and slippage. | `src/TradingBot.Web/Services/UserBotInstance.cs`, `src/TradingBot.Web/Data/UserOrder.cs` |
| T23 | Data quality normalization | 30 | Supporting | Removed duplicate news, cached/normalized identifiers, handled stale or missing prices, and separated equity from crypto symbols. | `src/TradingBot/Services/State/ProcessedNewsStore.cs`, `src/TradingBot/Services/News/CikCache.cs`, `src/TradingBot.Web/Services/Shared/SecCikCache.cs`, `src/TradingBot.Web/Services/UserBotInstance.cs` |
| T24 | Prompt/model version tracking | 25 | Core evidence | Persisted model names, prompts, raw responses, verdicts, reasons, and latency to compare AI behaviour over time. | `src/TradingBot.Web/Data/UserGateCall.cs`, `src/TradingBot.Web/Services/UserBotInstance.cs`, `src/TradingBot.Web/Services/UserBot/UserGrokConfirmation.cs`, `UserClaudeVerification.cs` |
| T25 | Safety controls, kill switch, and max-loss guards | 30 | Supporting / Core depending on experiment | Added master trading enablement, paper default, daily loss, trade count, position cap, and dashboard start/stop controls. | `src/TradingBot/Services/Risk/RiskManager.cs`, `src/TradingBot.Web/Pages/Dashboard.cshtml.cs`, `src/TradingBot.Web/Pages/Keys.cshtml.cs`, `src/TradingBot.Web/Data/UserSettings.cs` |
| T26 | Test harness and regression checks | 35 | Supporting | Added repeatable diagnostics and replay-style validation to catch provider failures, source failures, and decision-regression risk. | `src/TradingBot.Web/Services/Diagnostics/SourceTester.cs`, `src/TradingBot.Web/Pages/Diagnostics.cshtml.cs`, `src/TradingBot.Web/Services/Backtesting/BacktestReplayService.cs` |
| T27 | Deployment and monitoring experiments | 25 | Supporting | Ran the bot as a hosted multi-user service and monitored live-vs-replay differences, token/provider failures, and operational execution gaps. | `src/TradingBot.Web/Program.cs`, `src/TradingBot.Web/Services/UserBotHost.cs`, `src/TradingBot.Web/Services/ContactNotifier.cs`, `src/TradingBot.Web/Pages/Dashboard.cshtml` |
| T28 | Public release, legal, and risk documentation | 20 | Supporting / commercialisation | Prepared public risk disclosure, README instructions, signup/demo closure, and liability language for community release. | `README.md`, `docs/PUBLIC_RISK_REPORT.md`, `src/TradingBot.Web/Pages/Terms.cshtml`, `src/TradingBot.Web/Pages/Contact.cshtml` |
| T29 | Provider failure resilience and routing experiments | 25 | Core | Tested whether cross-provider fallback (Gemini → Llama on Groq), client-side HTTP routing fixes (dedicated `HttpClient` per provider to avoid `BaseAddress` collisions), and reasoning-token mitigation (`thinkingConfig.thinkingBudget = 0` for Gemini 2.5) preserve decision-engine throughput when an individual AI provider throttles, returns 4xx/5xx, applies a safety block, or changes hidden quota behaviour. Each failure mode is logged in `UserGateCalls` with the original provider's error reason captured before fallback. | `src/TradingBot.Web/Services/UserBotInstance.cs`, `src/TradingBot.Web/Services/UserBot/UserClaudeVerification.cs`, `src/TradingBot.Web/Services/UserBot/UserGrokConfirmation.cs`, `src/TradingBot.Web/Data/UserGateCall.cs` |
| T30 | Live operations observability and cost telemetry | 20 | Core evidence | Captured per-call latency, model name, prompt, raw response, and error reason for each gate call so provider regressions, billing exhaustions, and per-trade verification cost could be diagnosed empirically rather than from anecdote. Surfaced on the Dashboard as a click-to-expand audit tile and queried via SQL for cross-day rate-of-failure comparison. | `src/TradingBot.Web/Data/UserGateCall.cs`, `src/TradingBot.Web/Pages/Dashboard.cshtml`, `src/TradingBot.Web/Services/UserBotInstance.cs` |

Indicative total: 1,130 hours. (T29 + T30 added 2026-06-10 — adjust the line above before lodging if hour estimates change after adviser review.)

## Code Snapshots

These are representative code snapshots. Keep full commit hashes and dates with the final claim file.

### Snapshot A: Decision Gate Pipeline

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
var bullishOK = verdict.Sentiment.Equals("bullish", StringComparison.OrdinalIgnoreCase)
    && verdict.Actionable
    && effectiveConfidence >= _settings.MinConfidence;

if (!bullishOK && !bearishOK)
{
    db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "NoTradeGate",
        $"Sentiment gate ({verdict.Sentiment}, {verdict.Confidence:P0}, actionable={verdict.Actionable})",
        priceUsd: priceSnapshot));
    await db.SaveChangesAsync(ct);
    continue;
}
```

Description: This shows the decision engine did not simply follow AI output. The AI verdict was converted into an auditable gate result with a stored reason and price snapshot.

### Snapshot B: Confirmation Window Experiment

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
if (bullishOK && _settings.RequiredSignalCount > 1 && _signals is not null)
{
    var count = _signals.RecordAndCount(ticker, "bullish", DateTimeOffset.UtcNow);
    if (count < _settings.RequiredSignalCount)
    {
        db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "NoTradeGate",
            $"Confirmation gate: {count}/{_settings.RequiredSignalCount} bullish signals in last {_settings.ConfirmationWindowMinutes}m",
            priceUsd: priceSnapshot));
        await db.SaveChangesAsync(ct);
        continue;
    }
}
```

Description: This supports the experiment testing whether one news signal was too noisy and whether multiple signals inside a time window improved decision quality.

### Snapshot C: Grok and Claude Verification Gates

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
await SaveGateCallAsync("Grok", gc.ModelName, ticker, a, gc.Verdict.ToString(),
    gc.Reason, gc.Prompt, gc.RawResponse, gc.LatencyMs, ct);

if (gc.Verdict != GrokVerdict.Approve)
{
    db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
        $"Grok {gc.Verdict}: {gc.Reason}", priceUsd: priceSnapshot));
    await db.SaveChangesAsync(ct);
    continue;
}
```

Description: This shows the independent AI gate was tested as a measurable module, with prompt, response, verdict, reason, and latency persisted.

### Snapshot D: Claude Advisor / Shadow Veto

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
if (cv.Verdict != ClaudeVerdict.Approve)
{
    if (_settings.ClaudeAdvisorMode)
    {
        claudeShadowVetoNote = $"[Claude shadow {cv.Verdict}: {Truncate(cv.Reason, 200)}]";
    }
    else
    {
        db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
            $"Claude {cv.Verdict}: {cv.Reason}", priceUsd: priceSnapshot));
        await db.SaveChangesAsync(ct);
        continue;
    }
}
```

Description: This is strong R&D evidence because it tests two modes: Claude as a hard blocker and Claude as an advisory/shadow signal.

### Snapshot E: Replay and Shadow Portfolios

Evidence file: `src/TradingBot.Web/Services/Backtesting/BacktestReplayService.cs`

```csharp
var portfolios = new[] { actualOrders, baseline, noConfirmation, noGrok, noClaude, macroTagged };
var actualPnl = actualOrders.Pnl;
portfolios = portfolios
    .Select(p => p with { DeltaVsActual = p.Id == actualOrders.Id ? null : p.Pnl - actualPnl })
    .ToArray();
```

Description: This shows the project measured actual trading outcomes against counterfactual strategy variants, rather than relying on a subjective claim that a gate was useful.

### Snapshot F: Replay Data Sources

Evidence file: `src/TradingBot.Web/Services/Backtesting/BacktestReplayService.cs`

```csharp
var decisions = await _db.UserDecisions.AsNoTracking()
    .Where(d => d.UserId == userId)
    .OrderByDescending(d => d.Id)
    .Take(limit)
    .ToListAsync(ct);

var ordersQuery = _db.UserOrders.AsNoTracking()
    .Where(o => o.UserId == userId);
```

Description: This supports the replay harness claim: the system reuses stored decisions and orders to reconstruct experimental outcomes.

### Snapshot G: Price Snapshot Capture

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
var priceSnapshot = await GetLatestPriceAsync(ticker, ct);

db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "NoTradeGate",
    $"Sentiment gate ({verdict.Sentiment}, {verdict.Confidence:P0}, actionable={verdict.Actionable})",
    priceUsd: priceSnapshot));
```

Description: This records the market price at decision time, which is necessary for later replay and missed-trade analysis.

### Snapshot H: Order Submission Evidence

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
db.UserOrders.Add(new UserOrder
{
    UserId = _userId,
    OrderId = submitted.OrderId.ToString(),
    Ticker = ticker,
    Side = side == OrderSide.Buy ? "Buy" : "Sell",
    Quantity = qty,
    Status = "new",
    SubmittedAtUtc = DateTimeOffset.UtcNow,
    Reason = reason,
    PriceAtSubmitUsd = priceAtSubmit,
});
```

Description: This links the experimental decision process to broker execution evidence and later reconciliation.

### Snapshot I: Crypto Execution Difference

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
var order = side == OrderSide.Buy
    ? MarketOrder.Buy(ticker, quantity)
    : MarketOrder.Sell(ticker, quantity);
var submitted = await _trading.PostOrderAsync(order.WithDuration(TimeInForce.Gtc), ct);
```

Description: Crypto required a separate execution pathway because it uses different quantity semantics and time-in-force behaviour from equity orders.

### Snapshot J: Risk Manager Gates

Evidence file: `src/TradingBot/Services/Risk/RiskManager.cs`

```csharp
if (!_opts.TradingEnabled)
    return Reject("Trading disabled by config (Trading:TradingEnabled=false)");

if (sentiment.Confidence < _opts.MinConfidence)
    return Reject($"Confidence {sentiment.Confidence:P0} below threshold {_opts.MinConfidence:P0}");

if (tradesToday >= _opts.MaxTradesPerDay)
    return Reject($"Daily trade count cap hit ({tradesToday}/{_opts.MaxTradesPerDay})");
```

Description: This shows deterministic risk controls were experimentally layered with AI signals rather than allowing AI to directly place trades.

### Snapshot K: Gate Call Audit Trail

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
db.UserGateCalls.Add(new UserGateCall
{
    UserId = _userId,
    AtUtc = DateTimeOffset.UtcNow,
    Gate = gate,
    ModelName = modelName ?? "",
    Ticker = ticker,
    Prompt = Truncate(prompt, 8000),
    RawResponse = Truncate(rawResponse, 8000),
    Verdict = verdict ?? "",
    Reason = reason ?? "",
    LatencyMs = latencyMs,
});
```

Description: This is evidence for prompt/model tracking and explains how AI behaviour can be reviewed after the fact.

### Snapshot L: Model Fallback Experiment

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
await SaveGateCallAsync("LlamaFallback", _llamaModel, ticker,
    new Article(Id: "", Source: geminiFailReason,
        Headline: headline, Summary: summary, Url: "",
        PublishedAt: DateTimeOffset.UtcNow),
    auditVerdict, auditReason, user, fbRawResponse, fbLatencyMs, ct);
```

Description: This supports experimentation around model-provider failure, safety blocks, and fallback classification.

### Snapshot M: Cross-Provider Fallback Consolidation

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
string? geminiFailReason = null;
if (!resp.IsSuccessStatusCode)
    geminiFailReason = $"Gemini HTTP {(int)resp.StatusCode}: {Truncate(raw, 200)}";
else
{
    var safetyReason = DetectGeminiBlock(raw);
    if (safetyReason is not null)
        geminiFailReason = $"Gemini blocked: {safetyReason}";
}
if (geminiFailReason is not null && _llamaFallbackHttp is not null)
{
    // route ALL Gemini failures (429/safety/empty) through Llama, not just safety blocks
}
```

Description: Evidence for the experiment that *any* Gemini failure mode (HTTP 429 rate-limit, HTTP 5xx outage, safety filter trigger) should route to the Llama fallback rather than only the safety-block path. The consolidation was driven by observed behaviour: 23 Gemini HTTP-429 events in one 24h window during NVDA news bursts were silently dropped before the fallback was widened.

### Snapshot N: Reasoning-Token Budget Mitigation

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
generationConfig = new {
    maxOutputTokens = 2000,
    temperature = 0.0,
    thinkingConfig = new { thinkingBudget = 0 }
}
```

Description: Evidence for the experiment that gemini-2.5-flash emits hidden "thinking" tokens that consume the visible `maxOutputTokens` budget, returning empty `parts[]` arrays under load. Mitigation: set `thinkingBudget = 0` to disable internal chain-of-thought and reserve the full output budget for the JSON response. Verified by direct API probe (45-token prompt → 103 content + 191 thinking tokens before fix).

### Snapshot O: Dedicated HttpClient Routing Fix

Evidence file: `src/TradingBot.Web/Services/UserBotInstance.cs`

```csharp
// Dedicated Anthropic client — previously _claudeVerify shared _llmHttp whose BaseAddress
// was pinned to generativelanguage.googleapis.com (because LlmProvider="Gemini"), causing
// every Claude call to hit https://generativelanguage.googleapis.com/v1/messages → HTTP 404.
_claudeHttp = new HttpClient { BaseAddress = new Uri("https://api.anthropic.com/"), Timeout = TimeSpan.FromSeconds(60) };
_claudeHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
_claudeVerify = new UserClaudeVerification(_claudeHttp, _anthropicKey!, _anthropicModel, _log);
```

Description: Evidence for an instrumentation-driven debugging experiment. The 21/21 Claude-gate failure rate across a 24h window was traced — using stored `UserGateCall.LatencyMs` and `RawResponse` data — to a shared `HttpClient` whose `BaseAddress` was pinned to whichever provider had been first-instantiated. Per-provider `HttpClient` isolation restored Claude latency from ~89 ms (404 fast-fail) to ~21–30 s (real web-search call).

## Experimental Discoveries / Negative Results

Negative results (the experiment-failed-but-we-learned-something cases) are some of the strongest R&D evidence because they show genuine technical uncertainty rather than a foregone engineering build. Each item below has commit notes, DB rows, and/or live-API probes as supporting evidence.

| Date | Discovery | Hypothesis tested | Outcome | Action taken |
| --- | --- | --- | --- | --- |
| 2026-06-09 | Gemini 2.5 Flash emits hidden "thinking" tokens that consume `maxOutputTokens`, returning empty `parts[]` under load. | "A 2k-token output budget is sufficient for ~500-char JSON verdicts." | False. Thinking tokens consumed 60–90 % of the budget on long prompts. | Added `thinkingConfig.thinkingBudget = 0` (Snapshot N). |
| 2026-06-09 | Llama 4 Scout (17B MoE) on Groq, in strict-JSON mode, truncates at ~46 chars regardless of `max_tokens`. | "Scout's lower per-token price would make it the most cost-efficient fallback." | False — strict-JSON mode triggered premature-stop bug; outputs were unparseable. | Switched fallback default to `llama-3.3-70b-versatile` and dropped `response_format = json_object`. |
| 2026-06-09 | Shared `HttpClient` whose `BaseAddress` was pinned to Google caused every Claude verification call to return HTTP 404 in ~89 ms. | "One `HttpClient` per process is sufficient; provider differences can be expressed at request time." | False — `HttpClient.BaseAddress` is set-once and silently overrides absolute URIs only when combined with relative request paths, but headers also carried over. | Per-provider `HttpClient` isolation (Snapshot O). |
| 2026-06-09 | Gemini paid-tier credits depleted silently — failures returned the credit-depleted error rather than an explicit billing event. | "Paid-tier API access tolerates burst load without operator intervention." | False at our usage profile (~500 articles/day × 7 sources). | Widened Llama rescue (Snapshot M) and noted alerting gap for future work. |
| 2026-06-10 | Grok HTTP 403 (`team has used all available credits`) treated as a Veto by the decision pipeline, blocking all 7 bullish entries that survived sentiment. | "An error from a verification gate is equivalent to a Veto for safety." | Disputed — conflates uncertainty with rejection. Open experimental question whether `Error → Skip-this-gate` improves throughput without harming precision. | Recorded as open hypothesis for the next experiment cycle. |
| 2026-06-09 | Claude (Sonnet 4.5 with web_search) vetoed 4 of 4 Grok-approved entries. Intraday outcome: 3 of 4 vetoes correct (−1.6 to −1.9 %), 1 incorrect (+2.4 %). | "Claude verification adds alpha vs. Grok-only baseline." | Provisionally true on n=4; statistically insignificant alone. Drove the design of advisor/shadow mode to gather n≥30 before concluding. | Shipped `ClaudeAdvisorMode` (Snapshot D) to collect counterfactual data without blocking trades. |
| 2026-06-09 | Postmark account in "pending approval" mode silently restricted deliverability to the same domain — emails to gmail.com were accepted but never delivered. | "Postmark works out-of-the-box for transactional notifications." | False under pending-approval state. | Documented restriction and identified domain-verification as a release blocker. |

## Suggested R&D Wording By Activity

### Core experimental wording

Use this style for the stronger tasks:

```text
The activity involved testing whether a modular decision gate improved automated trading outcomes compared with a simpler baseline. The experiment required storing decisions, gate reasons, orders, and price snapshots, then replaying the data to compare actual outcomes against counterfactual portfolios.
```

### Supporting activity wording

Use this style for infrastructure tasks:

```text
The activity supported the core experiments by collecting input data, preserving experimental state, enabling repeatable replay, or allowing safe execution and monitoring of the decision engine.
```

## Evidence Still Worth Adding

Add these before lodging or giving the pack to an adviser:

- Commit hashes for each task.
- Approximate start and finish dates.
- Screenshots or exports from Reports showing shadow-portfolio results.
- Example `UserDecision` rows showing each gate outcome.
- Example `UserGateCall` rows showing Grok/Claude prompt, verdict, and reason.
- Example `UserOrder` rows showing submitted/fill status and price-at-submit.
- A short conclusion for each experiment, especially where a gate was disabled or changed.
- Timesheets or reasonable developer-hour records matching the task estimates.

