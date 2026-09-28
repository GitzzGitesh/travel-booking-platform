using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Infrastructure;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Provider notifications (webhooks) through the Api's endpoint, a real SQL Server and the Worker's job (ADR 0006):
/// verified first, stored once per provider event, and processed by a lookup with the provider, so duplicates and
/// out-of-order events never change a payment on their own.
/// </summary>
public sealed class PaymentNotificationTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private static readonly Money _total = new(540m, new CurrencyCode("XTS"));

    [Fact]
    public async Task A_completed_challenge_is_learnt_from_the_notification_by_a_lookup()
    {
        var payment = (await Authorize(MockPaymentMethods.ChallengeCompleted)).Value;
        payment.Status.ShouldBe(OrderPaymentStatus.ActionRequired);

        (await Notify(EventFor(payment.PaymentId))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJob();

        var attempt = await Load(payment.PaymentId);
        attempt.Status.ShouldBe(PaymentAttemptStatus.Authorized);
        attempt.Events.OrderBy(e => e.Id).Select(e => (e.ToStatus, e.Actor)).Last().ShouldBe(("Authorized", PaymentAttemptReconciler.Actor));
    }

    [Fact]
    public async Task A_failed_challenge_ends_declined_and_an_abandoned_one_stays_awaiting_the_customer()
    {
        var failed = (await Authorize(MockPaymentMethods.ChallengeFailed)).Value;
        var abandoned = (await Authorize(MockPaymentMethods.RequiresAction)).Value;

        await Notify(EventFor(failed.PaymentId));
        await Notify(EventFor(abandoned.PaymentId));
        await RunJob();

        (await Load(failed.PaymentId)).Status.ShouldBe(PaymentAttemptStatus.Declined);
        (await Load(abandoned.PaymentId)).Status.ShouldBe(PaymentAttemptStatus.ActionRequired); // never reported as paid
    }

    [Fact]
    public async Task A_duplicate_delivery_is_acknowledged_and_stored_once()
    {
        var payment = (await Authorize(MockPaymentMethods.ChallengeCompleted)).Value;
        var body = EventFor(payment.PaymentId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Notify(body)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        (await Notifications(EventIdOf(body))).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_late_or_out_of_order_notification_never_moves_a_settled_payment()
    {
        var payment = (await Authorize(MockPaymentMethods.Approved)).Value;
        var before = await Load(payment.PaymentId);

        await Notify(EventFor(payment.PaymentId)); // e.g. an old "requires_action" event arriving after authorization
        await RunJob();

        var after = await Load(payment.PaymentId);
        after.Status.ShouldBe(PaymentAttemptStatus.Authorized);
        after.Events.Count.ShouldBe(before.Events.Count);
    }

    [Fact]
    public async Task Forged_and_unknown_provider_notifications_are_refused_and_nothing_is_stored()
    {
        var payment = (await Authorize(MockPaymentMethods.ChallengeCompleted)).Value;
        var body = EventFor(payment.PaymentId);

        (await Notify(body, signature: "forged")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Notify(body, provider: "stripe")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await Notifications(EventIdOf(body))).ShouldBeEmpty();
        (await Load(payment.PaymentId)).Status.ShouldBe(PaymentAttemptStatus.ActionRequired);
    }

    [Fact]
    public async Task F38_a_notification_and_reconciliation_racing_on_one_payment_make_one_transition()
    {
        var payment = (await Authorize(MockPaymentMethods.ChallengeCompleted)).Value;
        await Notify(EventFor(payment.PaymentId));

        await Task.WhenAll(RunJob(), Reconcile(payment.PaymentId), Reconcile(payment.PaymentId));

        var attempt = await Load(payment.PaymentId);
        attempt.Status.ShouldBe(PaymentAttemptStatus.Authorized);
        attempt.Events.Count(e => e.ToStatus == "Authorized").ShouldBe(1);
    }

    [Fact]
    public async Task An_oversized_notification_is_refused_unread()
    {
        var body = JsonSerializer.Serialize(new { id = "evt-big", reference = new string('a', 70_000) });

        (await Notify(body)).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await Notifications("evt-big")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notification_for_a_payment_that_is_not_ours_is_processed_without_effect()
    {
        var body = JsonSerializer.Serialize(new { id = $"evt-{Guid.NewGuid():N}", reference = $"{Guid.NewGuid():N}" });

        await Notify(body);
        await RunJob();

        (await Notifications(EventIdOf(body))).ShouldHaveSingleItem().Outcome.ShouldBe("No payment attempt of ours");
    }

    private async Task<HttpResponseMessage> Notify(string body, string signature = TestPaymentNotifications.ValidSignature, string provider = "mockpay")
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/payments/notifications/{provider}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestPaymentNotifications.SignatureHeader, signature);
        return await client.SendAsync(request, Ct);
    }

    private static string EventFor(Guid paymentId, string? eventId = null) =>
        JsonSerializer.Serialize(new { id = eventId ?? $"evt-{Guid.NewGuid():N}", reference = paymentId.ToString("N") });

    private static string EventIdOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

    private async Task RunJob()
    {
        using var scope = api.Services.CreateScope();
        await ((IBackgroundJob)scope.ServiceProvider.GetRequiredService<ProcessPaymentNotificationsJob>()).RunOnceAsync(Ct);
    }

    private async Task Reconcile(Guid attemptId)
    {
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PaymentAttemptReconciler>().ReconcileAsync(attemptId, Ct);
    }

    private async Task<Result<OrderPaymentResult, OrderPaymentFailure>> Authorize(string token)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderPayments>()
            .AuthorizeAsync(new OrderPaymentRequest(Guid.NewGuid(), "test-customer-1", $"pay-{Guid.NewGuid():N}", _total, token, "test-trace"), Ct);
    }

    private async Task<PaymentAttempt> Load(Guid id)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts.AsNoTracking()
            .Include(a => a.Events).SingleAsync(a => a.Id == id, Ct);
    }

    private async Task<List<PaymentNotificationRecord>> Notifications(string eventId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentNotifications.AsNoTracking()
            .Where(n => n.EventId == eventId).ToListAsync(Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>
/// Notifications for the mock provider, as the tests send them: a fixed test signature header, and a body with the
/// event id and our reference. It exercises the core's intake and processing; real signature checks are the adapter's
/// (StripePaymentProviderTests).
/// </summary>
internal sealed class TestPaymentNotifications : IPaymentNotifications
{
    public const string SignatureHeader = "X-Test-Signature";
    public const string ValidSignature = "valid";

    public string ProviderId => "mockpay";

    public Result<PaymentNotification, PaymentNotificationRejection> Verify(string body, IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue(SignatureHeader, out var signature) || signature != ValidSignature)
        {
            return Result<PaymentNotification, PaymentNotificationRejection>.Failure(PaymentNotificationRejection.InvalidSignature);
        }

        var root = JsonDocument.Parse(body).RootElement;
        return Result<PaymentNotification, PaymentNotificationRejection>.Success(new PaymentNotification(
            root.GetProperty("id").GetString()!, PaymentNotificationKind.Payment, new PaymentReference(root.GetProperty("reference").GetString()!), null));
    }
}
