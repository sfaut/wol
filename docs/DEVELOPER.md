# Developer guide

How to build, test and release Wol from its sources. For using Wol, see the
[README](../README.md).

## Repository layout

| Folder | Content |
|---|---|
| `cli/` | The `wol` command line, in Rust: magic packets, state checks, the `wol.toml` file. It holds **all** the logic. |
| `gui/src/Wol.Desktop/` | The Windows desktop app `wol-gui.exe`, in C# / .NET 10 / WinUI 3. A front-end that runs `wol.exe` and reads its JSON output. |
| `gui/launcher/` | A small Rust launcher, the `wol-gui.exe` at the root of a release (see [Release layout](#release-layout)). |
| `gui/tools/` | `Build-Icons.ps1`, which builds the `.ico` files from `design/assets/*.svg`. |
| `docs/` | [`cli-contract.md`](cli-contract.md): the contract between the CLI and its front-ends (configuration file, commands, JSON output, exit codes). |
| `design/` | Design of the desktop app: [brief](../design/brief.md), mockups, specs and icon sources. |
| `.github/workflows/ci.yaml` | Continuous integration (see [CI](#continuous-integration)). |

The CLI and the desktop app only share the contract: update `docs/cli-contract.md` **before**
changing any JSON output, and bump `schema` on a breaking change.

## Prerequisites

### Command line (all systems)

- [Rust](https://rustup.rs/) stable, 1.85 or later (edition 2024), with `rustfmt` and `clippy`
  (installed by default by rustup).
- **Windows:** the MSVC toolchain, from
  [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022)
  with the "Desktop development with C++" workload (MSVC and Windows SDK).
- **Linux:** a C toolchain (`build-essential`), and `musl-tools` for the static release builds.
- **macOS:** the Xcode command line tools (`xcode-select --install`).

### Desktop app (Windows only)

- Windows 10 1809 or later, or Windows 11.
- Everything the CLI needs on Windows (Rust and the MSVC toolchain: the Native AOT link uses it
  too).
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- The Windows App SDK comes as NuGet packages, restored by the build: nothing to install.

Development uses VS Code, and everything builds from the command line. The Visual Studio IDE
is not needed.

## Command line

From `cli/`:

```sh
cargo build                      # debug build: target/debug/wol(.exe)
cargo build --release            # optimized build: target/release/wol(.exe)
cargo run -- status              # run it, e.g. with the status command
cargo run -- --config test.toml list   # on a configuration file of your own
```

The release profile is optimized for size (LTO, stripped, `panic = "abort"`). On Windows,
`cli/.cargo/config.toml` links the C runtime statically, so `wol.exe` runs without the Visual C++
Redistributable.

### Tests and lint

```sh
cargo test                       # all tests: unit tests in src/, end-to-end tests in tests/cli.rs
cargo test <name>                # tests whose name contains <name>
cargo test --test cli <name>     # end-to-end tests only
cargo fmt                        # format (the CI runs cargo fmt --check)
cargo clippy --all-targets -- -D warnings
```

The end-to-end tests replace the network scan with the `WOL_FAKE_SCAN` variable: `IP=MAC`
pairs separated by commas, or `none` for no local network. It also helps to try the commands by
hand:

```sh
WOL_FAKE_SCAN="192.168.1.20=A4:BB:6D:12:34:56" cargo run -- --config test.toml status
```

The scan has platform-specific code (`src/scan.rs`). After changing it, lint the other
systems too:

```sh
rustup target add x86_64-unknown-linux-gnu aarch64-apple-darwin
cargo clippy --all-targets --target x86_64-unknown-linux-gnu -- -D warnings
cargo clippy --all-targets --target aarch64-apple-darwin -- -D warnings
```

### Other targets

```sh
rustup target add aarch64-pc-windows-msvc          # Windows ARM64 (from Windows)
rustup target add x86_64-unknown-linux-musl        # static Linux build (from Linux)
cargo build --release --target <target>            # target/<target>/release/wol
```

Linux release builds target musl, for a static executable that runs on any distribution.

## Desktop app

The desktop app runs `wol.exe` from its own folder. Its build copies
`cli/target/release/wol.exe` (or the debug build when there is no release one) next to
`wol-gui.exe`: **build the CLI first**, and rebuild it after changing it.

From `gui/` (solution `Wol.slnx`):

```sh
dotnet build                               # debug build
dotnet run --project src/Wol.Desktop       # build and start it
```

The debug build lands in `src/Wol.Desktop/bin/Debug/net10.0-windows10.0.19041.0/win-x64/`,
everything in one folder. Start it from a non-elevated console: WinUI 3 drag and drop
(reordering groups) fails in an elevated process.

By default the app uses your own configuration (`%APPDATA%\wol\wol.toml`). To work on a
separate one, drop a `wol.toml` in the output folder (portable mode, ignored by Git): the app
then also keeps its settings there, in `wol-gui.json`.

There is no GUI test project yet: check changes by hand, in English and in French (the layout
must tolerate French strings about 30% longer).

### Publish

```sh
cd cli && cargo build --release && cd ..
cd gui/launcher && cargo build --release && cd ../..
cd gui
dotnet publish src/Wol.Desktop -c Release -r win-x64
```

The release lands in `src/Wol.Desktop/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`
(or in the folder given with `--output`). The publish stops with an error when the CLI or the
launcher is not built.

The Native AOT link runs `vswhere.exe`. If it is not found, put the Visual Studio Installer
folder on the `PATH` first:

```powershell
$env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
```

`-r win-arm64` publishes for Windows ARM64, but still copies the CLI and the launcher from
`target/release`: they must then be built for that target and copied by hand.

### Release layout

```text
wol-gui.exe      Rust launcher (gui/launcher), starts runtime\wol-gui.exe with the same arguments
wol.exe          the CLI
runtime\         the desktop app and the ~80 files of the Windows App SDK
```

The launcher keeps the executables out of the Windows App SDK files. The desktop app finds the
root of the release in `Services/AppFolder.cs`: `wol.exe`, the portable `wol.toml` /
`wol-gui.json` and the target of the "Start with Windows" shortcut all live there.

### Constraints

- **Portable, minimal footprint:** no installer, no registry writes, no system-wide runtime. The
  app is unpackaged and self-contained (Windows App SDK included). Notifications use tray
  balloons (`Shell_NotifyIcon`), since `AppNotificationManager` writes to the registry; "Start
  with Windows" is an opt-in shortcut in `shell:startup`.
- **Native AOT:** no reflection. JSON goes through `System.Text.Json` source generation, and
  WinRT objects cannot be cast to interfaces they were not created as (create an
  `OverlappedPresenter` and keep it, for example, instead of casting `AppWindow.Presenter`).
- **The CLI owns the logic:** the app never reads or writes `wol.toml`; every change goes
  through a `wol` command run with `--json`, and monitoring through one long-lived
  `wol watch --json` process.
- **Localization:** English (default) and French, in
  `src/Wol.Desktop/Strings/<locale>/Resources.resw`, used with `x:Uid` in XAML or
  `Strings.Get` in code.

### Icons

The `.ico` files of `src/Wol.Desktop/Assets/` are built from `design/assets/*.svg` and
committed. After changing an SVG, build them again (renders with Microsoft Edge headless):

```powershell
gui/tools/Build-Icons.ps1
```

## Continuous integration

`.github/workflows/ci.yaml` runs on pushes to `master`, on pull requests, on published releases
and by hand (`workflow_dispatch`):

- **CLI**, on Windows, Linux (musl) and macOS, x86_64 and ARM64: `cargo fmt --check`, clippy,
  tests on the native runners, release build.
- **GUI**, on Windows x64: CLI and launcher builds, launcher lint, `dotnet publish`.

The builds are kept as workflow artifacts (Actions tab, run page).

## Releases

A version is published by creating a GitHub release:

1. Bump the version in `cli/Cargo.toml`, `gui/launcher/Cargo.toml` (and their `Cargo.lock`)
   and `<Version>` in `gui/src/Wol.Desktop/Wol.Desktop.csproj`, add its entry to the changelog,
   commit and push.
2. Create the annotated tag `vX.Y.Z` on that commit, push it, and create the release from it:

   ```sh
   git tag --annotate vX.Y.Z --message "Wol vX.Y.Z" origin/master
   git push origin vX.Y.Z
   gh release create vX.Y.Z --verify-tag --title vX.Y.Z --notes-file notes.md
   ```

3. The published release runs the CI on its tag. When every build passes, the `Release` job
   checks that the tag matches the three versions, then attaches the archives to the release:
   `.zip` for Windows, `.tar.gz` for Linux and macOS (`wol-vX.Y.Z-<system>-<arch>`,
   `wol-gui-vX.Y.Z-windows-x64`). When a build fails, the release stays without them.

Follow the CI run with `gh run watch`.
