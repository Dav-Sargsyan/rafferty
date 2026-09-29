# Rafferty functional parity audit

Audit date: 2026-09-30  
Reference: official upstream repository revision `249a70424aae2676f99c5363e21073ed89873eda` (2026-09-26)  
Reference release version reported by `service.bat`: `1.10.3`

The prior Rafferty build was not functionally equivalent. It had four hand-written profiles, an 18-byte inactive IPSet, missing user exclude files, and incomplete `--new` chains. A process-only smoke test therefore produced a false sense of readiness.

| Feature | Reference | Rafferty before audit | Current status | Fix / evidence |
|---|---|---|---|---|
| Strategy set | 22 `general*.bat` files | 4 approximations | OK | All 22 strategies imported from the pinned revision. |
| Full rule chain | Usually 9 ordered sections | Main profile stopped before four final sections | OK | `general` has 88 arguments and 8 `--new` separators (9 sections). |
| Argument quoting | BAT quoting and continued lines | Not compared | OK | Arguments use `ProcessStartInfo.ArgumentList`; exact reconstructed commands can be exported with `--export-command-lines`. |
| `--wf-tcp` / `--wf-udp` | Present in every profile | Partial | OK | Preserved per strategy, including Discord voice ranges and inert game-filter port 12. |
| TCP/UDP/filter/L7 options | Per-profile | Partial | OK | Preserved in original order, including repeated options. |
| Fake QUIC/TLS/Discord/STUN/game payloads | 17 payload files | Some payloads present | OK | Official files synchronized byte-for-byte and covered by SHA-256 manifest/extraction checks. |
| WinDivert runtime | DLL + signed driver | Present but only process existence checked | OK | Startup now requires admin, BFE, non-exited `winws`, and a RUNNING WinDivert driver. |
| Engine stdout/stderr | Visible in console | Captured, but command omitted | OK | Exact command, stdout, stderr, exit code, driver initialization, rule and section counts are logged. |
| Host lists | Current reference lists | Reduced/older | OK | Current host/general/google/exclude lists embedded and repaired on startup. |
| User lists | Created by `service.bat` | Wrong/missing filenames | OK | Correct three user files are created automatically with safe non-empty defaults. |
| IPSet | Downloaded list (33,048 entries in audited copy) | 18-byte sentinel | OK | Loaded 572,927-byte list; runtime log confirms 33,048 IP/subnets. |
| IPSet excludes | System + user | User file missing | OK | Both files extracted/created and read successfully by `winws`. |
| Game filters | Configurable TCP/UDP ranges | Broad hard-coded ranges | PARTIAL | Default now matches upstream disabled state (port 12); UI range configuration is not yet exposed. |
| TCP timestamps | Checked/enabled by service script | Ignored | PARTIAL | State is inspected; known disabled states are enabled and logged. Unknown localized output is left unchanged rather than modified blindly. |
| Windows service mode | Optional upstream path | Separate obsolete service project | NOT REQUIRED FOR FOREGROUND MODE | Direct elevated `winws` + WinDivert is confirmed working. Persistent service installation is not used by the portable UI. |
| Driver cleanup/conflict diagnostics | Extensive service diagnostics | Missing | PARTIAL | BFE/admin/driver failures block protected state; complete interactive conflict removal is intentionally not automatic. |
| YouTube frontend/CDN | Multi-target HTTP tests | Basic HTTPS only | IMPROVED | Frontend, Google Video endpoint and exact HTTP/3/QUIC probe are reported separately. Actual video playback still requires Russian field validation. |
| Discord API/CDN/Gateway | Multiple targets | API only | IMPROVED | API, CDN and real Gateway WebSocket HELLO are tested. |
| Discord Voice/STUN | UDP strategy and fake payload | Generic STUN test | PARTIAL | Exact voice UDP filters/fakes are applied; UDP/STUN transport is tested. A real authenticated RTC call cannot be synthesized without a Discord session. |
| False “Protected” prevention | N/A | Process running implied protected | OK | “Protected” requires admin + engine + active WinDivert + applied strategy + minimum connectivity suite. Otherwise status is “Active — verification required”. |
| A/B parity test | External PowerShell test runner | Missing | OK | Advanced UI now runs OFF then ON checks and reports each endpoint side by side. |
| Diagnostic export | Console/test files | Missing | OK | Sanitized JSON includes versions, revision, admin/driver state, strategy, command, connectivity, list hashes and engine errors. |
| Runtime integrity | Release contents | Partial engine manifest | OK | Every embedded engine/list/license resource is SHA-256 compared and repaired; engine manifest covers all engine files. |
| Licensing | Bundled notices required | Notices present but documentation inaccurate | OK | MIT and WinDivert notices remain bundled and are shown from About. |

## Validation performed locally

- Solution builds with zero warnings and zero errors.
- Importer tests verify preservation of all `--new` sections, CMD-escaped literal exclamation marks, and the upstream disabled game-filter default.
- Export contains 22 generated command lines; `general` contains all 8 `--new` separators.
- Elevated startup log reports `windivert initialized. capture is started.`
- Runtime log reports 59 general hosts, 128 excluded hosts and 33,048 IP/subnets loaded.
- Engine startup reports `driver=active`, 101 arguments and 9 sections for the selected strategy.
- Full elevated strategy validation started every imported profile with an active WinDivert driver: 22/22 passed.

## Remaining external validation boundary

This machine is not on a Russian ISP path. No local test can honestly prove success against every deployed Russian DPI profile. The included A/B test and diagnostic export are the required evidence path for the next Russian field test. Until that field result is received, the build should be described as parity-correct and locally validated, not universally proven.
