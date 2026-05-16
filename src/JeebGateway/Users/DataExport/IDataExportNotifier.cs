using System.Collections.Concurrent;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Seam for sending the "your export is ready" message to the user.
/// Production wiring will fan this out to notification-service (email +
/// push, per the AC). The MVP records calls in <see cref="InMemoryDataExportNotifier"/>
/// so integration tests can assert that the link reached the user.
/// </summary>
public interface IDataExportNotifier
{
    Task NotifyReadyAsync(string userId, string exportId, string downloadToken, DateTimeOffset linkExpiresAt, CancellationToken ct);
}

public class InMemoryDataExportNotifier : IDataExportNotifier
{
    public record Sent(string UserId, string ExportId, string DownloadToken, DateTimeOffset LinkExpiresAt);

    private readonly ConcurrentBag<Sent> _sent = new();

    public IReadOnlyList<Sent> All => _sent.ToArray();

    public Task NotifyReadyAsync(string userId, string exportId, string downloadToken, DateTimeOffset linkExpiresAt, CancellationToken ct)
    {
        _sent.Add(new Sent(userId, exportId, downloadToken, linkExpiresAt));
        return Task.CompletedTask;
    }
}
