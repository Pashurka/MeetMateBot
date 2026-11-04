using MeetMateBot.Domain.Entities;

namespace MeetMateBot.Application.Services;

internal sealed class OnlinePreferenceModel
{
    private const int FeatureCount = 9;
    private readonly double[] _weights;

    public OnlinePreferenceModel(IReadOnlyList<double>? initialWeights = null)
    {
        var weights = initialWeights?.ToArray()
            ?? MachineLearningOptions.DefaultInitialWeights.ToArray();

        if (weights.Length != FeatureCount)
        {
            weights = MachineLearningOptions.DefaultInitialWeights.ToArray();
        }

        _weights = weights;
    }

    public int TrainingSampleCount { get; private set; }

    public void Train(IEnumerable<TrainingExample> examples)
    {
        var sampleList = examples.ToList();
        TrainingSampleCount = sampleList.Count;
        if (sampleList.Count == 0)
        {
            return;
        }

        const double learningRate = 0.35;
        const double regularization = 0.0005;
        const int epochs = 120;

        for (var epoch = 0; epoch < epochs; epoch++)
        {
            foreach (var example in sampleList)
            {
                var prediction = PredictProbability(example.Features);
                var error = example.Label - prediction;

                for (var featureIndex = 0; featureIndex < FeatureCount; featureIndex++)
                {
                    _weights[featureIndex] += learningRate * ((error * example.Features[featureIndex]) - regularization * _weights[featureIndex]);
                }
            }
        }
    }

    public int Score(FeatureVector features)
    {
        return (int)Math.Round(PredictProbability(features.Values) * 100);
    }

    private double PredictProbability(IReadOnlyList<double> features)
    {
        var dotProduct = 0.0;
        for (var featureIndex = 0; featureIndex < FeatureCount; featureIndex++)
        {
            dotProduct += _weights[featureIndex] * features[featureIndex];
        }

        return 1.0 / (1.0 + Math.Exp(-dotProduct));
    }

    internal static FeatureVector BuildFeatures(
        DatingProfile currentProfile,
        DatingProfile candidate,
        double cityAffinity,
        double interestAffinity,
        double ageAffinity)
    {
        var sameCity = string.Equals(currentProfile.City, candidate.City, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
        var interestSimilarity = CalculateInterestSimilarity(currentProfile, candidate);
        var ageSimilarity = Math.Max(0.0, 1.0 - Math.Abs(currentProfile.Age - candidate.Age) / 15.0);
        var completeness = CalculateCompleteness(candidate);
        var freshness = IsRecentlyUpdated(candidate) ? 1.0 : 0.0;

        return new FeatureVector(
            [
                1.0,
                sameCity,
                interestSimilarity,
                ageSimilarity,
                completeness,
                freshness,
                cityAffinity,
                interestAffinity,
                ageAffinity
            ]);
    }

    internal static double CalculateInterestSimilarity(DatingProfile currentProfile, DatingProfile candidate)
    {
        var distinctCurrent = currentProfile.Interests
            .Where(static interest => !string.IsNullOrWhiteSpace(interest))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var distinctCandidate = candidate.Interests
            .Where(static interest => !string.IsNullOrWhiteSpace(interest))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctCurrent.Count == 0 || distinctCandidate.Count == 0)
        {
            return 0;
        }

        var commonCount = distinctCurrent
            .Intersect(distinctCandidate, StringComparer.OrdinalIgnoreCase)
            .Count();
        var unionCount = distinctCurrent
            .Union(distinctCandidate, StringComparer.OrdinalIgnoreCase)
            .Count();

        return unionCount == 0 ? 0 : commonCount * 1.0 / unionCount;
    }

    internal static double CalculateCompleteness(DatingProfile candidate)
    {
        var completedFields = 0;

        if (!string.IsNullOrWhiteSpace(candidate.Name))
        {
            completedFields++;
        }

        if (candidate.Age >= 18)
        {
            completedFields++;
        }

        if (!string.IsNullOrWhiteSpace(candidate.City))
        {
            completedFields++;
        }

        if (candidate.Interests.Count > 0)
        {
            completedFields++;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Bio) && candidate.Bio.Trim().Length >= 10)
        {
            completedFields++;
        }

        return completedFields / 5.0;
    }

    internal static bool IsRecentlyUpdated(DatingProfile candidate)
    {
        return candidate.UpdatedAtUtc != default
            && candidate.UpdatedAtUtc >= DateTime.UtcNow.AddDays(-30);
    }

    internal sealed record FeatureVector(double[] Values);

    internal sealed record TrainingExample(double[] Features, double Label);
}
