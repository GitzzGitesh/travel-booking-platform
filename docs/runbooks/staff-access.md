# Staff access: granting and revoking roles

**When:** a staff member joins, changes job, or leaves; or a role must be removed at once (suspected compromise).
**Owner:** access administrators (the Administrator role). Every change needs **two** of them (maker-checker).

Background: ADR 0008, ADR 0022, `docs/architecture/security.md`.

## Roles
Code checks permissions, never role names. A role is a bundle of permissions (`Modules.Access`, `StaffRoles`):

| Role | Permissions |
|---|---|
| Operations | `orders.read`, `bookings.review.resolve`, `payments.read`, `payments.review.resolve`, `refunds.request` |
| Privacy | `orders.read`, `personal-data.legal-hold` (place a hold, request its release) |
| Legal | `orders.read`, `personal-data.legal-hold.approve` (approve or reject a release, ADR 0026) |
| Finance | `orders.read`, `payments.read`, `refunds.approve` (approve or reject a refund, ADR 0027) |
| Administrator | all, including `access.grants.read`, `access.grants.request` and `access.grants.approve` |

## Signing in (admin-web)
- Staff sign in through admin-web's **Sign in**. The Api runs the sign-in with the staff tenant and sets a session cookie; the browser never holds a token (ADR 0023).
- A session ends after 30 minutes without activity, 8 hours after sign-in, or at **Sign out**. Sign-out ends our session only, not the Entra session.
- A refused sign-in (no MFA, not a staff account) comes back as "Sign-in did not complete". The reason is in the `StaffTokenRefused` security event.
- **Local development only:** with `Authentication:StaffSession:DevelopmentSignIn` = `true`, `POST /api/admin/v1/session/development-sign-in` with `{ "objectId": "<lowercase GUID>" }` and the header `X-TB-Staff-Csrf: 1` signs in without a tenant. Roles still come from `Access:RoleAssignments`, which is keyed by object id only: signing in as a real colleague's object id gives their configured roles, so use synthetic ids locally. The Api refuses to start with this setting outside Development, and refuses a Development-issued session outside Development.
  - **From the apps:** in a dev server build (`npx ng serve`), admin-web's **Sign in** uses this stand-in as the local test staff member `0c0de000-0000-4000-8000-0000000000a1`. Give it a role locally, for example `Access__RoleAssignments__0__ObjectId=0c0de000-0000-4000-8000-0000000000a1` and `Access__RoleAssignments__0__Roles__0=Operations`; maker-checker steps need a second account (the POST above). customer-web's **Sign in** likewise uses `POST /api/v1/session/development-sign-in` (`Authentication:CustomerSession:DevelopmentSignIn` = `true`) as the local test customer `0c0de000-0000-4000-8000-00000000c001`: the same customer, and trips, every time. When the Api does not offer the stand-in (404), the buttons start the tenant's sign-in as in production builds.

## Grant or revoke a role
In admin-web, **Staff access** does the same: **Request a change**, then a second administrator opens **Decide** on the waiting request. The API calls below are for tooling.

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
2. Then request the revocation of their roles here, and have a second administrator approve it. This also ends what an admin-web session can do: the session keeps the account only, and permissions are read on every request. Disabling the account in Entra does not end a session that is already open (ADR 0023).
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
