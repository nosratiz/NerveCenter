# Host pages modernization — Design

Status: draft, 2026-09-14.

## 1. Context

The Nocturne design system (`docs/design.md` §10) and a flat, borderless visual
language have already been rolled out to the app shell (sidebar, app-bar —
`06695ca`, `5c80dec`) and to the Service Bus plugin's Queues page (`79400af`,
`a956799`): no shadowed cards, MudButton/MudTextField/MudTable used directly
on the page surface, a toolbar row for filters/actions.

This is the next phase: bring the remaining **host** pages (the ones owned by
`SbConsole.Web` itself, not the Service Bus plugin) in line with that same
convention. Plugin pages beyond Queues (Topics & Subscriptions, Dead-letter
overview, Peek, the create/send dialogs) are out of scope for this pass — a
later phase.

## 2. What's actually inconsistent

Auditing all six host pages against Queues.razor's established pattern found
that most of them already match it:

- **Connections, Audit, Plugins** — already flat: plain `MudTable`, toolbar
  rows with `MudTextField`/`MudButton`/`MudSpacer`, no `MudPaper`, no explicit
  elevation. **No changes.**
- **Settings** — already flat (no `MudPaper`), but its fields stretch
  full-width with no `max-width`, unlike the capped filter/input fields used
  elsewhere (e.g. Queues' `queue-filter` at `max-width:220px`). A layout
  looseness, not a leftover-Material issue. **One small change.**
- **Home** — the dashboard tiles use `MudPaper Elevation="1"`, the only place
  across all host pages a shadowed card construct still appears. **One
  change.**
- **Login** — the real outlier: raw unstyled `<input>`/`<button>` HTML (no
  theme colors, no rounded corners, no focus states) inside a `MudPaper` with
  an unset (default-shadowed) elevation. Every other page in the app uses
  MudBlazor's own styled components. **Needs the most work.**

Non-goal: no behavior, routing, or functional changes anywhere in this pass —
visual only. No changes to Connections, Audit, or Plugins.

## 3. Login.razor

`Login.razor` renders a real `<form method="post" action="/auth/login">` —
this is a plain HTTP POST handled by a minimal API endpoint
(`AuthEndpointsTests`), not an interactive Blazor form, and it must keep
working exactly the same way (no JS/circuit dependency, since it's the page a
user hits before any session exists). So the `<input name="password">` and
`<button type="submit">` stay native HTML — swapping them for
`MudTextField`/`MudButton` would be wrong here, not just unnecessary.

Changes:

- `MudPaper` gets `Elevation="0"` (matches the flat-card convention already
  established for `MudPaper` where it must appear at all).
- A new `Login.razor.css` (scoped, following the same pattern as
  `MainLayout.razor.css`) restyles the native `<input>` and `<button>` using
  the theme's existing CSS custom properties (`--mud-palette-primary`,
  `--mud-palette-lines-default`, `--mud-palette-surface`, the theme's
  `--mud-default-borderradius`) instead of inline `style="..."` attributes:
  rounded corners, an accent-colored focus ring, consistent padding, and a
  hover/active state on the submit button that reads as a primary action.
- No change to the form's `method`, `action`, input `name`, or the
  password-only/no-username shape (`docs/design.md` §5.1).

## 4. Home.razor

- The dashboard tile loop's `MudPaper Class="dashboard-tile pa-4" ...
  Elevation="1"` becomes `Elevation="0"` plus a `1px solid
  var(--mud-palette-lines-default)` border, so tiles stay visually distinct
  as discrete blocks without reintroducing a shadow.
- The `dashboard-tile` class name is unchanged (`HomeTests.cs` asserts
  `cut.FindAll(".dashboard-tile")`).
- No other change to Home: the "Needs attention" alerts and "Recent activity"
  table already use plain `MudAlert`/`MudTable`, consistent with the rest of
  the app.

## 5. Settings.razor

- Cap the width of the individual form fields (`instance-name` text field,
  theme select, session-timeout number field, audit-retention number field)
  with a `max-width` inline style, matching the convention already used for
  input widths elsewhere (e.g. `queue-filter`, `connection-filter`:
  `max-width` in the 220-280px range). Exact value picked per field during
  implementation to keep labels/values legible — no fixed number specified
  here.
- No structural change: still two `Typo.h6`-labeled sections ("General",
  "Safety"), same fields, same save/discard behavior.

## 6. Testing

- No new tests required — this is a pure visual/CSS change with no new
  behavior to cover.
- `HomeTests.cs`'s existing `.dashboard-tile` selector must keep passing
  unchanged.
- `AuthEndpointsTests.cs` must keep passing unchanged — it posts directly to
  `/auth/login` and never renders `Login.razor`, so it's unaffected by the
  markup/CSS change, but the manual verification step (§7) confirms the real
  rendered form still submits correctly.
- Gate: `dotnet build -warnaserror` and `dotnet test` green before commit,
  per `docs/design.md` §8.

## 7. Manual verification

Since this is a visual change, verify in a running browser before calling it
done:

- Login page: flat card, styled input/button, focus ring visible on tab,
  successful sign-in still works, failed-password message still renders.
- Home page: dashboard tiles read as flat bordered blocks in both light and
  dark theme (no shadow), "Needs attention"/"Recent activity" unchanged.
- Settings page: fields no longer stretch full width; save/discard still
  work.

## 8. Out of scope / future phases

- Service Bus plugin pages beyond Queues (Topics & Subscriptions,
  Dead-letter overview, Peek, SubscriptionPeek) and the dialogs (Send
  Message, Create Queue/Topic/Subscription, Confirm) — a later phase per the
  same Nocturne conventions.
- Any revisiting of the Nocturne design system's own tokens (colors,
  typography, shape) — this phase only applies the existing system.
- `MudTable`'s own default elevation (present uniformly across every
  table-based page, including the already-shipped Queues page) — untouched;
  changing it is a separate, cross-cutting decision outside this pass.
