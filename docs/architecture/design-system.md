# Design system

The visual language of `customer-web` and `admin-web`. The design freeze on customer-web was lifted by the product owner on
2026-10-10. This document is the brief that every UI change follows. The tokens live in
`src/frontend/projects/design-system/tokens.css`, which each app imports from its own `styles.css`; the apps never import
each other (ADR 0009).

## Direction
- **Subject:** international flights and hotels, for travellers who compare, book and come back to manage a trip.
- **Not a market look:** the product is international and not market-specific.
- **Not another company's identity:** it keeps its own neutral name ("Travel booking") and borrows nobody's brand, logo or trade dress.
- **References are for interaction patterns, not looks:** large travel booking sites for search, results and filter patterns; modern travel sites for pacing and motion.
- **Its own vernacular comes from travel itself:** airport codes, departure boards, boarding passes and timetables. The memorable element is the **itinerary set as type**:
  - airport codes, times and prices in condensed numerals;
  - the search form shaped like a boarding pass, with a perforated stub that separates *where* from *who and when*.
- Everything around that one element stays quiet and disciplined.

**Avoided on purpose**, because they make a page look templated:
- all-caps labels above fields;
- rows of identical icon cards;
- decorative gradients and glows;
- tinted near-black standing in for black;
- a single bright accent on a black page;
- the same radius and shadow on every surface.

## Colour
| Token | Value | Role |
|---|---|---|
| `--color-brand` (signal orange) | `#f2600c` | The one loud colour. Primary actions, the route line, the selected state. Always ink text on it (5.1:1), never white. |
| `--color-accent-text` | `#b63f00` | Orange as text or link on light surfaces (5.7:1 on white). Also the focus ring. |
| `--color-ink` / `--color-night` (carbon) | `#1c1e21` | Text, the header band, the boarding-pass stub, admin navigation. |
| `--color-canvas` (stone) | `#f1f2ef` | Page background: a cool neutral, not cream. |
| `--color-surface` | `#ffffff` | Surfaces people read or act on. |
| `--color-line` / `--color-line-strong` | `#d8dad6` / `#8d9189` | Hairlines; strong lines for control boundaries (3.2:1). |

- **Feedback colours** each have a soft tint for backgrounds: success `#1a7f45`, danger `#b42318`, warning text `#8a4b00`, info `#1f5fbf`.
- **Contrast** was measured for every pair in `tokens.css`, which records the ratios.

## Type
- **One family:** Archivo, variable on weight and width (SIL OFL 1.1, self-hosted, Latin and Latin Extended).
  - Normal width for reading.
  - Condensed (`font-stretch: var(--stretch-condensed)`, heavy weight) for airport codes, times, prices and booking references: the itinerary as type.
  - Tabular figures wherever numbers are compared (times, prices, tables).
- **Scale:** classical, 12, 14, 16, 18, 21, 24, 30, 36, 48 and 60. Headings are sentence case. Lines stay under about 75 characters.

## Layout
- **customer-web**
  - A carbon header band.
  - Pages on the stone canvas, with content on white surfaces.
  - Left-aligned, in a 1200px column.
  - Search is the boarding pass:
    ```
    ┌──────────────────────────────────────────┬┄┄┬───────────────────┐
    │ LHR  ─ ─ ─ ✈ ─ ─ ─  JFK                 │  │ Departure  Return │
    │ London Heathrow        New York JFK      │  │ 1 traveller · Eco │
    └──────────────────────────────────────────┴┄┄┴───────────────────┘
                                                   [ Search flights ]
    ```
  - Results are itinerary rows: departure time, then the route line and stops, then arrival. The price is right-aligned in condensed numerals, the largest number in the row.
  - A booking moves through a real sequence (travellers, then payment, then confirmed), so it is shown as numbered steps.
- **admin-web**
  - The same tokens, denser.
  - A carbon navigation rail.
  - 14px tables with tabular figures.
  - One status badge vocabulary for every state machine.
  - Each page has a header (title, key facts), then actions, then history.
  - Approving actions are filled; refusing and withdrawing are outlined.
  - Maker-checker steps say who asked.

## Motion
- **Tokens:** `--ease-out`, `--ease-in-out`, and durations `instant` 90ms, `quick` 160ms, `base` 240ms and `enter` 360ms, plus a 40ms `stagger`.
- **One orchestrated moment per journey:** search results arrive. A skeleton holds their place, then the rows enter with a short stagger.
- **Everything else answers a person's action:** press, open, expand, select, confirm.
- **Never:**
  - entrance animations on every section;
  - hover lifts on every card;
  - motion that delays content.
- **`prefers-reduced-motion: reduce`** sets every duration to zero (`tokens.css`).

## Components and conventions
- **Shared primitives live in each app's `styles.css`:**
  - customer-web: `.btn`, `.card`, `.badge`, `.chip`, `.segmented`, `.tile`, `.field-label`, `.field-error`, `.alert`, `.skeleton`, `dialog.modal-sheet`;
  - admin-web: the same vocabulary, plus `.status` badges.
  - Components keep only their own layout.
  - Colours, sizes and durations come from tokens, never from hex values in a component.
- **Tests are a contract:** class names, ids, accessible names and texts that unit specs or Playwright use stay. A redesign changes how they look, not what they are called.
- **Accessibility is checked, not assumed:**
  - axe (WCAG 2.2 AA) runs in Playwright on every journey.
  - Keyboard focus is always visible: an orange ring on light surfaces, a lighter orange on carbon.
  - No horizontal scroll at 360, 390 or 768px.
  - Live regions and focus moves already in the code are kept.

## Delivery
| Batch | Scope |
|---|---|
| 1 | Foundation: tokens, font, this document; customer shell and flight search |
| 2 | Customer results, filters and hotels |
| 3 | Customer booking, checkout, confirmation, trips and cancellation |
| 4 | admin-web: shell, queues and search, order, payment, refunds and access |
| 5 | Motion and interaction polish; browser QA at 360, 768 and 1440px; accessibility pass |

Each batch is browser-verified (agent-browser) before it is called done.
