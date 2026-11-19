namespace PHANTOM.Infrastructure.Intelligence;

public sealed class AiOptions
{
    public const string SectionName = "Ai";
    public const string GeminiProvider = "Gemini";

    public string Provider { get; set; } = GeminiProvider;

    /// <summary>Secret. From <c>Ai__ApiKey</c> or user-secrets, never from source.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model names change over time (design R9), so this is configuration.</summary>
    public string Model { get; set; } = "gemini-3.8-flash";

    public Uri BaseUrl { get; set; } = new("https://generativelanguage.googleapis.com/v1beta/");
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 2;
    public double Temperature { get; set; } = 0.2;
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>Lets tests and tooling run the API without the background worker.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 10;
    public int StuckAfterMinutes { get; set; } = 5;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}
