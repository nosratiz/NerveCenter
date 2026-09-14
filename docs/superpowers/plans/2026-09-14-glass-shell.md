# Glass Shell (Sidebar + App-Bar) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the app-bar and drawer a distinctly colored, gradient-glass appearance with a pronounced ambient glow that bleeds through the whole app (content included), in both light and dark theme, entirely via existing theme tokens.

**Architecture:** One global CSS change in `wwwroot/app.css` (the only place that can target `.mud-appbar`/`.mud-drawer`/`body`/`.mud-main-content` — this codebase has already established, twice, that Blazor's scoped `.razor.css` files can't reach these components' root elements). Every new color is built from `color-mix(in srgb, var(--mud-palette-X) N%, transparent)` over existing MudBlazor CSS custom properties, so light and dark theme both render correctly with zero branching. No `NocturneTheme.cs` palette token changes.

**Tech Stack:** Plain CSS (`color-mix()`, `backdrop-filter`, `linear-gradient`/`radial-gradient`) against MudBlazor 9.9.0's existing CSS custom properties.

## Global Constraints

- No `NocturneTheme.cs` changes — only `wwwroot/app.css`.
- No changes to component shapes, spacing, typography, drawer collapse behavior, or Login (which uses `EmptyLayout` and has no app-bar/drawer).
- `App.razor` links `app.css` after `MudBlazor.min.css` (confirmed: `App.razor:9-10`), so plain selectors in `app.css` win over MudBlazor's own `.mud-appbar`/`.mud-drawer` rules by source order — no `!important` needed anywhere in this plan.
- **Testing exception, justified by existing precedent:** this plan's CSS changes have no automated test. bUnit renders DOM/attributes, not computed styles from an external linked stylesheet, so it cannot meaningfully assert on `background`/`backdrop-filter` property values — the same reason `Login.razor.css`'s actual property values (added in the prior host-pages-modernization plan) were never bUnit-tested, only the markup class hooks were. There are no new markup class hooks in this plan (all rules target MudBlazor's own existing `.mud-appbar`/`.mud-drawer`/`.page-content`/`body` selectors), so there is nothing new for bUnit to hook into. Verification is manual, in a running browser, per Task 2.
- `dotnet build -warnaserror` and `dotnet test` must stay green (no C#/Razor changes in this plan, so this just confirms nothing broke).

---

## Task 1: Add the glass-shell CSS

**Files:**
- Modify: `src/SbConsole.Web/wwwroot/app.css`

**Interfaces:**
- Consumes: `--mud-palette-primary`, `--mud-palette-info`, `--mud-palette-primary-darken`, `--mud-palette-text-primary`, `--mud-palette-surface` (all confirmed present in `MudBlazor.min.css` 9.9.0, and/or set by `NocturneTheme.cs` for both palettes).
- Produces: nothing consumed by other tasks (Task 2 is docs-only).

- [ ] **Step 1: Add the ambient body glow**

In `src/SbConsole.Web/wwwroot/app.css`, add this new rule immediately after the existing `.page-content { ... }` rule (after line 18, before the "Collapsed (mini/icon-rail) sidebar" comment):

```css
/* Ambient glow behind the whole app shell (docs/design.md §10 addendum, 2026-09-14): MudBlazor's
   own body{background-color:var(--mud-palette-background)} rule is the only thing painted behind
   .mud-layout (which sets no background of its own), so a glow added here as background-image
   layers is what the app-bar/drawer's backdrop-filter blur below, and .page-content's new
   translucency, actually reveal. Built entirely from color-mix() over existing palette tokens
   (the same alpha-mixing approach NocturneTheme.cs already uses for Divider/TextSecondary) so it
   adapts correctly to both Light and Dark without any prefers-color-scheme branching. */
body {
    background-image:
        radial-gradient(circle at 20% 20%, color-mix(in srgb, var(--mud-palette-primary) 45%, transparent) 0%, transparent 45%),
        radial-gradient(circle at 85% 80%, color-mix(in srgb, var(--mud-palette-info) 40%, transparent) 0%, transparent 45%),
        radial-gradient(circle at 60% 50%, color-mix(in srgb, var(--mud-palette-primary-darken) 35%, transparent) 0%, transparent 55%);
    background-attachment: fixed;
    background-repeat: no-repeat;
}
```

- [ ] **Step 2: Make the app-bar and drawer gradient-glass**

In the same file, add these two new rules directly after the body rule from Step 1:

```css
/* Gradient-glass app-bar and drawer (docs/design.md §10 addendum, 2026-09-14): replaces the flush,
   same-as-background app-bar/drawer from the prior sidebar modernization with a translucent,
   blurred gradient that reveals the body glow above. Colors are color-mix()'d from the same
   tokens as the glow, so both themes stay in sync automatically. */
.mud-appbar {
    background: linear-gradient(100deg, color-mix(in srgb, var(--mud-palette-primary) 30%, transparent), color-mix(in srgb, var(--mud-palette-info) 14%, transparent));
    backdrop-filter: blur(18px) saturate(160%);
    -webkit-backdrop-filter: blur(18px) saturate(160%);
    border-bottom: 1px solid color-mix(in srgb, var(--mud-palette-text-primary) 12%, transparent);
}

.mud-drawer {
    background: linear-gradient(165deg, color-mix(in srgb, var(--mud-palette-primary) 35%, transparent), color-mix(in srgb, var(--mud-palette-info) 18%, transparent));
    backdrop-filter: blur(18px) saturate(160%);
    -webkit-backdrop-filter: blur(18px) saturate(160%);
    border-right: 1px solid color-mix(in srgb, var(--mud-palette-text-primary) 12%, transparent);
}
```

- [ ] **Step 3: Let the glow bleed into the content area**

In the same file, change the existing `.page-content` rule's `background` line (currently line 17: `background: var(--mud-palette-surface);`) to:

```css
    background: color-mix(in srgb, var(--mud-palette-surface) 70%, transparent);
```

Update the comment above `.page-content` (lines 5-14) to note the new behavior — replace the sentence "Also gives the sidebar visual weight as its own panel: the drawer/app-bar stay on --mud-palette-background (the darker of the two theme tones) while the content pane sits on --mud-palette-surface (the lighter tone already used for cards/tables), instead of both inheriting the same body background." with:

```
   Also gives the sidebar visual weight as its own panel: the drawer/app-bar stay on
   --mud-palette-background (the darker of the two theme tones) while the content pane sits on
   --mud-palette-surface (the lighter tone already used for cards/tables), instead of both
   inheriting the same body background. (2026-09-14: --mud-palette-surface is now applied at 70%
   via color-mix(), not opaque, so the body's ambient glow — see the glass-shell rules below —
   bleeds through the content area too, per the approved "whole app" scope in
   docs/superpowers/specs/2026-09-14-glass-shell-design.md.) */
```

- [ ] **Step 4: Verify the build is still clean**

Run: `dotnet build -warnaserror`

Expected: build succeeds, 0 warnings, 0 errors (this task touches no `.cs`/`.razor` file, so this just confirms the CSS edit didn't break anything else).

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Web/wwwroot/app.css
git commit -m "$(cat <<'EOF'
feat: give the app-bar and drawer a gradient-glass shell with an ambient glow

Replaces the flush, same-as-background app-bar/drawer with a
translucent, blurred gradient over an ambient body glow that bleeds
through the whole app, content included (the approved "whole app"
scope). Every color is color-mix()'d from existing theme tokens, so
both Light and Dark theme render correctly with no branching.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Document the change in docs/design.md

**Files:**
- Modify: `docs/design.md`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing.

- [ ] **Step 1: Add a dated addendum to §10 Theming**

In `docs/design.md`, find the end of §10 (the "Theming (2026-09-11)" section — it ends with the paragraph about "A handful of tests pin the theme's key token values..." just before `## 11.` or the end of the file if there is no §11 yet). Add this new paragraph immediately after that last paragraph of §10, still inside §10:

```markdown
- **Glass shell (2026-09-14):** the app-bar and drawer no longer stay flush with
  `--mud-palette-background` — `wwwroot/app.css` now paints them as a translucent,
  blurred gradient (`.mud-appbar`, `.mud-drawer`) over a new ambient glow added to
  `body`'s background, with `.page-content` also made partially translucent so the
  glow bleeds through the content area too. This supersedes this section's earlier
  "flush shell, no separate nav treatment" line for the app-bar/drawer specifically —
  the underlying palette tokens (`Background`, `Surface`, `Primary`, etc.) are
  unchanged; only a new CSS layer sits on top of them. Every new color is built via
  `color-mix()` over the existing tokens (the same alpha-mixing approach already used
  for `Divider`/`TextSecondary`), so both Light and Dark theme render correctly
  without any `prefers-color-scheme` branching. Full rationale and the approved
  mockups' color/intensity choices: `docs/superpowers/specs/2026-09-14-glass-shell-design.md`.
```

Also update the file's top status/changelog line (currently starting "Status: approved 2026-09-09. Extended 2026-09-10 ... Extended 2026-09-13 ...") to add a new "Extended 2026-09-14 (Glass shell: §10 rewritten for the app-bar/drawer gradient-glass treatment)." clause, following the exact style of the existing entries in that line.

- [ ] **Step 2: Commit**

```bash
git add docs/design.md
git commit -m "$(cat <<'EOF'
docs: record the glass shell as a §10 Theming addendum

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Manual browser verification

**Files:** none (verification only).

**Interfaces:** none.

- [ ] **Step 1: Start the app**

Run: `dotnet run --project src/SbConsole.Web --launch-profile http`

(If a dev instance is already running on port 5249, reuse it instead of starting a second one.)

- [ ] **Step 2: Verify Dark theme**

With Settings → Theme set to Dark, open the Dashboard. Confirm:
- The app-bar and drawer read as a distinct, colored, blurred gradient-glass layer — not flush with the content.
- A colored ambient glow is visible bleeding through the app-bar, drawer, and the content area behind the dashboard tiles/table.
- Text and table content in the content area is still legible against the translucent background.
- Collapse the drawer to its icon-only rail (the menu toggle) and confirm the glass treatment still looks correct in that state.

- [ ] **Step 3: Verify Light theme**

Switch Settings → Theme to Light. Repeat the same checks — the gradient/glow should use the Light palette's tones (via the same `color-mix()` rules) and still read as intentional, not washed out or broken.

- [ ] **Step 4: Confirm Login is unaffected**

Sign out (or open `/login` directly) and confirm Login's flat card still renders exactly as it did before this plan — no app-bar/drawer exists on that page, so it should be completely unchanged.

- [ ] **Step 5: Spot-check another content-heavy page**

Open Connections or Audit (a table-heavy page) in both themes and confirm the table data stays legible with the translucent content background.

No commit for this task — it's a verification-only step confirming Tasks 1-2 together.
