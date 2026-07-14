using Microsoft.AspNetCore.DataProtection;

namespace TradingBot.Web.Services;

/// <summary>
/// Encrypts/decrypts API keys at rest using ASP.NET Core Data Protection. The protector's key ring
/// lives on disk under the app data directory and is rotated automatically by the framework.
/// Never log the plaintext — only the protected ciphertext or the masked form.
/// </summary>
public sealed class ApiKeyProtector
{
    private const string Purpose = "owlnest.api-keys.v1";
    private readonly IDataProtector _protector;

    public ApiKeyProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string? Protect(string? plaintext) =>
        string.IsNullOrWhiteSpace(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext)) return null;
        try { return _protector.Unprotect(ciphertext); }
        catch { return null; }
    }

    /// <summary>Show only the last 4 chars so the user can confirm which key is saved without leaking the rest.</summary>
    public static string Mask(string? plaintext) =>
        string.IsNullOrWhiteSpace(plaintext) ? "(not set)"
        : plaintext.Length <= 4 ? "****"
        : "•••• •••• " + plaintext[^4..];
}
