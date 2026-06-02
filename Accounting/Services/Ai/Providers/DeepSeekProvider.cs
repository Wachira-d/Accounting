using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Providers;

/// <summary>
/// DeepSeek chat-completion provider. DeepSeek's API is OpenAI-compatible
/// at /v1/chat/completions, so the same HTTP shape works for OpenAI /
/// Custom / LocalLlama with just the endpoint + key swapped — this
/// implementation is the reference; OpenAi/Custom/LocalLlama subclass
/// by overriding Kind and (for non-deepseek) the default endpoint.
///
/// Pricing reference (2025-01): deepseek-chat $0.14/1M input, $0.28/1M
/// output. Stored on AiProviderConfig so cost tracking doesn't need a
/// hardcoded table.
/// </summary>
public class DeepSeekProvider : IAiProvider
{
    public virtual AiProviderType Kind => AiProviderType.DeepSeek;
    protected virtual string DefaultEndpoint => "https://api.deepseek.com";
    protected virtual string ChatCompletionsPath => "/v1/chat/completions";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeepSeekProvider> _logger;

    public DeepSeekProvider(IHttpClientFactory httpClientFactory, ILogger<DeepSeekProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AiProviderRawResponse> CompleteAsync(
        AiRequest request, AiProviderConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            return new AiProviderRawResponse { Success = false, Error = "ApiKey ว่าง" };

        var endpoint = (string.IsNullOrWhiteSpace(config.Endpoint) ? DefaultEndpoint : config.Endpoint!).TrimEnd('/');
        var url = BuildChatCompletionsUrl(endpoint);

        var temperature = request.TemperatureOverride ?? config.Temperature;
        var maxTokens = request.MaxTokensOverride ?? config.MaxOutputTokens;

        // deepseek-reasoner (DeepSeek-R1) does NOT support temperature,
        // top_p, presence/frequency_penalty, or response_format per the
        // DeepSeek docs — sending them is rejected/ignored. Only the
        // chat models (deepseek-chat / OpenAI-compatible) take them.
        var isReasoner = (config.Model ?? "").Contains("reasoner", StringComparison.OrdinalIgnoreCase);

        // OpenAI-compatible chat completions payload. response_format
        // forces JSON output — every prompt builder relies on this so
        // the orchestrator can JsonDocument.Parse without try/catch
        // around the happy path. Built as a dict so unsupported fields
        // can be omitted per-model.
        var payload = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPromptJson },
            },
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
        };
        if (!isReasoner)
        {
            payload["temperature"] = (double)temperature;
            payload["response_format"] = new { type = "json_object" };
        }

        var json = JsonSerializer.Serialize(payload);

        using var http = _httpClientFactory.CreateClient();
        // Per-request timeout override lets bulk features (bank match) opt
        // into a longer wait without changing the provider-wide default.
        var timeoutSec = request.TimeoutSecondsOverride ?? config.RequestTimeoutSeconds;
        http.Timeout = TimeSpan.FromSeconds(Math.Max(2, timeoutSec + 2));
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        try
        {
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("DeepSeek HTTP {Status}: {Body}", (int)resp.StatusCode,
                    body.Length > 300 ? body[..300] : body);
                return new AiProviderRawResponse
                {
                    Success = false,
                    HttpStatus = (int)resp.StatusCode,
                    Error = DescribeHttpError((int)resp.StatusCode, body),
                };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var content = root.TryGetProperty("choices", out var choices)
                          && choices.ValueKind == JsonValueKind.Array
                          && choices.GetArrayLength() > 0
                          && choices[0].TryGetProperty("message", out var msg)
                          && msg.TryGetProperty("content", out var c)
                          ? c.GetString() ?? ""
                          : "";

            string? modelVer = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            int? inputTokens = null, outputTokens = null;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv)) inputTokens = ptv;
                if (usage.TryGetProperty("completion_tokens", out var ot) && ot.TryGetInt32(out var otv)) outputTokens = otv;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return new AiProviderRawResponse
                {
                    Success = false,
                    HttpStatus = (int)resp.StatusCode,
                    Error = "Provider returned empty content",
                    ModelVersion = modelVer,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                };
            }

            return new AiProviderRawResponse
            {
                Success = true,
                Content = content,
                ModelVersion = modelVer,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                HttpStatus = (int)resp.StatusCode,
            };
        }
        catch (TaskCanceledException)
        {
            return new AiProviderRawResponse
            {
                Success = false,
                Error = $"Provider timeout after {http.Timeout.TotalSeconds}s",
            };
        }
        catch (HttpRequestException ex)
        {
            return new AiProviderRawResponse
            {
                Success = false,
                Error = $"Network error: {ex.Message}",
            };
        }
        catch (JsonException ex)
        {
            return new AiProviderRawResponse
            {
                Success = false,
                Error = $"Provider returned malformed JSON: {ex.Message}",
            };
        }
        catch (Exception ex)
        {
            // Defensive — provider must NEVER bring down the call site.
            _logger.LogError(ex, "DeepSeek provider unexpected exception");
            return new AiProviderRawResponse
            {
                Success = false,
                Error = $"Unexpected: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Build the chat-completions URL from whatever the admin pasted into
    /// Endpoint. DeepSeek's docs list base_url as either
    /// https://api.deepseek.com or https://api.deepseek.com/v1 (the /v1 is
    /// NOT a version), and people commonly paste the full path too — accept
    /// all three so we never produce a doubled /v1 (→ 404).
    /// </summary>
    protected string BuildChatCompletionsUrl(string endpoint)
    {
        if (endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return endpoint;
        if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return endpoint + "/chat/completions";
        return endpoint + ChatCompletionsPath;
    }

    /// <summary>
    /// Turn a non-2xx provider response into a clear, actionable message.
    /// DeepSeek/OpenAI return {"error":{"message":...}}; we pull that out
    /// and map the common status codes (esp. 402 Insufficient Balance and
    /// 401 bad key) to guidance the admin can act on, instead of dumping
    /// raw JSON into the UI.
    /// </summary>
    private static string DescribeHttpError(int status, string body)
    {
        var detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    detail = msg.GetString() ?? body;
                else if (err.ValueKind == JsonValueKind.String)
                    detail = err.GetString() ?? body;
            }
        }
        catch (JsonException) { /* not JSON — keep the raw body */ }
        if (detail.Length > 200) detail = detail[..200];

        return status switch
        {
            400 => $"คำขอไม่ถูกต้อง (400) — ตรวจสอบชื่อโมเดล/พารามิเตอร์: {detail}",
            401 => $"API key ไม่ถูกต้องหรือถูกเพิกถอน (401): {detail}",
            402 => "ยอดเครดิตในบัญชี DeepSeek ไม่พอ (402 Insufficient Balance) — กรุณาเติมเงินที่ platform.deepseek.com/top_up แล้วลองใหม่",
            403 => $"ไม่มีสิทธิ์เข้าถึง (403): {detail}",
            404 => $"ไม่พบ endpoint (404) — ตรวจสอบ Endpoint URL ให้เป็น https://api.deepseek.com: {detail}",
            422 => $"พารามิเตอร์ไม่ถูกต้อง (422): {detail}",
            429 => $"เรียกถี่เกินกำหนดหรือเกินโควต้า (429 Rate Limit) — รอสักครู่แล้วลองใหม่: {detail}",
            >= 500 => $"เซิร์ฟเวอร์ผู้ให้บริการขัดข้อง ({status}) — ลองใหม่ภายหลัง: {detail}",
            _ => $"HTTP {status}: {detail}",
        };
    }

    public async Task<(bool ok, string? error)> TestConnectionAsync(AiProviderConfig config, CancellationToken ct)
    {
        // Minimal ping — single-token JSON response. Cost is rounding error.
        var testReq = new AiRequest
        {
            FeatureKey = AiFeatureKey.AdHocAnalysis,
            CompanyId = Guid.Empty,
            SystemPrompt = "Reply with the literal JSON {\"ok\":true} and nothing else.",
            UserPromptJson = "{\"ping\":true}",
            MaxTokensOverride = 20,
            TemperatureOverride = 0m,
        };
        var resp = await CompleteAsync(testReq, config, ct);
        if (!resp.Success) return (false, resp.Error);
        if (!resp.Content.Contains("\"ok\"")) return (false, "Response didn't contain expected token: " + resp.Content);
        return (true, null);
    }
}

