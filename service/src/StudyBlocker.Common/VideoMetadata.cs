namespace StudyBlocker.Common;

// What the extension reads straight from YouTube's own player data
// (page-bridge.js) and sends up to be classified. Category is YouTube's own
// label for the video, when the player response exposes it; the classifier
// treats it as a hint, never a verdict (plan §8).
public sealed record VideoMetadata
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string? Channel { get; init; }
    public string? ChannelId { get; init; }
    public string? Category { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public long? LengthSeconds { get; init; }
    public bool IsLive { get; init; }
    public bool IsShort { get; init; }
}
