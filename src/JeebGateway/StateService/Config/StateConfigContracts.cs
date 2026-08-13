using System.Text.Json;

namespace JeebGateway.StateService.Config;

// G-27 — the ONE versioned-config primitive on jeeb-state-service (/v1/config-surfaces + /v1/acks).
// Both the moderation lexicon and the CMS envelopes are surfaces; acks key to a surface version.
public sealed class ConfigDraftUpsertRequestV1
{
    public required string Application { get; init; }
    public string? Title { get; init; }
    public required JsonElement Data { get; init; }
}

public sealed class ConfigPublishRequestV1
{
    public required string Application { get; init; }
    public required string PublishedByRef { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }

    // Opaque token the surface's consumers acknowledge (the gateway's lexicon version string).
    public string? VersionTag { get; init; }
}

public sealed class ConfigVersionRecordV1
{
    public int Version { get; init; }
    public JsonElement Data { get; init; }
    public string VersionTag { get; init; } = string.Empty;
    public string PublishedByRef { get; init; } = string.Empty;
    public DateTimeOffset PublishedAt { get; init; }
}

public sealed class ConfigSurfaceRecordV1
{
    public string SurfaceKey { get; init; } = string.Empty;
    public string Application { get; init; } = string.Empty;
    public string? Title { get; init; }
    public JsonElement Draft { get; init; }
    public int LatestVersion { get; init; }
    public ConfigVersionRecordV1? Published { get; init; }
}

public sealed class ConfigAckUpsertRequestV1
{
    public required string Application { get; init; }
    public required string Version { get; init; }
    public DateTimeOffset? AckedAt { get; init; }
}

public sealed class ConfigAckRecordV1
{
    public string SubjectRef { get; init; } = string.Empty;
    public string SurfaceKey { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public DateTimeOffset AckedAt { get; init; }
}
