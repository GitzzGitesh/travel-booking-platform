using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Integrations.Payments.Mock.Persistence;

/// <summary>
/// The mock payment provider's own state (ADR 0032), in its own schema: never a module's tables, never read by a
/// module. It exists only where the mock runs (Development and Staging), and its migrations are applied only there.
/// </summary>
internal sealed class MockPaymentsDbContext(DbContextOptions<MockPaymentsDbContext> options) : DbContext(options)
{
    public const string Schema = "paymentsmock";

    /// <summary>The Payments database: the mock's state lives beside the attempts that refer to it.</summary>
    public const string ConnectionStringName = "Payments";

    public DbSet<MockPaymentRow> Payments => Set<MockPaymentRow>();

    public DbSet<MockOperationRow> Operations => Set<MockOperationRow>();

    public DbSet<MockRefundRow> Refunds => Set<MockRefundRow>();

    public DbSet<MockFailedOnceRow> FailedOnce => Set<MockFailedOnceRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var payment = modelBuilder.Entity<MockPaymentRow>();
        payment.ToTable("Payments");
        payment.HasKey(p => p.Reference);
        payment.Property(p => p.Reference).HasMaxLength(PaymentReference.MaxLength).IsUnicode(false);
        payment.Property(p => p.ProviderRef).HasMaxLength(100).IsUnicode(false);
        payment.Property(p => p.Amount).HasPrecision(19, 4);
        payment.Property(p => p.Captured).HasPrecision(19, 4);
        payment.Property(p => p.Refunded).HasPrecision(19, 4);
        payment.Property(p => p.Currency).HasColumnType("char(3)");
        payment.Property(p => p.Method).HasMaxLength(100).IsUnicode(false);
        payment.Property(p => p.Fingerprint).HasMaxLength(200);
        payment.Property(p => p.State).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        payment.Property(p => p.DeclineReason).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        payment.Property(p => p.ActionToken).HasMaxLength(100).IsUnicode(false);

        var operation = modelBuilder.Entity<MockOperationRow>();
        operation.ToTable("Operations");
        operation.HasKey(o => o.Key);
        operation.Property(o => o.Key).HasMaxLength(OperationKey.MaxLength).IsUnicode(false);
        operation.Property(o => o.Operation).HasMaxLength(400);

        var refund = modelBuilder.Entity<MockRefundRow>();
        refund.ToTable("Refunds");
        refund.HasKey(r => r.Key);
        refund.Property(r => r.Key).HasMaxLength(OperationKey.MaxLength).IsUnicode(false);
        refund.Property(r => r.Reference).HasMaxLength(PaymentReference.MaxLength).IsUnicode(false);
        refund.Property(r => r.ProviderRefundRef).HasMaxLength(100).IsUnicode(false);
        refund.Property(r => r.Amount).HasPrecision(19, 4);
        refund.Property(r => r.Currency).HasColumnType("char(3)");
        refund.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);

        var failedOnce = modelBuilder.Entity<MockFailedOnceRow>();
        failedOnce.ToTable("FailedOnce");
        failedOnce.HasKey(f => f.Key);
        failedOnce.Property(f => f.Key).HasMaxLength(OperationKey.MaxLength).IsUnicode(false);
    }
}

/// <summary>One mock payment. Amounts are in its one currency.</summary>
internal sealed class MockPaymentRow
{
    public required string Reference { get; init; }

    public required string ProviderRef { get; init; }

    public decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string Method { get; init; }

    public required string Fingerprint { get; init; }

    public PaymentState State { get; set; }

    public decimal Captured { get; set; }

    public decimal Refunded { get; set; }

    public PaymentDeclineReason? DeclineReason { get; set; }

    public string? ActionToken { get; set; }

    public static MockPaymentRow From(MockPayment payment)
    {
        var row = new MockPaymentRow
        {
            Reference = payment.Reference.Value,
            ProviderRef = payment.ProviderRef.Value,
            Amount = payment.Amount.Amount,
            Currency = payment.Amount.Currency.Value,
            Method = payment.Method,
            Fingerprint = payment.Fingerprint,
        };
        row.Update(payment);
        return row;
    }

    public void Update(MockPayment payment)
    {
        State = payment.State;
        Captured = payment.Captured.Amount;
        Refunded = payment.Refunded.Amount;
        DeclineReason = payment.DeclineReason;
        ActionToken = payment.ActionToken?.Value;
    }

    public MockPayment ToPayment()
    {
        var currency = new CurrencyCode(Currency);
        return new MockPayment(
            new PaymentReference(Reference), new ProviderPaymentRef(MockPaymentProvider.ProviderId, ProviderRef), new Money(Amount, currency), Method, Fingerprint)
        {
            State = State,
            Captured = new Money(Captured, currency),
            Refunded = new Money(Refunded, currency),
            DeclineReason = DeclineReason,
            ActionToken = ActionToken is { } token ? new CustomerActionToken(token) : null,
        };
    }
}

/// <summary>What an operation key did: a replay with the same key returns its result, another operation conflicts.</summary>
internal sealed class MockOperationRow
{
    public required string Key { get; init; }

    public required string Operation { get; init; }
}

/// <summary>The refund an operation key made.</summary>
internal sealed class MockRefundRow
{
    public required string Key { get; init; }

    public required string Reference { get; init; }

    public required string ProviderRefundRef { get; init; }

    public decimal Amount { get; init; }

    public required string Currency { get; init; }

    public RefundStatus Status { get; init; }

    public static MockRefundRow From(PaymentRefund refund) => new()
    {
        Key = refund.Key.Value,
        Reference = refund.Payment.Value,
        ProviderRefundRef = refund.Refund.Value,
        Amount = refund.Amount.Amount,
        Currency = refund.Amount.Currency.Value,
        Status = refund.Status,
    };

    public PaymentRefund ToRefund() => new(
        new PaymentReference(Reference), new OperationKey(Key), new ProviderRefundRef(MockPaymentProvider.ProviderId, ProviderRefundRef),
        new Money(Amount, new CurrencyCode(Currency)), Status);
}

/// <summary>An operation key whose "fails once" scenario has failed.</summary>
internal sealed class MockFailedOnceRow
{
    public required string Key { get; init; }
}

/// <summary>
/// Lets `dotnet ef migrations add` build the model without a host. It never connects (database rules). Applying these
/// migrations is for Development and Staging databases only (ADR 0032), so it is an allow-list, as in the hosts: any
/// other declared environment is refused. This is a best-effort guard; the production pipeline leaves this project's
/// migrations out (runbook mock-payment-provider.md).
/// </summary>
internal sealed class MockPaymentsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MockPaymentsDbContext>
{
    public MockPaymentsDbContext CreateDbContext(string[] args)
    {
        foreach (var variable in new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" })
        {
            var environment = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(environment)
                && !string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(environment, "Staging", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The mock payment provider's schema ({MockPaymentsDbContext.Schema}) is created in Development and Staging only ({variable}={environment}).");
            }
        }

        return new(new DbContextOptionsBuilder<MockPaymentsDbContext>()
            .UseSqlServer("Server=design-time-only", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", MockPaymentsDbContext.Schema))
            .Options);
    }
}
