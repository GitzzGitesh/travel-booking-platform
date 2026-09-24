# Documentation

Persistent project knowledge. If behaviour changes, the doc describing it changes in the same PR.

| Folder | Contains | Changes when |
|---|---|---|
| [`progress.md`](progress.md) | Current phase, active story, done/next, blockers | Every story |
| [`requirements/`](requirements/) | Product scope, non-functional requirements, glossary, open business questions | Scope or requirements change; questions answered |
| [`architecture/`](architecture/) | System overview, booking and payment lifecycles, provider integration, security, observability | Design changes (and major ones need an ADR) |
| [`adr/`](adr/) | Architecture Decision Records | A significant decision is made or superseded |
| [`quality/`](quality/) | Testing strategy, failure-scenario catalog, definition of done | Test approach changes; scenarios added or covered |
| [`runbooks/`](runbooks/) | Step-by-step operational procedures | A feature introduces an operational failure mode |

## Conventions

- Markdown only. Diagrams use Mermaid in fenced blocks so they diff in PRs.
- Status markers: **Draft** (under discussion), **Proposed** (awaiting approval), **Accepted**. Architecture docs state their status at the top.
- Mark unknown values as `TBD` and link the open question. Don't invent numbers.
- Documents added when their feature arrives: `architecture/data.md` (schemas per module), `architecture/api.md`, `deployment.md`, `local-development.md`, `troubleshooting.md`, individual runbooks.
