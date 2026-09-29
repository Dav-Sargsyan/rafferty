# Rafferty architecture

## Goals and boundaries

Rafferty is a lightweight Windows network utility. The React interface does
not inspect or modify packets. Privileged packet handling belongs to an
independently installed engine and, in a later phase, a Windows service.

```text
React UI (unprivileged)
        │ typed Tauri commands
        ▼
Rust controller
        │ validated strategy ID + fixed executable location
        ▼
Windows service (Phase 3)
        │ process lifecycle / least privilege
        ▼
External winws + WinDivert distribution
```

Phase 1 implements the UI shell and dashboard. Phase 2 implements a local Rust
controller for development and establishes the command contract that the
Windows service will later own. The current controller is deliberately not a
substitute for service isolation: closing the desktop app stops a child engine
that it started.

## Repository layout

- `src/` — React/TypeScript UI, visual state and the typed Tauri bridge.
- `src-tauri/` — Rust desktop shell and engine lifecycle controller.
- `strategies/` — versioned, declarative strategy database.
- `engine/` — documentation only; third-party binaries are installed outside
  the source tree.
- `config/` — brand and default user settings.
- `lists/` — seed include/exclude lists, never Wi-Fi credentials.
- `service/` — reserved for the Phase 3 Windows service.
- `logs/` — runtime destination documented here; actual logs live in AppData.
- `updater/` — reserved for signed/hash-verified component updates.

## Command surface

The UI can call only four commands:

- `engine_status`
- `list_strategies`
- `start_engine(strategyId)`
- `stop_engine`

The UI cannot pass an executable path or raw command line. `strategyId` is
resolved against an embedded database validated at application startup. The
controller starts processes with `std::process::Command`, not a shell, and
canonicalizes the fixed `winws.exe` path before execution.

## Runtime data

Tauri resolves the per-user application-data directory. The controller creates:

```text
net.rafferty.desktop/
├── engine/        # separately installed third-party component
├── lists/         # seed lists provisioned once, then user-maintained
└── logs/
    ├── controller.log
    └── engine.log
```

Settings, runtime state, strategy data and future network profiles remain
separate files. No network password is collected or stored.

## Strategy model

`strategies/strategies.json` has a schema version and an array of profiles.
Each profile exposes UI metadata and an argument array. Arguments are separate
process arguments, not a command string. The initial profiles are original,
small examples based on publicly documented winws options; they are not copied
Flowseal batch files and should be treated as seed profiles pending real-network
validation.

The future optimizer will first classify failures (DNS, HTTPS, QUIC, service
API, CDN, WebSocket, STUN), select only relevant protocol profiles, and score
success rate, latency, loss, stability and startup time. No machine learning is
needed.

## Privilege model

The UI should remain unprivileged. In Phase 3, install/start/stop and driver
operations move behind a narrowly scoped Windows service. The service should
authenticate local IPC clients, apply a command allow-list, protect its config
ACLs, and reject arbitrary executable paths or arguments. It must cap restart
attempts (for example, three crashes in 60 seconds) to avoid a restart loop.

## Lifecycle roadmap

1. React/Tauri UI shell and dashboard — implemented.
2. Rust external-engine controller — implemented for development.
3. Windows service and authenticated IPC.
4. Strategy schema expansion and signed database loading.
5. Non-destructive network test engine.
6. Rule-based auto optimization and scoring.
7. Privacy-preserving network profiles.
8. Tray and user-level autostart.
9. Diagnostics and narrowly scoped repair actions.
10. Signed/hash-verified updater with rollback.
11. Installer and ownership-aware uninstall.
12. Profiling and resource optimization.
