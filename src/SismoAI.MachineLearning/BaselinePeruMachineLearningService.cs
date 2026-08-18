using Microsoft.ML;
using Microsoft.ML.Data;
using SismoAI.Application;

namespace SismoAI.MachineLearning;

public sealed class BaselinePeruMachineLearningService : IMachineLearningService
{
    private const string ComparisonModelName = "Comparativo baseline sismico multi-escala";
    private const int MinimumSamples = 90;

    private static readonly string[] SeismicFeatureColumns =
    [
        nameof(ModelInput.EarthquakeCount),
        nameof(ModelInput.SignificantEarthquakeCount),
        nameof(ModelInput.MaxMagnitude),
        nameof(ModelInput.MeanMagnitude),
        nameof(ModelInput.MeanDepthKm),
        nameof(ModelInput.TotalEnergyJoules),
        nameof(ModelInput.EarthquakeCount1d),
        nameof(ModelInput.SignificantEarthquakeCount1d),
        nameof(ModelInput.MaxMagnitude1d),
        nameof(ModelInput.TotalEnergyJoules1d),
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
        nameof(ModelInput.EtasRate1d),
        nameof(ModelInput.OmoriPressure3d),
        nameof(ModelInput.RecentEventDensity3d),
        nameof(ModelInput.RecentSignificantDensity7d),
        nameof(ModelInput.HoursSinceLastEvent),
        nameof(ModelInput.HoursSinceLastSignificant),
        nameof(ModelInput.DaysSinceLastSignificant)
    ];

    private static readonly string[] ClimateFeatureColumns =
    [
        nameof(ModelInput.Temperature2mMean),
        nameof(ModelInput.Temperature2mMax),
        nameof(ModelInput.Temperature2mMin),
        nameof(ModelInput.PrecipitationSum),
        nameof(ModelInput.PressureMslMean),
        nameof(ModelInput.RelativeHumidity2mMean),
        nameof(ModelInput.WindSpeed10mMean),
        nameof(ModelInput.SoilMoisture0To10cmMean),
        nameof(ModelInput.ShortwaveRadiationSum)
    ];

    private static readonly string[] GeomagneticFeatureColumns =
    [
        nameof(ModelInput.GeomagneticSampleCount),
        nameof(ModelInput.GeomagneticRangeX),
        nameof(ModelInput.GeomagneticRangeY),
        nameof(ModelInput.GeomagneticRangeZ),
        nameof(ModelInput.GeomagneticRangeF),
        nameof(ModelInput.GeomagneticMeanAbsDeltaF)
    ];

    private static readonly VariantDefinition[] VariantDefinitions =
    [
        new(
            "seismic_only",
            "Sismico puro",
            [.. SeismicFeatureColumns],
            1),
        new(
            "seismic_climate",
            "Sismico + clima",
            [.. SeismicFeatureColumns, .. ClimateFeatureColumns],
            2),
        new(
            "seismic_climate_geomagnetic",
            "Sismico + clima + geomagnetismo",
            [.. SeismicFeatureColumns, .. ClimateFeatureColumns, .. GeomagneticFeatureColumns],
            3)
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
            .Select(ToModelInput)
            .ToList();

        DateOnly? latestFeatureDate = usable.LastOrDefault()?.Date is DateTime latestDate
            ? DateOnly.FromDateTime(latestDate)
            : null;
        var positiveRate = usable.Count == 0
            ? 0
            : Math.Round(usable.Count(x => x.Label) / (double)usable.Count, 4);

        if (usable.Count < MinimumSamples)
        {
            var variants = VariantDefinitions
                .Select(variant => new BaselineVariantDto(
                    variant.Key,
                    variant.Name,
                    false,
                    $"Aun no hay suficientes muestras diarias para evaluar la variante {variant.Name.ToLowerInvariant()} en {countryName}.",
                    0,
                    0,
                    0,
                    0,
                    0,
                    false))
                .ToList();

            var selectedVariant = VariantDefinitions[^1];
            return Task.FromResult(new CountryBaselineClassificationDto(
                countryCode,
                countryName,
                false,
                selectedVariant.Key,
                ComparisonModelName,
                $"Aun no hay suficientes muestras diarias para comparar variantes del baseline de {countryName}.",
                usable.Count,
                0,
                0,
                positiveRate,
                0,
                0,
                0,
                0,
                0,
                false,
                latestFeatureDate,
                variants,
                [],
                []));
        }

        var splitIndex = FindBestTemporalSplitIndex(usable);
        var trainRows = usable.Take(splitIndex).ToList();
        var testRows = usable.Skip(splitIndex).ToList();

        var variantResults = VariantDefinitions
            .Select(definition => BuildVariantResult(definition, countryName, usable, trainRows, testRows))
            .ToList();

        var selected = SelectBestVariant(variantResults);
        var baselineVariants = variantResults.Select(ToBaselineVariantDto).ToList();

        return Task.FromResult(new CountryBaselineClassificationDto(
            countryCode,
            countryName,
            selected.IsReady,
            selected.Definition.Key,
            selected.Definition.Name,
            BuildCountrySummary(countryName, selected, variantResults),
            usable.Count,
            trainRows.Count,
            testRows.Count,
            positiveRate,
            selected.Accuracy,
            selected.F1Score,
            selected.AreaUnderRocCurve,
            selected.AreaUnderPrecisionRecallCurve,
            selected.LatestProbability,
            selected.LatestPrediction,
            latestFeatureDate,
            baselineVariants,
            selected.TopPositiveSignals,
            selected.TopNegativeSignals));
    }

