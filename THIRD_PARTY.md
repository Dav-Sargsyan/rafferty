# Third-party components and licensing review

Reviewed on 2026-09-29 from the upstream repositories. This is an engineering
summary, not legal advice.

## Flowseal/zapret-discord-youtube

- Upstream: <https://github.com/Flowseal/zapret-discord-youtube>
- License: MIT.
- Copyright notice in the reviewed `LICENSE.txt`:
  `Copyright (c) 2016-2026 bol-van` and
  `Copyright (c) 2024-2026 Flowseal`.
- The repository combines batch launch strategies, lists, utilities and
  executables sourced from zapret release/bundle projects.
- Reuse is permitted by MIT when the copyright and permission notice are kept.

Rafferty redistributes the pinned runtime binaries, packet templates, lists and
strategy semantics needed for functional compatibility. The upstream UI and
branding are not used. The complete upstream MIT notice is bundled and exposed
from the application's About view.

## bol-van/zapret

- Upstream: <https://github.com/bol-van/zapret>
- Reviewed license: `docs/LICENSE.txt`, MIT.
- Copyright notice: `Copyright (c) 2016-2024 bol-van` in the reviewed revision.
- `winws` is the Windows packet-filtering engine in the zapret family.

If source or binaries are redistributed, the upstream MIT notice must accompany
them. This repository currently redistributes neither.

## bol-van/zapret-win-bundle

- Upstream: <https://github.com/bol-van/zapret-win-bundle>
- Purpose: Windows bundle containing winws/winws2, WinDivert-related files,
  helper tools and examples.
- The bundle contains components with different provenance. Treat every
  artifact according to its own upstream license and preserve notices.

Rafferty requires an explicit external install step and does not vendor this
bundle.

## WinDivert

- Upstream: <https://github.com/basil00/WinDivert>
- License: dual-licensed, at the recipient's choice, under LGPL version 3 or
  GPL version 2, as stated by upstream.
- The upstream package includes the complete license text. Redistributors must
  meet the selected license's obligations and retain notices. Driver binaries
  must also be appropriately signed for supported Windows configurations.

Rafferty ships the unmodified WinDivert DLL/driver used by the pinned upstream
runtime. The applicable WinDivert license text and source location are bundled.

## Practical distribution policy

1. Keep a recorded source revision, checksums and licenses for every bundled
   third-party EXE, DLL, SYS, packet template and list.
2. Download only over HTTPS from an official upstream release.
3. Verify a pinned SHA-256 hash or a trusted digital signature before install.
4. Keep all required license and copyright files next to installed components.
5. During uninstall, remove a driver only when ownership by this application is
   recorded and no other installation depends on it.
