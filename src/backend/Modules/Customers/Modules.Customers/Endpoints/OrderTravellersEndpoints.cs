using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Customers.Application;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Customers.Domain;

namespace TravelBooking.Modules.Customers.Endpoints;

/// <summary>
/// The booker's contact and one traveller per passenger (Q9). Nothing else is collected. Public because the .NET 10
/// validation source generator skips internal types (ADR 0003).
/// </summary>
public sealed class SaveTravellersRequest
{
    [Required]
    public ContactRequest? Contact { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(9)]
    // Not nullable, so the schema is a plain array the client generator follows (a missing list binds empty and fails MinLength).
    public List<TravellerRequest> Travellers { get; init; } = [];
}

public sealed class ContactRequest
{
    [Required]
    [StringLength(OrderTravellerSet.MaxEmailLength)]
    public string? Email { get; init; }

    /// <summary>E.164, e.g. +447700900123.</summary>
    [Required]
    [StringLength(16)]
    public string? Phone { get; init; }
}

/// <summary>A traveller as on their travel document: names in Latin letters, date of birth, gender.</summary>
public sealed class TravellerRequest
{
    [Required]
    public TravellerType? Type { get; init; }

    [Required]
    [StringLength(OrderTravellerSet.MaxNameLength)]
    public string? GivenNames { get; init; }

    [Required]
    [StringLength(OrderTravellerSet.MaxNameLength)]
    public string? Surname { get; init; }

    [Required]
    public DateOnly? DateOfBirth { get; init; }

    [Required]
    public TravellerGenderType? Gender { get; init; }
}

/// <summary>A travel document, only when the supplier requires one (Q9). It is encrypted before it is stored and never returned.</summary>
public sealed class TravelDocumentRequest
{
    [Required]
    public TravelDocumentKind? Type { get; init; }

    [Required]
    [StringLength(TravelDocumentDetails.MaxNumberLength)]
    public string? Number { get; init; }

