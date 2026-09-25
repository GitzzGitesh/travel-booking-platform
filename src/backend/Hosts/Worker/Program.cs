var builder = Host.CreateApplicationBuilder(args);

// Background processing (outbox dispatch, webhook processing, reconciliation, expiry jobs) is
// registered here as modules introduce it (ADR 0007).

var host = builder.Build();
host.Run();
