using System.Text.Json.Nodes;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.UnitTests.Intelligence;

/// <summary>Requirement 6.3: model output is parsed and validated against phantom.intel.v1 before it is saved.</summary>
public sealed class IntelligenceResultParserTests
{
    public static JsonObject ValidJson() => new()
    {
        ["category"] = "Fire",
        ["summary"] = "Smoke reported from a warehouse roof on Dock Road.",
        ["severity"] = "High",
        ["severityRationale"] = "Possible structure fire near occupied buildings.",
        ["namedLocations"] = new JsonArray("Dock Road"),
        ["potentialCasualties"] = "Unknown",
        ["possibleTrappedPersons"] = "NotIndicated",
        ["hazards"] = new JsonArray("smoke"),
        ["missingInformation"] = new JsonArray("Exact address"),
        ["recommendedNextActions"] = new JsonArray("Call the reporter to confirm the address."),
        ["extractedEntities"] = new JsonArray(new JsonObject { ["type"] = "Landmark", ["value"] = "Dock 4 warehouse" }),
        ["confidenceExplanation"] = "Single first-hand report.",
    };

    [Fact]
    public void Valid_output_parses_and_round_trips()
    {
        var parsed = IntelligenceResultParser.Parse(ValidJson().ToJsonString());

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(Severity.High, parsed.Value.Severity);
        Assert.Equal(IncidentCategory.Fire, parsed.Value.Category);
        Assert.Equal(EntityType.Landmark, Assert.Single(parsed.Value.ExtractedEntities).Type);

        var reparsed = IntelligenceResultParser.Parse(IntelligenceResultParser.Serialize(parsed.Value));
        Assert.True(reparsed.IsSuccess);
        Assert.Equal(parsed.Value.Summary, reparsed.Value.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"category\":")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Malformed_json_is_rejected(string raw) =>
        AssertInvalid(raw);

    [Theory]
    [InlineData("severity", "Catastrophic")]
    [InlineData("category", "Earthquake")]
    [InlineData("potentialCasualties", "Confirmed")]
    [InlineData("severity", "3")]
    [InlineData("severity", "High, Low")]
    public void Unknown_enum_values_are_rejected(string field, string value)
    {
        var json = ValidJson();
        json[field] = value;
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void Numeric_enum_is_rejected()
    {
        var json = ValidJson();
        json["severity"] = 2;
        AssertInvalid(json.ToJsonString());
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("severity")]
    [InlineData("hazards")]
    [InlineData("confidenceExplanation")]
    public void Missing_fields_are_rejected(string field)
    {
        var json = ValidJson();
        json.Remove(field);
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void Unexpected_fields_are_rejected()
    {
        var json = ValidJson();
        json["dispatchUnits"] = new JsonArray("Engine 7");
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void Null_where_a_value_is_required_is_rejected()
    {
        var json = ValidJson();
        json["summary"] = null;
        AssertInvalid(json.ToJsonString());
    }

    [Theory]
    [InlineData("summary", 1001)]
    [InlineData("severityRationale", 501)]
    [InlineData("confidenceExplanation", 501)]
    public void Oversize_strings_are_rejected(string field, int length)
    {
        var json = ValidJson();
        json[field] = new string('x', length);
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void Summary_at_the_limit_is_accepted()
    {
        var json = ValidJson();
        json["summary"] = new string('x', 1000);
        Assert.True(IntelligenceResultParser.Parse(json.ToJsonString()).IsSuccess);
    }

    [Fact]
    public void Too_many_list_items_are_rejected()
    {
        var json = ValidJson();
        json["hazards"] = new JsonArray([.. Enumerable.Range(0, 21).Select(i => (JsonNode?)JsonValue.Create($"hazard {i}"))]);
        AssertInvalid(json.ToJsonString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_list_items_and_required_text_are_rejected(string blank)
    {
        var json = ValidJson();
        json["namedLocations"] = new JsonArray(blank);
        AssertInvalid(json.ToJsonString());

        json = ValidJson();
        json["summary"] = blank;
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void Oversize_entity_value_is_rejected()
    {
        var json = ValidJson();
        json["extractedEntities"] = new JsonArray(new JsonObject { ["type"] = "Person", ["value"] = new string('x', 201) });
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void Failure_message_never_echoes_the_model_output()
    {
        var json = ValidJson();
        json["severity"] = "IGNORE-PREVIOUS-INSTRUCTIONS";
        var parsed = IntelligenceResultParser.Parse(json.ToJsonString());

        Assert.True(parsed.IsFailure);
        Assert.DoesNotContain("IGNORE-PREVIOUS-INSTRUCTIONS", parsed.Error!.Message, StringComparison.Ordinal);
        Assert.True(parsed.Error.Message.Length <= 500);
    }

    private static void AssertInvalid(string raw)
    {
        var parsed = IntelligenceResultParser.Parse(raw);
        Assert.True(parsed.IsFailure, $"Expected rejection of: {raw}");
        Assert.Equal("ai.invalid_output", parsed.Error!.Code);
    }
}
