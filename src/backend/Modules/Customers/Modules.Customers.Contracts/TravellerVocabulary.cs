namespace TravelBooking.Modules.Customers.Contracts;

// The values the traveller endpoints accept (Q9). Public here because request types use them and module internals stay
// internal (ADR 0002); the API serialises them as strings.

public enum TravellerType
{
    Adult,
    Child,
    Infant,
}

public enum TravellerGenderType
{
    Female,
    Male,
}

public enum TravelDocumentKind
{
    Passport,
    IdentityCard,
}
