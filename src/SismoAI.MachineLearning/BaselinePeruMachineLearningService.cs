using Microsoft.ML;
using Microsoft.ML.Data;
using SismoAI.Application;

namespace SismoAI.MachineLearning;

public sealed class BaselinePeruMachineLearningService : IMachineLearningService
{
    private const string ModelName = "Baseline sísmico regional multi-escala";

    private static readonly string[] FeatureColumns =
    [
        nameof(ModelInput.EarthquakeCount),
        nameof(ModelInput.SignificantEarthquakeCount),
        nameof(ModelInput.MaxMagnitude),
        nameof(ModelInput.MeanMagnitude),
        nameof(ModelInput.MeanDepthKm),
        nameof(ModelInput.TotalEnergyJoules),
        nameof(ModelInput.EarthquakeCount7d),
        nameof(ModelInput.EarthquakeCount30d),
        nameof(ModelInput.SignificantEarthquakeCount7d),
        nameof(ModelInput.SignificantEarthquakeCount30d),
        nameof(ModelInput.MaxMagnitude7d),
        nameof(ModelInput.MaxMagnitude30d),
        nameof(ModelInput.MeanMagnitude7d),
        nameof(ModelInput.MeanMagnitude30d),
        nameof(ModelInput.TotalEnergyJoules7d),
        nameof(ModelInput.TotalEnergyJoules30d),
        nameof(ModelInput.BValue30d),
        nameof(ModelInput.SignificantRate7d),
        nameof(ModelInput.SignificantRate30d),
        nameof(ModelInput.ActivityRatio7dTo30d),
        nameof(ModelInput.SignificantActivityRatio7dTo30d),
        nameof(ModelInput.EnergyRatio7dTo30d),
        nameof(ModelInput.DaysSinceLastSignificant),
        nameof(ModelInput.Temperature2mMean),
        nameof(ModelInput.PrecipitationSum),
        nameof(ModelInput.PressureMslMean),
        nameof(ModelInput.RelativeHumidity2mMean),
        nameof(ModelInput.WindSpeed10mMean),
        nameof(ModelInput.SoilMoisture0To10cmMean),
        nameof(ModelInput.ShortwaveRadiationSum),
        nameof(ModelInput.GeomagneticSampleCount),
        nameof(ModelInput.GeomagneticRangeX),
        nameof(ModelInput.GeomagneticRangeY),
        nameof(ModelInput.GeomagneticRangeZ),
        nameof(ModelInput.GeomagneticRangeF),
        nameof(ModelInput.GeomagneticMeanAbsDeltaF)
    ];

