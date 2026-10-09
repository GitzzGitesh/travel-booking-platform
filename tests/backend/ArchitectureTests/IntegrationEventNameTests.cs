using System.Reflection;
using System.Text.RegularExpressions;
using TravelBooking.BuildingBlocks.Background;

namespace TravelBooking.ArchitectureTests;

/// <summary>
/// Outbox messages are stored under each event's declared name, never its CLR name (ADR 0007 follow-up): every
/// integration event declares one, in the form "module.EventName", and no two events share it.
/// </summary>
public sealed partial class IntegrationEventNameTests
{
    private static readonly Type[] _events = [.. Directory.GetFiles(AppContext.BaseDirectory, "Modules.*.dll")
        .Select(path => Assembly.Load(Path.GetFileNameWithoutExtension(path)))
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(type))];

    [Fact]
    public void Every_integration_event_declares_a_stored_name()
    {
        _events.ShouldNotBeEmpty();
        _events.Where(type => type.GetCustomAttribute<IntegrationEventNameAttribute>() is null)
            .Select(type => type.FullName).ShouldBeEmpty("an event stored under its CLR name breaks when the type is renamed");
    }

    [Fact]
    public void Stored_names_are_unique_and_name_their_module()
    {
        var names = _events.Select(type => (Type: type, Name: IntegrationEventNames.Of(type))).ToList();

        names.GroupBy(n => n.Name).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty();
        names.Where(n => !StoredName().IsMatch(n.Name)).Select(n => n.Name).ShouldBeEmpty();
        names.Where(n => !n.Type.Namespace!.StartsWith($"TravelBooking.Modules.{Module(n.Name)}.", StringComparison.OrdinalIgnoreCase))
            .Select(n => $"{n.Name} ({n.Type.FullName})").ShouldBeEmpty("the name's module is the publishing module");
    }

    private static string Module(string name) => name[..name.IndexOf('.', StringComparison.Ordinal)];

    [GeneratedRegex("^[a-z]+\\.[A-Z][A-Za-z]+$")]
    private static partial Regex StoredName();
}