    /// <summary>ISO 3166-1 alpha-2, e.g. GB.</summary>
    [Required]
    [StringLength(2, MinimumLength = 2)]
    public string? IssuingCountry { get; init; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    [Required]
    [StringLength(2, MinimumLength = 2)]
    public string? Nationality { get; init; }

    [Required]
    public DateOnly? ExpiryDate { get; init; }
}

/// <summary>
/// The customer's own order's travellers (Q9). Thin: the customer id comes from the token only, and another customer's
/// order is not found. Personal data is never logged; a document's number is never returned.
/// </summary>
internal static class OrderTravellersEndpoints
{
    public static async Task<Results<Ok<OrderTravellersResponse>, ProblemHttpResult>> Save(
        Guid orderId, SaveTravellersRequest request, ClaimsPrincipal user, SaveOrderTravellersHandler handler, CancellationToken cancellationToken)
    {
        var travellers = request.Travellers.Select(t => new TravellerDetails(
            Kind(t.Type!.Value), t.GivenNames!, t.Surname!, t.DateOfBirth!.Value, Gender(t.Gender!.Value))).ToList();
        var result = await handler.HandleAsync(
            new SaveOrderTravellers(orderId, user.CustomerId()!, request.Contact!.Email!, request.Contact.Phone!, travellers, Activity.Current?.TraceId.ToString()),
            cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(OrderTravellersResponse.From(result.Value)) : Problem(result.Error);
    }

    public static async Task<Results<Ok<OrderTravellersResponse>, NotFound>> Get(
        Guid orderId, ClaimsPrincipal user, OrderTravellersQuery query, CancellationToken cancellationToken) =>
        await query.FindAsync(orderId, user.CustomerId()!, cancellationToken) is { } set
            ? TypedResults.Ok(OrderTravellersResponse.From(set))
            : TypedResults.NotFound();

    public static async Task<Results<NoContent, ProblemHttpResult>> SaveDocument(
        Guid orderId, int position, TravelDocumentRequest request, ClaimsPrincipal user, SaveTravelDocumentHandler handler, CancellationToken cancellationToken)
    {
        var document = new TravelDocumentDetails(DocumentType(request.Type!.Value), request.Number!, request.IssuingCountry!, request.Nationality!, request.ExpiryDate!.Value);
        var result = await handler.HandleAsync(
            new SaveTravelDocument(orderId, user.CustomerId()!, position, document, Activity.Current?.TraceId.ToString()), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : Problem(result.Error);
    }

    // Mapped by name, never by number: the API vocabulary and the domain's enums may evolve separately. An unknown value
    // maps to an undefined domain value, which validation refuses.
    private static PassengerKind Kind(TravellerType type) => type switch
    {
        TravellerType.Adult => PassengerKind.Adult,
        TravellerType.Child => PassengerKind.Child,
        TravellerType.Infant => PassengerKind.Infant,
        _ => (PassengerKind)(-1),
    };

    private static TravellerGender Gender(TravellerGenderType gender) => gender switch
    {
        TravellerGenderType.Female => TravellerGender.Female,
        TravellerGenderType.Male => TravellerGender.Male,
        _ => (TravellerGender)(-1),
    };

    private static TravelDocumentType DocumentType(TravelDocumentKind kind) => kind switch
    {
        TravelDocumentKind.Passport => TravelDocumentType.Passport,
        TravelDocumentKind.IdentityCard => TravelDocumentType.IdentityCard,
        _ => (TravelDocumentType)(-1),
    };

    private static ProblemHttpResult Problem(TravellersFailure failure) => failure switch
    {
        TravellersFailure.OrderNotFound => Problem(StatusCodes.Status404NotFound, "order-not-found", "This order was not found."),
        TravellersFailure.TravellerNotFound => Problem(StatusCodes.Status404NotFound, "traveller-not-found", "Give the travellers first, then their documents."),
        TravellersFailure.NotEditable => Problem(StatusCodes.Status409Conflict, "travellers-not-editable", "The travellers of this order can no longer be changed."),
        TravellersFailure.Conflict => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "The travellers were changed at the same time. Please try again."),
        TravellersFailure.DocumentsNotRequired => Problem(StatusCodes.Status409Conflict, "documents-not-required", "No travel document is needed for this booking."),
        TravellersFailure.WrongPassengers => Problem(StatusCodes.Status422UnprocessableEntity, "travellers-do-not-match", "Give exactly one traveller for each passenger of the booking."),
        TravellersFailure.InvalidDetails => Problem(StatusCodes.Status422UnprocessableEntity, "invalid-traveller-details",
            "Check the names (Latin letters, as on the travel document), dates of birth for each passenger type, the email and the phone number (e.g. +447700900123)."),
        TravellersFailure.DocumentsUnavailable => Problem(StatusCodes.Status503ServiceUnavailable, "documents-unavailable", "Travel documents cannot be saved right now. Please try again later."),
        _ => throw new InvalidOperationException($"Unmapped travellers failure {failure}."),
    };

    private static ProblemHttpResult Problem(int status, string type, string title) => TypedResults.Problem(statusCode: status, type: type, title: title);
}

/// <summary>What the owner gave for their order. A document is shown as provided or not, never its contents.</summary>
internal sealed record OrderTravellersResponse(ContactResponse Contact, IReadOnlyList<TravellerResponse> Travellers)
{
    public static OrderTravellersResponse From(OrderTravellerSet set) => new(
        new ContactResponse(set.ContactEmail, set.ContactPhone),
        set.Travellers.OrderBy(t => t.Position).Select(t => new TravellerResponse(
            t.Position, t.Kind.ToString(), t.GivenNames, t.Surname, t.DateOfBirth, t.Gender?.ToString(), t.DocumentId is not null)).ToList());
}

internal sealed record ContactResponse(string? Email, string? Phone);

internal sealed record TravellerResponse(int Position, string Type, string? GivenNames, string? Surname, DateOnly? DateOfBirth, string? Gender, bool DocumentProvided);
