# 0026. Releasing a legal hold: maker-checker and a grace period (Q16)

- **Status:** Accepted (2026-10-02), under the decision authority the project owner delegated on 2026-10-02. The grace period's length is **pending legal confirmation**
- **Date:** 2026-10-02
- **Deciders:** Engineering lead (delegated authority)
- **Related:** [0020](0020-traveller-personal-data-store-and-retention.md), [0022](0022-staff-access-module-and-audit-log.md), Q16

## Context
One staff member with `personal-data.legal-hold` can place or release a hold today. After a release, the hourly purge applies overdue retention, and shredding and anonymisation cannot be undone. Placing a hold is the safe direction, because it only keeps more data. Releasing it is the dangerous one.

## Decision
1. **Placing a hold stays a single, audited step** (`personal-data.legal-hold`).
2. **Releasing a hold is maker-checker:**
   - **Requester:** a staff member with `personal-data.legal-hold` requests the release with a case reference. The hold stays in force while it waits.
   - **Approver:** a *different* person holding the new permission `personal-data.legal-hold.approve`, compared by staff id and by workforce account (as for role grants, ADR 0022). The approver approves or rejects.
   - **Expiry:** a request expires after 7 days.
   - **Withdrawal:** only its requester can withdraw it.
   - **Order:** one pending request per order.
3. **Roles.** A new **Legal** role holds `personal-data.legal-hold.approve`. Administrators hold every permission. **Privacy** keeps placing holds and requesting releases.
4. **Grace period.** On approval the hold is released, and the purge may delete the data only after `Customers:Retention:LegalHoldReleaseGraceDays`.
   - **Default:** 30 days, pending legal confirmation.
   - **Undoing a mistake:** placing a hold again during the grace period stops it.
5. **No emergency override in the application.** Releasing is never urgent: keeping data longer harms no one. Real emergencies use configuration break-glass (runbook `staff-access.md`).
6. **Audit.** The request, the approval or rejection, and the release are each audit entries in `customers.AuditEntries`: actor, case reference, before and after, correlation id. Maker-checker refusals are security events.

Flow: requester → (request, hold still in force) → different approver → approve (released; purge after the grace period) or reject or expire (stays held) → audit at each step.

## Consequences
**Positive**
- No single account can cause irreversible deletion.
- A mistaken release can be undone within the grace period.

**Negative**
- A release needs two people. Staffing must cover the Legal role, or Administrators.

**Pending**
- The grace period's legal adequacy (counsel).
- Real distinct staff accounts (the staff tenant).
