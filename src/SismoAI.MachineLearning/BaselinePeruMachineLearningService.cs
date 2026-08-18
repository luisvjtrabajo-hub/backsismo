using Microsoft.ML;
using Microsoft.ML.Data;
using SismoAI.Application;

namespace SismoAI.MachineLearning;

public sealed class BaselinePeruMachineLearningService : IMachineLearningService
{
    private const string ComparisonModelName = "Comparativo baseline sismico multi-escala calibrado";
    private const int MinimumSamples = 120;
    private const int MinimumTrainingSamples = 60;
    private const int MinimumValidationSamples = 20;
    private const int MinimumTestSamples = 20;
    private const double TrainingPositiveRatioTarget = 0.35;
    private const int CalibrationBinCount = 10;
    private const int MinimumPopulatedCalibrationBins = 3;

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

        var allRows = features
            .OrderBy(x => x.Date)
            .Select(ToModelInput)
            .ToList();

        DateOnly? latestFeatureDate = allRows.LastOrDefault()?.Date is DateTime latestDate
            ? DateOnly.FromDateTime(latestDate)
            : null;
        var forecastRow = allRows.LastOrDefault();
        var labeledRows = allRows.Count > 1
            ? allRows.Take(allRows.Count - 1).ToList()
            : [];
        var positiveRate = labeledRows.Count == 0
            ? 0
            : Math.Round(labeledRows.Count(x => x.Label) / (double)labeledRows.Count, 4);

        if (labeledRows.Count < MinimumSamples)
        {
            return Task.FromResult(BuildUnavailableBaseline(
                countryCode,
                countryName,
                latestFeatureDate,
                labeledRows.Count,
                positiveRate,
                $"Aun no hay suficientes muestras diarias etiquetadas para entrenar un baseline robusto de {countryName}."));
        }

        var split = FindBestTemporalSplit(labeledRows);
        var trainRows = labeledRows.Take(split.TrainingEndIndex).ToList();
        var validationRows = labeledRows
            .Skip(split.TrainingEndIndex)
            .Take(split.ValidationEndIndex - split.TrainingEndIndex)
            .ToList();
        var testRows = labeledRows.Skip(split.ValidationEndIndex).ToList();

        var variants = VariantDefinitions
            .Select(definition => BuildVariantResult(definition, countryName, trainRows, validationRows, testRows, forecastRow))
            .ToList();

        var selected = SelectBestVariant(variants);

