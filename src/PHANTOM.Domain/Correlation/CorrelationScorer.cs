using System.Globalization;
using System.Text;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Domain.Correlation;

/// <summary>Weights and thresholds for duplicate suggestions (Requirement 7.2; design §6 defaults).</summary>
public sealed record CorrelationSettings
{
    public double GeoWeight { get; init; } = 0.35;
    public double TimeWeight { get; init; } = 0.15;
    public double CategoryWeight { get; init; } = 0.15;
    public double TextWeight { get; init; } = 0.20;
    public double LocationWeight { get; init; } = 0.15;
    public double GeoRadiusMeters { get; init; } = 1500;
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(6);
    public double SuggestThreshold { get; init; } = 0.55;
    public int MaxCandidates { get; init; } = 100;

    /// <summary>Applied when fewer than this many signals are available: less evidence, lower confidence.</summary>
    public const int MinSignalsForFullConfidence = 3;

    public const double SparseEvidenceFactor = 0.8;
}

/// <summary>What the scorer knows about one incident. <c>PlaceNames</c> are AI-extracted places plus the location text.</summary>
public sealed record IncidentFeatures(
    Guid Id,
    DateTimeOffset CreatedAt,
    IncidentCategory Category,
    double? Latitude,
    double? Longitude,
    string Text,
    IReadOnlyCollection<string> PlaceNames);

public sealed record SignalScore(bool Available, double Value);

public sealed record CorrelationResult(
    double Score,
    SignalScore Geo,
    SignalScore Time,
    SignalScore Category,
    SignalScore Text,
    SignalScore Locations,
    double? DistanceMeters,
    double MinutesApart,
    IReadOnlyList<string> SharedPlaceTokens,
    string Explanation);

