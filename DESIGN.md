# DESIGN.md — Upscaler Manager

Adapted from [`linear.app`](https://github.com/filobus97/awesome-design-md/tree/main/design-md/linear.app)
in the awesome-design-md collection. Linear was chosen for its **discipline**, not its
palette: near-black canvas, hairline borders, a single accent that appears only on focus
and the primary action, and meaning carried by hierarchy rather than prose.

This file is the contract. If a screen disagrees with it, the screen is wrong.

## Principles

1. **The obvious screen needs no paragraph.** If a control needs explaining, the control
   is wrong. Explanation is a tooltip of one sentence, or nothing.
2. **One primary action per screen**, in accent. Everything else is secondary or plain.
3. **The accent is never decorative.** Focus rings, the primary button, and a live state
   badge. Nothing else.
4. **Surface lift means interactive or selected**, not emphasis. Controls sit on
   `surface-2`; nothing is lifted to decorate.
5. **Show state, not instructions.** "DLSS 310.1.0" beats "this game has DLSS installed".
6. **Reachable by D-pad.** Every action is a focusable target; no hover-only affordance.

## Colors

| Token | Value | Use |
| --- | --- | --- |
| `canvas` | `#0B0B12` | Window background |
| `surface-1` | `#13131C` | Cards, rows, panels |
| `surface-2` | `#181824` | Controls, selected row, hovered card, tags |
| `surface-3` | `#1E1E2C` | Nested panel inside a card |
| `hairline` | `#22222F` | Every border, 1px |
| `hairline-strong` | `#2E2E3F` | Border of a focused or selected element |
| `accent` | `#8B73F8` | Primary button, focus ring, live badge |
| `accent-hover` | `#9D88FF` | Primary button hover |
| `accent-pressed` | `#7560E0` | Primary button pressed |
| `ink` | `#E8E8F2` | Primary text |
| `ink-muted` | `#9A9AB8` | Secondary text, labels |
| `ink-subtle` | `#5A5A78` | Disabled, placeholders, paths |
| `success` | `#5CB87E` | Installed / verified |
| `warning` | `#D4A04A` | Needs attention |
| `error` | `#E06060` | Failed / refused |
| `nvidia` | `#76B900` | DLSS tag only |
| `amd` | `#E8544A` | FSR tag only |
| `intel` | `#4A9BE8` | XeSS tag only |

Vendor colors tag a technology. They are never backgrounds, buttons or borders.

## Typography

Inter, or the platform sans. Four sizes and no more.

| Token | Size / weight | Use |
| --- | --- | --- |
| `title` | 18px / 600, -0.2px | Page heading |
| `heading` | 14px / 600 | Section heading, card title |
| `body` | 12.5px / 400 | Everything |
| `caption` | 11px / 400 | Labels, paths, provenance |

No italics. No bold inside body text — hierarchy comes from size and color.

## Geometry

Radius: `4` chips · `6` buttons and inputs · `8` cards and panels · `12` cover art.

Spacing between things: `4 · 8 · 12 · 16 · 24 · 32`, nothing else. Page gutter 24.
Padding inside a component is listed with the component below.

## Components

- **button-primary** — `accent` fill, `#FFFFFF` text, radius 6, padding 8×14. One per screen.
- **button-secondary** — `surface-2` fill, `hairline-strong` border, `ink` text, same metrics.
- **button-quiet** — no fill, no border, `ink-muted` text. For Back and Cancel.
- **card** — `surface-1`, `hairline` border, radius 8, padding 14.
- **game card** — a card, 188×396, art box 160×224 at radius 12, then the title, then
  three reserved chip rows pinned to the bottom edge so every card shares one baseline.
  Focus outlines it in 2px `accent`: on a handheld the card is the D-pad target, and
  1px is not enough to find at arm's length.
- **row** — `surface-1`, radius 6, padding 10×12. Title `body`, detail `caption` beneath.
- **tag** — `surface-2` fill, radius 4, padding 2×6, `caption`, text in the vendor color.
- **badge-live** — `accent` text on `surface-2`, radius pill. For "installed" state only.
- **focus ring** — 1px `accent`, radius matching the control, inset 2px. Replaces
  Avalonia's default adorner, which draws outside the control and cannot be restyled.

## Writing

- Sentence case. No title case, no ALL CAPS.
- A row label is a noun phrase: "Upscaler", not "Which upscaler to use".
- Never write "simply", "just", "please", or "Note that".
- A tooltip is one sentence. If it needs two, the label is wrong.
- Numbers before words: "310.9.1.0 available" beats "a newer version is available".
- Errors say what happened and what to do, in that order, in one sentence each.

## Code comments

The same discipline, because the code had the same problem.

- One sentence per public member, saying what it is — not why it exists.
- A `// why:` comment only where a non-obvious decision would otherwise be
  "corrected" by someone later. Keep it to two sentences.
- No essays in XML docs. If the reasoning needs a paragraph, it belongs in a commit
  message or in `docs/`, not above a method.
