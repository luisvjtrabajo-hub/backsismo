using SismoAI.Application;
using SismoAI.Domain;

namespace SismoAI.Analytics;

public sealed class StatisticalAnalyticsEngine : IAnalyticsEngine
{
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

        var score = 0d;
        var drivers = new List<string>();

        if (rateDelta > 0.5)
        {
            score += Math.Min(35, rateDelta * 20);
            drivers.Add($"Frecuencia sísmica elevada ({currentHourlyRate:F1}/h frente a {baselineHourlyRate:F1}/h).");
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
            "anomalia" => "La actividad reciente se desvía de la línea base histórica y merece seguimiento estadístico.",
            "correlacion" => "Se observan señales elevadas, pero todavía dentro de un rango de correlación exploratoria.",
            "actividad-elevada" => "La actividad subió respecto al promedio reciente sin constituir una anomalía fuerte.",
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
}
