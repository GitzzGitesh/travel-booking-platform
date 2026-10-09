using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Integrations.Payments.Mock.Persistence;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The mock payment provider's shared state (ADR 0032), at the provider: two separately composed providers, as the Api
/// and the Worker are (two processes, one database), see the same payments. What one authorized, the other captures,
/// voids or refunds; a key replayed by the other returns the first result; parallel calls from both act once.
/// </summary>
public sealed class SharedMockPaymentStateTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>, IAsyncDisposable
{
    // Nothing listens on port 1, so the connection is refused before any sign-in: no credentials are involved.
    private const string _unreachable = "Server=127.0.0.1,1;Database=unused;Connect Timeout=2;TrustServerCertificate=true";

    private static readonly CurrencyCode _eur = new("EUR");
    private readonly List<ServiceProvider> _processes = [];

    [Fact]
    public async Task A_payment_authorized_by_one_process_is_captured_and_refunded_by_the_other()
    {
        var (apiProcess, workerProcess) = (Process(), Process());
        var authorized = (await apiProcess.AuthorizeAsync(Authorization(MockPaymentMethods.Approved), Ct)).Value;

        var captured = await workerProcess.CaptureAsync(new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m)), Ct);
        var refundKey = Key();
        var refund = await workerProcess.RefundAsync(new RefundDetails(authorized.Reference, authorized.Payment, refundKey, Eur(20m)), Ct);

        captured.Value.State.ShouldBe(PaymentState.Captured);
        refund.Value.Status.ShouldBe(RefundStatus.Succeeded);
        var seen = (await apiProcess.RetrieveAsync(authorized.Reference, Ct)).Value.Payment.ShouldNotBeNull();
        (seen.State, seen.Captured, seen.Refunded).ShouldBe((PaymentState.Captured, Eur(120m), Eur(20m)));
        (await apiProcess.RetrieveRefundAsync(authorized.Reference, refundKey, Ct)).Value.Refund.ShouldBe(refund.Value);
    }

    [Fact]
    public async Task A_hold_made_by_one_process_is_voided_by_the_other_and_can_no_longer_be_captured()
    {
        var (apiProcess, workerProcess) = (Process(), Process());
        var authorized = (await apiProcess.AuthorizeAsync(Authorization(MockPaymentMethods.Approved), Ct)).Value;

        var voided = await workerProcess.VoidAsync(new VoidDetails(authorized.Reference, authorized.Payment, Key()), Ct);
        var capture = await apiProcess.CaptureAsync(new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m)), Ct);

        voided.Value.State.ShouldBe(PaymentState.Voided);
        capture.Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
        (await apiProcess.RetrieveAsync(authorized.Reference, Ct)).Value.Payment.ShouldNotBeNull().State.ShouldBe(PaymentState.Voided);
    }

    [Fact]
    public async Task A_key_replayed_by_the_other_process_returns_the_first_result_and_another_use_of_it_conflicts()
    {
        var (apiProcess, workerProcess) = (Process(), Process());
        var details = Authorization(MockPaymentMethods.Approved);
        var authorized = (await apiProcess.AuthorizeAsync(details, Ct)).Value;
        var capture = new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m));

        var first = (await apiProcess.CaptureAsync(capture, Ct)).Value;
        var replay = (await workerProcess.CaptureAsync(capture, Ct)).Value;

        replay.ShouldBe(first);
        (await workerProcess.VoidAsync(new VoidDetails(capture.Reference, capture.Payment, capture.Key), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.IdempotencyConflict);
        (await workerProcess.AuthorizeAsync(details, Ct)).Value.Reference.ShouldBe(details.Reference); // the same authorization: its result
        (await workerProcess.AuthorizeAsync(details with { PaymentMethod = new PaymentMethodToken(MockPaymentMethods.Declined) }, Ct))
            .Error.Kind.ShouldBe(ProviderErrorKind.IdempotencyConflict);
    }

    // F-23 across processes: the capture happened, its answer was lost, and the other process's retry under the same key
    // gets it, captured once.
    [Fact]
    public async Task A_capture_whose_answer_was_lost_is_returned_to_the_other_process_retrying_its_key()
    {
        var (apiProcess, workerProcess) = (Process(), Process());
        var authorized = (await apiProcess.AuthorizeAsync(Authorization(MockPaymentMethods.CaptureTimeout), Ct)).Value;
        var capture = new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m));

        (await workerProcess.CaptureAsync(capture, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unknown);
        var retried = await apiProcess.CaptureAsync(capture, Ct);

        (retried.Value.State, retried.Value.Captured).ShouldBe((PaymentState.Captured, Eur(120m)));
        (await workerProcess.CaptureAsync(capture with { Key = Key() }, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest); // never twice
    }

    // F-43 across processes: refused before processing once; the other process's retry under the same key succeeds.
    [Fact]
    public async Task A_refund_refused_once_succeeds_when_the_other_process_retries_its_key()
    {
        var (apiProcess, workerProcess) = (Process(), Process());
        var authorized = (await apiProcess.AuthorizeAsync(Authorization(MockPaymentMethods.RefundUnavailableOnce), Ct)).Value;
        (await apiProcess.CaptureAsync(new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m)), Ct)).IsSuccess.ShouldBeTrue();
        var refund = new RefundDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m));

        (await workerProcess.RefundAsync(refund, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
        (await apiProcess.RefundAsync(refund, Ct)).Value.Status.ShouldBe(RefundStatus.Succeeded);

        (await workerProcess.RetrieveAsync(authorized.Reference, Ct)).Value.Payment.ShouldNotBeNull().Refunded.ShouldBe(Eur(120m));
    }

    [Fact]
    public async Task A_challenge_completed_in_the_browser_is_learnt_by_the_other_process()
    {
        var (apiProcess, workerProcess) = (Process(), Process());
        var challenged = (await apiProcess.AuthorizeAsync(Authorization(MockPaymentMethods.ChallengeCompleted), Ct)).Value;

        var found = (await workerProcess.RetrieveAsync(challenged.Reference, Ct)).Value.Payment.ShouldNotBeNull();

        challenged.State.ShouldBe(PaymentState.RequiresAction);
        (found.State, found.CustomerActionToken).ShouldBe((PaymentState.Authorized, (CustomerActionToken?)null));
        (await apiProcess.RetrieveAsync(challenged.Reference, Ct)).Value.Payment.ShouldNotBeNull().State.ShouldBe(PaymentState.Authorized);
    }

    // Both processes capture one payment at once, each under its own key: one capture wins, every other is refused, and
    // nothing is captured twice (testing rules: concurrency-sensitive commands).
    [Fact]
    public async Task Parallel_captures_from_both_processes_capture_once()
    {
        var processes = new[] { Process(), Process() };
        var authorized = (await processes[0].AuthorizeAsync(Authorization(MockPaymentMethods.Approved), Ct)).Value;

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            processes[i % 2].CaptureAsync(new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m)), Ct)));

        attempts.Count(a => a.IsSuccess).ShouldBe(1);
        attempts.Where(a => !a.IsSuccess).ShouldAllBe(a => a.Error.Kind == ProviderErrorKind.InvalidRequest);
        (await processes[1].RetrieveAsync(authorized.Reference, Ct)).Value.Payment.ShouldNotBeNull().Captured.ShouldBe(Eur(120m));
    }

    // The same capture sent by both processes at once (a retry racing the original): every answer is the one capture.
    [Fact]
    public async Task Parallel_replays_of_one_capture_key_from_both_processes_capture_once()
    {
        var processes = new[] { Process(), Process() };
        var authorized = (await processes[0].AuthorizeAsync(Authorization(MockPaymentMethods.Approved), Ct)).Value;
        var capture = new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => processes[i % 2].CaptureAsync(capture, Ct)));

        attempts.ShouldAllBe(a => a.IsSuccess && a.Value.State == PaymentState.Captured && a.Value.Captured == Eur(120m));
    }

    // Refunds from both processes at once, each under its own key, never refund more than was captured.
    [Fact]
    public async Task Parallel_refunds_from_both_processes_never_refund_more_than_was_captured()
    {
        var processes = new[] { Process(), Process() };
        var authorized = (await processes[0].AuthorizeAsync(Authorization(MockPaymentMethods.Approved), Ct)).Value;
        (await processes[1].CaptureAsync(new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m)), Ct)).IsSuccess.ShouldBeTrue();

        var refunds = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            processes[i % 2].RefundAsync(new RefundDetails(authorized.Reference, authorized.Payment, Key(), Eur(50m)), Ct)));

        refunds.Count(r => r.IsSuccess).ShouldBe(2); // 2 x 50 of 120
        refunds.Where(r => !r.IsSuccess).ShouldAllBe(r => r.Error.Kind == ProviderErrorKind.InvalidRequest);
        (await processes[0].RetrieveAsync(authorized.Reference, Ct)).Value.Payment.ShouldNotBeNull().Refunded.ShouldBe(Eur(100m));
    }

    // One reference authorized from both processes at once with different details: one payment, and every other request
    // either gets its result (the same details) or a conflict (other details).
    [Fact]
    public async Task Parallel_authorizations_of_one_reference_make_one_payment()
    {
        var processes = new[] { Process(), Process() };
        var approved = Authorization(MockPaymentMethods.Approved);
        var declined = approved with { PaymentMethod = new PaymentMethodToken(MockPaymentMethods.Declined) };

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => processes[i % 2].AuthorizeAsync(i % 3 == 0 ? declined : approved, Ct)));

        var made = (await processes[1].RetrieveAsync(approved.Reference, Ct)).Value.Payment.ShouldNotBeNull();
        attempts.Where(a => a.IsSuccess).ShouldAllBe(a => a.Value.State == made.State && a.Value.Payment == made.Payment);
        attempts.Where(a => !a.IsSuccess).ShouldAllBe(a => a.Error.Kind == ProviderErrorKind.IdempotencyConflict);
        attempts.Count(a => a.IsSuccess).ShouldBeGreaterThan(0);
    }

    // The commit happened but its answer was lost: the outcome is Unknown (reconcile, never resubmit), and the same key
    // from the other process returns the capture that was kept.
    [Fact]
    public async Task A_commit_whose_answer_was_lost_is_unknown_and_the_same_key_returns_the_kept_capture()
    {
        var failing = new FailingCommit(afterCommit: true);
        var apiProcess = Intercepted(failing);
        var authorized = (await apiProcess.AuthorizeAsync(Authorization(MockPaymentMethods.Approved), Ct)).Value;
        var capture = new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m));

        failing.Armed = true;
        (await apiProcess.CaptureAsync(capture, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unknown);

        var workerProcess = Process();
        (await workerProcess.RetrieveAsync(authorized.Reference, Ct)).Value.Payment.ShouldNotBeNull().State.ShouldBe(PaymentState.Captured);
        (await workerProcess.CaptureAsync(capture, Ct)).Value.Captured.ShouldBe(Eur(120m)); // the kept capture, not a second one
    }

    // The commit never reached the database: still Unknown to the caller (it cannot tell), nothing was kept, and a retry
    // under the same key, after a lookup finds it only authorized, captures once.
    [Fact]
    public async Task A_commit_that_never_happened_is_unknown_and_keeps_nothing()
    {
        var failing = new FailingCommit(afterCommit: false);
        var apiProcess = Intercepted(failing);
        var authorized = (await apiProcess.AuthorizeAsync(Authorization(MockPaymentMethods.Approved), Ct)).Value;
        var capture = new CaptureDetails(authorized.Reference, authorized.Payment, Key(), Eur(120m));

        failing.Armed = true;
        (await apiProcess.CaptureAsync(capture, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unknown);

        var workerProcess = Process();
        (await workerProcess.RetrieveAsync(authorized.Reference, Ct)).Value.Payment.ShouldNotBeNull().State.ShouldBe(PaymentState.Authorized);
        (await workerProcess.CaptureAsync(capture, Ct)).Value.State.ShouldBe(PaymentState.Captured);
    }

    // A state store that cannot be reached is the provider being unavailable: never a success, never an exception.
    [Fact]
    public async Task An_unreachable_state_store_makes_the_provider_unavailable()
    {
        var result = await Process(_unreachable).AuthorizeAsync(Authorization(MockPaymentMethods.Approved), Ct);

        result.Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            await process.DisposeAsync();
        }
    }

    // One process's composition of the mock, as each host composes it (Development, the shared state).
    private IPaymentProvider Process(string? connectionString = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("ConnectionStrings:Payments", connectionString ?? api.ConnectionString)])
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Development })
            .AddMockPaymentProvider(configuration)
            .BuildServiceProvider();
        _processes.Add(services);
        return services.GetRequiredService<IPaymentProvider>();
    }

    // The mock on the shared state, with an interceptor on its database calls (the commit failures above).
    private IPaymentProvider Intercepted(IInterceptor interceptor)
    {
        var services = new ServiceCollection()
            .AddDbContext<MockPaymentsDbContext>(options => options
                .UseSqlServer(api.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", MockPaymentsDbContext.Schema))
                .AddInterceptors(interceptor))
            .BuildServiceProvider();
        _processes.Add(services);
        return new MockPaymentProvider(
            Options.Create(new MockPaymentProviderOptions()),
            new SqlMockPaymentLedger(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SqlMockPaymentLedger>.Instance));
    }

    // Fails the next commit once armed: before it reaches the database, or after it did (its answer lost).
    private sealed class FailingCommit(bool afterCommit) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && !afterCommit)
            {
                Armed = false;
                throw new InvalidOperationException("Simulated: the commit never reached the database.");
            }

            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && afterCommit)
            {
                Armed = false;
                throw new InvalidOperationException("Simulated: the commit happened, its answer was lost.");
            }

            return Task.CompletedTask;
        }
    }

    private static AuthorizationDetails Authorization(string method) =>
        new(new PaymentReference($"pay-{Guid.NewGuid():N}"), Eur(120m), new PaymentMethodToken(method));

    private static OperationKey Key() => new($"op-{Guid.NewGuid():N}");

    private static Money Eur(decimal amount) => new(amount, _eur);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