/// <summary>
/// Deterministic, side-effect-free duplicate scoring (design §6). Properties: the score is in [0, 1],
/// symmetric in its two incidents, and never decreases when one available signal improves.
/// </summary>
public static class CorrelationScorer
{
    private const double EarthRadiusMeters = 6_371_000;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "of", "in", "on", "at", "to", "for", "from", "by", "with", "near", "next",
        "is", "are", "was", "were", "be", "been", "it", "its", "this", "that", "there", "here", "i", "we", "you", "they",
        "he", "she", "my", "our", "their", "his", "her", "me", "us", "them", "as", "so", "very", "just", "now", "can",
        "has", "have", "had", "not", "no", "some", "all", "any", "out", "up", "down", "over", "into", "about", "please",
        "help", "someone", "people", "person", "see", "seen", "saw", "there's", "im", "dont", "also", "still",
    };

    /// <summary>Generic street words that would make unrelated places look alike ("Dock Road" vs "Depot Road").</summary>
    private static readonly HashSet<string> PlaceNoise = new(StringComparer.Ordinal)
    {
        "road", "rd", "street", "st", "avenue", "ave", "way", "lane", "close", "crescent", "drive", "junction",
        "bus", "stop", "area", "side", "beside", "opposite", "behind", "front", "corner", "estate",
    };

    public static CorrelationResult Score(IncidentFeatures a, IncidentFeatures b, CorrelationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(settings);

        double? distance = null;
        var geo = new SignalScore(false, 0);
        if (a.Latitude is { } aLat && a.Longitude is { } aLon && b.Latitude is { } bLat && b.Longitude is { } bLon)
        {
            distance = HaversineMeters(aLat, aLon, bLat, bLon);
            geo = new SignalScore(true, Clamp(1 - (distance.Value / settings.GeoRadiusMeters)));
        }

        var minutes = Math.Abs((a.CreatedAt - b.CreatedAt).TotalMinutes);
        var time = new SignalScore(true, Clamp(1 - (minutes / settings.Window.TotalMinutes)));

        var category = new SignalScore(true,
            a.Category == b.Category ? 1
            : a.Category == IncidentCategory.Other || b.Category == IncidentCategory.Other ? 0.5
            : 0);

        var textA = Tokenize(a.Text);
        var textB = Tokenize(b.Text);
        var text = textA.Count == 0 && textB.Count == 0 ? new SignalScore(false, 0) : new SignalScore(true, Jaccard(textA, textB));

        var placesA = PlaceTokens(a.PlaceNames);
        var placesB = PlaceTokens(b.PlaceNames);
        var shared = placesA.Intersect(placesB).Order(StringComparer.Ordinal).ToList();
        var locations = placesA.Count == 0 && placesB.Count == 0 ? new SignalScore(false, 0) : new SignalScore(true, Jaccard(placesA, placesB));

        var score = Combine(
        [
            (geo, settings.GeoWeight),
            (time, settings.TimeWeight),
            (category, settings.CategoryWeight),
            (text, settings.TextWeight),
            (locations, settings.LocationWeight),
        ]);

        return new CorrelationResult(score, geo, time, category, text, locations, distance, minutes, shared,
            Explain(distance, minutes, a.Category, b.Category, text, shared));
    }

    /// <summary>
    /// Weighted mean over available signals, damped when evidence is sparse. Non-decreasing in each
    /// available signal's value for fixed availability and weights.
    /// </summary>
    public static double Combine(IReadOnlyList<(SignalScore Signal, double Weight)> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        double weighted = 0, totalWeight = 0;
        var available = 0;
        foreach (var (signal, weight) in signals)
        {
            if (!signal.Available || weight <= 0)
            {
                continue;
            }

            weighted += weight * Clamp(signal.Value);
            totalWeight += weight;
            available++;
        }

        if (totalWeight <= 0)
        {
            return 0;
        }

        var score = weighted / totalWeight;
        if (available < CorrelationSettings.MinSignalsForFullConfidence)
        {
            score *= CorrelationSettings.SparseEvidenceFactor;
        }

        return Clamp(score);
    }

    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Lower-case, punctuation stripped, stop-words removed, light stemming.</summary>
    public static HashSet<string> Tokenize(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var normalised = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            normalised.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        foreach (var raw in normalised.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 2 || StopWords.Contains(raw))
            {
                continue;
            }

            tokens.Add(Stem(raw));
        }

        return tokens;
    }

    private static HashSet<string> PlaceTokens(IEnumerable<string> placeNames)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in placeNames)
        {
            tokens.UnionWith(Tokenize(name).Where(t => !PlaceNoise.Contains(t)));
        }

        return tokens;
    }

    private static string Stem(string word)
    {
        if (word.Length <= 4 || word.All(char.IsDigit))
        {
            return word;
        }

        foreach (var suffix in (string[])["ing", "ed", "es", "s"])
        {
            if (word.EndsWith(suffix, StringComparison.Ordinal) && word.Length - suffix.Length >= 3)
            {
                return word[..^suffix.Length];
            }
        }

        return word;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }

        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    private static string Explain(double? distance, double minutes, IncidentCategory a, IncidentCategory b, SignalScore text, List<string> shared)
    {
        var parts = new List<string>
        {
            distance is null ? "distance unknown (coordinates missing)"
                : distance < 1000 ? string.Create(CultureInfo.InvariantCulture, $"reported {distance:0} m apart")
                : string.Create(CultureInfo.InvariantCulture, $"reported {distance / 1000:0.0} km apart"),
            minutes < 60 ? string.Create(CultureInfo.InvariantCulture, $"{minutes:0} min apart")
                : string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(minutes / 60):0} h {minutes % 60:0} min apart"),
            a == b ? "same category" : a == IncidentCategory.Other || b == IncidentCategory.Other ? "one category is Other" : "different categories",
        };

        if (text.Available)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{text.Value * 100:0}% word overlap"));
        }

        if (shared.Count > 0)
        {
            parts.Add($"shared place names: {string.Join(", ", shared.Take(5))}");
        }

        var sentence = string.Join(", ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }
}
