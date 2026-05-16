using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Disputes;
using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-025 / JEEB-43: dispute reporting API integration tests.
///
/// Every AC bullet is pinned:
///   * filer can POST a dispute against an existing delivery
///   * dispute lands in state <c>filed</c> with the supplied category, description, and photos
///   * categories outside the allowed set are rejected with 400
///   * photo cap is 3
///   * one open dispute per delivery
///   * GET /disputes returns the caller's own disputes
///   * GET /disputes/{id} is filer-or-admin readable
///   * PUT /admin/disputes/{id}/resolve enforces the state machine (filed → under_review → resolved/dismissed)
///   * non-admin cannot resolve
///   * push fan-out fires through the unified IPushNotificationService pipeline
/// </summary>
public class DisputeServiceTests
{
    private const string OtherUser = "other-user";

    [Fact]
    public async Task File_Without_Identity_Returns_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var anon = factory.CreateClient();
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "any");

        var resp = await anon.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "damaged box"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task File_Against_Unknown_Delivery_Returns_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var http = ClientFor(factory, "u-1");

        var resp = await http.PostAsJsonAsync("/deliveries/does-not-exist/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "damaged box"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task File_Creates_Dispute_In_Filed_State()
    {
        using var factory = new WebApplicationFactory<Program>();
        var filer = "u-filed";
        var http = ClientFor(factory, filer);
        var deliveryId = await SeedDeliveryAsync(factory, clientId: filer);
        var photos = new List<string> { "https://cdn.example.com/a.jpg", "https://cdn.example.com/b.jpg" };
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var resp = await http.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "Box arrived crushed",
            PhotoUrls = photos
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<DisputeResponse>();

        body.Should().NotBeNull();
        body!.Id.Should().StartWith("dsp_");
        body.DeliveryId.Should().Be(deliveryId);
        body.FiledByUserId.Should().Be(filer);
        body.Category.Should().Be(DisputeCategory.DamagedGoods);
        body.Description.Should().Be("Box arrived crushed");
        body.PhotoUrls.Should().BeEquivalentTo(photos);
        body.State.Should().Be(DisputeState.Filed);
        body.FiledAt.Should().BeOnOrAfter(before);
        body.ReviewedAt.Should().BeNull();
        body.ResolverAdminId.Should().BeNull();
        body.Resolution.Should().BeNull();
    }

    [Fact]
    public async Task File_Sends_Filer_Push_Notification()
    {
        using var factory = new WebApplicationFactory<Program>();
        var filer = "u-push";
        var http = ClientFor(factory, filer);
        var deliveryId = await SeedDeliveryAsync(factory, clientId: filer);
        var tracker = factory.Services.GetRequiredService<InMemoryPushDeliveryTracker>();

        (await http.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.SafetyConcern,
            Description = "driver was unsafe"
        })).EnsureSuccessStatusCode();

        // No devices are registered, so the unified push pipeline records a
        // NoDevices outcome — that's still the assertion we want: SendAsync
        // was invoked with the filer's id under the StatusChange trigger.
        var entries = await tracker.GetForUserAsync(filer, CancellationToken.None);
        entries.Should().Contain(r => r.Trigger == NotificationTrigger.StatusChange,
            "filing a dispute must fan out a StatusChange push to the filer");
    }

    [Theory]
    [InlineData("not_a_category")]
    [InlineData("")]
    [InlineData(null)]
    public async Task File_With_Invalid_Category_Returns_400(string? category)
    {
        using var factory = new WebApplicationFactory<Program>();
        var http = ClientFor(factory, "u-bad-cat");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "u-bad-cat");

        var resp = await http.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = category,
            Description = "something"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task File_With_Empty_Description_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var http = ClientFor(factory, "u-empty-desc");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "u-empty-desc");

        var resp = await http.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.NoDelivery,
            Description = "   "
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task File_With_More_Than_Three_Photos_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var http = ClientFor(factory, "u-photo-cap");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "u-photo-cap");

        var resp = await http.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "lots of photos",
            PhotoUrls = new List<string>
            {
                "https://cdn.example.com/a.jpg",
                "https://cdn.example.com/b.jpg",
                "https://cdn.example.com/c.jpg",
                "https://cdn.example.com/d.jpg"
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task File_With_Malformed_Photo_Url_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var http = ClientFor(factory, "u-bad-url");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "u-bad-url");

        var resp = await http.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "weird url",
            PhotoUrls = new List<string> { "ftp://example.com/x.jpg" }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task File_Second_Open_Dispute_For_Same_Delivery_Returns_409()
    {
        using var factory = new WebApplicationFactory<Program>();
        var http = ClientFor(factory, "u-dup");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "u-dup");

        var first = await http.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "first attempt"
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await http.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "second attempt"
        });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_Returns_Only_The_Callers_Disputes()
    {
        using var factory = new WebApplicationFactory<Program>();
        var alice = ClientFor(factory, "alice");
        var bob = ClientFor(factory, "bob");

        var d1 = await SeedDeliveryAsync(factory, clientId: "alice");
        var d2 = await SeedDeliveryAsync(factory, clientId: "bob");

        (await alice.PostAsJsonAsync($"/deliveries/{d1}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.Overcharged,
            Description = "alice case"
        })).EnsureSuccessStatusCode();

        (await bob.PostAsJsonAsync($"/deliveries/{d2}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.NoDelivery,
            Description = "bob case"
        })).EnsureSuccessStatusCode();

        var aliceList = await alice.GetFromJsonAsync<DisputeListResponse>("/disputes");
        aliceList!.Total.Should().Be(1);
        aliceList.Items.Single().FiledByUserId.Should().Be("alice");

        var bobList = await bob.GetFromJsonAsync<DisputeListResponse>("/disputes");
        bobList!.Total.Should().Be(1);
        bobList.Items.Single().FiledByUserId.Should().Be("bob");
    }

    [Fact]
    public async Task GetOne_As_Other_Non_Admin_User_Returns_403()
    {
        using var factory = new WebApplicationFactory<Program>();
        var alice = ClientFor(factory, "alice");
        var stranger = ClientFor(factory, OtherUser);

        var deliveryId = await SeedDeliveryAsync(factory, clientId: "alice");
        var fileResp = await alice.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.WrongDelivery,
            Description = "wrong parcel"
        });
        var dispute = await fileResp.Content.ReadFromJsonAsync<DisputeResponse>();

        var resp = await stranger.GetAsync($"/disputes/{dispute!.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetOne_As_Admin_Returns_Any_Dispute()
    {
        using var factory = new WebApplicationFactory<Program>();
        var alice = ClientFor(factory, "alice");
        var admin = AdminClientFor(factory, "admin-1");

        var deliveryId = await SeedDeliveryAsync(factory, clientId: "alice");
        var fileResp = await alice.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.ProhibitedItem,
            Description = "they tried to ship a banned item"
        });
        var dispute = await fileResp.Content.ReadFromJsonAsync<DisputeResponse>();

        var resp = await admin.GetAsync($"/disputes/{dispute!.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<DisputeResponse>();
        body!.Id.Should().Be(dispute.Id);
        body.FiledByUserId.Should().Be("alice");
    }

    [Fact]
    public async Task GetOne_Unknown_Returns_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var admin = AdminClientFor(factory, "admin-2");

        var resp = await admin.GetAsync("/disputes/dsp_does_not_exist");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolve_As_Non_Admin_Returns_403()
    {
        using var factory = new WebApplicationFactory<Program>();
        var filer = ClientFor(factory, "filer");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "filer");
        var fileResp = await filer.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.NoDelivery,
            Description = "parcel never arrived"
        });
        var dispute = await fileResp.Content.ReadFromJsonAsync<DisputeResponse>();

        var resp = await filer.PutAsJsonAsync($"/admin/disputes/{dispute!.Id}/resolve", new ResolveDisputeRequest
        {
            Action = "resolve",
            Resolution = "refunded"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Resolve_With_Open_Then_Resolve_Walks_The_State_Machine()
    {
        using var factory = new WebApplicationFactory<Program>();
        var filer = ClientFor(factory, "sm-filer");
        var admin = AdminClientFor(factory, "sm-admin");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "sm-filer");

        var fileResp = await filer.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "box damaged"
        });
        var dispute = await fileResp.Content.ReadFromJsonAsync<DisputeResponse>();

        // filed → under_review
        var open = await admin.PutAsJsonAsync($"/admin/disputes/{dispute!.Id}/resolve", new ResolveDisputeRequest
        {
            Action = "open"
        });
        open.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterOpen = await open.Content.ReadFromJsonAsync<DisputeResponse>();
        afterOpen!.State.Should().Be(DisputeState.UnderReview);
        afterOpen.ResolverAdminId.Should().Be("sm-admin");
        afterOpen.ReviewedAt.Should().NotBeNull();

        // under_review → resolved
        var resolve = await admin.PutAsJsonAsync($"/admin/disputes/{dispute.Id}/resolve", new ResolveDisputeRequest
        {
            Action = "resolve",
            Resolution = "refund issued"
        });
        resolve.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterResolve = await resolve.Content.ReadFromJsonAsync<DisputeResponse>();
        afterResolve!.State.Should().Be(DisputeState.Resolved);
        afterResolve.Resolution.Should().Be("refund issued");
    }

    [Fact]
    public async Task Resolve_Filed_Directly_To_Dismissed_Is_Allowed()
    {
        using var factory = new WebApplicationFactory<Program>();
        var filer = ClientFor(factory, "fd-filer");
        var admin = AdminClientFor(factory, "fd-admin");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "fd-filer");

        var fileResp = await filer.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.SafetyConcern,
            Description = "concern noted"
        });
        var dispute = await fileResp.Content.ReadFromJsonAsync<DisputeResponse>();

        var resp = await admin.PutAsJsonAsync($"/admin/disputes/{dispute!.Id}/resolve", new ResolveDisputeRequest
        {
            Action = "dismiss",
            Resolution = "insufficient evidence"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<DisputeResponse>();
        body!.State.Should().Be(DisputeState.Dismissed);
        body.Resolution.Should().Be("insufficient evidence");
    }

    [Fact]
    public async Task Resolve_Already_Terminal_Returns_409()
    {
        using var factory = new WebApplicationFactory<Program>();
        var filer = ClientFor(factory, "rt-filer");
        var admin = AdminClientFor(factory, "rt-admin");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "rt-filer");

        var fileResp = await filer.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "damaged"
        });
        var dispute = await fileResp.Content.ReadFromJsonAsync<DisputeResponse>();

        (await admin.PutAsJsonAsync($"/admin/disputes/{dispute!.Id}/resolve", new ResolveDisputeRequest
        {
            Action = "resolve",
            Resolution = "refunded"
        })).EnsureSuccessStatusCode();

        var second = await admin.PutAsJsonAsync($"/admin/disputes/{dispute.Id}/resolve", new ResolveDisputeRequest
        {
            Action = "dismiss",
            Resolution = "too late"
        });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Resolve_Missing_Resolution_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var filer = ClientFor(factory, "no-res-filer");
        var admin = AdminClientFor(factory, "no-res-admin");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "no-res-filer");

        var fileResp = await filer.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.NoDelivery,
            Description = "no parcel"
        });
        var dispute = await fileResp.Content.ReadFromJsonAsync<DisputeResponse>();

        var resp = await admin.PutAsJsonAsync($"/admin/disputes/{dispute!.Id}/resolve", new ResolveDisputeRequest
        {
            Action = "resolve",
            Resolution = "   "
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resolve_Unknown_Action_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var filer = ClientFor(factory, "ua-filer");
        var admin = AdminClientFor(factory, "ua-admin");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "ua-filer");

        var fileResp = await filer.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.Overcharged,
            Description = "fee too high"
        });
        var dispute = await fileResp.Content.ReadFromJsonAsync<DisputeResponse>();

        var resp = await admin.PutAsJsonAsync($"/admin/disputes/{dispute!.Id}/resolve", new ResolveDisputeRequest
        {
            Action = "frobnicate"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resolve_Unknown_Dispute_Returns_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var admin = AdminClientFor(factory, "u4-admin");

        var resp = await admin.PutAsJsonAsync("/admin/disputes/dsp_does_not_exist/resolve", new ResolveDisputeRequest
        {
            Action = "resolve",
            Resolution = "n/a"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolve_Sends_Filer_Push_Notification()
    {
        using var factory = new WebApplicationFactory<Program>();
        var filer = ClientFor(factory, "res-push-filer");
        var admin = AdminClientFor(factory, "res-push-admin");
        var deliveryId = await SeedDeliveryAsync(factory, clientId: "res-push-filer");

        var fileResp = await filer.PostAsJsonAsync($"/deliveries/{deliveryId}/dispute", new FileDisputeRequest
        {
            Category = DisputeCategory.DamagedGoods,
            Description = "broken"
        });
        var dispute = await fileResp.Content.ReadFromJsonAsync<DisputeResponse>();

        var tracker = factory.Services.GetRequiredService<InMemoryPushDeliveryTracker>();

        (await admin.PutAsJsonAsync($"/admin/disputes/{dispute!.Id}/resolve", new ResolveDisputeRequest
        {
            Action = "resolve",
            Resolution = "refunded"
        })).EnsureSuccessStatusCode();

        var entries = await tracker.GetForUserAsync("res-push-filer", CancellationToken.None);
        // The filer gets at least two StatusChange pushes — one at file time,
        // one at resolve time. Both record through the unified pipeline.
        entries.Count(r => r.Trigger == NotificationTrigger.StatusChange).Should().BeGreaterOrEqualTo(2,
            "resolving must fan out a second StatusChange push to the filer");
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", Roles.Client);
        return c;
    }

    private static HttpClient AdminClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", Roles.Admin);
        return c;
    }

    private static async Task<string> SeedDeliveryAsync(
        WebApplicationFactory<Program> factory,
        string clientId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "test delivery"
        }, CancellationToken.None);
        return created.Id;
    }
}
