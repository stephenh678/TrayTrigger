# The beta channel

TrayTrigger updates itself from GitHub Releases. Stable releases go to everyone. Beta releases are marked as pre-releases on GitHub and are only offered to people who opt in here.

## What you get

- New features and fixes before the stable release, usually days to weeks earlier.
- Builds that have had less testing. A beta can have a bug that a stable release would not.
- Version numbers such as v1.3.0-beta.2. The suffix tells you it is a beta and which one.

## How versions are chosen

- With the toggle off, TrayTrigger asks GitHub for the latest stable release only. Betas are invisible.
- With the toggle on, it looks at every release and offers the highest version, beta or stable.
- A stable release always outranks its own betas. When v1.3.0 ships, everyone on any v1.3.0 beta is offered it, toggle on or off.

## Leaving the beta channel

- Turn the toggle off. You keep the beta you have until the next stable release overtakes it, then update as normal.
- To go back to stable immediately, install the current stable release from the GitHub Releases page over the top. Your library and settings are kept.

## The repository field

The repository is where TrayTrigger looks for releases. Leave it at the default unless you are building TrayTrigger yourself from a fork.
