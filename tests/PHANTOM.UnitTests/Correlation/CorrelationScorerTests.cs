using FsCheck.Xunit;
using PHANTOM.Domain.Correlation;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.UnitTests.Correlation;

/// <summary>Requirement 7.2 and 7.8; design §6. Integer seeds keep generated doubles finite and in range.</summary>
public sealed class CorrelationScorerTests
{
    private static readonly CorrelationSettings Settings = new();
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] Vocabulary = ["fire", "smoke", "warehouse", "flood", "water", "crash", "car", "bus", "bridge", "market", "road", "trapped", "injured", "roof"];
    private static readonly IncidentCategory[] Categories = Enum.GetValues<IncidentCategory>();

    private static IncidentFeatures Make(int seed, int minutes, bool coords, int category, int words, string? place = null)
    {
        var lat = 6.4 + (Math.Abs(seed % 1000) / 10_000.0);
        var lon = 3.3 + (Math.Abs(seed / 7 % 1000) / 10_000.0);
        var text = string.Join(' ', Enumerable.Range(0, Math.Abs(words % 6) + 1).Select(i => Vocabulary[Math.Abs(seed + (i * 3)) % Vocabulary.Length]));
        return new IncidentFeatures(Guid.NewGuid(), T0.AddMinutes(Math.Abs(minutes % 720)), Categories[Math.Abs(category % Categories.Length)],
            coords ? lat : null, coords ? lon : null, text, place is null ? [] : [place]);
    }

    // ── Properties ─────────────────────────────────────────────────────────────

    [Property(MaxTest = 500)]
    public bool Score_is_bounded(int s1, int s2, int m1, int m2, bool c1, bool c2, int k1, int k2, int w1, int w2)
    {
        var score = CorrelationScorer.Score(Make(s1, m1, c1, k1, w1, "Dock Road"), Make(s2, m2, c2, k2, w2), Settings).Score;
        return score is >= 0 and <= 1;
    }

    [Property(MaxTest = 500)]
    public bool Score_is_symmetric(int s1, int s2, int m1, int m2, bool c1, bool c2, int k1, int k2, int w1, int w2)
    {
        var a = Make(s1, m1, c1, k1, w1, "Apapa wharf");
        var b = Make(s2, m2, c2, k2, w2, "Dock Road Apapa");
        var ab = CorrelationScorer.Score(a, b, Settings);
        var ba = CorrelationScorer.Score(b, a, Settings);
        return ab.Score == ba.Score && ab.DistanceMeters == ba.DistanceMeters;
    }

    [Property(MaxTest = 1000)]
    public bool Score_never_decreases_when_one_signal_improves(bool[] available, int[] weights, int[] values, int index, int improvement)
    {
        if (available.Length == 0)
        {
            return true;
        }

        var n = available.Length;
        double W(int i) => i < weights.Length ? Math.Abs(weights[i] % 101) / 100.0 : 0.2;
        double V(int i) => i < values.Length ? Math.Abs(values[i] % 101) / 100.0 : 0.5;
        var signals = Enumerable.Range(0, n).Select(i => (new SignalScore(available[i], V(i)), W(i))).ToList();
        var k = Math.Abs(index % n);
        var improved = signals.ToList();
        improved[k] = (new SignalScore(available[k], Math.Min(1, V(k) + (Math.Abs(improvement % 101) / 100.0))), W(k));

        return CorrelationScorer.Combine(improved) >= CorrelationScorer.Combine(signals) - 1e-12;
    }

    [Property(MaxTest = 300)]
    public bool Identical_incidents_meet_the_suggestion_threshold(int seed, int minutes, bool coords, int category, int words)
    {
        var a = Make(seed, minutes, coords, category, words);
        var twin = a with { Id = Guid.NewGuid() };
        return CorrelationScorer.Score(a, twin, Settings).Score >= Settings.SuggestThreshold;
    }

    // ── Edge cases ─────────────────────────────────────────────────────────────

    [Fact]
    public void Missing_coordinates_make_geo_unavailable_not_zero()
    {
        var a = Make(1, 0, coords: true, category: 0, words: 3);
        var b = a with { Id = Guid.NewGuid(), Latitude = null, Longitude = null };

        var result = CorrelationScorer.Score(a, b, Settings);

        Assert.False(result.Geo.Available);
        Assert.Null(result.DistanceMeters);
        Assert.Equal(1, result.Score, 9);
        Assert.StartsWith("Distance unknown", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_text_scores_full_text_overlap()
    {
        var a = new IncidentFeatures(Guid.NewGuid(), T0, IncidentCategory.Fire, null, null, "Smoke pouring from the warehouse roof!", []);
        var b = a with { Id = Guid.NewGuid(), Text = "smoke POURING from the Warehouse roof" };

        Assert.Equal(1, CorrelationScorer.Score(a, b, Settings).Text.Value, 9);
    }

    [Theory]
    [InlineData(IncidentCategory.Fire, IncidentCategory.Fire, 1.0)]
    [InlineData(IncidentCategory.Fire, IncidentCategory.Flood, 0.0)]
    [InlineData(IncidentCategory.Other, IncidentCategory.Flood, 0.5)]
    [InlineData(IncidentCategory.Security, IncidentCategory.Other, 0.5)]
    public void Category_signal(IncidentCategory a, IncidentCategory b, double expected)
    {
        var x = new IncidentFeatures(Guid.NewGuid(), T0, a, null, null, "x", []);
        var y = x with { Id = Guid.NewGuid(), Category = b };
        Assert.Equal(expected, CorrelationScorer.Score(x, y, Settings).Category.Value, 9);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(180, 0.5)]
    [InlineData(360, 0.0)]
    [InlineData(720, 0.0)]
    public void Time_signal_falls_to_zero_at_the_window_boundary(int minutesApart, double expected)
    {
        var a = new IncidentFeatures(Guid.NewGuid(), T0, IncidentCategory.Fire, null, null, "fire", []);
        var b = a with { Id = Guid.NewGuid(), CreatedAt = T0.AddMinutes(minutesApart) };
        Assert.Equal(expected, CorrelationScorer.Score(a, b, Settings).Time.Value, 9);
    }

    [Fact]
    public void Geo_signal_reaches_zero_at_the_radius_and_distance_is_reported()
    {
        // ~1.11 km per 0.01° of latitude.
        var a = new IncidentFeatures(Guid.NewGuid(), T0, IncidentCategory.Fire, 6.45, 3.39, "fire", []);
        var near = a with { Id = Guid.NewGuid(), Latitude = 6.4527 };
        var far = a with { Id = Guid.NewGuid(), Latitude = 6.47 };

        var nearResult = CorrelationScorer.Score(a, near, Settings);
        Assert.InRange(nearResult.DistanceMeters!.Value, 295, 305);
        Assert.InRange(nearResult.Geo.Value, 0.79, 0.81);
        Assert.StartsWith("Reported 300 m apart", nearResult.Explanation, StringComparison.Ordinal);
        Assert.Equal(0, CorrelationScorer.Score(a, far, Settings).Geo.Value, 9);
    }

    [Fact]
    public void Sparse_evidence_is_damped()
    {
        var full = CorrelationScorer.Combine([(new SignalScore(true, 1), 1), (new SignalScore(true, 1), 1), (new SignalScore(true, 1), 1)]);
        var sparse = CorrelationScorer.Combine([(new SignalScore(true, 1), 1), (new SignalScore(true, 1), 1), (new SignalScore(false, 1), 1)]);
        Assert.Equal(1, full, 9);
        Assert.Equal(CorrelationSettings.SparseEvidenceFactor, sparse, 9);
        Assert.Equal(0, CorrelationScorer.Combine([(new SignalScore(false, 1), 1)]));
    }

    [Fact]
    public void Two_reports_of_the_same_fire_are_suggested()
    {
        var a = new IncidentFeatures(Guid.NewGuid(), T0, IncidentCategory.Fire, 6.4500, 3.3900,
            "Warehouse fire. Thick black smoke from the warehouse roof at Dock 4", ["Dock Road, Apapa", "Dock 4"]);
        var b = new IncidentFeatures(Guid.NewGuid(), T0.AddMinutes(12), IncidentCategory.Fire, 6.4521, 3.3912,
            "Fire at warehouse. Black smoke and flames from roof near Dock 4", ["Apapa docks, Dock 4"]);

        var result = CorrelationScorer.Score(a, b, Settings);

        Assert.True(result.Score >= Settings.SuggestThreshold, $"score {result.Score}: {result.Explanation}");
        Assert.Contains("dock", result.SharedPlaceTokens);
        Assert.Contains("same category", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_incidents_far_apart_are_not_suggested()
    {
        var a = new IncidentFeatures(Guid.NewGuid(), T0, IncidentCategory.Fire, 6.4500, 3.3900, "Warehouse fire at Dock 4", ["Apapa"]);
        var b = new IncidentFeatures(Guid.NewGuid(), T0.AddHours(4), IncidentCategory.RoadAccident, 6.6000, 3.3500, "Bus crash on the expressway", ["Ikeja"]);

        Assert.True(CorrelationScorer.Score(a, b, Settings).Score < Settings.SuggestThreshold);
    }

    [Fact]
    public void Street_words_alone_do_not_count_as_shared_places()
    {
        var a = new IncidentFeatures(Guid.NewGuid(), T0, IncidentCategory.Fire, null, null, "fire", ["Dock Road"]);
        var b = a with { Id = Guid.NewGuid(), PlaceNames = ["Depot Road"] };

        var result = CorrelationScorer.Score(a, b, Settings);

        Assert.Empty(result.SharedPlaceTokens);
        Assert.Equal(0, result.Locations.Value, 9);
    }

    [Fact]
    public void Tokenizer_normalises_case_punctuation_stopwords_and_suffixes()
    {
        var tokens = CorrelationScorer.Tokenize("The FLOODING, near the markets... it's flooded!");
        Assert.Equal(["flood", "market"], tokens.Order(StringComparer.Ordinal));
    }
}

public sealed class IncidentRelationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pairs_are_stored_in_one_order_regardless_of_argument_order()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var r1 = IncidentRelation.Suggest(Guid.NewGuid(), a, b, 0.7m, "{}", "x", Now);
        var r2 = IncidentRelation.Suggest(Guid.NewGuid(), b, a, 0.7m, "{}", "x", Now);

        Assert.Equal((r1.SourceIncidentId, r1.TargetIncidentId), (r2.SourceIncidentId, r2.TargetIncidentId));
        Assert.Throws<ArgumentException>(() => IncidentRelation.Order(a, a));
    }

    [Fact]
    public void Decisions_follow_the_allowed_paths()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var user = Guid.NewGuid();
        var relation = IncidentRelation.Suggest(Guid.NewGuid(), a, b, 0.6m, "{}", "x", Now);

        Assert.True(relation.MarkUnrelated(user, Now).IsSuccess);
        Assert.True(relation.MarkUnrelated(user, Now).IsFailure);
        Assert.True(relation.MarkRelated(user, Now).IsSuccess);
        Assert.True(relation.ConfirmDuplicate(Guid.NewGuid(), user, Now).IsFailure);
        Assert.True(relation.ConfirmDuplicate(b, user, Now).IsSuccess);
        Assert.Equal(RelationType.ConfirmedDuplicate, relation.Type);
        Assert.Equal(b, relation.PrimaryIncidentId);
        // A confirmed duplicate is final.
        Assert.True(relation.MarkUnrelated(user, Now).IsFailure);
        Assert.True(relation.MarkRelated(user, Now).IsFailure);
    }
}
