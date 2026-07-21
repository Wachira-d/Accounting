namespace Accounting.Helpers;

/// <summary>
/// Wraps <see cref="EncryptionHelper"/> with the configured key from
/// <c>Security:EncryptionKey</c> so call-sites don't have to know about
/// the key. Use for sensitive fields stored in DB (SMTP password, OAuth
/// client secrets, refresh tokens, RD API secret, etc).
///
/// Transparent over legacy plaintext: <see cref="Unprotect"/> returns the
/// input unchanged if it doesn't look like our ciphertext, so old rows
/// keep working until the next save re-writes them encrypted.
/// </summary>
public interface ISecretProtector
{
    string? Protect(string? plaintext);
    string? Unprotect(string? value);
}

public class SecretProtector : ISecretProtector
{
    private readonly string _key;
    private readonly ILogger<SecretProtector> _logger;

    public SecretProtector(IConfiguration config, ILogger<SecretProtector> logger)
    {
        _key = config["Security:EncryptionKey"] ?? "default-dev-key-change-in-production";
        _logger = logger;
        if (_key == "default-dev-key-change-in-production")
            _logger.LogWarning("Security:EncryptionKey not configured — using the development default. Set ENCRYPTION_KEY before going to production.");
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        // Idempotent: don't double-encrypt
        if (EncryptionHelper.IsEncrypted(plaintext)) return plaintext;
        return EncryptionHelper.Encrypt(plaintext, _key);
    }

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Legacy plaintext rows — return as-is so the app keeps working
        // until the next save migrates the value to ciphertext.
        if (!EncryptionHelper.IsEncrypted(value)) return value;
        try { return EncryptionHelper.Decrypt(value, _key); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt protected secret — returning empty to avoid leaking ciphertext to a downstream client.");
            return string.Empty;
        }
    }
}