    public Task<CountryBaselineClassificationDto> BuildCountryBaselineAsync(
        string countryCode,
        string countryName,
        IReadOnlyList<CountryDailyFeatureDto> features,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var usable = features
            .OrderBy(x => x.Date)
            .Select(x => new ModelInput
            {
                Date = x.Date.ToDateTime(TimeOnly.MinValue),
                EarthquakeCount = x.EarthquakeCount,
                SignificantEarthquakeCount = x.SignificantEarthquakeCount,
                MaxMagnitude = (float)x.MaxMagnitude,
                MeanMagnitude = (float)x.MeanMagnitude,
                MeanDepthKm = (float)x.MeanDepthKm,
                TotalEnergyJoules = NormalizeEnergy(x.TotalEnergyJoules),
                EarthquakeCount7d = x.EarthquakeCount7d,
                EarthquakeCount30d = x.EarthquakeCount30d,
                SignificantEarthquakeCount7d = x.SignificantEarthquakeCount7d,
                SignificantEarthquakeCount30d = x.SignificantEarthquakeCount30d,
                MaxMagnitude7d = (float)x.MaxMagnitude7d,
                MaxMagnitude30d = (float)x.MaxMagnitude30d,
                MeanMagnitude7d = (float)x.MeanMagnitude7d,
                MeanMagnitude30d = (float)x.MeanMagnitude30d,
                TotalEnergyJoules7d = NormalizeEnergy(x.TotalEnergyJoules7d),
                TotalEnergyJoules30d = NormalizeEnergy(x.TotalEnergyJoules30d),
                BValue30d = (float)x.BValue30d,
                SignificantRate7d = (float)x.SignificantRate7d,
                SignificantRate30d = (float)x.SignificantRate30d,
                ActivityRatio7dTo30d = (float)x.ActivityRatio7dTo30d,
                SignificantActivityRatio7dTo30d = (float)x.SignificantActivityRatio7dTo30d,
                EnergyRatio7dTo30d = (float)x.EnergyRatio7dTo30d,
                DaysSinceLastSignificant = x.DaysSinceLastSignificant,
                Temperature2mMean = (float)(x.Temperature2mMean ?? 0),
                PrecipitationSum = (float)(x.PrecipitationSum ?? 0),
                PressureMslMean = (float)(x.PressureMslMean ?? 0),
                RelativeHumidity2mMean = (float)(x.RelativeHumidity2mMean ?? 0),
                WindSpeed10mMean = (float)(x.WindSpeed10mMean ?? 0),
                SoilMoisture0To10cmMean = (float)(x.SoilMoisture0To10cmMean ?? 0),
                ShortwaveRadiationSum = (float)(x.ShortwaveRadiationSum ?? 0),
                GeomagneticSampleCount = x.GeomagneticSampleCount,
                GeomagneticRangeX = (float)(x.GeomagneticRangeX ?? 0),
                GeomagneticRangeY = (float)(x.GeomagneticRangeY ?? 0),
                GeomagneticRangeZ = (float)(x.GeomagneticRangeZ ?? 0),
                GeomagneticRangeF = (float)(x.GeomagneticRangeF ?? 0),
                GeomagneticMeanAbsDeltaF = (float)(x.GeomagneticMeanAbsDeltaF ?? 0),
                Label = x.NextDayHadSignificantEarthquake
            })
            .ToList();

        if (usable.Count < 90)
        {
            return Task.FromResult(new CountryBaselineClassificationDto(
                countryCode,
                countryName,
                false,
                ModelName,
                $"Aún no hay suficientes muestras diarias para entrenar un baseline sísmico regional estable de {countryName}.",
                usable.Count,
                0,
                0,
                usable.Count == 0 ? 0 : Math.Round(usable.Count(x => x.Label) / (double)usable.Count, 4),
                0,
                0,
                0,
                0,
                0,
                false,
                usable.LastOrDefault()?.Date is DateTime latestDate ? DateOnly.FromDateTime(latestDate) : null,
                [],
                []));
        }

        var splitIndex = FindBestTemporalSplitIndex(usable);
        var trainRows = usable.Take(splitIndex).ToList();
        var testRows = usable.Skip(splitIndex).ToList();
        var trainHasBothClasses = HasBothClasses(trainRows);
        var testHasBothClasses = HasBothClasses(testRows);

        var mlContext = new MLContext(seed: 42);
        var trainData = mlContext.Data.LoadFromEnumerable(trainRows);

        var pipeline = mlContext.Transforms.Concatenate("Features", FeatureColumns)
            .Append(mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                labelColumnName: nameof(ModelInput.Label),
                featureColumnName: "Features"));

        var model = pipeline.Fit(trainData);
        var latestInput = mlContext.Data.LoadFromEnumerable([usable[^1]]);
        var latestPrediction = model.Transform(latestInput);
        var latestScored = mlContext.Data.CreateEnumerable<ModelPrediction>(latestPrediction, reuseRowObject: false).First();
        var influences = BuildFeatureInfluences(trainRows)
            .OrderByDescending(x => Math.Abs(x.Weight))
            .ToList();

        var topPositive = influences.Where(x => x.Weight > 0).Take(5).ToList();
        var topNegative = influences.Where(x => x.Weight < 0).Take(5).ToList();

        if (!trainHasBothClasses || !testHasBothClasses)
        {
            return Task.FromResult(new CountryBaselineClassificationDto(
                countryCode,
                countryName,
                false,
                ModelName,
                BuildInsufficientClassSummary(
                    countryName,
                    latestScored.Probability,
                    latestScored.PredictedLabel,
                    usable.Count,
                    trainRows.Count,
                    testRows.Count,
                    trainHasBothClasses,
                    testHasBothClasses),
                usable.Count,
                trainRows.Count,
                testRows.Count,
                Math.Round(usable.Count(x => x.Label) / (double)usable.Count, 4),
                0,
                0,
                0,
                0,
                Math.Round(latestScored.Probability, 4),
                latestScored.PredictedLabel,
                DateOnly.FromDateTime(usable[^1].Date),
                topPositive,
                topNegative));
        }

        var testData = mlContext.Data.LoadFromEnumerable(testRows);
        var predictions = model.Transform(testData);
        var metrics = mlContext.BinaryClassification.Evaluate(
            predictions,
            labelColumnName: nameof(ModelInput.Label));