    private static VariantResult BuildVariantResult(
        VariantDefinition definition,
        string countryName,
        IReadOnlyList<ModelInput> usable,
        IReadOnlyList<ModelInput> trainRows,
        IReadOnlyList<ModelInput> testRows)
    {
        var trainHasBothClasses = HasBothClasses(trainRows);
        var testHasBothClasses = HasBothClasses(testRows);
        var trainPositiveRate = trainRows.Count == 0 ? 0 : trainRows.Count(x => x.Label) / (double)trainRows.Count;
        var influences = BuildFeatureInfluences(trainRows, definition.FeatureColumns)
            .OrderByDescending(x => Math.Abs(x.Weight))
            .ToList();
        var topPositive = influences.Where(x => x.Weight > 0).Take(5).ToList();
        var topNegative = influences.Where(x => x.Weight < 0).Take(5).ToList();

        if (!trainHasBothClasses)
        {
            var fallbackProbability = Math.Round(trainPositiveRate, 4);
            return new VariantResult(
                definition,
                false,
                BuildInsufficientTrainingSummary(countryName, definition.Name, usable.Count, trainRows.Count, testRows.Count, fallbackProbability),
                0,
                0,
                0,
                0,
                fallbackProbability,
                fallbackProbability >= 0.5,
                topPositive,
                topNegative);
        }

        var mlContext = new MLContext(seed: 42);
        var trainData = mlContext.Data.LoadFromEnumerable(trainRows);
        var pipeline = mlContext.Transforms.Concatenate("Features", definition.FeatureColumns)
            .Append(mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                labelColumnName: nameof(ModelInput.Label),
                featureColumnName: "Features"));

        var model = pipeline.Fit(trainData);
        var latestInput = mlContext.Data.LoadFromEnumerable([usable[^1]]);
        var latestPrediction = model.Transform(latestInput);
        var latestScored = mlContext.Data.CreateEnumerable<ModelPrediction>(latestPrediction, reuseRowObject: false).First();
        var latestProbability = Math.Round(latestScored.Probability, 4);

        if (!testHasBothClasses)
        {
            return new VariantResult(
                definition,
                false,
                BuildInsufficientTestSummary(
                    countryName,
                    definition.Name,
                    usable.Count,
                    trainRows.Count,
                    testRows.Count,
                    latestProbability,
                    latestScored.PredictedLabel),
                0,
                0,
                0,
                0,
                latestProbability,
                latestScored.PredictedLabel,
                topPositive,
                topNegative);
        }

        var testData = mlContext.Data.LoadFromEnumerable(testRows);
        var predictions = model.Transform(testData);
        var metrics = mlContext.BinaryClassification.Evaluate(
            predictions,
            labelColumnName: nameof(ModelInput.Label));

