# Update Dialog Redesign — Short Design

Date: 2026-10-31
Status: approved in conversation (mockup v7 "sunset background" = locked design)

## Intent

The BeastStrap self-update dialog (what you see while the app downloads its own
new build) looked plain. Redesigned with the user through mockups to a locked
visual: the app's own layout DNA plus the project's `updater.png` sunset accent.

## What the new dialog looks like (mockup v7, approved)

- Window: 460 wide, height auto, no resize, `ExtendsContentIntoTitleBar`, no
  close/min/max affordances that cancel the download (same as today).
- Background: `Resources/updater.png` fills the entire window (`ImageBrush`,
  `UniformToFill`, centre-anchored), under a vertical dark scrim gradient for
  text legibility (approx rgba(7,12,22) 55% top → 18-25% middle → 45% bottom).
- Title bar row: BeastStrap icon + "Updating BeastStrap" (same as today).
- Hero header (centred, from the launcher's header pattern):
  `BeastStrapIcon.png` at ~58px + "BeastStrap" (22px SemiBold) beside it, with
  "v{tagName} · updating" (12px, secondary/tertiary) under the name.
- Progress section (margins ~28 left/right):
  - Row: "Downloading update…" (14px SemiBold) with the live percentage
    right-aligned (14px SemiBold).
  - Bar: 5px tall, rounded, track = translucent white (~7%), fill =
    `BrandGradientBrush` (cyan #38BDF8 → sky #7DD3FC).
  - Status line: Consolas 11px, secondary: "34.2 MB / 51.8 MB · 11.3 MB/s".
    When total size is still unknown: "Connecting…" as today, indeterminate bar.
  - Hint: 11px tertiary: "BeastStrap will restart and reopen the menu when the
    download finishes."

## Goals

- Replace today's flat title+thin bar with the locked design above.
- Show download speed (and keep everything else's behaviour identical).

## Non-goals

- No behaviour changes: app still auto-relaunches on success, dialog still
  refuses to close mid-download, failures still surface via `FailureReason` to
  the caller's existing message.
- No changes to other dialogs, bootstrapper styles, themes (Light etc.) — this
  dialog intentionally uses the fixed sunset art (it's its own accent piece,
  per the user's pick), not theme-following brushes, except text tokens that
  read fine on the scrim.

## Approach picked

Single XAML/code-behind restyle of `UpdateProgressDialog.xaml(.cs)` using
existing assets: `Resources/updater.png`, `Resources/BeastStrapIcon.png`,
`BrandGradientBrush`. Small rolling-average speed calculation in code-behind
over `AppUpdater.DownloadProgress` samples; percentage goes in its own row-end
TextBlock. No new dependencies.

## Constraints

- Must build with the existing .csproj (no new packages).
- XAML resources referenced by pack URIs like the rest of the app.
- Keep `UpdateStarted` / `FailureReason` contract with callers untouched.
