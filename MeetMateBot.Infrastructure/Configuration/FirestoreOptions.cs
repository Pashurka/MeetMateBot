namespace MeetMateBot.Infrastructure.Configuration;

public sealed class FirestoreOptions
{
    public const string SectionName = "Firestore";

    public string ProjectId { get; init; } = string.Empty;

    public string CredentialsPath { get; init; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProjectId)
        && !ProjectId.StartsWith("PASTE_", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(CredentialsPath)
        && !CredentialsPath.StartsWith("PASTE_", StringComparison.OrdinalIgnoreCase);
}
