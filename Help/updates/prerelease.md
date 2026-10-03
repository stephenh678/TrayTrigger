# Updates and the beta channel

TrayTrigger updates itself from GitHub Releases. Stable releases go to everyone. Beta releases are marked as pre-releases on GitHub and are only offered to people who opt in.

## How updates work

- With "Automatically check for updates on startup" on, TrayTrigger asks GitHub for the latest release a few seconds after it starts and once a day while it runs. Check for Updates Now does it on demand.
- When there is a newer version, a dialog shows what changed. Download & Install fetches the installer and runs it; Remind Me Later asks again another day.
- A download is only installed when it matches the release's checksum file and that file carries TrayTrigger's signature, so a tampered release is refused.
- Nothing is sent but the request for the release list. Untick the option to stop the checks altogether.

## What you get

- New features and fixes before the stable release, usually days to weeks earlier.
- Builds that have had less testing. A beta can have a bug that a stable release would not.
- Version numbers such as v1.3.0-beta.2. The suffix tells you it is a beta and which one.

## How versions are chosen

- With the toggle on, TrayTrigger looks at every release and offers the highest version, beta or stable.
- With the toggle off, it asks GitHub for the latest stable release only, and betas are invisible - unless the build you are running is itself a beta, in which case later betas are still offered, so a beta installed by hand is never stranded.
- A stable release always outranks its own betas. When v1.3.0 ships, everyone on any v1.3.0 beta is offered it, toggle on or off.

## Joining from a build you installed by hand

Installing a beta's Setup.exe does not tick the toggle for you. The build still follows later betas, as above, so you stay current either way - but tick it anyway if you want the beta channel to survive you moving back to a stable release later.

## Leaving the beta channel

- Turn the toggle off. You keep the beta you have until the next stable release overtakes it, then update as normal. While you are on a beta, later betas keep arriving.
- To go back to stable immediately, install the current stable release from the GitHub Releases page over the top. Your library and settings are kept.

## The repository field

The repository is where TrayTrigger looks for releases. Leave it at the default unless you are building TrayTrigger yourself from a fork.
