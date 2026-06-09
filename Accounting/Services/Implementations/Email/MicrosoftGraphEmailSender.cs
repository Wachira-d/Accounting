using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations.Email;

/// <summary>
/// Microsoft Graph API sender — uses OAuth2 client credentials (app-only auth).
/// Requires Azure AD app registration with Mail.Send permission granted by tenant admin.
/// Sends as a specific mailbox (UPN).
/// </summary>
public class MicrosoftGraphEmailSender : IEmailSender
{
    public EmailProvider Provider => EmailProvider.MicrosoftGraph;

    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _senderUpn;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger? _logger;

    public MicrosoftGraphEmailSender(string tenantId, string clientId, string clientSecret, string senderUpn,
        IHttpClientFactory httpFactory, ILogger? logger = null)
    {
        _tenantId = tenantId;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _senderUpn = senderUpn;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_tenantId) || string.IsNullOrWhiteSpace(_clientId)
            || string.IsNullOrWhiteSpace(_clientSecret) || string.IsNullOrWhiteSpace(_senderUpn))
            return EmailSendResult.Fail("Microsoft Graph not fully configured (tenantId/clientId/clientSecret/senderUpn required)");

        try
        {
            var token = await GetAccessTokenAsync(ct);
            if (string.IsNullOrEmpty(token)) return EmailSendResult.Fail("Failed to acquire Microsoft Graph token");

            var client = _httpFactory.CreateClient("Graph");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Build the message as a dictionary so we can conditionally add the
            // "from" field (anonymous objects can't omit a property at runtime).
            var graphMessage = new Dictionary<string, object?>
            {
                ["subject"] = message.Subject,
                ["body"] = new { contentType = "HTML", content = message.HtmlBody },
                ["toRecipients"] = message.To.Select(addr => new { emailAddress = new { address = addr } }).ToArray(),
                ["ccRecipients"] = message.Cc.Select(addr => new { emailAddress = new { address = addr } }).ToArray(),
                ["bccRecipients"] = message.Bcc.Select(addr => new { emailAddress = new { address = addr } }).ToArray(),
                ["replyTo"] = string.IsNullOrWhiteSpace(message.ReplyTo)
                    ? Array.Empty<object>()
                    : new[] { new { emailAddress = new { address = message.ReplyTo } } },
                ["attachments"] = message.Attachments.Select(a => new
                {
                    @odata_type = "#microsoft.graph.fileAttachment",
                    name = a.FileName,
                    contentType = a.ContentType,
                    contentBytes = Convert.ToBase64String(a.Content)
                }).ToArray()
            };

            // SEND-AS: when the configured From address differs from the sending
            // mailbox (e.g. a shared/distribution address like
            // "Accounting@contoso.com" sent through the licensed mailbox
            // "user@contoso.com"), set the message "from" so the recipient sees
            // that identity. REQUIRES the sending mailbox (_senderUpn) to have
            // "Send As" rights on that address in Exchange — otherwise Graph
            // returns ErrorAccessDenied. When the From matches the mailbox (or is
            // blank) we omit it and Graph defaults to the mailbox owner (the
            // previous behaviour — no regression for single-mailbox setups).
            if (!string.IsNullOrWhiteSpace(message.FromAddress)
                && !string.Equals(message.FromAddress, _senderUpn, StringComparison.OrdinalIgnoreCase))
            {
                graphMessage["from"] = new
                {
                    emailAddress = new
                    {
                        address = message.FromAddress,
                        name = string.IsNullOrWhiteSpace(message.FromName) ? message.FromAddress : message.FromName
                    }
                };
            }

            var payload = new
            {
                message = graphMessage,
                saveToSentItems = true
            };

            // Graph requires "@odata.type" as the literal property name
            var json = JsonSerializer.Serialize(payload).Replace("\"@odata_type\"", "\"@odata.type\"");
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(_senderUpn)}/sendMail")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };

            var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return EmailSendResult.Ok();

            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger?.LogWarning("Graph sendMail failed {Status}: {Body}", resp.StatusCode, body);
            return EmailSendResult.Fail($"Graph {(int)resp.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MicrosoftGraphEmailSender failed");
            return EmailSendResult.Fail(ex.Message);
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("GraphAuth");
        var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(_tenantId)}/oauth2/v2.0/token";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        });

        var resp = await client.PostAsync(url, form, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return json.TryGetProperty("access_token", out var t) ? t.GetString() : null;
    }
}
