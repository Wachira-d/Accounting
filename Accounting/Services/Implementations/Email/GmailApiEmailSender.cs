using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations.Email;

/// <summary>
/// Gmail API sender — uses OAuth2 refresh token flow.
/// Requires Google Cloud project with Gmail API enabled and OAuth client.
/// User grants permission once, refresh token used afterward.
/// </summary>
public class GmailApiEmailSender : IEmailSender
{
    public EmailProvider Provider => EmailProvider.GmailApi;

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _refreshToken;
    private readonly string _fromAddress;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger? _logger;

    public GmailApiEmailSender(string clientId, string clientSecret, string refreshToken, string fromAddress,
        IHttpClientFactory httpFactory, ILogger? logger = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _refreshToken = refreshToken;
        _fromAddress = fromAddress;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret)
            || string.IsNullOrWhiteSpace(_refreshToken))
            return EmailSendResult.Fail("Gmail API not fully configured (clientId/clientSecret/refreshToken required)");

        try
        {
            var token = await GetAccessTokenAsync(ct);
            if (string.IsNullOrEmpty(token)) return EmailSendResult.Fail("Failed to acquire Gmail access token");

            var rawMime = BuildMime(message);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawMime))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            var client = _httpFactory.CreateClient("Gmail");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://gmail.googleapis.com/gmail/v1/users/me/messages/send")
            {
                Content = JsonContent.Create(new { raw = encoded })
            };

            var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var rj = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                var id = rj.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                return EmailSendResult.Ok(id);
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger?.LogWarning("Gmail send failed {Status}: {Body}", resp.StatusCode, body);
            return EmailSendResult.Fail($"Gmail {(int)resp.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GmailApiEmailSender failed");
            return EmailSendResult.Fail(ex.Message);
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("GmailAuth");
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["refresh_token"] = _refreshToken,
            ["grant_type"] = "refresh_token"
        });

        var resp = await client.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return json.TryGetProperty("access_token", out var t) ? t.GetString() : null;
    }

    private string BuildMime(EmailMessage m)
    {
        var sb = new StringBuilder();
        var boundary = "----=_NextAccBoundary_" + Guid.NewGuid().ToString("N");

        sb.AppendLine($"From: {EncodeHeader(m.FromName ?? _fromAddress)} <{_fromAddress}>");
        sb.AppendLine($"To: {string.Join(", ", m.To)}");
        if (m.Cc.Count > 0) sb.AppendLine($"Cc: {string.Join(", ", m.Cc)}");
        if (m.Bcc.Count > 0) sb.AppendLine($"Bcc: {string.Join(", ", m.Bcc)}");
        if (!string.IsNullOrWhiteSpace(m.ReplyTo)) sb.AppendLine($"Reply-To: {m.ReplyTo}");
        sb.AppendLine($"Subject: {EncodeHeader(m.Subject)}");
        sb.AppendLine("MIME-Version: 1.0");

        if (m.Attachments.Count == 0)
        {
            sb.AppendLine("Content-Type: text/html; charset=UTF-8");
            sb.AppendLine("Content-Transfer-Encoding: base64");
            sb.AppendLine();
            sb.AppendLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(m.HtmlBody)));
        }
        else
        {
            sb.AppendLine($"Content-Type: multipart/mixed; boundary=\"{boundary}\"");
            sb.AppendLine();
            sb.AppendLine($"--{boundary}");
            sb.AppendLine("Content-Type: text/html; charset=UTF-8");
            sb.AppendLine("Content-Transfer-Encoding: base64");
            sb.AppendLine();
            sb.AppendLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(m.HtmlBody)));

            foreach (var a in m.Attachments)
            {
                sb.AppendLine($"--{boundary}");
                sb.AppendLine($"Content-Type: {a.ContentType}; name=\"{a.FileName}\"");
                sb.AppendLine("Content-Transfer-Encoding: base64");
                sb.AppendLine($"Content-Disposition: attachment; filename=\"{a.FileName}\"");
                sb.AppendLine();
                sb.AppendLine(Convert.ToBase64String(a.Content));
            }
            sb.AppendLine($"--{boundary}--");
        }

        return sb.ToString();
    }

    private static string EncodeHeader(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // RFC 2047 encoded-word for non-ASCII
        if (s.All(c => c < 128)) return s;
        return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";
    }
}
