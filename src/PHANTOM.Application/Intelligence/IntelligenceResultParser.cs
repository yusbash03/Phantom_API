using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Intelligence;

/// <summary>
/// Treats model output as untrusted input (security steering): strict JSON parsing against
/// <see cref="IntelligenceResultV1"/> — no unknown fields, no missing fields, no numeric enums —
/// followed by bounds validation.
/// </summary>
public static class IntelligenceResultParser
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new StrictEnumConverterFactory() },
        MaxDepth = 8,
    };

    private static readonly IntelligenceResultValidator Validator = new();

    public static Result<IntelligenceResultV1> Parse(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Invalid("The AI response was empty.");
        }

        IntelligenceResultV1? result;
        try
        {
            result = JsonSerializer.Deserialize<IntelligenceResultV1>(rawJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            // JsonException messages describe the path and expected type, never the content.
            return Invalid($"The AI response is not valid {IncidentAnalysis.CurrentSchemaVersion} JSON ({Describe(ex)}).");
        }

        if (result is null)
        {
            return Invalid("The AI response was null.");
        }

        var validation = Validator.Validate(result);
        if (!validation.IsValid)
        {
            var problems = string.Join("; ", validation.Errors.Take(3).Select(e => e.ErrorMessage));
            return Invalid($"The AI response failed {IncidentAnalysis.CurrentSchemaVersion} validation: {problems}");
        }

        return result;
    }

    /// <summary>Canonical JSON for storage: the validated object re-serialised, not the raw model text.</summary>
    public static string Serialize(IntelligenceResultV1 result) => JsonSerializer.Serialize(result, JsonOptions);

    private static string Describe(JsonException ex) =>
        ex.Path is { Length: > 0 } path ? $"problem at {path}" : "malformed JSON";

    private static Error Invalid(string message) => Error.Validation("ai.invalid_output", message);
}

internal sealed class IntelligenceResultValidator : AbstractValidator<IntelligenceResultV1>
{
    public IntelligenceResultValidator()
    {
        RuleFor(r => r.Category).IsInEnum();
        RuleFor(r => r.Severity).IsInEnum();
        RuleFor(r => r.PotentialCasualties).IsInEnum();
        RuleFor(r => r.PossibleTrappedPersons).IsInEnum();

        RuleFor(r => r.Summary).NotEmpty().MaximumLength(IntelligenceResultV1.MaxSummaryLength);
        RuleFor(r => r.SeverityRationale).NotEmpty().MaximumLength(IntelligenceResultV1.MaxTextLength);
        RuleFor(r => r.ConfidenceExplanation).NotEmpty().MaximumLength(IntelligenceResultV1.MaxTextLength);

        RuleFor(r => r.NamedLocations).Must(BeBounded).WithMessage(ListMessage("namedLocations"));
        RuleFor(r => r.Hazards).Must(BeBounded).WithMessage(ListMessage("hazards"));
        RuleFor(r => r.MissingInformation).Must(BeBounded).WithMessage(ListMessage("missingInformation"));
        RuleFor(r => r.RecommendedNextActions).Must(BeBounded).WithMessage(ListMessage("recommendedNextActions"));

        RuleFor(r => r.ExtractedEntities).Must(e => e.Count <= IntelligenceResultV1.MaxItems)
            .WithMessage($"extractedEntities must have at most {IntelligenceResultV1.MaxItems} items.");
        RuleForEach(r => r.ExtractedEntities).ChildRules(entity =>
        {
            entity.RuleFor(e => e.Type).IsInEnum();
            entity.RuleFor(e => e.Value).NotEmpty().MaximumLength(IntelligenceResultV1.MaxEntityValueLength);
        });
    }

    private static bool BeBounded(IReadOnlyList<string> items) =>
        items.Count <= IntelligenceResultV1.MaxItems
        && items.All(i => !string.IsNullOrWhiteSpace(i) && i.Length <= IntelligenceResultV1.MaxTextLength);

    private static string ListMessage(string name) =>
        $"{name} must have at most {IntelligenceResultV1.MaxItems} non-empty items of at most {IntelligenceResultV1.MaxTextLength} characters.";
}

/// <summary>
/// Accepts only an exact enum member name (case-insensitive). Unlike <see cref="JsonStringEnumConverter"/>,
/// it rejects numbers, numeric strings and comma-separated flag combinations such as "High, Low".
/// </summary>
internal sealed class StrictEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(StrictEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class StrictEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        private static readonly Dictionary<string, T> Names =
            Enum.GetValues<T>().ToDictionary(v => v.ToString(), v => v, StringComparer.OrdinalIgnoreCase);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name && Names.TryGetValue(name, out var value))
            {
                return value;
            }

            throw new JsonException($"Expected one of: {string.Join(", ", Names.Keys)}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteStringValue(value.ToString());
        }
    }
}
