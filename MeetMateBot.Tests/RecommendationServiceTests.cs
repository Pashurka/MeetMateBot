using MeetMateBot.Application.Interfaces;
using MeetMateBot.Application.Services;
using MeetMateBot.Domain.Entities;
using Microsoft.Extensions.Options;

namespace MeetMateBot.Tests;

public sealed class RecommendationServiceTests
{
    [Fact]
    public async Task GetBestRecommendationAsync_PrefersProfileWithBetterCombinedScore()
    {
        var currentProfile = CreateProfile(
            1,
            "Olia",
            24,
            "female",
            "male",
            "Kyiv",
            ["music", "travel", "cinema"],
            "I like meaningful conversations and traveling.");

        var strongCandidate = CreateProfile(
            2,
            "Artem",
            25,
            "male",
            "female",
            "Kyiv",
            ["music", "travel", "coffee"],
            "Active, open-minded, and ready for new connections.",
            updatedAtUtc: DateTime.UtcNow.AddDays(-2));

        var weakerCandidate = CreateProfile(
            3,
            "Maksym",
            33,
            "male",
            "female",
            "Lviv",
            ["sport"],
            "Hi.",
            updatedAtUtc: DateTime.UtcNow.AddDays(-90));

        var service = CreateService([currentProfile, strongCandidate, weakerCandidate]);

        var recommendation = await service.GetBestRecommendationAsync(currentProfile.TelegramChatId, CancellationToken.None);

        Assert.NotNull(recommendation);
        Assert.Equal(strongCandidate.TelegramChatId, recommendation.RecommendedProfile.TelegramChatId);
        Assert.True(recommendation.InterestScore >= 18);
        Assert.True(recommendation.CompatibilityPercent > 70);
    }

    [Fact]
    public async Task GetBestRecommendationAsync_ExcludesProfilesWithExistingMatches()
    {
        var currentProfile = CreateProfile(1, "Olia", 24, "female", "male", "Kyiv", ["music"], "Looking for interesting conversations.");
        var matchedCandidate = CreateProfile(2, "Artem", 25, "male", "female", "Kyiv", ["music"], "I enjoy concerts.");
        var availableCandidate = CreateProfile(3, "Ivan", 26, "male", "female", "Kyiv", ["music", "cinema"], "I like cinema and coffee.");

        var matchRepository = new InMemoryMatchRepository(
            [
                new ProfileMatch
                {
                    FirstTelegramChatId = currentProfile.TelegramChatId,
                    SecondTelegramChatId = matchedCandidate.TelegramChatId,
                    IsActive = true
                }
            ]);

        var service = CreateService(
            [currentProfile, matchedCandidate, availableCandidate],
            matchRepository: matchRepository);

        var recommendation = await service.GetBestRecommendationAsync(currentProfile.TelegramChatId, CancellationToken.None);

        Assert.NotNull(recommendation);
        Assert.Equal(availableCandidate.TelegramChatId, recommendation.RecommendedProfile.TelegramChatId);
    }

    [Fact]
    public void CalculateScore_RewardsFreshAndCompleteProfiles()
    {
        var currentProfile = CreateProfile(1, "Olia", 24, "female", "male", "Kyiv", ["music", "cinema"], "Looking for someone with similar interests.");

        var completeProfile = CreateProfile(
            2,
            "Artem",
            25,
            "male",
            "female",
            "Kyiv",
            ["music", "cinema"],
            "I like movies, traveling, and real conversations.",
            updatedAtUtc: DateTime.UtcNow.AddDays(-1));

        var incompleteProfile = CreateProfile(
            3,
            "I",
            25,
            "male",
            "female",
            "Kyiv",
            [],
            "bio",
            updatedAtUtc: DateTime.UtcNow.AddDays(-120));

        var service = CreateService([currentProfile, completeProfile, incompleteProfile]);

        var completeScore = service.CalculateScore(currentProfile, completeProfile);
        var incompleteScore = service.CalculateScore(currentProfile, incompleteProfile);

        Assert.True(completeScore > incompleteScore);
    }

