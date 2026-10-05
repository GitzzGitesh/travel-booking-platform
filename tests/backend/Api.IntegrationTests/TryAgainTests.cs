using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.Modules.Orders.Application;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// A request that changed nothing and may simply be repeated (an order that kept changing while it was read) is answered
/// 503 with a generic Problem Details, never a 500 and never the exception's details.
/// </summary>
public sealed class TryAgainTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string _probe = "/api/test/order-kept-changing";

    [Fact]
    public async Task An_order_that_kept_changing_is_a_503_try_again_without_details()
    {
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, ThrowsKeptChanging>()));
        using var client = host.CreateClient();

        using var response = await client.GetAsync(_probe, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain("kept changing", Case.Insensitive); // the exception's message stays in the logs
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        (problem.GetProperty("status").GetInt32(), problem.GetProperty("type").GetString()).ShouldBe((503, "try-again")); // as checkout answers it
    }

    // After the Api's own pipeline (so behind its exception handler): a request no endpoint matched reaches this.
    private sealed class ThrowsKeptChanging : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use((HttpContext http, RequestDelegate rest) =>
                http.Request.Path == _probe ? throw new OrderKeptChangingException(Guid.NewGuid()) : rest(http));
        };
    }
}
