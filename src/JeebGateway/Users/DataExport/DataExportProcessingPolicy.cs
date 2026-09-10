using Microsoft.Extensions.Options;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// One immutable snapshot shared by hosted, manual and direct execution paths.
/// This policy never gates user-facing export availability or readiness.
/// </summary>
public sealed class DataExportProcessingPolicy(IOptions<DataExportOptions> options)
{
    public bool IsPaused { get; } = !options.Value.ProcessingEnabled;

    public void EnsureProcessingEnabled()
    {
        if (IsPaused)
            throw new DataExportProcessingPausedException();
    }
}

public sealed class DataExportProcessingPausedException()
    : InvalidOperationException("Data export processing is paused.");
