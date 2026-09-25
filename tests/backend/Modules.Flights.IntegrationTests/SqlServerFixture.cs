using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using TravelBooking.Modules.Flights.Infrastructure;

[assembly: AssemblyFixture(typeof(TravelBooking.Modules.Flights.IntegrationTests.SqlServerFixture))]

namespace TravelBooking.Modules.Flights.IntegrationTests;

/// <summary>
/// A real SQL Server in a container (testing rules: never the EF InMemory provider or SQLite), with the Flights
/// migrations applied the same way the pipeline applies them.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    internal FlightsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FlightsDbContext>()
            .UseSqlServer(_container.GetConnectionString(), sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", FlightsDbContext.Schema))
            .Options);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
