# Update Dialog Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the self-update dialog (`UpdateProgressDialog`) to the approved mockup v7: `Resources/updater.png` sunset backdrop under a dark scrim, launcher-style hero header, cyan→sky gradient progress bar with live percentage and download speed.

**Architecture:** Two commits on one file pair. Task 1 is the static look — XAML markup, the scrim, gradient bar, and its csproj/ctor wiring. Task 2 is the dynamic behaviour — percentage text, rolling-window speed calculation over the existing `AppUpdater.DownloadProgress` reports. No behaviour contract changes.

**Tech Stack:** WPF (.NET 8, `net8.0-windows`), WPF-UI 2022 namespace, existing brand resources (`BrandGradientBrush`, `Resources/BeastStrapIcon.png`), new embedded resource `Resources/updater.png`. No new packages.

**Spec:** `docs/superpowers/specs/2026-10-31-update-dialog-redesign.md`

## Global Constraints

- No new NuGet dependencies (spec constraint).
- Public contract untouched: `UpdateProgressDialog(GithubRelease, IEnumerable<string>)` ctor signature, `UpdateStarted`, `FailureReason`, close-blocking `WpfUiWindow_Closing`, and the `RunAsync` flow (spec non-goals).
- Strings stay hardcoded in the dialog (it is untranslatable today; do not introduce a translation dependency).
- Resolution of resource URIs uses `pack://application:,,,/...` like the rest of the app.
- Repo deliberately has no test project (removed in `09e04b0`); verification = `dotnet build` passes with zero errors, plus manual visual check.
- Commit style is conventional commits (`feat:`, `chore:`), one commit per task.

## Review Focus

- Unknown content length (`Total == 0`): bar stays indeterminate, percentage TextBlock hidden, status shows "downloaded" wording. Owning check: Task 2 implementation is diff-reviewed branch-wide; no automated test exists (repo removed the test project).
- Early speed samples: first report arrives as (0, total) — delta time and delta bytes are ~0; speed math must guard against divide-by-zero / Infinity / NaN before formatting.
- Missing csproj resource: forgetting `<Resource Include="Resources\updater.png" />` makes the pack URI throw at runtime (`InitializeComponent`). Test: Task 1 build + launch step catches it.
- Percentage display: `Value` is a double; displayed percent must be the integer percent only (no decimals), matching the bar's throttle granularity.

---

### Task 1: Static look — sunset backdrop, scrim, hero header, gradient bar

**Files:**
- Modify: `MrExStrap/BeastStrap.csproj` (after line 26, with the other `<Resource Include="Resources\...">` entries)
- Modify: `MrExStrap/UI/Elements/Dialogs/UpdateProgressDialog.xaml` (full rewrite of the body)
- Modify: `MrExStrap/UI/Elements/Dialogs/UpdateProgressDialog.xaml.cs:31` (ctor text assignment)

**Interfaces:**
- Consumes: `GithubRelease.TagName` (already a ctor parameter);
  `BrandGradientBrush` from `UI/Style/Brand.xaml` (merged app-wide in `App.xaml`)
- Produces: named elements `VersionText`, `HeaderText`, `PercentText`, `StatusText`, `ProgressBar` that Task 2 fills/updates. `StatusText` and `ProgressBar` keep today's names and roles.

- [ ] **Step 1: Add the background image as an embedded resource**

In `MrExStrap/BeastStrap.csproj`, directly below the `Resources\BeastStrapIcon.png` entry (line 26), add:

```xml
<Resource Include="Resources\updater.png" />
```

- [ ] **Step 2: Rewrite the XAML body of `UpdateProgressDialog.xaml`**

Window attributes unchanged except `Background` (remove it — the image + scrim grid covers everything). Body structure, in order inside the one root `Grid`:

1. `Image` filling the window: `Source="pack://application:,,,/Resources/updater.png"`, `Stretch="UniformToFill"`.
2. Scrim over it: `Rectangle` with a vertical `LinearGradientBrush` (three stops, `StartPoint 0,0`, `EndPoint 0,1`):
   - Offset 0, `#8B070C16` (≈55% ink)
   - Offset 0.5, `#30070C16` (≈19% ink)
   - Offset 1, `#73070C16` (≈45% ink)
3. Content `StackPanel` on top (row 0 of the same grid is the unchanged `ui:TitleBar`; the panel sits beneath it):
   - Hero header, `HorizontalAlignment="Center"`, horizontal `StackPanel` with left `Margin` spacing: `Image` 58×58 `Source="pack://application:,,,/Resources/BeastStrapIcon.png"`; beside it a vertical `StackPanel` with:
     - `TextBlock` "BeastStrap", 22 SemiBold
     - `TextBlock x:Name="VersionText"`, 12, `{DynamicResource TextFillColorSecondaryBrush}` (text set in ctor, Step 4)
   - Download row: 2-column `Grid` `Margin="28,22,28,0"`:
     - left: `TextBlock x:Name="HeaderText"`, 14 SemiBold, text "Downloading update…"
     - right: `TextBlock x:Name="PercentText"`, 14 SemiBold, `Visibility="Collapsed"`, text "0%"
   - `ProgressBar x:Name="ProgressBar"` `Margin="28,12,28,0"`, `Height="5"`, `Minimum="0"`, `Maximum="100"`, `IsIndeterminate="True"`, `Foreground="{DynamicResource BrandGradientBrush}"`, `Background="#12FFFFFF"`, `BorderThickness="0"` — a plain element like every other ProgressBar in the app; do not invent a custom style.
   - `TextBlock x:Name="StatusText"` `Margin="28,9,28,0"`, FontSize 11, `FontFamily="Consolas"`, `{DynamicResource TextFillColorSecondaryBrush}` — initial text "Connecting…" (as today).
   - Hint `TextBlock` `Margin="28,10,28,22"`, FontSize 11, `{DynamicResource TextFillColorTertiaryBrush}`, `TextWrapping="Wrap"`, text "BeastStrap will restart and reopen the menu when the download finishes."

