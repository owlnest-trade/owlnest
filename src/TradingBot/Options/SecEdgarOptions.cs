namespace TradingBot.Options;

public sealed class SecEdgarOptions
{
    public const string SectionName = "SecEdgar";

    /// <summary>Master switch — if false the SEC provider is skipped entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// SEC EDGAR requires every API client to identify itself in the User-Agent. Set this to
    /// something like "TradingBot/1.0 (you@example.com)". If left blank the provider self-disables.
    /// </summary>
    public string ContactEmail { get; set; } = "";

    /// <summary>Base URL for the EDGAR JSON submissions API.</summary>
    public string BaseUrl { get; set; } = "https://data.sec.gov/";

    /// <summary>URL for the ticker → CIK map (downloaded once on first use).</summary>
    public string CompanyTickersUrl { get; set; } = "https://www.sec.gov/files/company_tickers.json";

    /// <summary>
    /// Which filing form types to surface as NewsItems. 8-K is the high-signal one (material events).
    /// 10-Q and 10-K are earnings reports. Add 6-K for foreign private issuers, S-1 for IPO/secondary, etc.
    /// </summary>
    public string[] Forms { get; set; } = ["8-K", "10-Q", "10-K"];
}
