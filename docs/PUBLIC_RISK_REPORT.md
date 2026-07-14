# Owlnest Public Risk and Liability Report

Last updated: 2026-06-18

This report is a plain-language risk disclosure for people reading, self-hosting, modifying, contributing to, or using Owlnest. It is not legal advice and does not replace a lawyer-drafted license, privacy policy, or terms of service.

## Summary

Owlnest is open-source automated trading software. It can read market news, ask AI models to classify headlines, apply configurable gates, and submit orders through a connected Alpaca account.

The project is shared for education, experimentation, inspection, and community development. It is not financial advice, investment advice, a signal service, portfolio management, brokerage, or a guarantee of profit.

Users and self-hosters are responsible for their own decisions, settings, infrastructure, API keys, accounts, and losses.

## Core Conditions

- Use Owlnest at your own risk.
- Start in paper mode.
- Do not enable live trading unless you fully understand the code, settings, logs, broker behavior, and risk.
- Do not use money you cannot afford to lose.
- Monitor the bot while it runs.
- Stop the bot immediately if behavior is unexpected.
- Keep secrets out of source control, logs, screenshots, issues, and support messages.
- Rotate any key, token, connection string, or broker credential that was exposed.

## No Financial Advice

Owlnest, its operators, maintainers, contributors, and affiliates are not brokers, investment advisers, financial planners, tax advisers, legal advisers, fiduciaries, or portfolio managers.

Nothing in the software, repository, documentation, examples, dashboards, reports, comments, pull requests, issues, contact replies, or managed-instance conversations should be treated as financial, investment, legal, tax, or trading advice.

Each user chooses:

- whether to run the software,
- whether to self-host or request a managed instance,
- which broker account to connect,
- whether to use paper or live mode,
- which symbols to trade,
- which AI/news providers to use,
- which gates and thresholds to apply,
- how much risk to accept,
- when to stop the bot.

## Trading Risk

Trading involves risk. Losses can happen quickly.

Specific risks include:

- AI misclassification,
- stale or false news,
- delayed news feeds,
- duplicated headlines,
- market gaps,
- slippage,
- wide spreads,
- partial fills,
- rejected orders,
- illiquidity,
- trading halts,
- broker restrictions,
- API outages,
- regulatory events,
- earnings surprises,
- crypto volatility,
- user misconfiguration.

Paper trading does not prove live profitability. Backtests and replays do not prove live profitability. Past results do not predict future returns.

## AI Risk

AI models can be wrong. They can:

- hallucinate facts,
- miss context,
- misread a headline,
- treat an old story as new,
- classify bearish news as bullish,
- approve a bad trade,
- veto a good trade,
- return malformed JSON,
- time out,
- refuse a request,
- produce inconsistent results across calls.

Using multiple models or verification gates can reduce some errors but cannot eliminate them.

## Replay and Backtest Limits

Owlnest includes replay/backtest features, but these reports are diagnostic tools, not predictions.

Replay may omit or simplify:

- real fill prices,
- slippage,
- fees,
- spreads,
- latency,
- liquidity,
- market impact,
- rejected broker orders,
- missing price snapshots,
- queueing behavior,
- live broker differences,
- data-provider errors.

Treat replay as evidence for investigation, not proof that a strategy is safe or profitable.

## Software Risk

Owlnest may contain bugs, incomplete logic, security flaws, bad assumptions, dependency issues, or operational weaknesses.

Examples include:

- incorrect order sizing,
- incorrect symbol handling,
- incorrect crypto quantity handling,
- missed exits,
- duplicate processing,
- stale settings,
- database issues,
- background-service failure,
- authentication mistakes,
- secrets-management mistakes,
- cloud deployment mistakes.

Open-source users and contributors should review the code carefully before trusting it.

## Infrastructure and Third-Party Risk

Owlnest depends on third-party systems, which may fail or change without notice.

Examples include:

- Alpaca,
- Finnhub,
- Google News,
- Google Gemini,
- Anthropic Claude,
- xAI Grok,
- Groq,
- Microsoft Azure,
- Postmark,
- Google OAuth,
- SEC EDGAR,
- Federal Reserve feeds,
- Manifold.

Third-party terms, privacy policies, rate limits, outages, billing changes, API changes, broker restrictions, and account actions are outside Owlnest's control.

## Self-Hosting Responsibility

Self-hosters are responsible for:

- deployment,
- database security,
- backups,
- API keys,
- OAuth secrets,
- broker credentials,
- data retention,
- monitoring,
- logs,
- updates,
- access control,
- firewall rules,
- compliance obligations,
- incident response.

If you self-host and make the instance public, you are responsible for how your users access it and what legal obligations apply to you.

## Managed Instance Responsibility

Managed hosting, if offered, is operational help only. It is not trading advice or account management.

Managed-instance users remain responsible for:

- their broker account,
- their API keys,
- their settings,
- their live-mode choice,
- their trades,
- their losses,
- their compliance obligations.

## No Warranty

Owlnest is provided "as is" and "as available."

To the maximum extent permitted by law, there are no warranties, express or implied, including warranties of:

- profitability,
- accuracy,
- uptime,
- security,
- merchantability,
- fitness for a particular purpose,
- non-infringement,
- error-free operation,
- uninterrupted access,
- suitability for live trading.

## Limitation of Liability

To the maximum extent permitted by law, Owlnest's operators, maintainers, contributors, affiliates, and service providers are not liable for:

- trading losses,
- lost profits,
- missed gains,
- missed trades,
- account losses,
- data loss,
- broker actions,
- API failures,
- AI/model errors,
- incorrect reports,
- infrastructure outages,
- security incidents,
- user misconfiguration,
- direct damages,
- indirect damages,
- incidental damages,
- consequential damages,
- special damages,
- exemplary damages,
- punitive damages.

If a jurisdiction does not allow some exclusions or limitations, the exclusions apply only to the maximum extent permitted by that jurisdiction.

## Public Release Checklist

Before announcing Owlnest publicly:

- Add a real open-source license file.
- Add `SECURITY.md`.
- Add `CONTRIBUTING.md` if contributions are welcome.
- Remove local databases and private runtime artifacts.
- Audit git history for secrets.
- Rotate exposed secrets and broker keys.
- Review screenshots for private account data.
- Confirm public signup and demo access are closed.
- Confirm live mode has explicit risk confirmation.
- Confirm README and Terms make risk clear.

## Recommended User Warning

Use this wording in release notes or repository announcements:

> Owlnest is experimental automated trading software. It is not financial advice and does not guarantee profit. Start in paper mode. Review the code and logs before trusting it. If you enable live trading, you can lose real money and you are solely responsible for the result.
