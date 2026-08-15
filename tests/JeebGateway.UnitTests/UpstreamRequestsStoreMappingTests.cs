using System.Text.Json;
using JeebGateway.Requests;
using Xunit;

namespace JeebGateway.UnitTests;

// W5-04: the owner-record → DeliveryRequest translation is the load-bearing
// seam of the upstream-authority store; prove it field-for-field without HTTP.
public class UpstreamRequestsStoreMappingTests
{
    private static RequestOwnerRow FullRow() => new()
    {
        RequestId = "req-1",
        ClientId = "client-1",
        ProviderId = "jeeber-1",
        Status = "picked_up",
        ConversationId = "conv-1",
        CreatedAt = DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
        AcceptedAt = DateTimeOffset.Parse("2026-08-01T10:05:00Z"),
        Title = "Groceries",
        TierId = "tier-1",
        TierName = "Express",
        PickupAddress = "A st",
        DropoffAddress = "B st",
        Description = "two bags",
        Transcription = "raw stt",
        TranscriptionConfidence = 0.87,
        AudioUrl = "https://cdn/x.m4a",
        Photos = new[] { "https://cdn/p1.jpg" },
        PickupLat = 33.5,
        PickupLng = 35.5,
        DropoffLat = 33.6,
        DropoffLng = 35.6,
        RecipientPhone = "+9613000077",
        ScheduledAt = DateTimeOffset.Parse("2026-08-02T09:00:00Z"),
        ActivatedAt = DateTimeOffset.Parse("2026-08-02T08:30:00Z"),
        ExpiredAt = null,
        AcceptedFee = 12.5m,
        GpsTrackingActive = true,
        UpdatedAt = DateTimeOffset.Parse("2026-08-01T11:00:00Z"),
        CancelledBy = "client",
        CancellationReason = "late",
        CancellationRequestedAt = DateTimeOffset.Parse("2026-08-01T10:30:00Z"),
        CancellationPreviousStatus = "picked_up",
        UnreachableAt = DateTimeOffset.Parse("2026-08-01T10:40:00Z"),
        EscalationRef = "esc-1",
    };

    [Fact]
    public void Map_carries_every_served_field()
    {
        var req = UpstreamRequestsStore.Map(FullRow());

        Assert.Equal("req-1", req.Id);
        Assert.Equal("client-1", req.ClientId);
        Assert.Equal("picked_up", req.Status);
        Assert.Equal("two bags", req.Description);
        Assert.Equal("raw stt", req.Transcription);
        Assert.Equal(0.87, req.TranscriptionConfidence);
        Assert.Equal("https://cdn/x.m4a", req.AudioUrl);
        Assert.Equal(new[] { "https://cdn/p1.jpg" }, req.Photos);
        Assert.Equal("tier-1", req.TierId);
        Assert.NotNull(req.PickupLocation);
        Assert.Equal(33.5, req.PickupLocation!.Lat);
        Assert.Equal(35.5, req.PickupLocation.Lng);
        Assert.Equal(33.6, req.DropoffLocation!.Lat);
        Assert.Equal("A st", req.PickupAddress);
        Assert.Equal("B st", req.DropoffAddress);
        Assert.Equal("+9613000077", req.RecipientPhone);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T10:00:00Z"), req.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-08-02T09:00:00Z"), req.ScheduledAt);
        Assert.Equal(DateTimeOffset.Parse("2026-08-02T08:30:00Z"), req.ActivatedAt);
        Assert.Null(req.ExpiredAt);
        Assert.Equal("jeeber-1", req.JeeberId);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T10:05:00Z"), req.AcceptedAt);
        Assert.Equal(12.5m, req.AcceptedFee);
        Assert.Equal("conv-1", req.ConversationId);
        Assert.True(req.GpsTrackingActive);
        Assert.Equal("client", req.CancelledBy);
        Assert.Equal("late", req.CancellationReason);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T10:30:00Z"), req.CancellationRequestedAt);
        Assert.Equal("picked_up", req.CancellationPreviousStatus);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T10:40:00Z"), req.ClientUnreachableAt);
        Assert.Equal("esc-1", req.OtpEscalationId);
        // The owner never stores the code; the row-level OTP stays unissued.
        Assert.Null(req.DeliveryOtp);
        Assert.Equal(0, req.OtpAttemptCount);
        Assert.Null(req.OtpLockedAt);
    }

    [Fact]
    public void Map_normalises_absent_optionals()
    {
        var req = UpstreamRequestsStore.Map(new RequestOwnerRow
        {
            RequestId = "req-2",
            ClientId = "client-2",
            Status = "pending",
            CreatedAt = DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
            ProviderId = "",
            ConversationId = "",
            Photos = null,
            PickupLat = 33.5, // lng missing → no half-built point
        });

        Assert.Equal(string.Empty, req.Description);
        Assert.Empty(req.Photos);
        Assert.Null(req.JeeberId);
        Assert.Null(req.ConversationId);
        Assert.Null(req.PickupLocation);
        Assert.Null(req.DropoffLocation);
        Assert.Null(req.AcceptedFee);
        Assert.False(req.GpsTrackingActive);
    }

    [Fact]
    public void OwnerJson_deserialises_on_the_go_marshalled_keys()
    {
        // Shape as delivery-service emits it (Go default marshalling).
        const string json = """
        {
          "RequestID": "req-3", "ClientID": "client-3", "ProviderID": "jeeber-3",
          "Status": "accepted", "Title": "T", "TierID": "tier-x", "TierName": null,
          "PickupAddress": "A", "DropoffAddress": null, "ConversationID": "conv-3",
          "OffersCount": 2, "CreatedAt": "2026-08-01T10:00:00Z",
          "AcceptedAt": "2026-08-01T10:05:00Z", "Description": "d",
          "Transcription": null, "TranscriptionConfidence": null, "AudioURL": null,
          "Photos": ["u1", "u2"], "PickupLat": 1.5, "PickupLng": 2.5,
          "DropoffLat": null, "DropoffLng": null, "RecipientPhone": null,
          "ScheduledAt": null, "ActivatedAt": null, "ExpiredAt": null,
          "AcceptedFee": 9.75, "GpsTrackingActive": false,
          "UpdatedAt": "2026-08-01T10:06:00Z", "CancelledBy": null,
          "CancellationReason": null, "CancellationRequestedAt": null,
          "CancellationApprovedAt": null, "CancellationRejectedAt": null,
          "CancellationPreviousStatus": null, "UnreachableAt": null, "EscalationRef": null
        }
        """;

        var row = JsonSerializer.Deserialize<RequestOwnerRow>(json)!;
        var req = UpstreamRequestsStore.Map(row);

        Assert.Equal("req-3", req.Id);
        Assert.Equal("accepted", req.Status);
        Assert.Equal(new[] { "u1", "u2" }, req.Photos);
        Assert.Equal(9.75m, req.AcceptedFee);
        Assert.Equal(1.5, req.PickupLocation!.Lat);
        Assert.Null(req.DropoffLocation);
        Assert.Equal("jeeber-3", req.JeeberId);
    }
}