Keep: `ui:TitleBar` element exactly as today (icon, title, hidden buttons, `Closing` wiring), `MinWidth`/`MinHeight`/`Width="460"`/`SizeToContent="Height"`/`ResizeMode="NoResize"`/`ExtendsContentIntoTitleBar="True"`/`WindowStartupLocation="CenterScreen"`.

- [ ] **Step 3: Update the ctor text in `UpdateProgressDialog.xaml.cs`**

Replace line 31 (`HeaderText.Text = $"Downloading BeastStrap {release.TagName}…";`) with:

```csharp
VersionText.Text = $"{release.TagName} · updating";
```

Nothing else in the file changes (Task 2 owns `ApplyProgress`).

- [ ] **Step 4: Build**

Run: `dotnet build D:\BeastStrap\MrExStrap\BeastStrap.csproj -c Debug`
Expected: build succeeds, 0 errors (warnings pre-existing only).

- [ ] **Step 5: Commit**

```bash
git -C D:\BeastStrap add MrExStrap/BeastStrap.csproj MrExStrap/UI/Elements/Dialogs/UpdateProgressDialog.xaml MrExStrap/UI/Elements/Dialogs/UpdateProgressDialog.xaml.cs
git -C D:\BeastStrap commit -m "feat: restyle update dialog with updater.png backdrop and hero header"
```

---

### Task 2: Dynamic progress — percentage and rolling-window speed

**Files:**
- Modify: `MrExStrap/UI/Elements/Dialogs/UpdateProgressDialog.xaml.cs` (`ApplyProgress` at lines 57–71 plus new fields/helpers)

**Interfaces:**
- Consumes: `AppUpdater.DownloadProgress(long Downloaded, long Total)` reports via existing `Progress<>` wire-up (unchanged in `RunAsync`); named elements from Task 1 (`PercentText`, `StatusText`, `ProgressBar`).
- Produces: no public interface changes; `FormatBytes(long)` helper stays as-is (used by ctx: status line), new private helper for the speed tail (`FormatSpeed(long bytesPerSecond) → string`, e.g. `" 11.3 MB/s"`).

- [ ] **Step 1: Add speed-sample tracking to `UpdateProgressDialog`**

Add a private field: `private readonly List<(DateTime TimestampUtc, long Bytes)> _samples = new();` — timezone-insensitive because all samples are `DateTime.UtcNow` pairs and only deltas matter.

- [ ] **Step 2: Rewrite `ApplyProgress`**

Behaviour, pinned to the spec/text tokens:

1. Record the sample: append `(DateTime.UtcNow, p.Downloaded)`; drop samples older than 5 s from the front; speed = `(last.Bytes - first.Bytes) / (last.TimestampUtc - first.TimestampUtc).TotalSeconds` from the remaining window — **skip the speed entirely** while the window has fewer than 2 samples or the elapsed seconds (or delta bytes) are not positive. Store both as `long bytesPerSecond` rounded from the double.
2. `p.Total > 0`:
   - `ProgressBar.IsIndeterminate = false`
   - `ProgressBar.Value = pct` (same integral-percent double as today)
   - `PercentText.Visibility = Visible`; `PercentText.Text = $"{(int)pct}%"`
   - `StatusText.Text = $"{FormatBytes(p.Downloaded)} / {FormatBytes(p.Total)}{speedTail}"`
3. `p.Total == 0`:
   - `ProgressBar.IsIndeterminate = true` (as today)
   - `PercentText` stays/returns `Collapsed`
   - `StatusText.Text = $"{FormatBytes(p.Downloaded)} downloaded{speedTail}"`
4. `speedTail` = speed known ? `" · " + FormatSpeed(bytesPerSecond)` : `""`, and `FormatSpeed(b) => FormatBytes(b) + "/s"` (reuses `FormatBytes`, giving `"11.3 MB/s"`, `"540.0 KB/s"` etc.).

The rounding/int guard lives in one place `ComputeSpeed()` or inline — implementer's call, but there must be no path that emits `Infinity`, `NaN`, or a divide by zero.

- [ ] **Step 3: Build**

Run: `dotnet build D:\BeastStrap\MrExStrap\BeastStrap.csproj -c Debug`
Expected: build succeeds, 0 errors.

- [ ] **Step 4: Manual visual + behaviour check (no test project exists)**

The dialog only appears when GitHub has a release newer than the running build. Verification, in order:

1. Static look — launch the Debug exe; open the launch menu → "Check for updates". If a newer release exists, `UpdateProgressDialog` opens. Confirm: sunset image fills the window, scrim keeps text legible, hero header shows icon + "BeastStrap" + `v{tag} · updating`, and "Connecting…" resolves into a counting percentage with the gradient cyan→sky bar.
2. Speed line — confirm the status line reads "X MB / Y MB · Z MB/s" while downloading, with no `NaN`/`Infinity` at any point. (Slow connections show the speed more reliably; on instant downloads the speed tail may briefly be absent — that is acceptable, the byte counters must not be.)
3. Close-block — click × mid-download: the dialog stays open exactly as before (existing behaviour, confirm unchanged).

If GitHub shows no newer release: skip 1–3, state that in the task report, and rely on the build result.

- [ ] **Step 5: Commit**

```bash
git -C D:\BeastStrap add MrExStrap/UI/Elements/Dialogs/UpdateProgressDialog.xaml.cs
git -C D:\BeastStrap commit -m "feat: show live percentage and download speed in update dialog"
```
