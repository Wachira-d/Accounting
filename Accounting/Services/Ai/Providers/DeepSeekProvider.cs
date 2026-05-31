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
        var url = endpoint + ChatCompletionsPath;

        var temperature = request.TemperatureOverride ?? config.Temperature;
        var maxTokens = request.MaxTokensOverride ?? config.MaxOutputTokens;

        // OpenAI-compatible chat completions payload. response_format
        // forces JSON output — every prompt builder relies on this so
        // the orchestrator can JsonDocument.Parse without try/catch
        // around the happy path.
        var payload = new
        {
            model = config.Model,
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPromptJson },
            },
            temperature = (double)temperature,
            max_tokens = maxTokens,
            response_format = new { type = "json_object" },
            stream = false,
        };

        var json = JsonSerializer.Serialize(payload);

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(Math.Max(2, config.RequestTimeoutSeconds + 2));
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
                    Error = $"HTTP {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}",
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
