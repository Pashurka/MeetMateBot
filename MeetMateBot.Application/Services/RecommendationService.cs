using MeetMateBot.Application.DTOs;
using MeetMateBot.Application.Interfaces;
using MeetMateBot.Domain.Entities;
using Microsoft.Extensions.Options;

namespace MeetMateBot.Application.Services;

public sealed class RecommendationService : IRecommendationService
{
    private const int MaxHeuristicScore = 105;
    private const int MaxMachineLearningContribution = 45;
    private const int SameCityScore = 25;
    private const int MaxInterestScore = 35;
    private const int MaxAgeScore = 25;
    private const int MaxCompletenessScore = 15;
    private const int MaxFreshnessScore = 5;
    private const int MaxRecommendationScore = MaxHeuristicScore + MaxMachineLearningContribution;

    private readonly IProfileRepository _profileRepository;
    private readonly IReactionRepository _reactionRepository;
    private readonly IMatchRepository _matchRepository;
    private readonly MachineLearningOptions _machineLearningOptions;

    public RecommendationService(
        IProfileRepository profileRepository,
        IReactionRepository reactionRepository,
        IMatchRepository matchRepository,
        IOptions<MachineLearningOptions> machineLearningOptions)
    {
        _profileRepository = profileRepository;
        _reactionRepository = reactionRepository;
        _matchRepository = matchRepository;
        _machineLearningOptions = machineLearningOptions.Value;
    }

    public async Task<ProfileRecommendation?> GetBestRecommendationAsync(long telegramChatId, CancellationToken cancellationToken)
    {
        var currentProfile = await _profileRepository.GetByTelegramChatIdAsync(telegramChatId, cancellationToken);
        if (currentProfile is null)
        {
            return null;
        }

        var reactedProfileIds = await _reactionRepository.GetReactedProfileIdsAsync(telegramChatId, cancellationToken);
        var reactions = await _reactionRepository.GetByFromTelegramChatIdAsync(telegramChatId, cancellationToken);
        var matchedProfileIds = await GetMatchedProfileIdsAsync(telegramChatId, cancellationToken);
        var allProfiles = await _profileRepository.GetAllAsync(cancellationToken);
        var reactionTargets = allProfiles.ToDictionary(profile => profile.TelegramChatId);
        var preferenceSnapshot = BuildPreferenceSnapshot(reactions, reactionTargets);
        var preferenceModel = BuildPreferenceModel(currentProfile, reactions, reactionTargets, preferenceSnapshot, _machineLearningOptions);

        return allProfiles
            .Where(candidate => candidate.TelegramChatId != telegramChatId)
            .Where(candidate => !reactedProfileIds.Contains(candidate.TelegramChatId))
            .Where(candidate => !matchedProfileIds.Contains(candidate.TelegramChatId))
            .Where(candidate => IsGenderCompatible(currentProfile, candidate))
            .Select(candidate => BuildRecommendation(currentProfile, candidate, preferenceSnapshot, preferenceModel))
            .OrderByDescending(recommendation => recommendation.Score)
            .ThenByDescending(recommendation => recommendation.MachineLearningScore)
            .ThenByDescending(recommendation => recommendation.InterestScore)
            .ThenByDescending(recommendation => recommendation.AgeScore)
            .FirstOrDefault();
    }

