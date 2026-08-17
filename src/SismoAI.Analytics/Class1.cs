using SismoAI.Application;
using SismoAI.Domain;

namespace SismoAI.Analytics;

public sealed class StatisticalAnalyticsEngine : IAnalyticsEngine
{
    private static readonly (string Keyword, string Country)[] CountryHints =
    [
        ("perú", "Perú"),
        ("peru", "Perú"),
        ("lima", "Perú"),
        ("ancash", "Perú"),
        ("áncash", "Perú"),
        ("ica", "Perú"),
        ("arequipa", "Perú"),
        ("cañete", "Perú"),
        ("nasca", "Perú"),
        ("huari", "Perú"),
        ("chile", "Chile"),
        ("argentina", "Argentina"),
        ("ecuador", "Ecuador"),
        ("colombia", "Colombia"),
        ("bolivia", "Bolivia"),
        ("mexico", "México"),
        ("méxico", "México"),
        ("guatemala", "Guatemala"),
        ("costa rica", "Costa Rica"),
        ("nicaragua", "Nicaragua"),
        ("panamá", "Panamá"),
        ("panama", "Panamá"),
        ("el salvador", "El Salvador"),
        ("california", "Estados Unidos"),
        ("nevada", "Estados Unidos"),
        ("alaska", "Estados Unidos"),
        ("hawaii", "Estados Unidos"),
        ("oklahoma", "Estados Unidos"),
        ("texas", "Estados Unidos"),
        ("puerto rico", "Estados Unidos"),
        ("united states", "Estados Unidos"),
        ("usa", "Estados Unidos"),
        ("canada", "Canadá"),
        ("canadá", "Canadá"),
        ("austria", "Austria"),
        ("greece", "Grecia"),
        ("grecia", "Grecia"),
        ("italy", "Italia"),
        ("italia", "Italia"),
        ("japan", "Japón"),
        ("japón", "Japón"),
        ("indonesia", "Indonesia"),
        ("philippines", "Filipinas"),
        ("filipinas", "Filipinas"),
        ("turkiye", "Turquía"),
        ("türkiye", "Turquía"),
        ("turquía", "Turquía"),
        ("turquia", "Turquía"),
        ("china", "China"),
        ("russia", "Rusia"),
        ("rusia", "Rusia"),
        ("afghanistan", "Afganistán"),
        ("afganistán", "Afganistán"),
        ("iran", "Irán"),
        ("irán", "Irán"),
        ("new zealand", "Nueva Zelanda"),
        ("nueva zelanda", "Nueva Zelanda")
    ];

