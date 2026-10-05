# Rafferty

Rafferty 1.5.0 is a self-contained native Windows application for applying and testing DPI-desynchronization profiles. The native WPF/.NET UI can run either the bundled Classic `winws` engine or the Next-gen `winws2`/Lua engine and verifies that WinDivert is active before reporting protection.

## Current capabilities

- 22 Classic profiles plus 6 prepared Next-gen profiles;
- automatic, Classic-only and Next-gen-only engine selection with cached fallback;
- complete ordered multi-rule chains, including every `--new` section;
- bundled WinDivert runtime, payloads, host lists and IP sets;
- SHA-256 verification and automatic restoration of embedded runtime files;
- YouTube frontend/CDN/HTTP3 and Discord API/CDN/Gateway/STUN diagnostics;
- automatic strategy selection, exact manual ALT selection and latency testing;
- A/B testing with the engine disabled and enabled;
- sanitized JSON diagnostic export and rotating local logs;
- Russian and English UI, tray integration and startup settings;
- self-contained single-file `Rafferty.exe` publication.
- cached Auto Mode with early exit and selected-service quick checks;
- GitHub Releases API updates with exact asset discovery and SHA-256 verification;
- interactive IPSet/game filtering and direct Dashboard engine/Classic strategy selection.

The shipped desktop application is the WPF project under `Rafferty.UI`; the network and strategy logic lives in `Rafferty.Core` and shared contracts in `Rafferty.Shared`.

## Build

Requirements:

- Windows 10 or Windows 11 x64;
- .NET 10 SDK;
- PowerShell 7 or Windows PowerShell 5.1.

Build and test:

```powershell
dotnet build Rafferty.sln -c Release -p:Platform=x64
dotnet test Rafferty.Core.Tests\Rafferty.Core.Tests.csproj -c Release -p:Platform=x64 --no-build
```

Publish one self-contained executable:

```powershell
dotnet publish Rafferty.UI\Rafferty.UI.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o Rafferty-1.5.0
```

Runtime network interception requires administrator rights. Do not disable antivirus or firewall protection globally.

## Verification commands

The application supports non-interactive validation switches:

```powershell
Rafferty.exe --smoke-test
Rafferty.exe --latency-test
Rafferty.exe --validate-strategies strategy-validation.json
Rafferty.exe --export-command-lines command-lines.json
Rafferty.exe --export-diagnostics diagnostics.json
Rafferty.exe --parity-test parity.txt
Rafferty.exe --manual-strategy general--alt7 manual-alt7.json
Rafferty.exe --auto-optimize auto-optimize.json
```

See [PARITY_AUDIT.md](PARITY_AUDIT.md) for the reference comparison and the limits of local validation.
See [AUTO_MODE_BEHAVIOR.md](AUTO_MODE_BEHAVIOR.md) for the exact cached-strategy and forced-optimization behavior.

## Reference and licensing

Classic strategy definitions and runtime assets are synchronized from the pinned `Flowseal/zapret-discord-youtube` revision. Next-gen binaries and Lua files come from the official `bol-van/zapret2` v1.0.5.2 release. Checksums and notices are retained under `engine`, `engine-nextgen`, and `licenses`, and summarized in [THIRD_PARTY.md](THIRD_PARTY.md).

The bundled third-party files keep their respective upstream licenses. No separate open-source license is granted for the original Rafferty source unless a license file is added later.
