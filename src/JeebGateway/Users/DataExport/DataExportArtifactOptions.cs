namespace JeebGateway.Users.DataExport;

// gwdbx W1-06 (G-20) — gateway-held AES-GCM key + the owner-decided cdn upload prefix.
// The key NEVER leaves the gateway; cdn only ever receives ciphertext.
public sealed class DataExportArtifactOptions
{
    public const string SectionName = "DataExport";

    // Base64 AES key (16/24/32 bytes). Unset is legal while DataExportMode is "local";
    // the boot validator refuses any higher ladder value without it.
    public string? ArtifactKey { get; set; }

    // cdn slot = the object-key PREFIX (owner decision). Never user-derived: the random
    // part of the objectRef comes from the mint, not from this value.
    public string CdnSlot { get; set; } = "data-export";

    // Signed-PUT lifetime requested from cdn; cdn clamps it to <= 300s (BR-2).
    public int UploadTtlSeconds { get; set; } = 300;
}