    public async Task<RecommendationDiagnosticsDto?> GetRecommendationDiagnosticsAsync(long telegramChatId, CancellationToken cancellationToken)
    {
        var currentProfile = await _profileRepository.GetByTelegramChatIdAsync(telegramChatId, cancellationToken);
        if (currentProfile is null)
        {
            return null;
        }

        var reactions = await _reactionRepository.GetByFromTelegramChatIdAsync(telegramChatId, cancellationToken);
        var allProfiles = await _profileRepository.GetAllAsync(cancellationToken);
        var reactionTargets = allProfiles.ToDictionary(profile => profile.TelegramChatId);
        var topRecommendation = await GetBestRecommendationAsync(telegramChatId, cancellationToken);

        var likedTargets = reactions
            .Where(reaction => reaction.IsLike)
            .Select(reaction => reactionTargets.GetValueOrDefault(reaction.ToTelegramChatId))
            .OfType<DatingProfile>()
            .ToList();

        return new RecommendationDiagnosticsDto(
            reactions.Count,
            reactions.Count(reaction => reaction.IsLike),
            reactions.Count(reaction => !reaction.IsLike),
            likedTargets
                .Where(profile => !string.IsNullOrWhiteSpace(profile.City))
                .GroupBy(profile => profile.City, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(group => $"{group.Key} ({group.Count()})")
                .ToList(),
            likedTargets
                .SelectMany(profile => profile.Interests)
                .Where(static interest => !string.IsNullOrWhiteSpace(interest))
                .GroupBy(interest => interest, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(group => $"{group.Key} ({group.Count()})")
                .ToList(),
            topRecommendation);
    }

    public bool IsGenderCompatible(DatingProfile currentProfile, DatingProfile candidate)
    {
        return string.Equals(candidate.Gender, currentProfile.LookingForGender, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.LookingForGender, currentProfile.Gender, StringComparison.OrdinalIgnoreCase);
    }

    public int CalculateScore(DatingProfile currentProfile, DatingProfile candidate)
    {
        return GetCityScore(currentProfile, candidate)
            + GetInterestScore(currentProfile, candidate)
            + GetAgeScore(currentProfile, candidate)
            + GetCompletenessScore(candidate)
            + GetFreshnessScore(candidate);
    }

    private ProfileRecommendation BuildRecommendation(
        DatingProfile currentProfile,
        DatingProfile candidate,
        PreferenceSnapshot preferenceSnapshot,
        OnlinePreferenceModel preferenceModel)
    {
        var cityScore = GetCityScore(currentProfile, candidate);
        var interestScore = GetInterestScore(currentProfile, candidate);
        var ageScore = GetAgeScore(currentProfile, candidate);
        var completenessScore = GetCompletenessScore(candidate);
        var isProfileRecentlyUpdated = IsRecentlyUpdated(candidate);
        var freshnessScore = isProfileRecentlyUpdated ? MaxFreshnessScore : 0;
        var heuristicScore = cityScore + interestScore + ageScore + completenessScore + freshnessScore;
        var featureVector = OnlinePreferenceModel.BuildFeatures(
            currentProfile,
            candidate,
            preferenceSnapshot.GetCityAffinity(candidate.City),
            preferenceSnapshot.GetInterestAffinity(candidate.Interests),
            preferenceSnapshot.GetAgeAffinity(candidate.Age));
        var machineLearningScore = preferenceModel.Score(featureVector);
        var machineLearningContribution = (int)Math.Round(machineLearningScore / 100.0 * MaxMachineLearningContribution);
        var totalScore = heuristicScore + machineLearningContribution;

        return new ProfileRecommendation(
            currentProfile,
            candidate,
            totalScore,
            Math.Clamp((int)Math.Round(totalScore * 100.0 / MaxRecommendationScore), 0, 100),
            machineLearningScore,
            preferenceModel.TrainingSampleCount,
            GetCommonInterests(currentProfile, candidate),
            string.Equals(currentProfile.City, candidate.City, StringComparison.OrdinalIgnoreCase),
            Math.Abs(currentProfile.Age - candidate.Age),
            cityScore,
            interestScore,
            ageScore,
            completenessScore,
            isProfileRecentlyUpdated);
    }

    private static IReadOnlyList<string> GetCommonInterests(DatingProfile currentProfile, DatingProfile candidate)
    {
        return currentProfile.Interests
            .Intersect(candidate.Interests, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlySet<long>> GetMatchedProfileIdsAsync(long telegramChatId, CancellationToken cancellationToken)
    {
        var matches = await _matchRepository.GetByTelegramChatIdAsync(telegramChatId, cancellationToken);

        return matches
            .Select(match => match.FirstTelegramChatId == telegramChatId
                ? match.SecondTelegramChatId
                : match.FirstTelegramChatId)
            .ToHashSet();
    }

    private static int GetCityScore(DatingProfile currentProfile, DatingProfile candidate)
    {
        return string.Equals(currentProfile.City, candidate.City, StringComparison.OrdinalIgnoreCase)
            ? SameCityScore
            : 0;
    }

    private static int GetInterestScore(DatingProfile currentProfile, DatingProfile candidate)
    {
        return (int)Math.Round(OnlinePreferenceModel.CalculateInterestSimilarity(currentProfile, candidate) * MaxInterestScore);
    }

    private static int GetAgeScore(DatingProfile currentProfile, DatingProfile candidate)
    {
        var ageDifference = Math.Abs(currentProfile.Age - candidate.Age);
        var normalized = Math.Max(0, 1 - ageDifference / 15.0);
        return (int)Math.Round(normalized * MaxAgeScore);
    }

    private static int GetCompletenessScore(DatingProfile candidate)
    {
        return (int)Math.Round(OnlinePreferenceModel.CalculateCompleteness(candidate) * MaxCompletenessScore);
    }

    private static int GetFreshnessScore(DatingProfile candidate)
    {
        return IsRecentlyUpdated(candidate)
            ? MaxFreshnessScore
            : 0;
    }

    private static bool IsRecentlyUpdated(DatingProfile candidate)
    {
        return OnlinePreferenceModel.IsRecentlyUpdated(candidate);
    }

    private static OnlinePreferenceModel BuildPreferenceModel(
        DatingProfile currentProfile,
        IReadOnlyList<ProfileReaction> reactions,
        IReadOnlyDictionary<long, DatingProfile> reactionTargets,
        PreferenceSnapshot preferenceSnapshot,
        MachineLearningOptions machineLearningOptions)
    {
        var model = new OnlinePreferenceModel(machineLearningOptions.InitialWeights);
        var examples = reactions
            .Where(reaction => reactionTargets.ContainsKey(reaction.ToTelegramChatId))
            .Select(reaction =>
            {
                var targetProfile = reactionTargets[reaction.ToTelegramChatId];
                var features = OnlinePreferenceModel.BuildFeatures(
                    currentProfile,
                    targetProfile,
                    preferenceSnapshot.GetCityAffinity(targetProfile.City),
                    preferenceSnapshot.GetInterestAffinity(targetProfile.Interests),
                    preferenceSnapshot.GetAgeAffinity(targetProfile.Age));

                return new OnlinePreferenceModel.TrainingExample(features.Values, reaction.IsLike ? 1.0 : 0.0);
            })
            .ToList();

        model.Train(examples);
        return model;
    }

    private static PreferenceSnapshot BuildPreferenceSnapshot(
        IReadOnlyList<ProfileReaction> reactions,
        IReadOnlyDictionary<long, DatingProfile> reactionTargets)
    {
        var cityScores = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var interestScores = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var ageScores = new List<(int Age, double Label)>();

        foreach (var reaction in reactions)
        {
            if (!reactionTargets.TryGetValue(reaction.ToTelegramChatId, out var targetProfile))
            {
                continue;
            }

            var label = reaction.IsLike ? 1.0 : 0.0;

            if (!string.IsNullOrWhiteSpace(targetProfile.City))
            {
                if (!cityScores.TryGetValue(targetProfile.City, out var values))
                {
                    values = [];
                    cityScores[targetProfile.City] = values;
                }

                values.Add(label);
            }

            foreach (var interest in targetProfile.Interests.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!interestScores.TryGetValue(interest, out var values))
                {
                    values = [];
                    interestScores[interest] = values;
                }

                values.Add(label);
            }

            ageScores.Add((targetProfile.Age, label));
        }

        return new PreferenceSnapshot(cityScores, interestScores, ageScores);
    }

    private sealed class PreferenceSnapshot
    {
        private readonly IReadOnlyDictionary<string, List<double>> _cityScores;
        private readonly IReadOnlyDictionary<string, List<double>> _interestScores;
        private readonly IReadOnlyList<(int Age, double Label)> _ageScores;

        public PreferenceSnapshot(
            IReadOnlyDictionary<string, List<double>> cityScores,
            IReadOnlyDictionary<string, List<double>> interestScores,
            IReadOnlyList<(int Age, double Label)> ageScores)
        {
            _cityScores = cityScores;
            _interestScores = interestScores;
            _ageScores = ageScores;
        }

        public double GetCityAffinity(string city)
        {
            return _cityScores.TryGetValue(city, out var scores)
                ? scores.Average()
                : 0.5;
        }

        public double GetInterestAffinity(IEnumerable<string> interests)
        {
            var values = interests
                .Where(static interest => !string.IsNullOrWhiteSpace(interest))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(interest => _interestScores.ContainsKey(interest))
                .Select(interest => _interestScores[interest].Average())
                .ToList();

            return values.Count == 0 ? 0.5 : values.Average();
        }

        public double GetAgeAffinity(int age)
        {
            var nearby = _ageScores
                .Where(entry => Math.Abs(entry.Age - age) <= 3)
                .Select(entry => entry.Label)
                .ToList();

            return nearby.Count == 0 ? 0.5 : nearby.Average();
        }
    }
}

