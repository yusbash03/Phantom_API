using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Infrastructure.Intelligence;

/// <summary>
/// Gemini <c>generateContent</c> over REST with structured JSON output. Returns the model's JSON text;
/// the application layer parses and validates it. Never logs prompts, responses or error bodies.
/// </summary>
internal sealed class GeminiIntelligenceService(
    HttpClient http,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<GeminiIntelligenceService> logger) : IIncidentIntelligenceService
{
    private static readonly JsonObject Schema = IntelligencePrompt.ResponseSchema();
    private readonly AiOptions _options = options.Value;

    public async Task<IntelligenceOutcome> AnalyzeAsync(IntelligenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = _options.Model;
        var body = BuildBody(request);

        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            HttpResponseMessage response;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, $"models/{Uri.EscapeDataString(model)}:generateContent")
                {
                    Content = JsonContent.Create(body),
                };
                message.Headers.Add("x-goog-api-key", _options.ApiKey);
                response = await http.SendAsync(message, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Gemini call timed out after {TimeoutSeconds}s (attempt {Attempt})", _options.TimeoutSeconds, attempt + 1);
                return Failure(model, AnalysisFailureCode.Timeout, $"The AI provider did not respond within {_options.TimeoutSeconds} seconds.");
            }
            catch (HttpRequestException ex)
            {
                if (attempt < _options.MaxRetries)
                {
                    logger.LogWarning("Gemini request failed ({Error}); retrying", ex.HttpRequestError);
                    await DelayAsync(attempt, null, cancellationToken);
                    continue;
                }

                return Failure(model, AnalysisFailureCode.ProviderError, "The AI provider could not be reached.");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;
                if (transient && attempt < _options.MaxRetries)
                {
                    logger.LogWarning("Gemini returned {StatusCode}; retrying (attempt {Attempt})", status, attempt + 1);
                    await DelayAsync(attempt, response.Headers.RetryAfter?.Delta, cancellationToken);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return Failure(model, AnalysisFailureCode.RateLimited, "The AI provider's rate limit was reached. Try again later.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Gemini returned {StatusCode}", status);
                    return Failure(model, AnalysisFailureCode.ProviderError, $"The AI provider returned an error (HTTP {status}).");
                }

                return await ReadEnvelopeAsync(response, model, cancellationToken);
            }
        }
    }

    private JsonObject BuildBody(IntelligenceRequest request) => new()
    {
        ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = IntelligencePrompt.SystemInstruction }) },
        ["contents"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray(new JsonObject { ["text"] = IntelligencePrompt.BuildUserContent(request) }),
        }),
        ["generationConfig"] = new JsonObject
        {
            ["temperature"] = _options.Temperature,
            ["maxOutputTokens"] = _options.MaxOutputTokens,
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = Schema.DeepClone(),
        },
    };

    private static async Task<IntelligenceOutcome> ReadEnvelopeAsync(HttpResponseMessage response, string model, CancellationToken cancellationToken)
    {
        JsonNode? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
        }
        catch (JsonException)
        {
            return Failure(model, AnalysisFailureCode.ProviderError, "The AI provider returned an unreadable response.");
        }

        var reportedModel = envelope?["modelVersion"]?.GetValue<string>() ?? model;
        if (envelope?["promptFeedback"]?["blockReason"] is JsonNode block)
        {
            return Failure(reportedModel, AnalysisFailureCode.InvalidOutput, $"The AI provider declined the request ({Short(block.ToString())}).");
        }

        var candidate = envelope?["candidates"]?[0];
        var finishReason = candidate?["finishReason"]?.GetValue<string>();
        if (candidate is null)
        {
            return Failure(reportedModel, AnalysisFailureCode.InvalidOutput, "The AI provider returned no answer.");
        }

        if (finishReason is not null and not "STOP")
        {
            return Failure(reportedModel, AnalysisFailureCode.InvalidOutput, $"The AI answer was incomplete (finish reason {Short(finishReason)}).");
        }

        // Thinking models may return "thought" parts; only the answer parts form the JSON.
        var text = new StringBuilder();
        foreach (var part in candidate["content"]?["parts"]?.AsArray() ?? [])
        {
            if (part?["thought"]?.GetValue<bool>() == true)
            {
                continue;
            }

            text.Append(part?["text"]?.GetValue<string>());
        }

        return text.Length == 0
            ? Failure(reportedModel, AnalysisFailureCode.InvalidOutput, "The AI provider returned an empty answer.")
            : IntelligenceOutcome.Success(AiOptions.GeminiProvider, reportedModel, text.ToString());
    }

    private Task DelayAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(0, 250));
        var delay = retryAfter is { } hint && hint > backoff ? hint : backoff;
        return Task.Delay(delay > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay, time, cancellationToken);
    }

    private static IntelligenceOutcome Failure(string model, AnalysisFailureCode code, string message) =>
        IntelligenceOutcome.Failure(AiOptions.GeminiProvider, model, code, message);

    private static string Short(string value)
    {
        var safe = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        return safe.Length <= 40 ? safe : safe[..40];
    }
}
