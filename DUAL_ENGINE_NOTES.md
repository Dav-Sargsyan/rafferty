# Dual-engine runtime

Rafferty keeps the existing Classic engine behavior and adds an isolated
Next-gen runtime based on official zapret2 v1.0.5.2.

- Runtime extraction: `%LOCALAPPDATA%\Rafferty\runtime\1.4.1-dual-engine\classic`
  and `nextgen`.
- Classic executable: `winws.exe`.
- Next-gen executable: `winws2.exe`, with matching `zapret-lib.lua` and
  `zapret-antidpi.lua` from the same release.
- Every embedded file is compared by SHA-256 before replacement.
- The engine manager stops the other engine before starting a strategy, so two
  WinDivert engines are never intentionally active at the same time.
- Normal automatic selection tests at most three Classic and three Next-gen
  candidates. The explicit Re-optimize action performs the deeper search.
- The persisted state records both the successful strategy and its engine.

Next-gen profiles are prepared JSON definitions. Rafferty does not generate or
download Lua code while running.