/// <summary>OpenAI uses the exact same wire format — only endpoint differs.</summary>
public class OpenAiProvider : DeepSeekProvider
{
    public OpenAiProvider(IHttpClientFactory hcf, ILogger<DeepSeekProvider> logger) : base(hcf, logger) { }
    public override AiProviderType Kind => AiProviderType.OpenAi;
    protected override string DefaultEndpoint => "https://api.openai.com";
}

/// <summary>Custom OpenAI-compatible endpoint (Azure OpenAI, Together,
/// proxy, etc.). No default endpoint — admin MUST supply it.</summary>
public class OpenAiCompatibleProvider : DeepSeekProvider
{
    public OpenAiCompatibleProvider(IHttpClientFactory hcf, ILogger<DeepSeekProvider> logger) : base(hcf, logger) { }
    public override AiProviderType Kind => AiProviderType.Custom;
    protected override string DefaultEndpoint => "";  // require admin to set
}

/// <summary>Local Llama / Ollama / llama.cpp server with the OpenAI-
/// compat /v1/chat/completions endpoint. Default to localhost:11434.</summary>
public class LocalLlamaProvider : DeepSeekProvider
{
    public LocalLlamaProvider(IHttpClientFactory hcf, ILogger<DeepSeekProvider> logger) : base(hcf, logger) { }
    public override AiProviderType Kind => AiProviderType.LocalLlama;
    protected override string DefaultEndpoint => "http://localhost:11434";
}
