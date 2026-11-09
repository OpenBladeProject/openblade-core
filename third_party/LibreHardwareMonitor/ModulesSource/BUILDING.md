# Corresponding PawnIO module source

These files are unmodified sources from PawnIO.Modules commit
`52a7e536dff3e53c96917a28caac5e0fa6510696` (official release 0.2.11).
Only `IntelMSR.p` and `AMDFamily17.p` produce modules embedded by this project.
The included `include` directory is their complete header closure; every original
license and copyright notice is retained. `COPYING` is LGPL-2.1.

The upstream compilation recipe is:

```sh
pawncc IntelMSR.p -iinclude -C64 '-;+' '-(+' -p
pawncc AMDFamily17.p -iinclude -C64 '-;+' '-(+' -p
```

Upstream uses Pawn compiler 4.1.7152 from its `_pawn` directory; the compiler is a
separate build tool and is not executed or bundled with OpenBlade. Its source
package and upstream build workflow are available at the same pinned commit:

- [Compiler source package](https://github.com/namazso/PawnIO.Modules/blob/52a7e536dff3e53c96917a28caac5e0fa6510696/_pawn/pawn-4.1.7152-1.el9.src.rpm)
- [Upstream workflow](https://github.com/namazso/PawnIO.Modules/blob/52a7e536dff3e53c96917a28caac5e0fa6510696/.github/workflows/ci.yml)

Compilation produces unsigned `.amx` bytecode. OpenBlade embeds the unchanged,
official signed `.bin` release files; their hashes are recorded in the parent
source manifest. Rebuilding does not reproduce the publisher's private signature.
No driver or sensor is run by compiling these sources.
