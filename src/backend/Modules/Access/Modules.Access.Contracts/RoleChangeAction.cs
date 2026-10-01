namespace TravelBooking.Modules.Access.Contracts;

/// <summary>What a role change request asks for (ADR 0022: managed grants). Public here because a request type uses it.</summary>
public enum RoleChangeAction
{
    Grant,
    Revoke,
}
