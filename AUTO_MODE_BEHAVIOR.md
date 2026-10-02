# Rafferty Auto Mode behavior

Audit date: 2026-09-30

## Behavior before version 1.3.0

`RaffertyController.EnableAsync` loaded `state.json` and looked for `ActiveStrategyId`. If it existed, Rafferty started that strategy and ran the complete 12-check diagnostic suite. A fully successful result returned immediately. Any failed selected service stopped the engine and always launched `OptimizationEngine.OptimizeAsync`; there was no user setting that could prohibit this fallback.

If no saved strategy existed, Enable immediately performed full optimization. A successful result was stored in `state.json` with up to three backup strategy IDs. The explicit **Run optimization** button stopped the engine, cleared the saved strategy and therefore forced a full scan.

Manual mode was enforced by the WPF layer: Enable called `ApplyStrategyAsync` with the selected manual ID and did not call optimization. The selected mode and ID were stored in `config.json`; the active strategy was also stored in `state.json`.

## Behavior from version 1.3.0

### First automatic launch

1. No saved successful strategy is found.
2. Enable runs full Auto Optimize.
3. The selected working strategy and backups are saved in `state.json`.
4. The selected strategy remains running.

### Later automatic launches

1. Enable loads the saved strategy.
2. It starts that exact strategy.
3. By default it runs only a quick health check for the enabled targets: YouTube HTTPS, Discord API and/or UDP STUN.
4. If those checks pass, Enable returns without scanning other ALT profiles.
5. If the saved strategy fails and **Find another strategy after failure** is enabled, Rafferty stops it and performs full optimization.
6. If automatic fallback is disabled, Rafferty stops the failed strategy and reports the failure without selecting another profile.

The optional **Recheck on startup** setting replaces the quick health check with the full diagnostic suite. It still does not trigger a scan when the saved strategy passes.

### Manual mode

Manual Enable always starts the selected manual strategy. It never selects another strategy automatically. A full search happens only when the user explicitly presses **Run optimization**, which also changes the mode to automatic.

### Forced optimization

The **Run optimization** button always stops the active engine, clears the saved automatic result, scans strategies again and replaces `ActiveStrategyId` with the new winner.

### Persistence files

- `%LOCALAPPDATA%\Rafferty\config.json`: UI mode, manual strategy, selected service targets, runtime options and automatic-fallback settings.
- `%LOCALAPPDATA%\Rafferty\state.json`: last successful automatic/manual strategy, backup candidates, test timestamp and network identity.

## Dual-engine behavior from version 1.4.0

- The cache stores the successful strategy together with `Classic` or `NextGen`.
- Automatic mode tries that exact pair first and performs a quick health check.
- If it fails, the bounded search tries up to three profiles from the same
  engine and then up to three from the other engine. Each strategy id is tested
  once per cycle and the search stops after enough working candidates are found.
- Choosing Classic or Next-gen in Settings restricts both manual choices and
  automatic recovery to that engine.
- The explicit Re-optimize action is the only normal UI path that performs the
  deeper strategy search.

## Fast search from version 1.4.1

- Only enabled services are checked, in parallel, once per candidate.
- The first candidate that restores every enabled service wins immediately.
- Normal automatic search is bounded to three candidates per engine; a full
  strategy scan is available only from the explicit optimization button.
- Recent successes are tried first and failures from the last seven days are
  moved behind untested candidates without being blocked permanently.
- `state.json` keeps the last successful engine/strategy and up to 64 recent
  strategy results.
