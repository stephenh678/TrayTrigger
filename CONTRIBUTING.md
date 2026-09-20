# Contributing to TrayTrigger

Thanks for your interest in contributing. TrayTrigger is a solo-maintained project, but bug reports, feature ideas, and pull requests are all welcome.

## Getting Started

### Prerequisites
- Windows 10 (1809+) or Windows 11
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 (17.12+), VS Code, or JetBrains Rider — any editor with .NET 10 / C# 13 support works

### Build & Run
```bash
git clone https://github.com/stephenh678/TrayTrigger.git
cd TrayTrigger
dotnet build
dotnet run
```

### Running Tests
TrayTrigger has a companion test project, `TrayTrigger.Tests`. Run it before opening a PR that touches app logic:
```bash
dotnet test
```

## Reporting Bugs

Please use the [Bug Report](https://github.com/stephenh678/TrayTrigger/issues/new?template=bug_report.yml) issue template. Include your TrayTrigger version, Windows version, and — if the bug involves a specific game — whether it's a Steam or non-Steam entry. Logs (Settings → Troubleshooting) are extremely helpful for anything related to game detection, scanning, or tweak application.

## Requesting Features

Open a [Feature Request](https://github.com/stephenh678/TrayTrigger/issues/new?template=feature_request.yml). Describe the problem you're trying to solve, not just the solution — it makes it easier to evaluate alternatives that might already be closer to shipping.

## Submitting a Pull Request

- Keep PRs focused on a single change; large, unrelated changes bundled together are hard to review and hard to revert if something regresses.
- Match the existing code style in the file you're editing.
- Add or update tests in `TrayTrigger.Tests` for behavior changes, not just new features.
- Describe *why* the change is needed in the PR description, not just what changed.
- If your PR touches a system tweak (`Services/SystemTweaksService.cs`) or a performance profile (`Services/PerformanceProfileService.cs`), explain what you verified the tweak actually does and, if possible, cite the Microsoft documentation you based it on — these are the parts of the app users are trusting the most.

## Project Structure (quick orientation)

- `Models/` — data models and settings
- `Services/` — core app logic (game scanning, Steam/SteamGridDB integration, tweaks, performance profiles, updates)
- `ViewModels/` / `Views/` — WPF UI. Each page is its own `UserControl` in `Views/`
  (`LibraryView`, `SettingsView`, `SystemView`, `ToolsView`, `AboutView`); `MainWindow.xaml` holds
  the sidebar, shows one page at a time and owns what spans them (drag-and-drop, global keys, the
  launch toast)
- `Help/` — in-app "Learn more" content, embedded as resources
- `TrayTrigger.Tests/` — unit tests
- `docs/` — developer-facing playbooks not tied to any single file (e.g. [adding a new game-platform integration](docs/adding-a-platform-integration.md))

## Logging

A tester's `debug.log` is usually the only evidence there is: the bug is on someone else's PC, in a library you cannot see. The test for any log line is whether it would let you answer "what happened, and why?" without asking them to try again. When a log cannot answer that, the fix costs a whole beta cycle, so the logging goes in with the feature, not after the first report.

Everything goes through `LoggingService`. The levels:

- **`Info`** — something the user did, or that changed their data, once per action: a launch, an import, a removal, a tweak applied, an override written. Written whether or not verbose logging is on, so keep it to one line per action.
- **`Warn` / `Error`** — something failed that the user would notice or that loses something. Say what was being attempted and on what, not only the exception message.
- **`Verbose`** — every decision the app makes on its own, and why. Only written when the user turns verbose logging on (Settings → Troubleshooting), so it can be generous.

What must be logged at `Verbose`:

- **Why a feature did nothing.** An early `return`, a candidate rejected, a fallback taken, a search that found nothing: say so, and say what was looked at. "No DLSS files found" is useless without the folder that was searched and how that folder was chosen.
- **Every item a scan or filter drops, with the reason**, one line per item: already in library (and as which entry), on the ignore list, a DLC, an install with no executable on disk. A scan leg that is switched off says that too. `ImportCoordinator.OfferedBy` is the pattern.
- **What the user was shown.** Status lines, dialogs (and the answer given), toasts and the launch popup are echoed by `LoggingService.Shown` at the one place each is set. A new surface that puts words in front of the user calls it too.
- **Exceptions that are caught and not acted on**: `LoggingService.Swallowed(category, ex, "what was being attempted")`.

Rules the tests hold the code to (`LoggingConventionTests`):

- A `catch` block logs, rethrows, uses the exception it caught, or carries a comment saying why saying nothing is right. The comment is the honest choice on a hot path: code that runs for every process on every two-second poll must not write a line per miss.
- The category (the `[Tag]` in the log) names the **area**, not the class: `SteamScanner`, not `SteamScannerService`. One spelling per area, so a log can be searched by tag.

And two that they cannot:

- Anything that loops, joins or formats a collection to build a verbose line is wrapped in `if (LoggingService.IsVerboseEnabled)`, so it costs nothing when verbose logging is off.
- Never log an API key, a token or a password, and nothing from inside a user's files beyond a path.

## Code of Conduct

Be respectful and constructive. Disagreements about implementation are fine; personal attacks are not. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for the full policy.
