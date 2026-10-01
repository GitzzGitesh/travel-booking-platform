# Staff access: granting and revoking roles

**When:** a staff member joins, changes job, or leaves; or a role must be removed at once (suspected compromise).
**Owner:** access administrators (the Administrator role). Every change needs **two** of them (maker-checker).

Background: ADR 0008, ADR 0022, `docs/architecture/security.md`.

## Roles
Code checks permissions, never role names. A role is a bundle of permissions (`Modules.Access`, `StaffRoles`):

| Role | Permissions |
|---|---|
| Operations | `orders.read`, `bookings.review.resolve`, `payments.read`, `payments.review.resolve` |
| Privacy | `orders.read`, `personal-data.legal-hold` |
| Administrator | all, including `access.grants.read`, `access.grants.request` and `access.grants.approve` |

## Grant or revoke a role
1. **Request:** `POST /api/admin/v1/access/role-changes` with `{ "objectId": "<the person's Entra object id>", "role": "Operations", "action": "Grant" | "Revoke", "reason": "<ticket reference>" }`.
   - The object id comes from the Entra admin portal (user, then Object ID), as a lowercase GUID.
   - You cannot request a change to your own access.
   - A role granted by configuration is changed there, not here (`granted-by-configuration`).
2. **Approve:** a **different** administrator approves it with `POST /api/admin/v1/access/role-changes/{requestId}/decision` and `{ "approve": true, "reason": "<ticket reference>" }`. Use `false` to reject.
   - Nobody approves their own request, and the account a change concerns never decides on it, either way.
   - A revocation can only be withdrawn (rejected) by its requester, so nobody can veto a removal.
   - A request waits at most 7 days. After that it can only be rejected; request again.
   - A request whose requester has since lost the right to request cannot be approved.
3. **Takes effect:** at the person's next request. Permissions are read on every staff sign-in, so a revocation applies at once.

Pending requests are listed by `GET /api/admin/v1/access/role-changes` (also `?status=Approved|Rejected`). Active grants are listed by `GET /api/admin/v1/access/role-grants`.

## Suspected compromise
1. **First, disable the person's Entra account and revoke their sessions** in the Entra portal. Their tokens stop working at once, whatever roles they hold.
2. Then request the revocation of their roles here, and have a second administrator approve it.
3. If the role comes from configuration, remove it there too.

## Bootstrap and break-glass
`Access:RoleAssignments` (per environment, not a secret) grants roles outside the managed flow. Use it **only** for:
- the first two administrators of an environment;
- an emergency when no second administrator is reachable.

A configuration change is traced by the platform's configuration change log, not by our audit, so record the reason in the change ticket. Move bootstrap administrators to managed grants once two administrators exist.

## Audit
Every request and decision is in `access.AuditEntries`:
- actions `access.role-change.request`, `access.role-change.approve` and `access.role-change.reject`;
- the target `role-change:{requestId}`;
- the actor `staff:{id}`.

Refusals by the API are security events:
- `StaffAuthorizationDenied` for a 403 on a permission;
- `StaffTokenRefused` for a 401;
- `RoleChangeRefused` for a maker-checker refusal (a self-request, a self-approval, or a revocation someone else tried to withdraw).

## Do NOT
- Do not edit `access.RoleGrants` or `access.RoleChangeRequests` in the database. That skips maker-checker and the audit.
- Do not share an administrator account so one person can approve their own request.
