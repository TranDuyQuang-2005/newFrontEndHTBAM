using System.Diagnostics;
using System.Net;
using Google.GenAI;
using Google.GenAI.Types;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HTBAM.Infrastructure.Services.Llm;

public sealed class GeminiLlmProvider : ILlmProvider
{
    private readonly string? _apiKey;
    private readonly int _timeoutSeconds;
    private readonly double _temperature;
    private readonly int _maxOutputTokens;
    private readonly ILogger<GeminiLlmProvider> _logger;

    public GeminiLlmProvider(IConfiguration configuration, ILogger<GeminiLlmProvider> logger)
    {
        _apiKey = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? System.Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        ModelName = System.Environment.GetEnvironmentVariable("GEMINI_MODEL")
            ?? configuration["Llm:Model"]
            ?? "gemini-3.5-flash-lite";
        _timeoutSeconds = Math.Clamp(configuration.GetValue("Llm:TimeoutSeconds", 45), 1, 180);
        _temperature = Math.Clamp(configuration.GetValue("Llm:Temperature", 0.2d), 0d, 2d);
        _maxOutputTokens = Math.Clamp(configuration.GetValue("Llm:MaxOutputTokens", 500), 32, 8192);
        _logger = logger;
    }

    public string ProviderName => "GEMINI";
    public string ModelName { get; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(ModelName);

    public async Task<LlmGenerationResult?> GenerateCommentAsync(LlmCommentRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return Error("NOT_CONFIGURED");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        try
        {
            await using var client = new Client(apiKey: _apiKey);
            var response = await client.Models.GenerateContentAsync(
                model: ModelName,
                contents: LlmPromptBuilder.Build(request),
                config: new GenerateContentConfig
                {
                    SystemInstruction = new Content
                    {
                        Parts = [new Part { Text = LlmPromptBuilder.SystemInstruction }]
                    },
                    Temperature = _temperature,
                    MaxOutputTokens = _maxOutputTokens,
                    ResponseMimeType = "text/plain"
                },
                cancellationToken: timeout.Token);

            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                LogFailure("EMPTY_RESPONSE", request);
                return Error("EMPTY_RESPONSE");
            }

            return new LlmGenerationResult(text, ProviderName, ModelName, DateTime.UtcNow, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailure("TIMEOUT", request);
            return Error("TIMEOUT");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = Classify(exception);
            LogFailure(errorCode, request, exception.GetType().Name);
            return Error(errorCode);
        }
    }

    private LlmGenerationResult Error(string errorCode) =>
        new(string.Empty, ProviderName, ModelName, DateTime.UtcNow, false, errorCode);

    private void LogFailure(string errorCode, LlmCommentRequest request, string? exceptionType = null)
    {
        _logger.LogWarning(
            "LLM request failed. Provider={Provider} Model={Model} ErrorType={ErrorType} ExceptionType={ExceptionType} CorrelationId={CorrelationId} SubjectType={SubjectType} SubjectId={SubjectId}",
            ProviderName, ModelName, errorCode, exceptionType ?? "NONE",
            Activity.Current?.TraceId.ToString() ?? "NONE", request.SubjectType, request.SubjectId);
    }

    private static string Classify(Exception exception)
    {
        var status = FindHttpStatus(exception);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return "AUTH_ERROR";
        if (status == HttpStatusCode.TooManyRequests) return "RATE_LIMIT";
        if (status == HttpStatusCode.NotFound) return "INVALID_MODEL";
        if (status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout) return "SERVICE_UNAVAILABLE";
        if (exception is HttpRequestException) return "NETWORK_ERROR";

        var message = exception.Message.ToUpperInvariant();
        if (message.Contains("API KEY") || message.Contains("UNAUTHENTICATED") || message.Contains("PERMISSION_DENIED")) return "AUTH_ERROR";
        if (message.Contains("QUOTA") || message.Contains("RATE LIMIT") || message.Contains("RESOURCE_EXHAUSTED")) return "RATE_LIMIT";
        if (message.Contains("MODEL") && (message.Contains("NOT FOUND") || message.Contains("INVALID"))) return "INVALID_MODEL";
        return "SDK_ERROR";
    }

    private static HttpStatusCode? FindHttpStatus(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is HttpRequestException http && http.StatusCode is not null) return http.StatusCode;
            var statusProperties = exception.GetType().GetProperties()
                .Where(property =>
                    property.GetIndexParameters().Length == 0
                    && (property.Name == "HttpStatusCode" || property.Name == "StatusCode"));
            foreach (var statusProperty in statusProperties)
            {
                object? value;
                try { value = statusProperty.GetValue(exception); }
                catch { continue; }
                if (value is HttpStatusCode status) return status;
                if (value is int code && Enum.IsDefined(typeof(HttpStatusCode), code)) return (HttpStatusCode)code;
            }
            exception = exception.InnerException;
        }
        return null;
    }
}
