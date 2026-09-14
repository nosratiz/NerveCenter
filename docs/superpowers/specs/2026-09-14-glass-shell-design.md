# Glass shell (sidebar + app-bar) — Design

Status: approved via visual brainstorming (mockups A-D, glow intensities 1-3, dark/light pair), 2026-09-14.

## 1. Context

`docs/design.md` §10 documents Nocturne as a deliberately flush, borderless shell: the app-bar and drawer share the same `Background` color as the rest of the app, with no shadow, no separate nav treatment. That was the right call for the sidebar/app-bar modernization pass (`06695ca`) — but the user now wants the opposite for the nav chrome specifically: a distinctly colored, "glassy" sidebar and app-bar that reads as its own visible layer, with an ambient glow that bleeds through the whole app (content included), in both light and dark theme.

This supersedes the "no separate nav treatment" line of §10 for the app-bar/drawer specifically. It does **not** change any `NocturneTheme.cs` palette token (`Background`, `Surface`, `Primary`, etc.) — those stay exactly as they are. This is a new CSS layer painted on top of the existing tokens, not a retuning of the tokens themselves.

Approved through iterative visual mockups (browser-based brainstorming companion):
- **Style direction**: "D — gradient glass" (a soft accent-to-info-blue diagonal gradient, translucent, blurred) over three alternatives (subtle white frost, solid accent frost, deep dark frost).
- **Glow intensity**: "3 — pronounced" (bold ambient color bleeding through) over "no glow" and "subtle glow".
- **Scope**: explicitly confirmed **whole app**, not sidebar-only — the glow and translucency reach into the content area (tables, tiles, forms), not just the nav chrome. The user was shown and acknowledged the readability tradeoff this implies for a data-dense admin console and chose it anyway.
- **Theme coverage**: both light and dark (not dark-only).
- **App-bar**: matches the sidebar's gradient-glass treatment for a cohesive shell, rather than staying flush as today.

## 2. Non-goals

- No `NocturneTheme.cs` palette changes — `Background`, `Surface`, `Primary`, `Divider`, etc. are untouched.
- No changes to component shapes, spacing, typography, or the Mini-drawer collapse behavior.
- No changes to Login's own card/form treatment (from the prior modernization phase) — its flat, no-shadow `MudPaper` and themed native form controls are untouched. **Correction from the original draft of this design:** the ambient body glow (§3) is a `body`-level CSS rule, and `body` is shared by every page regardless of layout — including Login's `EmptyLayout`, which has no app-bar/drawer to scope the glow away from. The original assumption that "Login renders under a layout with no app-bar/drawer, so this design doesn't touch it" turned out to be wrong: the glow does reach Login too. Manually verified and explicitly approved as acceptable (consistent with the "whole app" glow scope decided in §1) rather than fixed — Login intentionally also shows the ambient glow behind its card.
- No new interactive behavior — this is a CSS-only visual change.

## 3. Technical approach

**Where the CSS lives:** `src/SbConsole.Web/wwwroot/app.css` (global), not a `.razor.css` scoped file. This codebase has already hit this exact wall twice (documented in `MainLayout.razor.css` and `app.css`'s own comments): a scoped CSS rule can't target `.mud-appbar`/`.mud-drawer`/`.mud-main-content`, because MudBlazor's rendered root elements for these don't carry the Blazor scope attribute of the layout component that declares them. `App.razor` links `app.css` *after* `MudBlazor.min.css` (confirmed: `App.razor:9-10`), so plain (non-`!important`) selectors in `app.css` already win over MudBlazor's own `.mud-appbar`/`.mud-drawer` background rules by source order — no specificity hacks needed.

**Theme-adaptive color without branching:** every new color is derived from the theme's own CSS custom properties via `color-mix()`, never a new hardcoded hex value. `--mud-palette-primary` and `--mud-palette-info` already differ correctly between the Dark and Light palettes (`NocturneTheme.cs`), so a gradient built from `color-mix(in srgb, var(--mud-palette-primary) 35%, transparent)` automatically renders the right tone in whichever theme is active — no `prefers-color-scheme` media query, no light/dark CSS branch, no JS. This is the same pattern this codebase already uses for `Divider`/`TextSecondary` (alpha-mixed over a base token, per `NocturneTheme.cs`'s own comments).

**Layering, confirmed against MudBlazor's actual CSS** (`MudBlazor.min.css` 9.9.0, decompiled/grepped directly, not assumed):
- `body` already gets `background-color: var(--mud-palette-background)` from MudBlazor's own stylesheet. Nothing else sits behind it — `.mud-layout` has no background rule at all. So the ambient glow is added as `background-image` layers on `body` (radial-gradients, `color-mix`'d from `--mud-palette-primary`/`--mud-palette-info`), left as *layers* alongside (not replacing) the existing `background-color`. `background-attachment: fixed` keeps the glow stationary rather than scrolling with page content.
- `.mud-appbar` and `.mud-drawer` each get their `background-color` replaced with a `linear-gradient` (different angle per element, matching the approved mockup) built the same `color-mix()` way, plus `backdrop-filter: blur(18px) saturate(160%)` (with `-webkit-backdrop-filter` alongside for Safari) and a 1px alpha-mixed border (bottom for the app-bar, right for the drawer) so the glass edge stays legible against the glow behind it.
- `.page-content` (the existing global override two rules above this one in `app.css`) changes from an opaque `background: var(--mud-palette-surface)` to a translucent `color-mix(in srgb, var(--mud-palette-surface) 70%, transparent)`, so the body glow reads through the content area too — the explicit "whole app" scope decision.

**Browser support:** `backdrop-filter` and `color-mix()` are supported without prefixes in current evergreen Chrome/Edge/Firefox; Safari needs `-webkit-backdrop-filter` (included) and has supported `color-mix()` since Safari 16.4. This is an internal, team-hosted admin console (`docs/design.md` §1) — no legacy-browser fallback is required. Without `backdrop-filter` support, the gradient still renders (just without blur) and stays fully readable.

## 4. Testing

Pure CSS/visual change, no new interactive behavior. Confirmed no existing test asserts on the background values of `.mud-appbar`, `.mud-drawer`, or `.page-content` (grepped the test suite) — so no existing test needs updating, and no new automated test is warranted for CSS color/gradient values (bUnit doesn't evaluate rendered CSS, only DOM/attributes).

Verify manually in a running browser, both themes, before calling this done:
- App-bar and drawer read as a distinct gradient-glass layer, not flush with the content.
- The ambient glow is visible bleeding through the app-bar, drawer, and content area.
- Collapsed (mini/icon-rail) drawer state still looks correct.
- Text and table content in the content area remains legible against the translucent background (this is the tradeoff the user explicitly accepted — verify it isn't so severe that primary content is actually unreadable, not that it looks identical to the fully-opaque version).
- Both Light and Dark theme (Settings → Theme) look intentional and match the mockups' color relationship, not just "technically renders."

## 5. Documentation

`docs/design.md` §10 gets a new dated addendum note describing this glass-shell layer as superseding the "flush, no separate nav treatment" line for the app-bar/drawer specifically — the underlying palette tokens are unchanged; only the app-bar/drawer/body/content-background CSS gained a translucent gradient-and-glow layer on top of them.
