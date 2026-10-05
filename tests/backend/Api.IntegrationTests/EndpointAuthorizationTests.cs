using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Deny by default (.claude/rules/security.md): every endpoint must declare an authorization policy or be
/// explicitly anonymous. Until the fallback policy arrives with authentication (Phase 4), this test is the
/// only guard, so it checks every environment in which endpoints may be mapped differently.
/// </summary>
public sealed class EndpointAuthorizationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Every_endpoint_declares_authorization(string environment)
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        endpoints.ShouldNotBeEmpty();

        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                && e.Metadata.GetMetadata<AuthorizationPolicy>() is null
                && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText);

        undeclared.ShouldBeEmpty();
    }

    // Anonymous access is a deliberate decision per route: a new anonymous endpoint fails here until it is added.
    // Every /api/v1 entry must be rate limited before it is mapped outside Development (docs/progress.md).
    private static readonly string[] _anonymousRoutes =
    [
        "/api/v1/flights/searches",
        "/api/v1/flights/selected-offers",
        "/api/v1/flights/selected-offers/{selectedOfferId:guid}/revalidations",
        "/api/v1/flights/selected-offers/{selectedOfferId:guid}/price-acceptances",

        // The payment provider's webhook: authenticated by the provider's signature, mapped only when Stripe is enabled.
        "/api/v1/payments/notifications/{providerId}",

        // Staff sign-in (ADR 0023): starts the tenant's sign-in; the Development stand-in exists in Development only.
        "/api/admin/v1/session/sign-in",
        "/api/admin/v1/session/development-sign-in",

        // Customer sign-in (ADR 0028), likewise.
        "/api/v1/session/sign-in",
        "/api/v1/session/development-sign-in",
    ];

    private static readonly string[] _anonymousAdminRoutes = ["/api/admin/v1/session/sign-in", "/api/admin/v1/session/development-sign-in"];

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Only_allow_listed_api_routes_are_anonymous(string environment)
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));

        var anonymous = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .Where(route => route.StartsWith("/api/", StringComparison.Ordinal))
            .Distinct();

        anonymous.ShouldBeSubsetOf(_anonymousRoutes);
    }

    // Admin routes (ADR 0022): only staff permission policies, never anonymous or customer access, and no staff policy
    // outside the admin group.
    [Fact]
    public void Admin_routes_use_staff_permission_policies_only_and_staff_policies_stay_in_the_admin_group()
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment("Development"));

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var admin = endpoints.Where(e => e.RoutePattern.RawText!.StartsWith("/api/admin/", StringComparison.Ordinal)).ToList();

        admin.ShouldNotBeEmpty();
        admin.Where(e => !_anonymousAdminRoutes.Contains(e.RoutePattern.RawText)).ShouldAllBe(e => e.Metadata.GetMetadata<IAllowAnonymous>() == null
            && e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any()
            && e.Metadata.GetOrderedMetadata<IAuthorizeData>().All(a => a.Policy != null && a.Policy.StartsWith("staff:")));
        endpoints.Except(admin)
            .ShouldAllBe(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy != null && a.Policy.StartsWith("staff:")));
    }

    // The Development stand-ins can never act outside Development (ADR 0023, ADR 0028): a well-formed session protected with
    // the host's own keys is refused there when its issuer is the Development one. (Outside Development the routes are not
    // even mapped yet; this checks the authentication itself, for when they are.)
    [Theory]
    [InlineData("Staging", "CustomerSession")]
    [InlineData("Production", "CustomerSession")]
    [InlineData("Staging", "StaffSession")]
    [InlineData("Production", "StaffSession")]
    public async Task A_development_session_never_authenticates_outside_development(string environment, string scheme)
    {
        var refusals = new RefusalLog();
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment(environment)
            .ConfigureServices(services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(refusals)));
        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
            .Get(scheme);
        var time = host.Services.GetRequiredService<TimeProvider>();
        var (principal, properties) = scheme == TravelBooking.BuildingBlocks.Http.CustomerIdentity.SessionScheme
            ? (TravelBooking.Modules.Customers.CustomerSessions.SessionPrincipal(TravelBooking.Modules.Customers.CustomerSessions.DevelopmentIssuer, Guid.NewGuid().ToString("D")),
                TravelBooking.Modules.Customers.CustomerSessions.Start(time))
            : (TravelBooking.Modules.Access.StaffSessions.SessionPrincipal(TravelBooking.Modules.Access.StaffSessions.DevelopmentIssuer, Guid.NewGuid().ToString("D")),
                TravelBooking.Modules.Access.StaffSessions.Start(time));
        var cookie = options.TicketDataFormat.Protect(new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, properties, scheme));
        using var scope = host.Services.CreateScope();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "https";
        http.Request.Method = "GET";
        http.Request.Headers.Cookie = $"{options.Cookie.Name}={cookie}";

        var result = await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.AuthenticateAsync(http, scheme);

        result.Succeeded.ShouldBeFalse();
        // Refused by the Development-only rule itself (a readable, unexpired session), never by an unrelated failure.
        refusals.Messages.ShouldContain(m => m.Contains("(session-invalid-session)", StringComparison.Ordinal));
        refusals.Messages.ShouldNotContain(m => m.Contains("expired", StringComparison.Ordinal));
    }

    // The security log's refusals (the session handlers log one reason code each).
    private sealed class RefusalLog : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name is "CustomerSessionRefused" or "StaffTokenRefused")
            {
                Messages.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }

    // The Development sign-in stand-ins (ADR 0023, ADR 0028) need both the Development environment and the setting, and
    // startup refuses the setting anywhere else.
    [Theory]
    [InlineData("StaffSession", "/api/admin/v1/session/development-sign-in")]
    [InlineData("CustomerSession", "/api/v1/session/development-sign-in")]
    public void Development_sign_in_is_mapped_only_in_development_with_the_setting(string section, string route)
    {
        var setting = $"Authentication:{section}:DevelopmentSignIn";
        bool Mapped(WebApplicationFactory<Program> host) => host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Any(e => e.RoutePattern.RawText == route);

        using (var withoutSetting = factory.WithWebHostBuilder(b => b.UseEnvironment("Development")))
        {
            Mapped(withoutSetting).ShouldBeFalse();
        }

        using (var enabled = factory.WithWebHostBuilder(b => b.UseEnvironment("Development").UseSetting(setting, "true")))
        {
            Mapped(enabled).ShouldBeTrue();
        }

        foreach (var environment in new[] { "Staging", "Production" })
        {
            using var refused = factory.WithWebHostBuilder(b => b.UseEnvironment(environment).UseSetting(setting, "true"));
            Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => refused.CreateClient());
        }
    }
}
