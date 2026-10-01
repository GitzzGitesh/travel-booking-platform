using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.Modules.Access.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Managed staff role grants with maker-checker (ADR 0022; security rules: permission grants are high-risk and support
/// maker-checker), over HTTP with a real SQL Server: one administrator requests, a different one approves; nobody
/// requests a change to their own access or approves their own request; access changes only on approval, at the next
/// sign-in; every request and decision is audited.
/// </summary>
public sealed class AccessRoleGrantTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _changes = "/api/admin/v1/access/role-changes";

    [Fact]
    public async Task A_grant_takes_effect_only_when_a_second_administrator_approves_it_and_a_revocation_likewise()
    {
        var newcomer = NewAccount();
        (await Queue(newcomer)).ShouldBe(HttpStatusCode.Forbidden); // no role yet

        var request = await RequestChange(Admin(), newcomer, "Operations", "Grant", "TICKET-100");
        (await Queue(newcomer)).ShouldBe(HttpStatusCode.Forbidden); // only requested
        using var selfApproval = await Decide(Admin(), request, approve: true, "TICKET-100");
        (await Problem(selfApproval)).ShouldBe((HttpStatusCode.Forbidden, "self-approval-not-allowed"));

        using var approved = await Decide(SecondAdmin(), request, approve: true, "TICKET-100 checked");
        (await Read(approved)).GetProperty("status").GetString().ShouldBe("Approved");
        (await Queue(newcomer)).ShouldBe(HttpStatusCode.OK); // granted at the next sign-in
        using var grants = await Send(HttpMethod.Get, "/api/admin/v1/access/role-grants", Admin());
        (await Read(grants)).EnumerateArray().ShouldContain(g => g.GetProperty("objectId").GetString() == newcomer && g.GetProperty("role").GetString() == "Operations");

        var revocation = await RequestChange(SecondAdmin(), newcomer, "Operations", "Revoke", "TICKET-101");
        (await Queue(newcomer)).ShouldBe(HttpStatusCode.OK); // still held until approved
        (await Decide(Admin(), revocation, approve: true, "TICKET-101")).Dispose();
        (await Queue(newcomer)).ShouldBe(HttpStatusCode.Forbidden); // revoked at once

        var audit = await Audit(request, revocation);
        audit.Select(a => a.Action).ShouldBe(["access.role-change.request", "access.role-change.approve", "access.role-change.request", "access.role-change.approve"]);
        audit.ShouldAllBe(a => a.Actor.StartsWith("staff:"));
    }

    [Fact]
    public async Task Nobody_requests_a_change_to_their_own_access()
    {
        using var response = await Send(HttpMethod.Post, _changes, Admin(),
            new { objectId = TestStaffTokens.Administrator, role = "Privacy", action = "Grant", reason = "TICKET-7" });

        (await Problem(response)).ShouldBe((HttpStatusCode.Forbidden, "self-approval-not-allowed"));
    }

    [Fact]
    public async Task One_pending_request_per_account_and_role_and_nothing_to_grant_twice_or_revoke_when_not_held()
    {
        var target = NewAccount();
        var request = await RequestChange(Admin(), target, "Privacy", "Grant", "TICKET-200");

        using var duplicate = await Send(HttpMethod.Post, _changes, SecondAdmin(), new { objectId = target, role = "Privacy", action = "Grant", reason = "TICKET-200" });
        using var revokeNotHeld = await Send(HttpMethod.Post, _changes, SecondAdmin(), new { objectId = target, role = "Operations", action = "Revoke", reason = "TICKET-201" });
        (await Problem(duplicate)).ShouldBe((HttpStatusCode.Conflict, "role-change-pending"));
        (await Problem(revokeNotHeld)).ShouldBe((HttpStatusCode.Conflict, "nothing-to-change"));

        (await Decide(SecondAdmin(), request, approve: true, "TICKET-200")).Dispose();
        using var again = await Decide(SecondAdmin(), request, approve: true, "TICKET-200");
        using var grantTwice = await Send(HttpMethod.Post, _changes, Admin(), new { objectId = target, role = "Privacy", action = "Grant", reason = "TICKET-202" });
        (await Problem(again)).ShouldBe((HttpStatusCode.Conflict, "already-decided"));
        (await Problem(grantTwice)).ShouldBe((HttpStatusCode.Conflict, "nothing-to-change"));
    }

    [Fact]
    public async Task A_rejected_request_grants_nothing()
    {
        var target = NewAccount();
        var request = await RequestChange(Admin(), target, "Operations", "Grant", "TICKET-300");

        using var rejected = await Decide(SecondAdmin(), request, approve: false, "TICKET-300 not needed");

        (await Read(rejected)).GetProperty("status").GetString().ShouldBe("Rejected");
        (await Queue(target)).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_account_a_change_concerns_never_decides_on_it_and_a_revocation_is_withdrawn_only_by_its_requester()
    {
        // The second administrator gets Privacy through a managed grant, so the first can request its revocation.
        var grant = await RequestChange(Admin(), TestStaffTokens.SecondAdministrator, "Privacy", "Grant", "TICKET-400");
        using var targetApproves = await Decide(SecondAdmin(), grant, approve: true, "TICKET-400");
        (await Problem(targetApproves)).ShouldBe((HttpStatusCode.Forbidden, "self-approval-not-allowed"));
        (await Decide(ThirdAdmin(), grant, approve: true, "TICKET-400")).Dispose();

        var revocation = await RequestChange(Admin(), TestStaffTokens.SecondAdministrator, "Privacy", "Revoke", "TICKET-401");
        using var targetVetoes = await Decide(SecondAdmin(), revocation, approve: false, "TICKET-401");
        using var otherRejects = await Decide(ThirdAdmin(), revocation, approve: false, "TICKET-401");
        (await Problem(targetVetoes)).ShouldBe((HttpStatusCode.Forbidden, "self-approval-not-allowed"));
        (await Problem(otherRejects)).ShouldBe((HttpStatusCode.Forbidden, "revocation-withdrawal-only"));

        using var withdrawn = await Decide(Admin(), revocation, approve: false, "TICKET-401 raised in error");
        (await Read(withdrawn)).GetProperty("status").GetString().ShouldBe("Rejected");
    }

    [Fact]
    public async Task Parallel_approvals_grant_once()
    {
        var target = NewAccount();
        var request = await RequestChange(Admin(), target, "Operations", "Grant", "TICKET-500");

        var responses = await Task.WhenAll(Decide(SecondAdmin(), request, approve: true, "TICKET-500"), Decide(ThirdAdmin(), request, approve: true, "TICKET-500"));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        using var grants = await Send(HttpMethod.Get, "/api/admin/v1/access/role-grants", Admin());
        (await Read(grants)).EnumerateArray().Count(g => g.GetProperty("objectId").GetString() == target).ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_role_from_configuration_is_changed_there_and_listed_with_its_source()
    {
        using var revoke = await Send(HttpMethod.Post, _changes, Admin(), new { objectId = TestStaffTokens.Operations, role = "Operations", action = "Revoke", reason = "TICKET-600" });
        using var grants = await Send(HttpMethod.Get, "/api/admin/v1/access/role-grants", Admin());

        (await Problem(revoke)).ShouldBe((HttpStatusCode.Conflict, "granted-by-configuration"));
        (await Read(grants)).EnumerateArray()
            .ShouldContain(g => g.GetProperty("objectId").GetString() == TestStaffTokens.Operations && g.GetProperty("source").GetString() == "configuration");
    }

    [Fact]
    public async Task A_request_whose_requester_lost_the_right_to_request_cannot_be_approved()
    {
        // A managed Administrator files a request, then loses Administrator; their queue is void.
        var maker = NewAccount();
        var makerGrant = await RequestChange(Admin(), maker, "Administrator", "Grant", "TICKET-700");
        (await Decide(SecondAdmin(), makerGrant, approve: true, "TICKET-700")).Dispose();
        var pending = await RequestChange(TestStaffTokens.For(maker), NewAccount(), "Operations", "Grant", "TICKET-701");
        var revocation = await RequestChange(Admin(), maker, "Administrator", "Revoke", "TICKET-702");
        (await Decide(SecondAdmin(), revocation, approve: true, "TICKET-702")).Dispose();

        using var approve = await Decide(Admin(), pending, approve: true, "TICKET-701");

        (await Problem(approve)).ShouldBe((HttpStatusCode.Conflict, "requester-no-longer-authorized"));
    }

    [Fact]
    public async Task A_request_pending_too_long_cannot_be_approved_and_an_unknown_one_is_not_found()
    {
        var request = await RequestChange(Admin(), NewAccount(), "Operations", "Grant", "TICKET-800");
        api.Clock.Advance(TimeSpan.FromDays(8));

        using var late = await Decide(SecondAdmin(), request, approve: true, "TICKET-800");
        using var unknown = await Decide(SecondAdmin(), Guid.NewGuid(), approve: true, "TICKET-800");
        using var badStatus = await Send(HttpMethod.Get, $"{_changes}?status=Whatever", Admin());

        (await Problem(late)).ShouldBe((HttpStatusCode.Conflict, "request-expired"));
        (await Problem(unknown)).ShouldBe((HttpStatusCode.NotFound, "role-change-not-found"));
        badStatus.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("SuperUser", "Grant", "TICKET-1")] // an unknown role
    [InlineData("Operations", "Grant", "TICKET 4111 1111 1111 1111")] // never a card number in an audited reason
    [InlineData("Operations", "Promote", "TICKET-1")] // not an action
    public async Task An_invalid_request_is_refused(string role, string action, string reason)
    {
        using var response = await Send(HttpMethod.Post, _changes, Admin(), new { objectId = NewAccount(), role, action, reason });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_object_id_is_a_canonical_lowercase_guid_so_no_account_matches_another_by_case()
    {
        using var upper = await Send(HttpMethod.Post, _changes, Admin(), new { objectId = NewAccount().ToUpperInvariant(), role = "Operations", action = "Grant", reason = "TICKET-1" });
        using var notAGuid = await Send(HttpMethod.Post, _changes, Admin(), new { objectId = "someone", role = "Operations", action = "Grant", reason = "TICKET-1" });

        (upper.StatusCode, notAGuid.StatusCode).ShouldBe((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest));
    }

    [Theory]
    [InlineData("GET", _changes)]
    [InlineData("GET", "/api/admin/v1/access/role-grants")]
    [InlineData("POST", _changes)]
    [InlineData("POST", _changes + "/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/decision")]
    public async Task Only_administrators_manage_access(string method, string url)
    {
        object? body = method == "POST" ? new { objectId = "0b6a4d1e-0000-4a55-9f86-0000000000ff", role = "Operations", action = "Grant", approve = true, reason = "TICKET-1" } : null;

        using var anonymous = await Send(new HttpMethod(method), url, null, body);
        using var customer = await Send(new HttpMethod(method), url, TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow), body);
        using var withoutMfa = await Send(new HttpMethod(method), url, TestStaffTokens.For(TestStaffTokens.Administrator, mfa: false), body);

        (anonymous.StatusCode, customer.StatusCode, withoutMfa.StatusCode).ShouldBe((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized));
        foreach (var notAnAdministrator in new[] { TestStaffTokens.Operations, TestStaffTokens.Privacy, TestStaffTokens.Unassigned })
        {
            using var refused = await Send(new HttpMethod(method), url, TestStaffTokens.For(notAnAdministrator), body);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    // ---------- Helpers ----------

    private static string Admin() => TestStaffTokens.For(TestStaffTokens.Administrator);

    private static string SecondAdmin() => TestStaffTokens.For(TestStaffTokens.SecondAdministrator);

    private static string ThirdAdmin() => TestStaffTokens.For(TestStaffTokens.ThirdAdministrator);

    /// <summary>A workforce account id as Entra issues it: a lowercase GUID.</summary>
    private static string NewAccount() => Guid.NewGuid().ToString();

    private async Task<HttpStatusCode> Queue(string objectId)
    {
        using var response = await Send(HttpMethod.Get, "/api/admin/v1/orders", TestStaffTokens.For(objectId));
        return response.StatusCode;
    }

    private async Task<Guid> RequestChange(string token, string objectId, string role, string action, string reason)
    {
        using var response = await Send(HttpMethod.Post, _changes, token, new { objectId, role, action, reason });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await Read(response)).GetProperty("requestId").GetGuid();
    }

    private Task<HttpResponseMessage> Decide(string token, Guid requestId, bool approve, string reason) =>
        Send(HttpMethod.Post, $"{_changes}/{requestId}/decision", token, new { approve, reason });

    private async Task<List<AuditEntry>> Audit(params Guid[] requests)
    {
        using var scope = api.Services.CreateScope();
        var targets = requests.Select(r => $"role-change:{r}").ToList();
        return await scope.ServiceProvider.GetRequiredService<AccessDbContext>().Set<AuditEntry>().AsNoTracking()
            .Where(a => targets.Contains(a.Target)).OrderBy(a => a.Id).ToListAsync(Ct);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token, object? body = null)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<(HttpStatusCode, string?)> Problem(HttpResponseMessage response) =>
        (response.StatusCode, (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
