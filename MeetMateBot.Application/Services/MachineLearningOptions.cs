namespace MeetMateBot.Application.Services;

public sealed class MachineLearningOptions
{
    public const string SectionName = "MachineLearning";

    public double[] InitialWeights { get; init; } = [0.0, 0.7, 1.1, 0.7, 0.4, 0.2, 1.0, 1.2, 1.0];

    public static double[] DefaultInitialWeights => [0.0, 0.7, 1.1, 0.7, 0.4, 0.2, 1.0, 1.2, 1.0];
}
