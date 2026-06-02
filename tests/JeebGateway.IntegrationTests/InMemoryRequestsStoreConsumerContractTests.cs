using FluentAssertions;
using JeebGateway.Requests;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// REQUEST-STORE-REGRESSION-TEST — pins the core <see cref="InMemoryRequestsStore"/>
/// behaviour that multiple consumers (RequestsController, OffersController,
/// DeliveriesController, RatingsController, SettlementService, DisputeCaseService)
/// rely on, so a future refactor cannot silently break the shared store contract.
///
/// PURELY ADDITIVE — test-only, exercises the existing store through its existing
/// <see cref="IRequestsStore"/> interface. It does NOT instantiate the store via a
/// removed/changed signature; it uses the public parameterless constructor the DI
/// container uses (see Program.cs: AddSingleton&lt;IRequestsStore, InMemoryRequestsStore&gt;()).
///
/// Asserts:
///   - CreateAsync persists a request in the Pending state with the caller's id.
///   - GetAsync round-trips the created request.
///   - CountActiveForClientAsync reflects the active request.
///   - SetStatusAsync drives a valid state transition.
///   - A terminal status removes the request from the active count (BR-9 path).
/// </summary>
public class InMemoryRequestsStoreConsumerContractTests
{
    private static InMemoryRequestsStore NewStore() => new(TimeProvider.System);

    private static CreateRequestInput SampleInput(string clientId) => new()
    {
        ClientId = clientId,
        Description = "Store contract probe",
        TierId = "flash",
        PickupLocation = new GeoPoint { Lat = 24.7, Lng = 46.7 },
        DropoffLocation = new GeoPoint { Lat = 24.6, Lng = 46.7 }
    };

    [Fact]
    public async Task Create_Then_Get_RoundTrips_In_Pending_State()
    {
        var store = NewStore();
        var clientId = $"client-{Guid.NewGuid()}";

        var created = await store.CreateAsync(SampleInput(clientId), CancellationToken.None);

        created.Id.Should().NotBeNullOrWhiteSpace();
        created.ClientId.Should().Be(clientId);
        created.Status.Should().Be(RequestStatus.Pending);

        var fetched = await store.GetAsync(created.Id, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(created.Id);
        fetched.ClientId.Should().Be(clientId);
    }

    [Fact]
    public async Task CountActiveForClient_Reflects_Open_Request()
    {
        var store = NewStore();
        var clientId = $"client-{Guid.NewGuid()}";

        (await store.CountActiveForClientAsync(clientId, CancellationToken.None)).Should().Be(0);

        await store.CreateAsync(SampleInput(clientId), CancellationToken.None);

        (await store.CountActiveForClientAsync(clientId, CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task SetStatus_Drives_Valid_Transition()
    {
        var store = NewStore();
        var clientId = $"client-{Guid.NewGuid()}";
        var created = await store.CreateAsync(SampleInput(clientId), CancellationToken.None);

        var ok = await store.SetStatusAsync(created.Id, RequestStatus.Matched, CancellationToken.None);
        ok.Should().BeTrue();

        var fetched = await store.GetAsync(created.Id, CancellationToken.None);
        fetched!.Status.Should().Be(RequestStatus.Matched);
    }

    [Fact]
    public async Task Terminal_Status_Drops_Request_From_Active_Count()
    {
        var store = NewStore();
        var clientId = $"client-{Guid.NewGuid()}";
        var created = await store.CreateAsync(SampleInput(clientId), CancellationToken.None);

        (await store.CountActiveForClientAsync(clientId, CancellationToken.None)).Should().Be(1);

        await store.SetStatusAsync(created.Id, RequestStatus.Cancelled, CancellationToken.None);

        (await store.CountActiveForClientAsync(clientId, CancellationToken.None)).Should().Be(0);
    }
}
