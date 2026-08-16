# Third-Party Runtime Components

Parallax Studio uses or interoperates with third-party native software. This file documents the technical boundary; it is not a substitute for the license texts or redistribution terms supplied by each upstream project.

## Bootstrapped components

The pinned dependency manifest is `native-dependencies.json`. It currently describes libmpv, libaacs, libbdplus, and libdvdcss. Bootstrap downloads are verified and staged for local development use.

## MakeMKV / LibMMBD

MakeMKV is **not downloaded by the Parallax Studio bootstrap and is not stored in the source repository**.

When MakeMKV is already installed on the developer/user machine, Parallax Studio can use:

- `libmmbd64.dll` as the current protected Blu-ray compatibility provider;
- `makemkvcon64.exe` or `makemkvcon.exe` for optical-disc title discovery metadata.

The local build script may copy the installed `libmmbd64.dll` into the local application output and stage local compatibility aliases expected by the libbluray/libmpv path. Those files originate from the user's MakeMKV installation, not from the Parallax Studio repository.

Before publishing installers, portable ZIPs, CI artifacts, or other redistributable binaries, review the licenses and redistribution terms for every third-party binary included in the package. Do not assume that a file staged successfully for local development is automatically redistributable with Parallax Studio.

## Project-owned code

Parallax Studio's own source is governed by the repository `LICENSE`. Upstream native components remain governed by their respective projects and licenses.
