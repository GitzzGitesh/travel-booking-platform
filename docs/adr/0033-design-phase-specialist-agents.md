# 0033. Specialist agents for the design phase

- **Status:** Accepted (2026-10-10). The product owner asked for these agents when lifting the design freeze.
- **Date:** 2026-10-10
- **Deciders:** Product owner; engineering lead
- **Related:** `.claude/rules/architecture.md` (new agents need an ADR), `docs/architecture/design-system.md`, ADR 0009

## Context
The product owner lifted the customer-web design freeze. They asked for the redesign of customer-web and admin-web to be
split across specialist roles:
- a creative director;
- a customer experience designer;
- an admin operations designer;
- a motion specialist;
- a design-system specialist;
- an accessibility and browser QA specialist.

The repository so far has three read-only reviewer agents, and its rules require an ADR for any new agent and for any
agent that gains Bash.

## Decision
We add six agents in `.claude/agents/`:

| Agent | Role | Tools |
|---|---|---|
| `design-director` | Visual direction and consistency across both apps; reviews proposals against `design-system.md` | Read, Grep, Glob |
| `customer-experience-designer` | Audits and specifies the customer journeys (search to trips) | Read, Grep, Glob |
| `admin-operations-designer` | Audits and specifies the operations workspace (queues, orders, payments, refunds, access) | Read, Grep, Glob |
| `motion-designer` | The motion language: tokens, the orchestrated moments, feedback, reduced motion | Read, Grep, Glob |
| `design-system-engineer` | Tokens, primitives and Angular component conventions; duplication and budget checks | Read, Grep, Glob |
| `ui-qa-specialist` | Renders the apps with agent-browser: viewports, keyboard, contrast, states, console errors | Read, Grep, Glob, Bash |

- **Advice only:** every agent reports. None edits files, approves or blocks merges, or commits.
- **The main session integrates:** it assigns explicit deliverables, applies the changes, runs the tests and owns delivery.
- **Bash for QA only:** `ui-qa-specialist` has Bash only to drive agent-browser and run the frontend builds and tests. It does not change git state, start or stop services, or touch any database.

## Consequences
**Positive**
- Specialist reviews can run in parallel without editing the same files.
- The design brief is applied consistently, because every agent reads `design-system.md` first.

**Negative / trade-offs**
- **Six more agent definitions to maintain.**
- **One agent with Bash:** its instructions limit what it runs, but the tool itself is not restricted further.

## Alternatives considered
| Option | Why not chosen |
|---|---|
| Ad-hoc prompts to general agents | Not reusable; the brief would drift between prompts |
| Agents that edit files | Concurrent edits to the same templates and styles; integration and test ownership would blur |
