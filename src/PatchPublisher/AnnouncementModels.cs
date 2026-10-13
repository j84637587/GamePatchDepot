using System.Text.Json.Serialization;

namespace GamePatchDepot;

public sealed record AnnouncementEntry
{
    public required string Id { get; init; }
    public long Revision { get; init; }
    public long ReadRevision { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string Category { get; init; } = "notice";
    public bool Popup { get; init; }
    public bool Withdrawn { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public override string ToString() => $"{(Withdrawn ? "[已下架] " : "")}{Title}";
}

public sealed record AnnouncementFeed
{
    public int SchemaVersion { get; init; } = 1;
    public string Product { get; init; } = "NovaStoryAnnouncements";
    public long Revision { get; init; }
    public List<AnnouncementEntry> Items { get; init; } = [];
}

public sealed record AnnouncementDraft
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string Category { get; init; } = "notice";
    public bool Popup { get; init; }
    public bool RemindAgain { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AnnouncementFeed))]
[JsonSerializable(typeof(AnnouncementDraft))]
public partial class AnnouncementJsonContext : JsonSerializerContext { }