    public AnalyticsResult Analyze(IReadOnlyList<EarthquakeEvent> recentEvents)
    {
        if (recentEvents.Count == 0)
        {
            return new AnalyticsResult(0, "normal", "Sin eventos recientes suficientes para evaluar anomalías.", []);
        }

        var ordered = recentEvents.OrderByDescending(x => x.OriginTimeUtc).ToList();
        var now = ordered[0].OriginTimeUtc;
        var last6Hours = ordered.Where(x => x.OriginTimeUtc >= now.AddHours(-6)).ToList();
        var baseline = ordered.Where(x => x.OriginTimeUtc < now.AddHours(-6)).ToList();

        if (baseline.Count < 10)
        {
            baseline = ordered;
        }

        var baselineMagnitude = baseline.Average(x => x.Magnitude);
        var baselineDepth = baseline.Average(x => x.DepthKm);
        var baselineHourlyRate = Math.Max(1d, baseline.Count / 24d);

        var currentHourlyRate = Math.Max(0d, last6Hours.Count / 6d);
        var rateDelta = (currentHourlyRate - baselineHourlyRate) / baselineHourlyRate;
        var magnitudeDelta = last6Hours.Count == 0 ? 0 : last6Hours.Average(x => x.Magnitude) - baselineMagnitude;
        var shallowRatio = last6Hours.Count == 0 ? 0 : last6Hours.Count(x => x.DepthKm <= 40) / (double)last6Hours.Count;
        var shallowBaseline = baseline.Count == 0 ? 0 : baseline.Count(x => x.DepthKm <= 40) / (double)baseline.Count;
        var shallowDelta = shallowRatio - shallowBaseline;
        var maxMagnitude = last6Hours.Count == 0 ? ordered.Max(x => x.Magnitude) : last6Hours.Max(x => x.Magnitude);
        var elevatedLocation = DescribeDominantLocation(last6Hours);

        var score = 0d;
        var drivers = new List<string>();

        if (rateDelta > 0.5)
        {
            score += Math.Min(35, rateDelta * 20);
            var locationSuffix = string.IsNullOrWhiteSpace(elevatedLocation) ? string.Empty : $" en {elevatedLocation}";
            drivers.Add($"Frecuencia sísmica elevada{locationSuffix} ({currentHourlyRate:F1}/h frente a {baselineHourlyRate:F1}/h).");
        }

        if (magnitudeDelta > 0.35)
        {
            score += Math.Min(25, magnitudeDelta * 30);
            drivers.Add($"Magnitud media reciente superior a la línea base ({baselineMagnitude:F2} -> {baselineMagnitude + magnitudeDelta:F2}).");
        }

        if (shallowDelta > 0.15)
        {
            score += Math.Min(20, shallowDelta * 60);
            drivers.Add("Aumentó la proporción de eventos someros en la ventana reciente.");
        }

        if (maxMagnitude >= 5.5)
        {
            score += Math.Min(20, (maxMagnitude - 5) * 12);
            drivers.Add($"Se detectó un evento destacado de magnitud {maxMagnitude:F1}.");
        }

        score = Math.Round(Math.Clamp(score, 0, 100), 2);
        var level = score switch
        {
            >= 70 => "anomalia",
            >= 40 => "correlacion",
            >= 20 => "actividad-elevada",
            _ => "normal"
        };

        var summary = level switch
        {
            "anomalia" => AppendLocationContext(
                "La actividad reciente se desvía de la línea base histórica y merece seguimiento estadístico.",
                elevatedLocation,
                "con concentración principal en"),
            "correlacion" => AppendLocationContext(
                "Se observan señales elevadas, pero todavía dentro de un rango de correlación exploratoria.",
                elevatedLocation,
                "con mayor concentración en"),
            "actividad-elevada" => AppendLocationContext(
                "La actividad subió respecto al promedio reciente sin constituir una anomalía fuerte.",
                elevatedLocation,
                "principalmente en"),
            _ => "La actividad reciente se mantiene dentro del rango esperado de la muestra disponible."
        };

        return new AnalyticsResult(score, level, summary, drivers);
    }

    public IReadOnlyList<TimelinePointDto> BuildBacktest(IReadOnlyList<EarthquakeEvent> events)
    {
        if (events.Count == 0)
        {
            return [];
        }

        var ordered = events.OrderBy(x => x.OriginTimeUtc).ToList();
        var result = new List<TimelinePointDto>();

        for (var index = 12; index < ordered.Count; index++)
        {
            var window = ordered.Take(index + 1).Reverse().Take(48).ToList();
            var score = Analyze(window).AnomalyScore;
            result.Add(new TimelinePointDto(ordered[index].OriginTimeUtc, score));
        }

        return result;
    }

    private static string AppendLocationContext(string summary, string? location, string connector)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return summary;
        }

        return $"{summary.TrimEnd('.')} {connector} {location}.";
    }

    private static string? DescribeDominantLocation(IReadOnlyList<EarthquakeEvent> events)
    {
        if (events.Count == 0)
        {
            return null;
        }

        var dominant = events
            .Select(x => new
            {
                Country = InferCountry(x),
                Region = ExtractRegionHint(x.LocationDescription)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Country) || !string.IsNullOrWhiteSpace(x.Region))
            .GroupBy(x => x.Country ?? x.Region!)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();

        if (dominant is null)
        {
            return null;
        }

        var country = dominant.Key;
        var region = dominant
            .Select(x => x.Region)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, country, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(region) ? country : $"{country} ({region})";
    }

    private static string? InferCountry(EarthquakeEvent earthquakeEvent)
    {
        if (string.Equals(earthquakeEvent.Source, "IGP", StringComparison.OrdinalIgnoreCase))
        {
            return "Perú";
        }

        var description = earthquakeEvent.LocationDescription?.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var normalized = description.ToLowerInvariant();
        foreach (var (keyword, country) in CountryHints)
        {
            if (normalized.Contains(keyword, StringComparison.Ordinal))
            {
                return country;
            }
        }

        return null;
    }

    private static string? ExtractRegionHint(string? locationDescription)
    {
        if (string.IsNullOrWhiteSpace(locationDescription))
        {
            return null;
        }

        var text = locationDescription.Trim();
        if (text.Contains(" - ", StringComparison.Ordinal))
        {
            return text.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        }

        if (text.Contains(',', StringComparison.Ordinal))
        {
            return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        }

        return text.Any(char.IsDigit) ? null : text;
    }
}