        return Task.FromResult(new CountryBaselineClassificationDto(
            countryCode,
            countryName,
            true,
            ModelName,
            BuildSummary(countryName, metrics, latestScored.Probability, latestScored.PredictedLabel, usable.Count),
            usable.Count,
            trainRows.Count,
            testRows.Count,
            Math.Round(usable.Count(x => x.Label) / (double)usable.Count, 4),
            Math.Round(metrics.Accuracy, 4),
            Math.Round(metrics.F1Score, 4),
            Math.Round(metrics.AreaUnderRocCurve, 4),
            Math.Round(metrics.AreaUnderPrecisionRecallCurve, 4),
            Math.Round(latestScored.Probability, 4),
            latestScored.PredictedLabel,
            DateOnly.FromDateTime(usable[^1].Date),
            topPositive,
            topNegative));
    }

    private static int FindBestTemporalSplitIndex(IReadOnlyList<ModelInput> usable)
    {
        var preferred = Math.Clamp((int)Math.Floor(usable.Count * 0.8), 60, usable.Count - 15);
        var minSplit = Math.Clamp((int)Math.Floor(usable.Count * 0.65), 60, usable.Count - 15);
        var maxSplit = Math.Clamp((int)Math.Floor(usable.Count * 0.9), minSplit, usable.Count - 15);

        var candidates = Enumerable.Range(minSplit, maxSplit - minSplit + 1)
            .OrderBy(index => Math.Abs(index - preferred));

        foreach (var candidate in candidates)
        {
            var trainRows = usable.Take(candidate).ToList();
            var testRows = usable.Skip(candidate).ToList();
            if (HasBothClasses(trainRows) && HasBothClasses(testRows))
            {
                return candidate;
            }
        }

        return preferred;
    }

    private static float NormalizeEnergy(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return (float)Math.Log10(value + 1);
    }

    private static string BuildSummary(string countryName, BinaryClassificationMetrics metrics, float probability, bool predictedLabel, int sampleCount)
    {
        var tendency = predictedLabel ? "probabilidad elevada" : "probabilidad baja";
        return $"Baseline sísmico regional de {countryName} entrenado con {sampleCount} días y señales multi-escala " +
               $"(actividad 1/7/30 días, frecuencia de sismos fuertes, energía y b-value). " +
               $"En prueba logró accuracy {metrics.Accuracy:P1}, F1 {metrics.F1Score:P1} y ROC AUC {metrics.AreaUnderRocCurve:P1}. " +
               $"Para el último día observado estima {tendency} de sismo significativo al día siguiente ({probability:P1}).";
    }

    private static string BuildInsufficientClassSummary(
        string countryName,
        float probability,
        bool predictedLabel,
        int sampleCount,
        int trainCount,
        int testCount,
        bool trainHasBothClasses,
        bool testHasBothClasses)
    {
        var tendency = predictedLabel ? "probabilidad elevada" : "probabilidad baja";
        var missingPartition = !trainHasBothClasses && !testHasBothClasses
            ? "entrenamiento y prueba"
            : !trainHasBothClasses
                ? "entrenamiento"
                : "prueba";

        return $"Baseline sísmico regional de {countryName} entrenado con {sampleCount} días y señales multi-escala, " +
               $"pero la partición de {missingPartition} no tuvo ambas clases ({trainCount} entrenamiento, {testCount} prueba). " +
               $"Se mantuvo la lectura operativa del último día, con {tendency} de sismo significativo al día siguiente ({probability:P1}), " +
               $"aunque aún no hay una evaluación AUC/F1 confiable.";
    }

    private static bool HasBothClasses(IReadOnlyList<ModelInput> rows)
    {
        return rows.Any(x => x.Label) && rows.Any(x => !x.Label);
    }

    private static IReadOnlyList<FeatureInfluenceDto> BuildFeatureInfluences(IReadOnlyList<ModelInput> rows)
    {
        var positives = rows.Where(x => x.Label).ToList();
        var negatives = rows.Where(x => !x.Label).ToList();
        if (positives.Count == 0 || negatives.Count == 0)
        {
            return [];
        }

        return FeatureColumns
            .Select(name =>
            {
                var positiveMean = positives.Average(x => ReadFeatureValue(x, name));
                var negativeMean = negatives.Average(x => ReadFeatureValue(x, name));
                var delta = positiveMean - negativeMean;

                return new FeatureInfluenceDto(
                    ToReadableName(name),
                    Math.Round(delta, 4),
                    delta >= 0 ? "incrementa" : "reduce");
            })
            .ToList();
    }

    private static float ReadFeatureValue(ModelInput input, string name)
    {
        return name switch
        {
            nameof(ModelInput.EarthquakeCount) => input.EarthquakeCount,
            nameof(ModelInput.SignificantEarthquakeCount) => input.SignificantEarthquakeCount,
            nameof(ModelInput.MaxMagnitude) => input.MaxMagnitude,
            nameof(ModelInput.MeanMagnitude) => input.MeanMagnitude,
            nameof(ModelInput.MeanDepthKm) => input.MeanDepthKm,
            nameof(ModelInput.TotalEnergyJoules) => input.TotalEnergyJoules,
            nameof(ModelInput.EarthquakeCount7d) => input.EarthquakeCount7d,
            nameof(ModelInput.EarthquakeCount30d) => input.EarthquakeCount30d,
            nameof(ModelInput.SignificantEarthquakeCount7d) => input.SignificantEarthquakeCount7d,
            nameof(ModelInput.SignificantEarthquakeCount30d) => input.SignificantEarthquakeCount30d,
            nameof(ModelInput.MaxMagnitude7d) => input.MaxMagnitude7d,
            nameof(ModelInput.MaxMagnitude30d) => input.MaxMagnitude30d,
            nameof(ModelInput.MeanMagnitude7d) => input.MeanMagnitude7d,
            nameof(ModelInput.MeanMagnitude30d) => input.MeanMagnitude30d,
            nameof(ModelInput.TotalEnergyJoules7d) => input.TotalEnergyJoules7d,
            nameof(ModelInput.TotalEnergyJoules30d) => input.TotalEnergyJoules30d,
            nameof(ModelInput.BValue30d) => input.BValue30d,
            nameof(ModelInput.SignificantRate7d) => input.SignificantRate7d,
            nameof(ModelInput.SignificantRate30d) => input.SignificantRate30d,
            nameof(ModelInput.ActivityRatio7dTo30d) => input.ActivityRatio7dTo30d,
            nameof(ModelInput.SignificantActivityRatio7dTo30d) => input.SignificantActivityRatio7dTo30d,
            nameof(ModelInput.EnergyRatio7dTo30d) => input.EnergyRatio7dTo30d,
            nameof(ModelInput.DaysSinceLastSignificant) => input.DaysSinceLastSignificant,
            nameof(ModelInput.Temperature2mMean) => input.Temperature2mMean,
            nameof(ModelInput.PrecipitationSum) => input.PrecipitationSum,
            nameof(ModelInput.PressureMslMean) => input.PressureMslMean,
            nameof(ModelInput.RelativeHumidity2mMean) => input.RelativeHumidity2mMean,
            nameof(ModelInput.WindSpeed10mMean) => input.WindSpeed10mMean,
            nameof(ModelInput.SoilMoisture0To10cmMean) => input.SoilMoisture0To10cmMean,
            nameof(ModelInput.ShortwaveRadiationSum) => input.ShortwaveRadiationSum,
            nameof(ModelInput.GeomagneticSampleCount) => input.GeomagneticSampleCount,
            nameof(ModelInput.GeomagneticRangeX) => input.GeomagneticRangeX,
            nameof(ModelInput.GeomagneticRangeY) => input.GeomagneticRangeY,
            nameof(ModelInput.GeomagneticRangeZ) => input.GeomagneticRangeZ,
            nameof(ModelInput.GeomagneticRangeF) => input.GeomagneticRangeF,
            nameof(ModelInput.GeomagneticMeanAbsDeltaF) => input.GeomagneticMeanAbsDeltaF,
            _ => 0
        };
    }

    private static string ToReadableName(string value)
    {
        return value switch
        {
            nameof(ModelInput.EarthquakeCount) => "Conteo diario de sismos",
            nameof(ModelInput.SignificantEarthquakeCount) => "Conteo diario de sismos significativos",
            nameof(ModelInput.MaxMagnitude) => "Magnitud máxima diaria",
            nameof(ModelInput.MeanMagnitude) => "Magnitud media diaria",
            nameof(ModelInput.MeanDepthKm) => "Profundidad media diaria",
            nameof(ModelInput.TotalEnergyJoules) => "Energía sísmica diaria",
            nameof(ModelInput.EarthquakeCount7d) => "Conteo sísmico 7 días",
            nameof(ModelInput.EarthquakeCount30d) => "Conteo sísmico 30 días",
            nameof(ModelInput.SignificantEarthquakeCount7d) => "Sismos significativos 7 días",
            nameof(ModelInput.SignificantEarthquakeCount30d) => "Sismos significativos 30 días",
            nameof(ModelInput.MaxMagnitude7d) => "Magnitud máxima 7 días",
            nameof(ModelInput.MaxMagnitude30d) => "Magnitud máxima 30 días",
            nameof(ModelInput.MeanMagnitude7d) => "Magnitud media 7 días",
            nameof(ModelInput.MeanMagnitude30d) => "Magnitud media 30 días",
            nameof(ModelInput.TotalEnergyJoules7d) => "Energía sísmica 7 días",
            nameof(ModelInput.TotalEnergyJoules30d) => "Energía sísmica 30 días",
            nameof(ModelInput.BValue30d) => "b-value 30 días",
            nameof(ModelInput.SignificantRate7d) => "Frecuencia de sismos fuertes 7 días",
            nameof(ModelInput.SignificantRate30d) => "Frecuencia de sismos fuertes 30 días",
            nameof(ModelInput.ActivityRatio7dTo30d) => "Aceleración sísmica 7d/30d",
            nameof(ModelInput.SignificantActivityRatio7dTo30d) => "Aceleración de sismos fuertes 7d/30d",
            nameof(ModelInput.EnergyRatio7dTo30d) => "Aceleración de energía 7d/30d",
            nameof(ModelInput.DaysSinceLastSignificant) => "Días desde el último sismo fuerte",
            nameof(ModelInput.Temperature2mMean) => "Temperatura media",
            nameof(ModelInput.PrecipitationSum) => "Precipitación",
            nameof(ModelInput.PressureMslMean) => "Presión atmosférica",
            nameof(ModelInput.RelativeHumidity2mMean) => "Humedad relativa",
            nameof(ModelInput.WindSpeed10mMean) => "Viento medio",
            nameof(ModelInput.SoilMoisture0To10cmMean) => "Humedad de suelo",
            nameof(ModelInput.ShortwaveRadiationSum) => "Radiación solar",
            nameof(ModelInput.GeomagneticSampleCount) => "Muestras geomagnéticas",
            nameof(ModelInput.GeomagneticRangeX) => "Rango geomagnético X",
            nameof(ModelInput.GeomagneticRangeY) => "Rango geomagnético Y",
            nameof(ModelInput.GeomagneticRangeZ) => "Rango geomagnético Z",
            nameof(ModelInput.GeomagneticRangeF) => "Rango geomagnético F",
            nameof(ModelInput.GeomagneticMeanAbsDeltaF) => "Variación media absoluta geomagnética F",
            _ => value
        };
    }

    private sealed class ModelInput
    {
        public DateTime Date { get; set; }
        public float EarthquakeCount { get; set; }
        public float SignificantEarthquakeCount { get; set; }
        public float MaxMagnitude { get; set; }
        public float MeanMagnitude { get; set; }
        public float MeanDepthKm { get; set; }
        public float TotalEnergyJoules { get; set; }
        public float EarthquakeCount7d { get; set; }
        public float EarthquakeCount30d { get; set; }
        public float SignificantEarthquakeCount7d { get; set; }
        public float SignificantEarthquakeCount30d { get; set; }
        public float MaxMagnitude7d { get; set; }
        public float MaxMagnitude30d { get; set; }
        public float MeanMagnitude7d { get; set; }
        public float MeanMagnitude30d { get; set; }
        public float TotalEnergyJoules7d { get; set; }
        public float TotalEnergyJoules30d { get; set; }
        public float BValue30d { get; set; }
        public float SignificantRate7d { get; set; }
        public float SignificantRate30d { get; set; }
        public float ActivityRatio7dTo30d { get; set; }
        public float SignificantActivityRatio7dTo30d { get; set; }
        public float EnergyRatio7dTo30d { get; set; }
        public float DaysSinceLastSignificant { get; set; }
        public float Temperature2mMean { get; set; }
        public float PrecipitationSum { get; set; }
        public float PressureMslMean { get; set; }
        public float RelativeHumidity2mMean { get; set; }
        public float WindSpeed10mMean { get; set; }
        public float SoilMoisture0To10cmMean { get; set; }
        public float ShortwaveRadiationSum { get; set; }
        public float GeomagneticSampleCount { get; set; }
        public float GeomagneticRangeX { get; set; }
        public float GeomagneticRangeY { get; set; }
        public float GeomagneticRangeZ { get; set; }
        public float GeomagneticRangeF { get; set; }
        public float GeomagneticMeanAbsDeltaF { get; set; }
        public bool Label { get; set; }
    }

    private sealed class ModelPrediction
    {
        public bool PredictedLabel { get; set; }
        public float Probability { get; set; }
    }
}
