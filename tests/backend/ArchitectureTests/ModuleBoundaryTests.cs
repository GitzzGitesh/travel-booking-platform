using ArchUnitNET.Domain;
using ArchUnitNET.Fluent.Syntax.Elements.Types;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using Assembly = System.Reflection.Assembly;

namespace TravelBooking.ArchitectureTests;

/// <summary>
/// Module boundary rules from ADR 0002, ADR 0004, and .claude/rules/architecture.md.
/// </summary>
public sealed class ModuleBoundaryTests
{
    // Discovered from the test output directory, not from compiled references, so modules composed only by
    // the Worker and Contracts projects referenced only by other modules are still checked.
    private static readonly Assembly[] _moduleAssemblies = LoadAssemblies("Modules.*.dll");

    private static readonly Assembly[] _moduleInternalAssemblies = _moduleAssemblies
        .Where(a => !IsContracts(a.GetName().Name!))
        .ToArray();

    private static readonly Assembly[] _integrationAssemblies = LoadAssemblies("Integrations.*.dll");

    private static readonly Assembly _buildingBlocks = Assembly.Load("BuildingBlocks");

    private static readonly Architecture _architecture = new ArchLoader()
        .LoadAssemblies([Assembly.Load("Api"), Assembly.Load("Worker"), _buildingBlocks, .. _moduleAssemblies, .. _integrationAssemblies])
        .Build();

    private const string _portsNamespace = @"^TravelBooking\.Modules\.[^.]+\.Ports$";

    [Fact]
    public void At_least_one_module_is_checked() =>
        _moduleInternalAssemblies.ShouldNotBeEmpty();

    // A module may reference only the framework, BuildingBlocks, and other modules' Contracts. This keeps out other
    // modules' internals, Integrations.* adapters, supplier SDKs, and the hosts.
    [Fact]
    public void Modules_reference_only_the_framework_and_other_modules_contracts()
    {
        var violations = _moduleAssemblies
            .SelectMany(module => module.GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(name => !IsAllowedModuleReference(name))
                .Select(name => $"{module.GetName().Name} -> {name}"))
            .ToList();

        violations.ShouldBeEmpty();
    }

    // Public exceptions: the static <Module>Module entry point; HTTP *Request types, which the .NET 10 validation
    // source generator only picks up when they are public (ADR 0003 spike); and the provider port with the types in
    // its signatures, in <Module>.Ports, which Integrations.* adapters implement (ADR 0014). Handlers stay internal.
    [Fact]
    public void Module_internals_are_not_public_except_entry_point_request_and_port_types() =>
        ModuleInternalTypes()
            .And().DoNotResideInNamespaceMatching(_portsNamespace)
            .And().DoNotHaveNameEndingWith("Module")
            .And().DoNotHaveNameEndingWith("Request")
            .Should().NotBePublic()
            .Because("other modules may only use a module through its Contracts project (ADR 0002)")
            .Check(_architecture);

    [Fact]
    public void Public_module_entry_points_live_in_the_module_root_namespace() =>
        ModuleInternalTypes()
            .And().HaveNameEndingWith("Module")
            .And().ArePublic()
            .Should().ResideInNamespaceMatching(@"^TravelBooking\.Modules\.[^.]+$")
            .Because("each module has exactly one entry point, TravelBooking.Modules.<X>.<X>Module")
            .Check(_architecture);

    [Fact]
    public void Public_request_types_live_in_a_module_endpoints_namespace() =>
        ModuleInternalTypes()
            .And().HaveNameEndingWith("Request")
            .And().ArePublic()
            .Should().ResideInNamespaceMatching(@"^TravelBooking\.Modules\.[^.]+\.Endpoints$")
            .Because("only HTTP request types may be public, and they belong to the module's endpoints")
            .Check(_architecture);

    [Fact]
    public void Module_domain_code_does_not_depend_on_web_data_or_http_frameworks() =>
        Types().That().ResideInNamespaceMatching(@"^TravelBooking\.Modules\.[^.]+\.Domain(\..+)?$")
            .Should().NotDependOnAny(Types(true).That().ResideInNamespaceMatching(@"^(Microsoft\.AspNetCore|Microsoft\.EntityFrameworkCore|System\.Net\.Http)(\..+)?$"))
            .Because("Domain has no dependencies on ASP.NET Core, EF Core, or HTTP (architecture rules)")
            .WithoutRequiringPositiveResults()
            .Check(_architecture);

    [Fact]
    public void Provider_ports_do_not_depend_on_web_data_or_http_frameworks() =>
        Types().That().ResideInNamespaceMatching(_portsNamespace)
            .Should().NotDependOnAny(Types(true).That().ResideInNamespaceMatching(@"^(Microsoft\.AspNetCore|Microsoft\.EntityFrameworkCore|System\.Net\.Http)(\..+)?$"))
            .Because("ports describe supplier-agnostic domain operations (ADR 0004)")
            .Check(_architecture);

    [Fact]
    public void At_least_one_integration_is_checked() =>
        _integrationAssemblies.ShouldNotBeEmpty();

    // An adapter may reference the framework, BuildingBlocks, its supplier's SDK, and the module whose port it
    // implements (Integrations.Flights.* -> Modules.Flights). It must never reference another module (Contracts
    // included), another adapter, or a host (ADR 0004, ADR 0014). A deny-list, so supplier SDKs stay allowed.
    [Fact]
    public void Integrations_reference_only_their_own_modules_port()
    {
        var violations = _integrationAssemblies
            .SelectMany(integration =>
            {
                var name = integration.GetName().Name!;
                var area = name.Split('.')[1];
                return integration.GetReferencedAssemblies()
                    .Select(reference => reference.Name!)
                    .Where(reference =>
                        (reference.StartsWith("Modules.", StringComparison.Ordinal) && reference != $"Modules.{area}")
                        || (reference.StartsWith("Integrations.", StringComparison.Ordinal) && reference != name)
                        || reference is "Api" or "Worker")
                    .Select(reference => $"{name} -> {reference}");
            })
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void BuildingBlocks_reference_only_the_framework() =>
        _buildingBlocks.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => !IsFrameworkOrBuildingBlocks(name))
            .ShouldBeEmpty();

    private static GivenTypesConjunction ModuleInternalTypes() =>
        Types().That().ResideInAssembly(_moduleInternalAssemblies[0], _moduleInternalAssemblies[1..]);

    private static bool IsAllowedModuleReference(string name) =>
        IsFrameworkOrBuildingBlocks(name)
        || (name.StartsWith("Modules.", StringComparison.Ordinal) && IsContracts(name));

    private static bool IsFrameworkOrBuildingBlocks(string name) =>
        name is "netstandard" or "mscorlib" or "System" or "BuildingBlocks"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal);

    private static bool IsContracts(string assemblyName) =>
        assemblyName.EndsWith(".Contracts", StringComparison.Ordinal);

    private static Assembly[] LoadAssemblies(string pattern) =>
        Directory.GetFiles(AppContext.BaseDirectory, pattern)
            .Select(path => Assembly.Load(Path.GetFileNameWithoutExtension(path)))
            .ToArray();
}
