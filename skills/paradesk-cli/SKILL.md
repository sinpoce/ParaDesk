---
name: paradesk-cli
description: Manage a ParaDesk Windows child session through its CLI, including status, connection, screenshots, and recording. Use when an agent should work in a parallel desktop while the user keeps using the main desktop.
---

# ParaDesk CLI

Use the installed ParaDesk executable to manage the parallel desktop. This skill is self-contained and has no Python, MCP, or machine-specific bridge dependency.

## Locate and inspect

Find `ParaDesk.exe` in the user's installed or extracted package directory. An existing process can reveal its location with `Get-Process ParaDesk | Select-Object Path`. Ask for the path only if local discovery cannot find it. Do not invent an installation path.

```powershell
# Set this to the actual installed or extracted executable.
$paraDeskExe = (Resolve-Path '.\ParaDesk.exe').Path
& $paraDeskExe --help | Out-String
& $paraDeskExe --status --json | Out-String
```

Pipe or capture output in PowerShell so the GUI executable completes before inspecting its result. Follow the installed version's `--help`; the commands below describe ParaDesk 1.2.0. Use `desktopState`, `attached`, `childSessionExists`, and `childSessionId` from the current status instead of assuming a session ID.

## Session lifecycle

- `--probe --json`: read-only environment check; exit 0 means ready, 2 means not ready or unsupported.
- `--start [--profile <name>] [--wait] --json`: start or reattach; `--wait` waits up to 100 seconds for sign-in or failure. It never requests elevation. Initial system setup and Windows credentials may require the user in the app.
- `--detach --json`: hide the desktop view and preserve the child session and its apps.
- `--quit --json`: finish recording, exit ParaDesk, and preserve the child session.
- `--close --json`: sign out of the child session and end every app in it. Use only when the user intends to end that session, after accounting for unsaved work.
- `--view-only on|off|toggle --json`: block or allow keyboard and mouse input through the parallel desktop view.
- `--topmost on|off|toggle --json`: change whether the view stays on top.

Do not change profiles, system setup, sharing preferences, or credentials merely to check status. ParaDesk shares the user's Windows account and files; it is not a security boundary.

## Capture and recording

```powershell
& $paraDeskExe --record start --json | Out-String
# Perform the requested work in the child session.
& $paraDeskExe --record stop --json | Out-String
& $paraDeskExe --status --json | Out-String
& $paraDeskExe --screenshot '.\child-desktop.png' --json | Out-String
```

Recording defaults to the parallel desktop window. `--record start --target monitor` records a monitor instead; use it only when the user requests that scope. `pause`, `resume`, and `toggle` are also supported. The desktop view must be open for window recording or screenshots. Inspect `recording`, `recordingPaused`, `recordingPath`, and `recordingTarget`, then check the resulting file before reporting success. Use a new screenshot path unless overwriting is intended; an explicit existing path is overwritten and output is always PNG.

`--rectest` really records a monitor and `--spike` really connects a child session. Do not treat either as a read-only diagnostic. Other exit codes are 1 for an operation failure, 3 when ParaDesk is not running, and 64 for invalid arguments.

## Working inside the child session

The CLI does **not** provide click, type, app-launch, or browser-control commands. Those require a separately configured automation agent or server running inside the child session. Do not imply that installing this skill installs such a controller.

Before sending input through a separate controller, verify its live process belongs to the current `childSessionId` and differs from the main console session. A stale identity file or a screenshot of a ParaDesk host window is not sufficient evidence. Observe the child screen, act on that observation, and refresh after each state-changing action.

When the user needs uninterrupted main-desktop input, avoid controlling the ParaDesk host with a main-session mouse/keyboard automation tool: focus and shortcuts may reach the main desktop. Use the verified in-session controller. If none is available, report that specific missing capability and continue any requested CLI-only work.

Honor controller ownership or user-control blocks. Wait the reported idle interval and recheck; do not disable its guard or substitute main-desktop input. A persistent block does not prove the user is actively using the computer. Stop input and explain the observed state if it cannot recover.

ParaDesk session management does not authorize unrelated external writes, account access, or information sharing. Keep credentials and tokens out of output and artifacts.
