using Microsoft.ML;
using Microsoft.ML.Data;
using SismoAI.Application;

namespace SismoAI.MachineLearning;

public sealed class BaselinePeruMachineLearningService : IMachineLearningService
{
    private static readonly string[] FeatureColumns =
    [
        nameof(ModelInput.EarthquakeCount),
        nameof(ModelInput.SignificantEarthquakeCount),
        nameof(ModelInput.MaxMagnitude),
        nameof(ModelInput.MeanMagnitude),
        nameof(ModelInput.MeanDepthKm),
        nameof(ModelInput.TotalEnergyJoules),
        nameof(ModelInput.Temperature2mMean),
        nameof(ModelInput.PrecipitationSum),
        nameof(ModelInput.PressureMslMean),
        nameof(ModelInput.RelativeHumidity2mMean),
        nameof(ModelInput.WindSpeed10mMean),
        nameof(ModelInput.SoilMoisture0To10cmMean),
        nameof(ModelInput.ShortwaveRadiationSum)
    ];

    public Task<PeruBaselineClassificationDto> BuildPeruBaselineAsync(
        IReadOnlyList<PeruDailyFeatureDto> features,
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
                Temperature2mMean = (float)(x.Temperature2mMean ?? 0),
                PrecipitationSum = (float)(x.PrecipitationSum ?? 0),
                PressureMslMean = (float)(x.PressureMslMean ?? 0),
                RelativeHumidity2mMean = (float)(x.RelativeHumidity2mMean ?? 0),
                WindSpeed10mMean = (float)(x.WindSpeed10mMean ?? 0),
                SoilMoisture0To10cmMean = (float)(x.SoilMoisture0To10cmMean ?? 0),
                ShortwaveRadiationSum = (float)(x.ShortwaveRadiationSum ?? 0),
                Label = x.NextDayHadSignificantEarthquake
            })
            .ToList();

        if (usable.Count < 60)
        {
            return Task.FromResult(new PeruBaselineClassificationDto(
                false,
                "SDCA Logistic Regression",
                "Aún no hay suficientes muestras diarias para entrenar un baseline estable de Perú.",
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

        var splitIndex = Math.Clamp((int)Math.Floor(usable.Count * 0.8), 40, usable.Count - 10);
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

        var topPositive = influences.Where(x => x.Weight > 0).Take(4).ToList();
        var topNegative = influences.Where(x => x.Weight < 0).Take(4).ToList();
            if (!trainHasBothClasses || !testHasBothClasses)
            {
                return Task.FromResult(new PeruBaselineClassificationDto(
                    false,
                    "SDCA Logistic Regression",
                    BuildInsufficientClassSummary(
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
            var summary = BuildSummary(metrics, latestScored.Probability, latestScored.PredictedLabel, usable.Count);

        return Task.FromResult(new PeruBaselineClassificationDto(
            true,
            "SDCA Logistic Regression",
            summary,
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

    private static float NormalizeEnergy(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return (float)Math.Log10(value + 1);
    }

    private static string BuildSummary(BinaryClassificationMetrics metrics, float probability, bool predictedLabel, int sampleCount)
    {
        var tendency = predictedLabel ? "probabilidad elevada" : "probabilidad baja";
        return $"Baseline Perú entrenado con {sampleCount} días. " +
               $"En prueba logró accuracy {metrics.Accuracy:P1} y F1 {metrics.F1Score:P1}. " +
               $"Para el último día observado estima {tendency} de sismo significativo al día siguiente ({probability:P1}).";
    }

    private static string BuildInsufficientClassSummary(
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

        return $"Baseline Perú entrenado con {sampleCount} días, pero la partición de {missingPartition} no tuvo ambas clases " +
               $"({trainCount} entrenamiento, {testCount} prueba), así que no se calcularon métricas AUC/F1 confiables. " +
               $"Para el último día observado estima {tendency} de sismo significativo al día siguiente ({probability:P1}).";
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
            nameof(ModelInput.Temperature2mMean) => input.Temperature2mMean,
            nameof(ModelInput.PrecipitationSum) => input.PrecipitationSum,
            nameof(ModelInput.PressureMslMean) => input.PressureMslMean,
            nameof(ModelInput.RelativeHumidity2mMean) => input.RelativeHumidity2mMean,
            nameof(ModelInput.WindSpeed10mMean) => input.WindSpeed10mMean,
            nameof(ModelInput.SoilMoisture0To10cmMean) => input.SoilMoisture0To10cmMean,
            nameof(ModelInput.ShortwaveRadiationSum) => input.ShortwaveRadiationSum,
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
            nameof(ModelInput.Temperature2mMean) => "Temperatura media",
            nameof(ModelInput.PrecipitationSum) => "Precipitación",
            nameof(ModelInput.PressureMslMean) => "Presión atmosférica",
            nameof(ModelInput.RelativeHumidity2mMean) => "Humedad relativa",
            nameof(ModelInput.WindSpeed10mMean) => "Viento medio",
            nameof(ModelInput.SoilMoisture0To10cmMean) => "Humedad de suelo",
            nameof(ModelInput.ShortwaveRadiationSum) => "Radiación solar",
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
        public float Temperature2mMean { get; set; }
        public float PrecipitationSum { get; set; }
        public float PressureMslMean { get; set; }
        public float RelativeHumidity2mMean { get; set; }
        public float WindSpeed10mMean { get; set; }
        public float SoilMoisture0To10cmMean { get; set; }
        public float ShortwaveRadiationSum { get; set; }
        public bool Label { get; set; }
    }

    private sealed class ModelPrediction
    {
        public bool PredictedLabel { get; set; }
        public float Probability { get; set; }
    }
}