    [Fact]
    public async Task GetBestRecommendationAsync_UsesMachineLearningFromReactionHistory()
    {
        var currentProfile = CreateProfile(
            1,
            "Olia",
            24,
            "female",
            "male",
            "Kyiv",
            ["music", "travel"],
            "Looking for a serious and warm connection.");

        var likedTravelProfile = CreateProfile(
            2,
            "Taras",
            27,
            "male",
            "female",
            "Lviv",
            ["travel", "hiking", "music"],
            "I love mountains, trips, and active weekends.");

        var dislikedCityProfile = CreateProfile(
            3,
            "Dmytro",
            24,
            "male",
            "female",
            "Kyiv",
            ["cars", "boxing"],
            "Focused on work and gym.");

        var newTravelCandidate = CreateProfile(
            4,
            "Andrii",
            26,
            "male",
            "female",
            "Lviv",
            ["travel", "music", "books"],
            "Trips, books, and coffee make my weekends better.");

        var sameCityCandidate = CreateProfile(
            5,
            "Roman",
            24,
            "male",
            "female",
            "Kyiv",
            ["cars", "gym"],
            "Always busy, always moving.");

        var reactionRepository = new InMemoryReactionRepository(
            reactedProfileIds: new HashSet<long> { likedTravelProfile.TelegramChatId, dislikedCityProfile.TelegramChatId },
            reactions:
            [
                new ProfileReaction
                {
                    FromTelegramChatId = currentProfile.TelegramChatId,
                    ToTelegramChatId = likedTravelProfile.TelegramChatId,
                    IsLike = true,
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-3)
                },
                new ProfileReaction
                {
                    FromTelegramChatId = currentProfile.TelegramChatId,
                    ToTelegramChatId = dislikedCityProfile.TelegramChatId,
                    IsLike = false,
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-2)
                }
            ]);

        var service = CreateService(
            [currentProfile, likedTravelProfile, dislikedCityProfile, newTravelCandidate, sameCityCandidate],
            reactionRepository: reactionRepository);

        var recommendation = await service.GetBestRecommendationAsync(currentProfile.TelegramChatId, CancellationToken.None);

        Assert.NotNull(recommendation);
        Assert.Equal(newTravelCandidate.TelegramChatId, recommendation.RecommendedProfile.TelegramChatId);
        Assert.Equal(2, recommendation.TrainingSampleCount);
        Assert.True(recommendation.MachineLearningScore > 50);
    }

    [Fact]
    public async Task GetRecommendationDiagnosticsAsync_ReturnsAggregatedPreferences()
    {
        var currentProfile = CreateProfile(1, "Olia", 24, "female", "male", "Kyiv", ["music"], "Looking for shared values.");
        var likedKyivProfile = CreateProfile(2, "Taras", 27, "male", "female", "Kyiv", ["music", "travel"], "Love travel and good talks.");
        var likedLvivProfile = CreateProfile(3, "Roman", 25, "male", "female", "Lviv", ["travel", "books"], "Books and trips.");
        var candidate = CreateProfile(4, "Andrii", 26, "male", "female", "Kyiv", ["music", "travel"], "Music, travel, coffee.");

        var reactionRepository = new InMemoryReactionRepository(
            reactedProfileIds: new HashSet<long> { likedKyivProfile.TelegramChatId, likedLvivProfile.TelegramChatId },
            reactions:
            [
                new ProfileReaction
                {
                    FromTelegramChatId = currentProfile.TelegramChatId,
                    ToTelegramChatId = likedKyivProfile.TelegramChatId,
                    IsLike = true
                },
                new ProfileReaction
                {
                    FromTelegramChatId = currentProfile.TelegramChatId,
                    ToTelegramChatId = likedLvivProfile.TelegramChatId,
                    IsLike = true
                }
            ]);

        var service = CreateService([currentProfile, likedKyivProfile, likedLvivProfile, candidate], reactionRepository: reactionRepository);

        var diagnostics = await service.GetRecommendationDiagnosticsAsync(currentProfile.TelegramChatId, CancellationToken.None);

        Assert.NotNull(diagnostics);
        Assert.Equal(2, diagnostics.TotalReactions);
        Assert.Equal(2, diagnostics.LikeCount);
        Assert.Contains("travel (2)", diagnostics.PreferredInterests);
        Assert.NotNull(diagnostics.TopRecommendation);
    }

    private static RecommendationService CreateService(
        IReadOnlyList<DatingProfile> profiles,
        IReactionRepository? reactionRepository = null,
        IMatchRepository? matchRepository = null)
    {
        return new RecommendationService(
            new InMemoryProfileRepository(profiles),
            reactionRepository ?? new InMemoryReactionRepository(),
            matchRepository ?? new InMemoryMatchRepository(),
            Options.Create(new MachineLearningOptions()));
    }

    private static DatingProfile CreateProfile(
        long chatId,
        string name,
        int age,
        string gender,
        string lookingForGender,
        string city,
        List<string> interests,
        string bio,
        DateTime? updatedAtUtc = null)
    {
        return new DatingProfile
        {
            TelegramChatId = chatId,
            Name = name,
            Age = age,
            Gender = gender,
            LookingForGender = lookingForGender,
            City = city,
            Interests = interests,
            Bio = bio,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-60),
            UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow.AddDays(-10)
        };
    }

    private sealed class InMemoryProfileRepository : IProfileRepository
    {
        private readonly IReadOnlyList<DatingProfile> _profiles;

        public InMemoryProfileRepository(IReadOnlyList<DatingProfile> profiles)
        {
            _profiles = profiles;
        }

        public Task<DatingProfile?> GetByTelegramChatIdAsync(long telegramChatId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_profiles.FirstOrDefault(profile => profile.TelegramChatId == telegramChatId));
        }

        public Task<IReadOnlyList<DatingProfile>> GetAllAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_profiles);
        }

        public Task SaveAsync(DatingProfile profile, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class InMemoryReactionRepository : IReactionRepository
    {
        private readonly IReadOnlySet<long> _reactedProfileIds;
        private readonly IReadOnlyList<ProfileReaction> _reactions;

        public InMemoryReactionRepository(
            IReadOnlySet<long>? reactedProfileIds = null,
            IReadOnlyList<ProfileReaction>? reactions = null)
        {
            _reactedProfileIds = reactedProfileIds ?? new HashSet<long>();
            _reactions = reactions ?? [];
        }

        public Task SaveAsync(ProfileReaction reaction, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> HasLikeAsync(long fromTelegramChatId, long toTelegramChatId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlySet<long>> GetReactedProfileIdsAsync(long fromTelegramChatId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_reactedProfileIds);
        }

        public Task<IReadOnlyList<ProfileReaction>> GetByFromTelegramChatIdAsync(long fromTelegramChatId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ProfileReaction>>(
                _reactions
                    .Where(reaction => reaction.FromTelegramChatId == fromTelegramChatId)
                    .ToList());
        }

        public Task<IReadOnlyList<ProfileReaction>> GetAllAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_reactions);
        }
    }

    private sealed class InMemoryMatchRepository : IMatchRepository
    {
        private readonly IReadOnlyList<ProfileMatch> _matches;

        public InMemoryMatchRepository(IReadOnlyList<ProfileMatch>? matches = null)
        {
            _matches = matches ?? [];
        }

        public Task<IReadOnlyList<ProfileMatch>> GetByTelegramChatIdAsync(long telegramChatId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ProfileMatch>>(
                _matches
                    .Where(match => match.FirstTelegramChatId == telegramChatId || match.SecondTelegramChatId == telegramChatId)
                    .ToList());
        }

        public Task<IReadOnlyList<ProfileMatch>> GetAllAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_matches);
        }

        public Task SaveAsync(ProfileMatch match, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
