# Aurora
*The package manager that doesn't waste your time.*

Aurora is the package manager and build system for **Lumina**. It is built on a simple premise: package management should be instant, atomic, and compatible. We took the architectural logic of Arch Linux's `pacman` and `makepkg`, rewrote it in modern C#, and compiled it to NativeAOT.

No runtime dependencies. No garbage collection pauses. No drama.

---

## Philosophy
We believe in standing on the shoulders of giants, but we aren't afraid to fix their posture.

-   **Native Performance**: Compiled to machine code. It starts faster than you can type `au`.
-   **Strict Compatibility**: Uses standard `PKGBUILD` specifications and `.PKGINFO` metadata. If it builds on Arch, it builds here.
-   **Modern Architecture**: Replaces ancient shell script logic with robust, type-safe structures while maintaining a seamless Bash execution environment for build scripts.
-   **Zero Trust**: Builds run in sanitized environments. We assume build scripts are broken until proven otherwise.

---

## Key Features

### The Core
-   **NativeAOT**: Zero dependencies on the target system. Just a single binary.
-   **Atomic Transactions**: Updates utilize a journaled staging system. If the power goes out during an update, your system doesn't die.
-   **Parallelism**: Multithreaded downloads and repository indexing. We saturate your bandwidth, not your patience.

### The Build System (ABS)
-   **Monolithic Script Generation**: Generates isolated, single-process Bash scripts to strictly emulate `makepkg` behavior, solving directory state persistence issues.
-   **Smart Environment**: Automatically sanitizes `PATH`, injects hardening flags (`_FORTIFY_SOURCE=3`), and handles `chroot`/`fakeroot` contexts without user intervention.
-   **Auto-Linking**: ELF scanning automatically detects `SONAME` dependencies (e.g., `libreadline.so=8-64`). No more broken partial upgrades.

### The Repository
-   **JSON Databases**: We replaced flat-file DBs with high-speed JSON. Parsing is instant.
-   **GPG Signing**: Every database and package is cryptographically signed.
-   **Legacy Free**: No support for ancient compression formats. We use Zstandard.

---

## Usage

Aurora unifies the build tool and the package manager into a single CLI.

### Package Management
**Sync Repositories**
```bash
au sync
```

**Install a Package**
```bash
au install neovim
# Or install a local file
au install ./binutils-2.41-1-x86_64.au
```

**System Update**
```bash
au update
```

**Remove a Package**
```bash
au remove nano
```

### Build System
**Build from Source**
Navigate to a directory containing a `PKGBUILD`.
```bash
au build
```
*Note: Aurora automatically checks for `fakeroot` and handles privilege escalation checks. Do not run as root.*

---

## Architecture

Aurora is split into three components:

1.  **Aurora.Core**: The brain. Handles parsing (Bash shims, JSON, .PKGINFO), network logic (concurrent downloads), and filesystem transactions (tar/zstd extraction).
2.  **Aurora.CLI (`au`)**: The user interface. Built with Spectre.Console for clean, informative output.
3.  **Aurora.RepoTool (`au-repotool`)**: The server-side indexer. Scans thousands of packages in parallel to generate the `core.json` repository database.

---

## Building Aurora

You need the .NET 10 SDK.

```bash
# Publish as a standalone native binary
dotnet publish Aurora.CLI -c Release -r linux-x64 /p:PublishAot=true -o ./out

# The resulting binary is './out/au'
```

---

## Contributing

We strictly follow the **Lumen Research Group** contribution guidelines.

1.  **Code**: Keep it clean. If your method is longer than your screen, refactor it.
2.  **Performance**: If it allocates unnecessary memory, kill it.
3.  **Compatibility**: We aim for 100% `PKGBUILD` compatibility. If a package builds on Arch but fails on Aurora, file an issue.

---

## Contact

-   **Issues**: File them in this repository.
-   **Discussions**: [lumina Discussions](https://github.com/lumen-rsg/lumina/discussions)

---

## Powered By
C# | NativeAOT | Linux | Caffeine
