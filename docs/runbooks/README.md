# Runbooks

Step-by-step procedures for operational situations. A runbook is written **in the same PR** as the feature that introduces the failure mode, and is linked from the alert that fires for it.

## Planned runbooks
| Runbook | Introduced with | Alert |
|---|---|---|
| `payment-hold-release.md` (written) | Phase 3 (background money safety) | Hold not released / release request given up |
| `booking-pending-confirmation.md` | Phase 3 (first vertical slice) | Booking stuck in `PendingConfirmation` |
| `payment-captured-booking-failed.md` | Phase 3 | Capture failure / payment–booking mismatch |
| `provider-outage.md` | Phase 2/3 | Supplier degraded / circuit open |
| `webhook-backlog.md` | Phase 5 (Stripe) | Inbox lag |
| `refund-failed.md` | Refund phase | Refund `Failed` |
| `reconciliation-mismatch.md` | Phase 5 | Finance reconciliation mismatch |
| `secret-rotation.md` | Phase 1 (first deployment) | Scheduled / suspected leak |
| `database-restore.md` | First production deployment | DR drill / incident |

## Template

```markdown
# <Title>

**Alert:** <alert name and condition>
**Severity:** <P1–P4>  **Owner:** <team/role>
**Customer impact:** <what customers experience>

## Diagnose
1. <Where to look: admin timeline, dashboard, query (read-only)>
2. <How to tell which case you are in>

## Resolve
### Case A: <…>
1. <Exact admin action or command, with required permission>
2. <Expected result and how to verify it>

### Case B: <…>

## Do NOT
- <Dangerous actions, e.g. "do not re-submit the booking to the supplier">

## Escalate
<When and to whom, with what information (order ID, trace ID, provider reference)>

## Follow-up
<Post-incident actions, customer communication>
```

## Rules for runbooks
- Actions go through audited admin features wherever possible, not direct database edits.
- Any direct data fix requires two people and is recorded in the audit log/incident record.
- Never include secrets or customer PII in a runbook.
