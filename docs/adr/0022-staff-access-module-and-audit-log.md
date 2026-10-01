# 0022. Staff access module, permission policies and the audit log

- **Status:** Accepted (2026-10-01) by the project owner
- **Date:** 2026-10-01
- **Deciders:** Project owner
- **Related:** [0002](0002-modular-monolith-and-module-boundaries.md), [0008](0008-identity-and-permissions.md) (implements its staff side), [0021](0021-booking-orchestration-supplier-booking-and-capture.md), `docs/architecture/security.md`, `.claude/rules/security.md`

## Context

ADR 0008 chose Entra ID (workforce) for staff, with MFA, permission-based authorization in our application, roles as bundles of permissions "managed in the Access module", and admin routes under `/api/admin/*` that accept only the staff scheme. Several operations were waiting for this:
- resolving a booking in manual review (ADR 0021; it must also settle the payment);
- payment review and legal holds;
- the operations queues.

The security rules also require an append-only audit log of admin actions: actor, action, target, before and after, IP and user agent, and correlation id. No staff tenant exists yet (as for customers).

## Decision

1. **A new module, `Modules.Access`** (schema `access`, connection string `Access`). It owns:
   - **staff authentication:** the `Staff` JwtBearer scheme, RS256 only, with tenant values from `Authentication:Staff` (Authority, Audience, RequiredScope; user-secrets or Key Vault). Until they are set, every staff token is refused.
   - **the staff directory:** the token's issuer and object id (`oid`) map to one internal staff id, created on first sign-in.
   - **the role catalog:** roles are code-defined bundles of permissions.

   Its Contracts project (`Modules.Access.Contracts`) publishes the permission catalog (`StaffPermissions`), which modules with admin endpoints reference (ADR 0002). The claims helpers (`StaffIdentity`: scheme, policies, `StaffId()`, `HasPermission()`) sit in BuildingBlocks, like `CustomerIdentity`.
2. **What a staff token must show, and what is refused:**
   - MFA must be shown. Conditional Access enforces it in the tenant, and the Api refuses a token without it too (fails closed). The signal is either a Conditional Access authentication context in `acrs` (`Authentication:Staff:MfaAuthenticationContext`, e.g. `c1`) or, by default, `amr` containing `mfa`. Entra v2 access tokens may not carry `amr`, so the setting is chosen with the tenant.
   - Every refused staff token is a security event (`StaffTokenRefused`, with a reason code and the trace, never the token or account).
   - App-only tokens are refused.
   - Our reserved claims (`tb_staff_id`, `tb_permission`) are refused in any spelling.
   - The mapped identity is created only by the Access module.
3. **Permissions, not roles, in code.** Each permission has a policy `staff:{permission}` that requires the staff scheme. Admin endpoints require exactly one permission policy, and customer tokens never satisfy one. An architecture test pins this: every `/api/admin` route uses only staff policies, and no staff policy is used elsewhere. A refused staff member (403) is a security event (`StaffAuthorizationDenied`).
4. **Role grants come from configuration for now:** `Access:RoleAssignments` (an object id and its roles), per environment. It is not a secret, and nobody holds a role by default. It is validated at startup (known roles, one entry per account) and read live, so a removed grant takes effect without a restart. Until managed grants exist, changes to it are traced by the platform's configuration change log, not by our audit. Managed grants, with maker-checker and their own audit, come with the access-administration screens. That is a change inside this module, not to the rule.
5. **The audit log lives in each module's own schema** (BuildingBlocks `AuditEntry`, `modelBuilder.AddAuditLog()`). It is written in the **same save** as the audited action, so an action is never recorded without having happened, nor happens without its record. It is append-only by application convention, like the timeline. Denying UPDATE and DELETE on `AuditEntries` to the application's database principal is a deployment follow-up. Before and after are short redacted summaries (statuses, amounts, references), never personal data; free-text reasons are restricted to a ticket-reference pattern. The client address is personal data, and its retention follows the security audit period (12 months, Q9). A central, cross-module audit view would read each module's entries through a query contract (ADR 0002); none exists yet.
6. **Admin routes** are `/api/admin/v1/...`:
   - in their own route group, mapped behind the same Development gate as the customer endpoints until the hosting decision;
   - rate limited per client;
   - in their own OpenAPI document (`admin-v1`, served in Development), so they stay out of the customer contract and its generated client (tested). Its snapshot and client come with `admin-web`.
7. **The first operations, in Orders:**
   - an operations queue by item status (oldest first, cursor);
   - an order's detail with its timeline (our ids, statuses, amounts, supplier references; never travellers' personal data);
   - **checking a booking in manual review.** It is settled only by a supplier lookup by our reference:
     - found as agreed → Confirmed;
     - absent after the consistency window → Failed, **unless** the item went to review because the supplier held a booking not as agreed (recorded with its locator): then its later absence proves nothing, and it stays in review;
     - otherwise, including a failed lookup, it stays in review, with the check on the timeline.

     The payment is settled (capture or release) and the action audited in the same save. A conflicted attempt is still audited. The check calls the supplier, so it carries the supplier-call rate limit.
   - **Idempotency is state-based, a deliberate exception to the Idempotency-Key rule:** a repeat after the item left review changes nothing and answers 409 `not-in-review` with the item's current status. Rowversion and the once-only settlement mean no capture or release can happen twice.
   - **Out of scope, a business decision:** what to do with a booking that stays not as agreed (Q15).

## Consequences

- **Positive:** the deferred operator paths have a safe, audited, permission-based home. Admin access fails closed until a tenant exists. Audit records can never disagree with the action.
- **Negative:**
  - Audit entries are spread across module schemas; a cross-module view needs query contracts.
  - Role grants need a configuration change until managed grants exist.
  - The MFA claim check must be confirmed against the real tenant's tokens.

## Alternatives considered

- **A central audit module written through a contract:** this would be a synchronous cross-module command on every admin action (ADR 0015 forbids it outside checkout), or an outbox event that could arrive after (or without) the action. Rejected.
- **Roles in Entra app roles, checked by name:** ADR 0008 rules out role checks in code. Mapping app roles to our permission bundles is still possible later, as a source of assignments.
