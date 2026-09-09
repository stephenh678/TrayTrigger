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
- `ViewModels/` / `Views/` — WPF UI
- `Help/` — in-app "Learn more" content, embedded as resources
- `TrayTrigger.Tests/` — unit tests

## Code of Conduct

Be respectful and constructive. Disagreements about implementation are fine; personal attacks are not.
