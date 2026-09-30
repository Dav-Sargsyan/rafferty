# Rafferty

Rafferty 1.3.0 is a self-contained native Windows application for applying and testing DPI-desynchronization profiles. The production UI is WPF/.NET; it starts the bundled `winws` engine directly with administrator rights and verifies that the WinDivert driver is actually active before reporting protection.

## Current capabilities

- 22 complete profiles synchronized from the pinned upstream reference revision;
- complete ordered multi-rule chains, including every `--new` section;
- bundled WinDivert runtime, payloads, host lists and IP sets;
- SHA-256 verification and automatic restoration of embedded runtime files;
- YouTube frontend/CDN/HTTP3 and Discord API/CDN/Gateway/STUN diagnostics;
- automatic strategy selection, exact manual ALT selection and latency testing;
- A/B testing with the engine disabled and enabled;
- sanitized JSON diagnostic export and rotating local logs;
- Russian and English UI, tray integration and startup settings;
- self-contained single-file `Rafferty.exe` publication.
- cached Auto Mode with quick health checks, interactive IPSet/game filtering and SHA-256 verified self-update support.

The React/Tauri files under `src` and `src-tauri` are retained as the earlier interface prototype. The shipped application is `Rafferty.UI`.

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
dotnet publish Rafferty.UI\Rafferty.UI.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o Rafferty-1.3.0
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

Strategy definitions and redistributed runtime assets are synchronized from the pinned official `Flowseal/zapret-discord-youtube` revision documented in `PARITY_AUDIT.md`. Their original legal notices are retained under `licenses` and summarized in [THIRD_PARTY.md](THIRD_PARTY.md).

The bundled third-party files keep their respective upstream licenses. No separate open-source license is granted for the original Rafferty source unless a license file is added later.
