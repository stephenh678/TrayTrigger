# A tool fails with "untrusted mount point" (error 448)

If a program started by a tool fails with Windows error 448, "The path cannot be traversed because it contains an untrusted mount point", while the same command works from a normal command prompt, TrayTrigger itself was probably started with a Windows restriction called redirection trust. Programs inherit it from their parent, so every tool TrayTrigger starts has it.

TrayTrigger never turns this on. It is inherited from whatever started TrayTrigger, such as an installer launched from another app. When TrayTrigger starts and finds it, it shows a tray notification and writes a warning to the log.

**To fix it:** quit TrayTrigger from the tray menu and start it again from the Start menu. A normal start does not have the restriction. After an in-app update, TrayTrigger is started through Explorer so it does not inherit it.