        return Task.FromResult(new CountryBaselineClassificationDto(
            countryCode,
            countryName,
            selected.IsReady,
            selected.Definition.Key,
            ComparisonModelName,
            BuildCountrySummary(countryName, selected, variants),
            selected.CalibrationMethod,
            labeledRows.Count,
            trainRows.Count,
            validationRows.Count,
            testRows.Count,
            positiveRate,
            selected.DecisionThreshold,
            selected.Accuracy,
            selected.BalancedAccuracy,
            selected.Precision,
            selected.Recall,
            selected.Specificity,
            selected.F1Score,
            selected.MatthewsCorrelationCoefficient,
            selected.AreaUnderRocCurve,
            selected.AreaUnderPrecisionRecallCurve,
            selected.BrierScore,
            selected.LogLoss,
            selected.CalibrationError,
            selected.LatestProbability,
            selected.LatestPrediction,
            latestFeatureDate,
            selected.ConfusionMatrix,
            variants.Select(ToBaselineVariantDto).ToList(),
            selected.TopPositiveSignals,
            selected.TopNegativeSignals));
    }

    private static CountryBaselineClassificationDto BuildUnavailableBaseline(
        string countryCode,
        string countryName,
        DateOnly? latestFeatureDate,
        int totalSamples,
        double positiveRate,
        string summary)
    {
        var variants = VariantDefinitions
            .Select(definition => new BaselineVariantDto(
                definition.Key,
                definition.Name,
                false,
                $"Todavia no hay suficiente historia etiquetada para evaluar la variante {definition.Name.ToLowerInvariant()} en {countryName}.",
                "Sin calibracion",
                0,
                0,
                0,
                0.5,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                EmptyConfusionMatrix()))
            .ToList();

        return new CountryBaselineClassificationDto(
            countryCode,
            countryName,
            false,
            VariantDefinitions[^1].Key,
            ComparisonModelName,
            summary,
            "Sin calibracion",
            totalSamples,
            0,
            0,
            0,
            positiveRate,
            0.5,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            latestFeatureDate,
            EmptyConfusionMatrix(),
            variants,
            [],
            []);
    }

    private static VariantResult BuildVariantResult(
        VariantDefinition definition,
        string countryName,
        IReadOnlyList<ModelInput> trainRows,
        IReadOnlyList<ModelInput> validationRows,
        IReadOnlyList<ModelInput> testRows,
        ModelInput? forecastRow)
    {
        var topPositive = BuildFeatureInfluences(trainRows, definition.FeatureColumns)
            .Where(x => x.Weight > 0)
            .OrderByDescending(x => x.Weight)
            .Take(5)
            .ToList();
        var topNegative = BuildFeatureInfluences(trainRows, definition.FeatureColumns)
            .Where(x => x.Weight < 0)
            .OrderBy(x => x.Weight)
            .Take(5)
            .ToList();

        if (!HasBothClasses(trainRows))
        {
            var fallbackProbability = trainRows.Count == 0 ? 0 : trainRows.Count(x => x.Label) / (double)trainRows.Count;
            return BuildUnavailableVariant(
                definition,
                BuildInsufficientTrainingSummary(countryName, definition.Name, trainRows.Count, validationRows.Count, testRows.Count, fallbackProbability),
                "Sin calibracion",
                trainRows.Count,
                validationRows.Count,
                testRows.Count,
                0.5,
                fallbackProbability,
                fallbackProbability >= 0.5,
                topPositive,
                topNegative);
        }

        if (!HasBothClasses(validationRows))
        {
            var fallbackProbability = trainRows.Count == 0 ? 0 : trainRows.Count(x => x.Label) / (double)trainRows.Count;
            return BuildUnavailableVariant(
                definition,
                BuildInsufficientValidationSummary(countryName, definition.Name, trainRows.Count, validationRows.Count, testRows.Count, fallbackProbability),
                "Sin calibracion",
                trainRows.Count,
                validationRows.Count,
                testRows.Count,
                0.5,
                fallbackProbability,
                fallbackProbability >= 0.5,
                topPositive,
                topNegative);
        }

        var balancedTrainRows = RebalanceTrainingRows(trainRows);
        var mlContext = new MLContext(seed: 42);
        var trainData = mlContext.Data.LoadFromEnumerable(balancedTrainRows);
        var pipeline = mlContext.Transforms.Concatenate("Features", definition.FeatureColumns)
            .Append(mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                labelColumnName: nameof(ModelInput.Label),
                featureColumnName: "Features"));

        var model = pipeline.Fit(trainData);
        var validationScoredRaw = ScoreRows(mlContext, model, validationRows);
        var calibrator = BuildCalibrator(validationScoredRaw);
        var validationScored = ApplyCalibration(validationScoredRaw, calibrator);
        var thresholdSelection = FindOptimalThreshold(validationScored);
        var latestProbability = forecastRow is null
            ? 0
            : ApplyCalibration(ScoreRows(mlContext, model, [forecastRow]), calibrator).First().Probability;
        var latestPrediction = latestProbability >= thresholdSelection.Threshold;

        if (testRows.Count == 0)
        {
            return BuildUnavailableVariant(
                definition,
                BuildInsufficientTestSummary(countryName, definition.Name, trainRows.Count, validationRows.Count, testRows.Count, latestProbability, thresholdSelection.Threshold),
                calibrator.Description,
                trainRows.Count,
                validationRows.Count,
                testRows.Count,
                thresholdSelection.Threshold,
                latestProbability,
                latestPrediction,
                topPositive,
                topNegative);
        }

        var testScored = ApplyCalibration(ScoreRows(mlContext, model, testRows), calibrator);
        var evaluation = EvaluatePredictions(mlContext, testScored, thresholdSelection.Threshold);
        var isReady = HasBothClasses(testRows);
        var summary = isReady
            ? BuildReadySummary(countryName, definition.Name, trainRows.Count, validationRows.Count, testRows.Count, thresholdSelection.Threshold, calibrator.Description, evaluation)
            : BuildLimitedTestSummary(countryName, definition.Name, trainRows.Count, validationRows.Count, testRows.Count, thresholdSelection.Threshold, calibrator.Description, evaluation);

        return new VariantResult(
            definition,
            isReady,
            summary,
            calibrator.Description,
            trainRows.Count,
            validationRows.Count,
            testRows.Count,
            thresholdSelection.Threshold,
            evaluation.Accuracy,
            evaluation.BalancedAccuracy,
            evaluation.Precision,
            evaluation.Recall,
            evaluation.Specificity,
            evaluation.F1Score,
            evaluation.MatthewsCorrelationCoefficient,
            evaluation.AreaUnderRocCurve,
            evaluation.AreaUnderPrecisionRecallCurve,
            evaluation.BrierScore,
            evaluation.LogLoss,
            evaluation.CalibrationError,
            latestProbability,
            latestPrediction,
            evaluation.ConfusionMatrix,
            topPositive,
            topNegative);
    }

    private static VariantResult BuildUnavailableVariant(
        VariantDefinition definition,
        string summary,
        string calibrationMethod,
        int trainingSamples,
        int validationSamples,
        int testSamples,
        double threshold,
        double latestProbability,
        bool latestPrediction,
        IReadOnlyList<FeatureInfluenceDto> topPositiveSignals,
        IReadOnlyList<FeatureInfluenceDto> topNegativeSignals)
    {
        return new VariantResult(
            definition,
            false,
            summary,
            calibrationMethod,
            trainingSamples,
            validationSamples,
            testSamples,
            threshold,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            latestProbability,
            latestPrediction,
            EmptyConfusionMatrix(),
            topPositiveSignals,
            topNegativeSignals);
    }

    private static BaselineVariantDto ToBaselineVariantDto(VariantResult variant)
    {
        return new BaselineVariantDto(
            variant.Definition.Key,
            variant.Definition.Name,
            variant.IsReady,
            variant.Summary,
            variant.CalibrationMethod,
            variant.TrainingSamples,
            variant.ValidationSamples,
            variant.TestSamples,
            variant.DecisionThreshold,
            variant.Accuracy,
            variant.BalancedAccuracy,
            variant.Precision,
            variant.Recall,
            variant.Specificity,
            variant.F1Score,
            variant.MatthewsCorrelationCoefficient,
            variant.AreaUnderRocCurve,
            variant.AreaUnderPrecisionRecallCurve,
            variant.BrierScore,
            variant.LogLoss,
            variant.CalibrationError,
            variant.LatestProbability,
            variant.LatestPrediction,
            variant.ConfusionMatrix);
    }

    private static VariantResult SelectBestVariant(IReadOnlyList<VariantResult> variants)
    {
        return variants
            .OrderByDescending(variant => variant.IsReady)
            .ThenByDescending(variant => HasBidirectionalPredictions(variant.ConfusionMatrix))
            .ThenByDescending(variant => variant.MatthewsCorrelationCoefficient)
            .ThenByDescending(variant => variant.AreaUnderPrecisionRecallCurve)
            .ThenByDescending(variant => variant.F1Score)
            .ThenByDescending(variant => variant.BalancedAccuracy)
            .ThenByDescending(variant => variant.Recall)
            .ThenByDescending(variant => variant.Definition.Rank)
            .First();
    }

    private static string BuildCountrySummary(
        string countryName,
        VariantResult selected,
        IReadOnlyList<VariantResult> variants)
    {
        var readyVariants = variants.Where(x => x.IsReady).ToList();
        if (readyVariants.Count == 0)
        {
            return $"{selected.Summary} Aun no hay suficiente diversidad de clases en test para validar bien el baseline de {countryName}.";
        }

        var trivialAccuracy = 1d - readyVariants.Max(x => x.TestPositiveRate);
        return $"{selected.Summary} En el comparativo, {selected.Definition.Name.ToLowerInvariant()} fue la variante mas fuerte para {countryName}. " +
               $"Como referencia, un baseline trivial de 'no habra sismo significativo' rondaria {trivialAccuracy:P1} de accuracy y F1 0.0%.";
    }

    private static TemporalSplit FindBestTemporalSplit(IReadOnlyList<ModelInput> rows)
    {
        var sampleCount = rows.Count;
        var preferredTrainEnd = Math.Clamp((int)Math.Floor(sampleCount * 0.70), MinimumTrainingSamples, sampleCount - MinimumValidationSamples - MinimumTestSamples);
        var preferredValidationEnd = Math.Clamp((int)Math.Floor(sampleCount * 0.85), preferredTrainEnd + MinimumValidationSamples, sampleCount - MinimumTestSamples);
        var minTrainEnd = MinimumTrainingSamples;
        var maxTrainEnd = sampleCount - MinimumValidationSamples - MinimumTestSamples;
        var candidates = new List<TemporalSplit>();

        for (var trainEnd = minTrainEnd; trainEnd <= maxTrainEnd; trainEnd++)
        {
            var minValidationEnd = trainEnd + MinimumValidationSamples;
            var maxValidationEnd = sampleCount - MinimumTestSamples;
            for (var validationEnd = minValidationEnd; validationEnd <= maxValidationEnd; validationEnd++)
            {
                var trainRows = rows.Take(trainEnd).ToList();
                var validationRows = rows.Skip(trainEnd).Take(validationEnd - trainEnd).ToList();
                if (!HasBothClasses(trainRows) || !HasBothClasses(validationRows))
                {
                    continue;
                }

                var testRows = rows.Skip(validationEnd).ToList();
                candidates.Add(new TemporalSplit(
                    trainEnd,
                    validationEnd,
                    HasBothClasses(testRows),
                    Math.Abs(trainEnd - preferredTrainEnd) + Math.Abs(validationEnd - preferredValidationEnd)));
            }
        }

        if (candidates.Count > 0)
        {
            return candidates
                .OrderByDescending(x => x.TestHasBothClasses)
                .ThenBy(x => x.DistanceToPreferred)
                .First();
        }

        return new TemporalSplit(preferredTrainEnd, preferredValidationEnd, false, 0);
    }

    private static List<ModelInput> RebalanceTrainingRows(IReadOnlyList<ModelInput> rows)
    {
        var positives = rows.Where(x => x.Label).ToList();
        var negatives = rows.Where(x => !x.Label).ToList();
        if (positives.Count == 0 || negatives.Count == 0)
        {
            return rows.ToList();
        }

        var desiredPositiveCount = Math.Max(
            positives.Count,
            (int)Math.Ceiling((TrainingPositiveRatioTarget * negatives.Count) / (1 - TrainingPositiveRatioTarget)));
        var balanced = rows.ToList();
        for (var index = positives.Count; index < desiredPositiveCount; index++)
        {
            balanced.Add(positives[index % positives.Count]);
        }

        return balanced;
    }

    private static IReadOnlyList<ScoredRow> ScoreRows(MLContext mlContext, ITransformer model, IReadOnlyList<ModelInput> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var data = mlContext.Data.LoadFromEnumerable(rows);
        var transformed = model.Transform(data);
        return mlContext.Data.CreateEnumerable<ModelPrediction>(transformed, reuseRowObject: false)
            .Select(item => new ScoredRow(item.Label, item.Score, item.Probability, false))
            .ToList();
    }

    private static CalibrationMapping BuildCalibrator(IReadOnlyList<ScoredRow> validationRows)
    {
        if (validationRows.Count == 0)
        {
            return new CalibrationMapping([], "Sin calibracion");
        }

        var bins = new List<CalibrationBin>(CalibrationBinCount);
        for (var binIndex = 0; binIndex < CalibrationBinCount; binIndex++)
        {
            var lower = binIndex / (double)CalibrationBinCount;
            var upper = (binIndex + 1) / (double)CalibrationBinCount;
            var binRows = validationRows
                .Where(row => row.Probability >= lower && (binIndex == CalibrationBinCount - 1 ? row.Probability <= upper : row.Probability < upper))
                .ToList();
            if (binRows.Count == 0)
            {
                continue;
            }

            var calibratedProbability = binRows.Count(row => row.Label) / (double)binRows.Count;
            bins.Add(new CalibrationBin(lower, upper, Math.Clamp(calibratedProbability, 1e-6, 1 - 1e-6)));
        }

        if (bins.Count < MinimumPopulatedCalibrationBins)
        {
            return new CalibrationMapping([], $"Sin calibracion explicita: solo {bins.Count} bins poblados en validacion");
        }

        return new CalibrationMapping(
            bins,
            $"Calibracion por bins sobre validacion ({bins.Count} bins poblados)");
    }

    private static IReadOnlyList<ScoredRow> ApplyCalibration(
        IReadOnlyList<ScoredRow> rows,
        CalibrationMapping mapping)
    {
        if (mapping.Bins.Count == 0)
        {
            return rows;
        }

        return rows.Select(row =>
        {
            var calibratedProbability = mapping.Bins
                .FirstOrDefault(bin => row.Probability >= bin.LowerBound && (bin.IsLastBin
                    ? row.Probability <= bin.UpperBound
                    : row.Probability < bin.UpperBound))
                ?.CalibratedProbability;

            if (calibratedProbability is null)
            {
                calibratedProbability = row.Probability;
            }

            return row with { Probability = (float)calibratedProbability.Value };
        }).ToList();
    }

    private static ThresholdSelection FindOptimalThreshold(IReadOnlyList<ScoredRow> validationScored)
    {
        var candidates = BuildThresholdCandidates(validationScored)
            .Select(threshold =>
            {
                var evaluation = EvaluatePredictions(null, validationScored, threshold);
                return new ThresholdSelection(
                    threshold,
                    HasBidirectionalPredictions(evaluation.ConfusionMatrix),
                    evaluation.Precision,
                    evaluation.Recall,
                    evaluation.Specificity,
                    evaluation.F1Score,
                    evaluation.BalancedAccuracy,
                    evaluation.MatthewsCorrelationCoefficient,
                    evaluation.PredictedPositiveCount,
                    evaluation.TruePositiveCount);
            })
            .ToList();

        return candidates
            .OrderByDescending(x => x.HasBidirectionalPredictions)
            .ThenByDescending(x => x.TruePositiveCount > 0)
            .ThenByDescending(x => x.MatthewsCorrelationCoefficient)
            .ThenByDescending(x => x.BalancedAccuracy)
            .ThenByDescending(x => x.Specificity)
            .ThenByDescending(x => x.F1Score)
            .ThenByDescending(x => x.Recall)
            .ThenByDescending(x => x.Precision)
            .ThenByDescending(x => x.Threshold)
            .First();
    }

    private static IReadOnlyList<double> BuildThresholdCandidates(IReadOnlyList<ScoredRow> scoredRows)
    {
        var fixedThresholds = Enumerable.Range(1, 50).Select(index => Math.Round(index / 100d, 4))
            .Concat(Enumerable.Range(11, 8).Select(index => Math.Round(index / 20d, 4)));
        var probabilityThresholds = scoredRows
            .Select(x => Math.Round(x.Probability, 4))
            .Where(x => x > 0 && x < 1);

        return fixedThresholds
            .Concat(probabilityThresholds)
            .Append(0.5)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    private static EvaluationMetrics EvaluatePredictions(
        MLContext? mlContext,
        IReadOnlyList<ScoredRow> rawRows,
        double threshold)
    {
        if (rawRows.Count == 0)
        {
            return EvaluationMetrics.Empty(threshold);
        }

        var rows = rawRows
            .Select(row => row with { PredictedLabel = row.Probability >= threshold })
            .ToList();
        var total = rows.Count;
        var positives = rows.Count(x => x.Label);
        var negatives = total - positives;
        var truePositives = rows.Count(x => x.PredictedLabel && x.Label);
        var falsePositives = rows.Count(x => x.PredictedLabel && !x.Label);
        var trueNegatives = rows.Count(x => !x.PredictedLabel && !x.Label);
        var falseNegatives = rows.Count(x => !x.PredictedLabel && x.Label);
        var accuracy = (truePositives + trueNegatives) / (double)total;
        var precision = truePositives + falsePositives == 0 ? 0 : truePositives / (double)(truePositives + falsePositives);
        var recall = truePositives + falseNegatives == 0 ? 0 : truePositives / (double)(truePositives + falseNegatives);
        var specificity = trueNegatives + falsePositives == 0 ? 0 : trueNegatives / (double)(trueNegatives + falsePositives);
        var balancedAccuracy = (recall + specificity) / 2d;
        var f1 = precision + recall == 0 ? 0 : 2d * precision * recall / (precision + recall);
        var mccDenominator = Math.Sqrt(
            Math.Max(1, (truePositives + falsePositives) * (truePositives + falseNegatives) * (trueNegatives + falsePositives) * (trueNegatives + falseNegatives)));
        var matthews = ((truePositives * trueNegatives) - (falsePositives * falseNegatives)) / mccDenominator;
        var brierScore = rows.Average(row =>
        {
            var label = row.Label ? 1d : 0d;
            return Math.Pow(row.Probability - label, 2);
        });
        var logLoss = rows.Average(row =>
        {
            var probability = Math.Clamp(row.Probability, 1e-6, 1 - 1e-6);
            return row.Label
                ? -Math.Log(probability)
                : -Math.Log(1 - probability);
        });
        var calibrationError = ComputeCalibrationError(rows);

        var areaUnderRocCurve = 0d;
        var areaUnderPrecisionRecallCurve = 0d;
        if (mlContext is not null && positives > 0 && negatives > 0)
        {
            var data = mlContext.Data.LoadFromEnumerable(rows);
            var aucMetrics = mlContext.BinaryClassification.Evaluate(
                data,
                labelColumnName: nameof(ScoredRow.Label),
                scoreColumnName: nameof(ScoredRow.Score),
                probabilityColumnName: nameof(ScoredRow.Probability),
                predictedLabelColumnName: nameof(ScoredRow.PredictedLabel));
            areaUnderRocCurve = aucMetrics.AreaUnderRocCurve;
            areaUnderPrecisionRecallCurve = aucMetrics.AreaUnderPrecisionRecallCurve;
        }

        return new EvaluationMetrics(
            threshold,
            positives / (double)total,
            truePositives + falsePositives,
            truePositives,
            Math.Round(accuracy, 4),
            Math.Round(balancedAccuracy, 4),
            Math.Round(precision, 4),
            Math.Round(recall, 4),
            Math.Round(specificity, 4),
            Math.Round(f1, 4),
            Math.Round(matthews, 4),
            Math.Round(areaUnderRocCurve, 4),
            Math.Round(areaUnderPrecisionRecallCurve, 4),
            Math.Round(brierScore, 4),
            Math.Round(logLoss, 4),
            Math.Round(calibrationError, 4),
            new ConfusionMatrixDto(truePositives, falsePositives, trueNegatives, falseNegatives));
    }

    private static double ComputeCalibrationError(IReadOnlyList<ScoredRow> rows)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var total = rows.Count;
        var error = 0d;
        for (var binIndex = 0; binIndex < CalibrationBinCount; binIndex++)
        {
            var lower = binIndex / (double)CalibrationBinCount;
            var upper = (binIndex + 1) / (double)CalibrationBinCount;
            var bin = rows
                .Where(row => row.Probability >= lower && (binIndex == CalibrationBinCount - 1 ? row.Probability <= upper : row.Probability < upper))
                .ToList();
            if (bin.Count == 0)
            {
                continue;
            }

            var meanProbability = bin.Average(x => x.Probability);
            var empiricalRate = bin.Count(x => x.Label) / (double)bin.Count;
            error += (bin.Count / (double)total) * Math.Abs(meanProbability - empiricalRate);
        }

        return error;
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
            .OrderByDescending(x => Math.Abs(x.Weight))
            .ToList();
    }

    private static string BuildReadySummary(
        string countryName,
        string variantName,
        int trainCount,
        int validationCount,
        int testCount,
        double threshold,
        string calibrationMethod,
        EvaluationMetrics evaluation)
    {
        var tendency = evaluation.Recall > 0 ? "logra recuperar parte de los positivos" : "sigue sin recuperar positivos";
        var trivialAccuracy = 1d - evaluation.PositiveRate;
        var predictionPattern = DescribePredictionPattern(evaluation.ConfusionMatrix);
        return $"La variante {variantName.ToLowerInvariant()} de {countryName} se entreno con {trainCount} dias, ajusto el threshold en validacion temporal ({validationCount} dias) y se evaluo en test ({testCount} dias). " +
               $"Uso threshold {threshold:P1}; en test logro PR AUC {evaluation.AreaUnderPrecisionRecallCurve:P1}, ROC AUC {evaluation.AreaUnderRocCurve:P1}, recall {evaluation.Recall:P1}, precision {evaluation.Precision:P1}, F1 {evaluation.F1Score:P1}, balanced accuracy {evaluation.BalancedAccuracy:P1} y MCC {evaluation.MatthewsCorrelationCoefficient:F3}. " +
               $"Frente al baseline trivial de 'no habra sismo significativo' ({trivialAccuracy:P1} accuracy y F1 0.0%), {tendency}. {predictionPattern}. {calibrationMethod}.";
    }

    private static string BuildInsufficientTrainingSummary(
        string countryName,
        string variantName,
        int trainCount,
        int validationCount,
        int testCount,
        double probability)
    {
        return $"La variante {variantName.ToLowerInvariant()} de {countryName} no pudo entrenarse bien porque el bloque de entrenamiento temporal ({trainCount} dias) no tuvo ambas clases. " +
               $"Validacion: {validationCount} dias. Test: {testCount} dias. Se conserva una lectura base de {probability:P1}.";
    }

    private static string BuildInsufficientValidationSummary(
        string countryName,
        string variantName,
        int trainCount,
        int validationCount,
        int testCount,
        double probability)
    {
        return $"La variante {variantName.ToLowerInvariant()} de {countryName} pudo entrenarse con {trainCount} dias, pero la validacion temporal ({validationCount} dias) no tuvo ambas clases y no permite calibrar threshold de forma confiable. " +
               $"Test: {testCount} dias. Se mantiene una lectura base de {probability:P1}.";
    }

    private static string BuildInsufficientTestSummary(
        string countryName,
        string variantName,
        int trainCount,
        int validationCount,
        int testCount,
        double probability,
        double threshold)
    {
        return $"La variante {variantName.ToLowerInvariant()} de {countryName} se entreno con {trainCount} dias y calibro threshold {threshold:P1} sobre validacion ({validationCount} dias), " +
               $"pero aun no hay suficientes dias de test ({testCount}) para validar bien el rendimiento final. La probabilidad operativa actual es {probability:P1}.";
    }

    private static string BuildLimitedTestSummary(
        string countryName,
        string variantName,
        int trainCount,
        int validationCount,
        int testCount,
        double threshold,
        string calibrationMethod,
        EvaluationMetrics evaluation)
    {
        var predictionPattern = DescribePredictionPattern(evaluation.ConfusionMatrix);
        return $"La variante {variantName.ToLowerInvariant()} de {countryName} se entreno con {trainCount} dias, ajusto threshold {threshold:P1} con validacion temporal ({validationCount} dias) y se probo sobre {testCount} dias, " +
               $"pero el test no tuvo ambas clases. Se reportan solo metricas de clasificacion disponibles: recall {evaluation.Recall:P1}, precision {evaluation.Precision:P1}, F1 {evaluation.F1Score:P1}, balanced accuracy {evaluation.BalancedAccuracy:P1} y MCC {evaluation.MatthewsCorrelationCoefficient:F3}. {predictionPattern}. {calibrationMethod}.";
    }

    private static string DescribePredictionPattern(ConfusionMatrixDto confusionMatrix)
    {
        var predictedPositive = confusionMatrix.TruePositives + confusionMatrix.FalsePositives;
        var predictedNegative = confusionMatrix.TrueNegatives + confusionMatrix.FalseNegatives;

        if (predictedPositive > 0 && predictedNegative == 0)
        {
            return "En esta ventana de test el modelo marco todos los dias como positivos";
        }

        if (predictedNegative > 0 && predictedPositive == 0)
        {
            return "En esta ventana de test el modelo marco todos los dias como negativos";
        }

        return "En esta ventana de test el modelo si diferencio entre positivos y negativos";
    }

    private static bool HasBidirectionalPredictions(ConfusionMatrixDto confusionMatrix)
    {
        var predictedPositive = confusionMatrix.TruePositives + confusionMatrix.FalsePositives;
        var predictedNegative = confusionMatrix.TrueNegatives + confusionMatrix.FalseNegatives;
        return predictedPositive > 0 && predictedNegative > 0;
    }

    private static ConfusionMatrixDto EmptyConfusionMatrix()
    {
        return new ConfusionMatrixDto(0, 0, 0, 0);
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
        return value <= 0 ? 0 : (float)Math.Log10(value + 1);
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
        string CalibrationMethod,
        int TrainingSamples,
        int ValidationSamples,
        int TestSamples,
        double DecisionThreshold,
        double Accuracy,
        double BalancedAccuracy,
        double Precision,
        double Recall,
        double Specificity,
        double F1Score,
        double MatthewsCorrelationCoefficient,
        double AreaUnderRocCurve,
        double AreaUnderPrecisionRecallCurve,
        double BrierScore,
        double LogLoss,
        double CalibrationError,
        double LatestProbability,
        bool LatestPrediction,
        ConfusionMatrixDto ConfusionMatrix,
        IReadOnlyList<FeatureInfluenceDto> TopPositiveSignals,
        IReadOnlyList<FeatureInfluenceDto> TopNegativeSignals)
    {
        public double TestPositiveRate
        {
            get
            {
                var total = ConfusionMatrix.TruePositives + ConfusionMatrix.FalsePositives + ConfusionMatrix.TrueNegatives + ConfusionMatrix.FalseNegatives;
                if (total == 0)
                {
                    return 0;
                }

                return (ConfusionMatrix.TruePositives + ConfusionMatrix.FalseNegatives) / (double)total;
            }
        }
    }

    private sealed record CalibrationMapping(
        IReadOnlyList<CalibrationBin> Bins,
        string Description);

    private sealed record CalibrationBin(
        double LowerBound,
        double UpperBound,
        double CalibratedProbability)
    {
        public bool IsLastBin => UpperBound >= 1d;
    }

    private sealed record TemporalSplit(
        int TrainingEndIndex,
        int ValidationEndIndex,
        bool TestHasBothClasses,
        int DistanceToPreferred);

    private sealed record ThresholdSelection(
        double Threshold,
        bool HasBidirectionalPredictions,
        double Precision,
        double Recall,
        double Specificity,
        double F1Score,
        double BalancedAccuracy,
        double MatthewsCorrelationCoefficient,
        int PredictedPositiveCount,
        int TruePositiveCount);

    private sealed record EvaluationMetrics(
        double Threshold,
        double PositiveRate,
        int PredictedPositiveCount,
        int TruePositiveCount,
        double Accuracy,
        double BalancedAccuracy,
        double Precision,
        double Recall,
        double Specificity,
        double F1Score,
        double MatthewsCorrelationCoefficient,
        double AreaUnderRocCurve,
        double AreaUnderPrecisionRecallCurve,
        double BrierScore,
        double LogLoss,
        double CalibrationError,
        ConfusionMatrixDto ConfusionMatrix)
    {
        public static EvaluationMetrics Empty(double threshold)
        {
            return new EvaluationMetrics(threshold, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, EmptyConfusionMatrix());
        }
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
        public bool Label { get; set; }
        public float Score { get; set; }
        public float Probability { get; set; }
    }

    private sealed record ScoredRow(
        bool Label,
        float Score,
        float Probability,
        bool PredictedLabel);
}
