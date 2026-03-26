using System.Text;
using System.Text.Json;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

public class LineNotifyService : ILineNotifyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LineNotifyService> _logger;

    public LineNotifyService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LineNotifyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendMessageAsync(string message)
    {
        var channelAccessToken = _configuration["Line:ChannelAccessToken"];
        var groupId = _configuration["Line:GroupId"];

        if (string.IsNullOrEmpty(channelAccessToken) || string.IsNullOrEmpty(groupId))
        {
            _logger.LogWarning("LINE Messaging API not configured. Skipping notification.");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", channelAccessToken);

            var payload = new
            {
                to = groupId,
                messages = new[]
                {
                    new { type = "text", text = message }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("https://api.line.me/v2/bot/message/push", content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("LINE message sent successfully to group {GroupId}", groupId);
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("LINE API error: {StatusCode} - {Body}", response.StatusCode, responseBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send LINE message");
        }
    }
}