        return new VariantResult(
            definition,
            true,
            BuildReadySummary(countryName, definition.Name, usable.Count, metrics, latestProbability, latestScored.PredictedLabel),
            Math.Round(metrics.Accuracy, 4),
            Math.Round(metrics.F1Score, 4),
            Math.Round(metrics.AreaUnderRocCurve, 4),
            Math.Round(metrics.AreaUnderPrecisionRecallCurve, 4),
            latestProbability,
            latestScored.PredictedLabel,
            topPositive,
            topNegative);
    }

    private static BaselineVariantDto ToBaselineVariantDto(VariantResult variant)
    {
        return new BaselineVariantDto(
            variant.Definition.Key,
            variant.Definition.Name,
            variant.IsReady,
            variant.Summary,
            variant.Accuracy,
            variant.F1Score,
            variant.AreaUnderRocCurve,
            variant.AreaUnderPrecisionRecallCurve,
            variant.LatestProbability,
            variant.LatestPrediction);
    }

    private static VariantResult SelectBestVariant(IReadOnlyList<VariantResult> variants)
    {
        return variants
            .OrderByDescending(variant => variant.IsReady)
            .ThenByDescending(variant => variant.F1Score)
            .ThenByDescending(variant => variant.AreaUnderPrecisionRecallCurve)
            .ThenByDescending(variant => variant.AreaUnderRocCurve)
            .ThenByDescending(variant => variant.Accuracy)
            .ThenByDescending(variant => variant.Definition.Rank)
            .First();
    }

    private static string BuildCountrySummary(
        string countryName,
        VariantResult selected,
        IReadOnlyList<VariantResult> variants)
    {
        var readyVariants = variants.Where(variant => variant.IsReady).ToList();
        if (readyVariants.Count >= 2)
        {
            return $"{selected.Summary} En el comparativo, {selected.Definition.Name.ToLowerInvariant()} fue la variante mas fuerte para {countryName}.";
        }

        return $"{selected.Summary} Se compararon tres variantes: sismico puro, sismico + clima y sismico + clima + geomagnetismo.";
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

    private static bool HasBothClasses(IReadOnlyList<ModelInput> rows)
    {
        return rows.Any(x => x.Label) && rows.Any(x => !x.Label);
    }

    private static IReadOnlyList<FeatureInfluenceDto> BuildFeatureInfluences(
        IReadOnlyList<ModelInput> rows,
        IReadOnlyList<string> featureColumns)
    {
        var positives = rows.Where(x => x.Label).ToList();
        var negatives = rows.Where(x => !x.Label).ToList();
        if (positives.Count == 0 || negatives.Count == 0)
        {
            return [];
        }

        return featureColumns
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

    private static string BuildReadySummary(
        string countryName,
        string variantName,
        int sampleCount,
        BinaryClassificationMetrics metrics,
        double probability,
        bool predictedLabel)
    {
        var tendency = predictedLabel ? "probabilidad elevada" : "probabilidad baja";
        return $"La variante {variantName.ToLowerInvariant()} de {countryName} se entreno con {sampleCount} dias. " +
               $"En prueba logro accuracy {metrics.Accuracy:P1}, F1 {metrics.F1Score:P1}, ROC AUC {metrics.AreaUnderRocCurve:P1} y PR AUC {metrics.AreaUnderPrecisionRecallCurve:P1}. " +
               $"Para el ultimo dia observado estima {tendency} de sismo significativo al dia siguiente ({probability:P1}).";
    }

    private static string BuildInsufficientTrainingSummary(
        string countryName,
        string variantName,
        int sampleCount,
        int trainCount,
        int testCount,
        double probability)
    {
        return $"La variante {variantName.ToLowerInvariant()} de {countryName} se preparo con {sampleCount} dias, " +
               $"pero el entrenamiento ({trainCount}) no tuvo ambas clases y la prueba queda en {testCount} dias. " +
               $"Se conserva una lectura operativa base ({probability:P1}) sin metricas confiables.";
    }

    private static string BuildInsufficientTestSummary(
        string countryName,
        string variantName,
        int sampleCount,
        int trainCount,
        int testCount,
        double probability,
        bool predictedLabel)
    {
        var tendency = predictedLabel ? "probabilidad elevada" : "probabilidad baja";
        return $"La variante {variantName.ToLowerInvariant()} de {countryName} se entreno con {sampleCount} dias, " +
               $"pero la ventana de prueba ({testCount} dias) no tuvo ambas clases. " +
               $"Se mantiene la lectura del ultimo dia con {tendency} ({probability:P1}), " +
               $"aunque aun no hay AUC/F1 confiables. Entrenamiento: {trainCount} dias.";
    }

    private static ModelInput ToModelInput(CountryDailyFeatureDto feature)
    {
        return new ModelInput
        {
            Date = feature.Date.ToDateTime(TimeOnly.MinValue),
            EarthquakeCount = feature.EarthquakeCount,
            SignificantEarthquakeCount = feature.SignificantEarthquakeCount,
            MaxMagnitude = (float)feature.MaxMagnitude,
            MeanMagnitude = (float)feature.MeanMagnitude,
            MeanDepthKm = (float)feature.MeanDepthKm,
            TotalEnergyJoules = NormalizeEnergy(feature.TotalEnergyJoules),
            EarthquakeCount1d = feature.EarthquakeCount1d,
            SignificantEarthquakeCount1d = feature.SignificantEarthquakeCount1d,
            MaxMagnitude1d = (float)feature.MaxMagnitude1d,
            TotalEnergyJoules1d = NormalizeEnergy(feature.TotalEnergyJoules1d),
            EarthquakeCount7d = feature.EarthquakeCount7d,
            EarthquakeCount30d = feature.EarthquakeCount30d,
            SignificantEarthquakeCount7d = feature.SignificantEarthquakeCount7d,
            SignificantEarthquakeCount30d = feature.SignificantEarthquakeCount30d,
            MaxMagnitude7d = (float)feature.MaxMagnitude7d,
            MaxMagnitude30d = (float)feature.MaxMagnitude30d,
            MeanMagnitude7d = (float)feature.MeanMagnitude7d,
            MeanMagnitude30d = (float)feature.MeanMagnitude30d,
            TotalEnergyJoules7d = NormalizeEnergy(feature.TotalEnergyJoules7d),
            TotalEnergyJoules30d = NormalizeEnergy(feature.TotalEnergyJoules30d),
            BValue30d = (float)feature.BValue30d,
            SignificantRate7d = (float)feature.SignificantRate7d,
            SignificantRate30d = (float)feature.SignificantRate30d,
            ActivityRatio7dTo30d = (float)feature.ActivityRatio7dTo30d,
            SignificantActivityRatio7dTo30d = (float)feature.SignificantActivityRatio7dTo30d,
            EnergyRatio7dTo30d = (float)feature.EnergyRatio7dTo30d,
            EtasRate1d = (float)feature.EtasRate1d,
            OmoriPressure3d = (float)feature.OmoriPressure3d,
            RecentEventDensity3d = (float)feature.RecentEventDensity3d,
            RecentSignificantDensity7d = (float)feature.RecentSignificantDensity7d,
            HoursSinceLastEvent = (float)feature.HoursSinceLastEvent,
            HoursSinceLastSignificant = (float)feature.HoursSinceLastSignificant,
            DaysSinceLastSignificant = feature.DaysSinceLastSignificant,
            Temperature2mMean = (float)(feature.Temperature2mMean ?? 0),
            Temperature2mMax = (float)(feature.Temperature2mMax ?? 0),
            Temperature2mMin = (float)(feature.Temperature2mMin ?? 0),
            PrecipitationSum = (float)(feature.PrecipitationSum ?? 0),
            PressureMslMean = (float)(feature.PressureMslMean ?? 0),
            RelativeHumidity2mMean = (float)(feature.RelativeHumidity2mMean ?? 0),
            WindSpeed10mMean = (float)(feature.WindSpeed10mMean ?? 0),
            SoilMoisture0To10cmMean = (float)(feature.SoilMoisture0To10cmMean ?? 0),
            ShortwaveRadiationSum = (float)(feature.ShortwaveRadiationSum ?? 0),
            GeomagneticSampleCount = feature.GeomagneticSampleCount,
            GeomagneticRangeX = (float)(feature.GeomagneticRangeX ?? 0),
            GeomagneticRangeY = (float)(feature.GeomagneticRangeY ?? 0),
            GeomagneticRangeZ = (float)(feature.GeomagneticRangeZ ?? 0),
            GeomagneticRangeF = (float)(feature.GeomagneticRangeF ?? 0),
            GeomagneticMeanAbsDeltaF = (float)(feature.GeomagneticMeanAbsDeltaF ?? 0),
            Label = feature.NextDayHadSignificantEarthquake
        };
    }

    private static float NormalizeEnergy(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return (float)Math.Log10(value + 1);
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
            nameof(ModelInput.EarthquakeCount1d) => input.EarthquakeCount1d,
            nameof(ModelInput.SignificantEarthquakeCount1d) => input.SignificantEarthquakeCount1d,
            nameof(ModelInput.MaxMagnitude1d) => input.MaxMagnitude1d,
            nameof(ModelInput.TotalEnergyJoules1d) => input.TotalEnergyJoules1d,
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
            nameof(ModelInput.EtasRate1d) => input.EtasRate1d,
            nameof(ModelInput.OmoriPressure3d) => input.OmoriPressure3d,
            nameof(ModelInput.RecentEventDensity3d) => input.RecentEventDensity3d,
            nameof(ModelInput.RecentSignificantDensity7d) => input.RecentSignificantDensity7d,
            nameof(ModelInput.HoursSinceLastEvent) => input.HoursSinceLastEvent,
            nameof(ModelInput.HoursSinceLastSignificant) => input.HoursSinceLastSignificant,
            nameof(ModelInput.DaysSinceLastSignificant) => input.DaysSinceLastSignificant,
            nameof(ModelInput.Temperature2mMean) => input.Temperature2mMean,
            nameof(ModelInput.Temperature2mMax) => input.Temperature2mMax,
            nameof(ModelInput.Temperature2mMin) => input.Temperature2mMin,
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
            nameof(ModelInput.SignificantEarthquakeCount) => "Sismos significativos diarios",
            nameof(ModelInput.MaxMagnitude) => "Magnitud maxima diaria",
            nameof(ModelInput.MeanMagnitude) => "Magnitud media diaria",
            nameof(ModelInput.MeanDepthKm) => "Profundidad media diaria",
            nameof(ModelInput.TotalEnergyJoules) => "Energia sismica diaria",
            nameof(ModelInput.EarthquakeCount1d) => "Conteo sismico 1 dia",
            nameof(ModelInput.SignificantEarthquakeCount1d) => "Sismos fuertes 1 dia",
            nameof(ModelInput.MaxMagnitude1d) => "Magnitud maxima 1 dia",
            nameof(ModelInput.TotalEnergyJoules1d) => "Energia sismica 1 dia",
            nameof(ModelInput.EarthquakeCount7d) => "Conteo sismico 7 dias",
            nameof(ModelInput.EarthquakeCount30d) => "Conteo sismico 30 dias",
            nameof(ModelInput.SignificantEarthquakeCount7d) => "Sismos fuertes 7 dias",
            nameof(ModelInput.SignificantEarthquakeCount30d) => "Sismos fuertes 30 dias",
            nameof(ModelInput.MaxMagnitude7d) => "Magnitud maxima 7 dias",
            nameof(ModelInput.MaxMagnitude30d) => "Magnitud maxima 30 dias",
            nameof(ModelInput.MeanMagnitude7d) => "Magnitud media 7 dias",
            nameof(ModelInput.MeanMagnitude30d) => "Magnitud media 30 dias",
            nameof(ModelInput.TotalEnergyJoules7d) => "Energia sismica 7 dias",
            nameof(ModelInput.TotalEnergyJoules30d) => "Energia sismica 30 dias",
            nameof(ModelInput.BValue30d) => "b-value 30 dias",
            nameof(ModelInput.SignificantRate7d) => "Frecuencia de sismos fuertes 7 dias",
            nameof(ModelInput.SignificantRate30d) => "Frecuencia de sismos fuertes 30 dias",
            nameof(ModelInput.ActivityRatio7dTo30d) => "Aceleracion sismica 7d/30d",
            nameof(ModelInput.SignificantActivityRatio7dTo30d) => "Aceleracion de sismos fuertes 7d/30d",
            nameof(ModelInput.EnergyRatio7dTo30d) => "Aceleracion de energia 7d/30d",
            nameof(ModelInput.EtasRate1d) => "Tasa ETAS-lite 1 dia",
            nameof(ModelInput.OmoriPressure3d) => "Presion tipo Omori 3 dias",
            nameof(ModelInput.RecentEventDensity3d) => "Densidad reciente 3 dias",
            nameof(ModelInput.RecentSignificantDensity7d) => "Densidad fuerte 7 dias",
            nameof(ModelInput.HoursSinceLastEvent) => "Horas desde el ultimo evento",
            nameof(ModelInput.HoursSinceLastSignificant) => "Horas desde el ultimo sismo fuerte",
            nameof(ModelInput.DaysSinceLastSignificant) => "Dias desde el ultimo sismo fuerte",
            nameof(ModelInput.Temperature2mMean) => "Temperatura media",
            nameof(ModelInput.Temperature2mMax) => "Temperatura maxima",
            nameof(ModelInput.Temperature2mMin) => "Temperatura minima",
            nameof(ModelInput.PrecipitationSum) => "Precipitacion",
            nameof(ModelInput.PressureMslMean) => "Presion atmosferica",
            nameof(ModelInput.RelativeHumidity2mMean) => "Humedad relativa",
            nameof(ModelInput.WindSpeed10mMean) => "Viento medio",
            nameof(ModelInput.SoilMoisture0To10cmMean) => "Humedad de suelo",
            nameof(ModelInput.ShortwaveRadiationSum) => "Radiacion solar",
            nameof(ModelInput.GeomagneticSampleCount) => "Muestras geomagneticas",
            nameof(ModelInput.GeomagneticRangeX) => "Rango geomagnetico X",
            nameof(ModelInput.GeomagneticRangeY) => "Rango geomagnetico Y",
            nameof(ModelInput.GeomagneticRangeZ) => "Rango geomagnetico Z",
            nameof(ModelInput.GeomagneticRangeF) => "Rango geomagnetico F",
            nameof(ModelInput.GeomagneticMeanAbsDeltaF) => "Variacion geomagnetica F",
            _ => value
        };
    }

    private sealed record VariantDefinition(
        string Key,
        string Name,
        string[] FeatureColumns,
        int Rank);

    private sealed record VariantResult(
        VariantDefinition Definition,
        bool IsReady,
        string Summary,
        double Accuracy,
        double F1Score,
        double AreaUnderRocCurve,
        double AreaUnderPrecisionRecallCurve,
        double LatestProbability,
        bool LatestPrediction,
        IReadOnlyList<FeatureInfluenceDto> TopPositiveSignals,
        IReadOnlyList<FeatureInfluenceDto> TopNegativeSignals);

    private sealed class ModelInput
    {
        public DateTime Date { get; set; }
        public float EarthquakeCount { get; set; }
        public float SignificantEarthquakeCount { get; set; }
        public float MaxMagnitude { get; set; }
        public float MeanMagnitude { get; set; }
        public float MeanDepthKm { get; set; }
        public float TotalEnergyJoules { get; set; }
        public float EarthquakeCount1d { get; set; }
        public float SignificantEarthquakeCount1d { get; set; }
        public float MaxMagnitude1d { get; set; }
        public float TotalEnergyJoules1d { get; set; }
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
        public float EtasRate1d { get; set; }
        public float OmoriPressure3d { get; set; }
        public float RecentEventDensity3d { get; set; }
        public float RecentSignificantDensity7d { get; set; }
        public float HoursSinceLastEvent { get; set; }
        public float HoursSinceLastSignificant { get; set; }
        public float DaysSinceLastSignificant { get; set; }
        public float Temperature2mMean { get; set; }
        public float Temperature2mMax { get; set; }
        public float Temperature2mMin { get; set; }
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
