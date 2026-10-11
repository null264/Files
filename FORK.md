# Fork notes

This is a personal fork of [files-community/Files](https://github.com/files-community/Files).
It carries a small set of local changes that are **not** intended for upstream. They are kept
as a patch series on top of upstream `main`, so syncing upstream is a **rebase**, not a merge
(no merge commits, no "re-apply my change"-style fixup commits).

## Branch model

| branch | role |
| --- | --- |
| `main` | Pristine mirror of `upstream/main`. Fast-forward only — never commit here. |
| `fork/main` | `upstream/main` + the patch series below. Build and release from here. |
| `backup/*` | Snapshots of the pre-restructure state (2026-10-11) and the old stash. |

## Syncing upstream

    python fork/sync-upstream.py            # fetch + mirror + rebase + range-diff report
    python fork/sync-upstream.py --push     # ... and force-push fork/main at the end

By hand it is:

    git fetch upstream
    git switch main      && git merge --ff-only upstream/main   # keep the mirror clean
    git switch fork/main && git rebase upstream/main            # replay the patch series
    git range-diff upstream/main@{1}..fork/main@{1} upstream/main..fork/main
    git push --force-with-lease origin fork/main                # CI builds the result

- `git config rerere.enabled true` + `rerere.autoupdate true` are set, so a conflict that was
  resolved once is replayed automatically the next time.
- Never `git checkout --ours/--theirs` a whole file: it silently drops the other side's
  non-conflicting changes. Resolve per hunk.
- After a rebase, prefer `git range-diff` over eyeballing: it shows whether the rebase changed
  anything beyond adapting to upstream.

## Patch series (`upstream/main..fork/main`)

| # | commit | what / why | upstreamable? |
| --- | --- | --- | --- |
| 1 | Feature: Use a dynamic STA worker pool and cancel pending work when the app is hidden | Replaces per-call STA `Thread` creation with a bounded, self-sizing pool (`STATaskPool`, `STATaskWorkItem`); `STATask.Run` overloads now take a `CancellationToken` + execution timeout, and work queued from those call sites is cancelled when the window is hidden (`App.WindowHideToken`). | The pool + timeout idea could be proposed upstream; the token plumbing touches many files. |
| 2 | Feature: Cache decoded bitmaps behind an xxhash-keyed MemoryCache | `BitmapHelper.ToBitmapAsync` becomes `ValueTask` and caches decoded `BitmapImage`s keyed by `XxHash64(data)` + decode size (400 entries, 5 min sliding / 15 min absolute). | Possibly, but cache sizing is preference. |
| 3 | Fix: Stop layout timers when navigating away and drop redundant `ToList` calls | Leaked `DispatcherTimer`s after navigation (`jumpTimer`, `dragOverTimer`, `tapDebounceTimer`, `hoverTimer`, `doubleClickTimer`) and a few redundant `ToList()` calls before LINQ. | Yes — small and generic. |
| 4 | Build: Pin the patched SQLite native library | Adds `SQLitePCLRaw.lib.e_sqlite3` so the bundled native sqlite is the patched one. | No (fork policy). |
| 5 | Build: Keep upstream packaging files untouched and inject the fork identity at build time | See below. | n/a |

## Build-time inputs (upstream files are not edited for these)

- `fork/Fork.props` (imported from `Directory.Build.props`) holds the fork's packaging inputs:
  - `ForkAppxPublisher` — **must match the signing certificate subject** (default is this fork's
    personal cert: `CN=Null's FilesDev Fork, O=Nullsoft, C=CN`).
  - `ForkAppxVersion` — leave empty to keep upstream's version, or stamp a release version.
  - `ForkAppxManifestPath` — generated manifest under `obj/`.
- `Directory.Build.targets` overrides `AppxBundlePlatforms` (default `x64`) *after* the project
  body, so `src/Files.App/Files.App.csproj` can stay upstream-identical.
- The tracked `src/Files.App/Package.appxmanifest` is **byte-identical to upstream**. At build
  time `Package.appxmanifest` is copied to `obj/Package.fork.appxmanifest`, the `Publisher` /
  `Version` attributes are stamped with `XmlPoke`, and the declared `AppxManifest` item points
  there — so upstream's capabilities/extensions are inherited automatically and never conflict.
- Verify with:
  `msbuild -t:Build src/Files.App/Files.App.csproj -p:Configuration=Debug -p:Platform=x64 -p:ForkAppxVersion=9.9.9.9`
  → `bin/x64/Debug/net10.0-windows*/win-x64/AppxManifest.xml` must show the injected values.

## Intentional differences from the old fork history

- `Microsoft.Windows.CsWin32` is no longer pinned to `0.3.335`; it follows upstream (the pin was
  a bad merge resolution, and it re-conflicted on every upstream bump).
- Removed stray UTF-8 BOMs, one unused `Microsoft.Extensions.Hosting` `PackageVersion` entry, and
  formatting-only edits (indentation, `if(` → `if (`) that had leaked into upstream-owned files.

## Intentional behavior changes (do not "fix" these)

- `Win32Helper.Storage.cs` `MountVhdDisk` opens the image with `explorer '<path>'` instead of
  `Mount-DiskImage`, and the old implementation is left commented out on purpose. Mounting
  through PowerShell requires elevation, and files on an administrator-mounted image then
  throw for normal users (and for an elevated process running with a restricted token), so the
  fork deliberately avoids mounting.

## Known items to revisit

Nothing outstanding. The shutdown path in `App.xaml.cs` briefly used a blocking
`Program.Pool.WaitOne()`; it is back to upstream's `await WaitOneAsync(pool)` because blocking
the dispatcher stalls WinRT wrapper finalizers during teardown.


## CI (planned)

- Add a fork-owned workflow file — never edit upstream's workflow files (pure additions never conflict).
- Trigger on `fork/main` and on tags; `main` is a mirror and does not need CI.
- Pass `-p:ForkAppxVersion=<x.y.z.w>` (e.g. derived from the tag) and, if signing, inject the
  publisher / certificate from secrets.

## Local build

    # VS 18 Developer PowerShell, x64
    msbuild -restore Files.slnx -p:Configuration=Debug -p:Platform=x64 -v:quiet -clp:ErrorsOnly

`msbuild` is not on `PATH` outside a Developer prompt. Enter one without depending on a
machine-specific Visual Studio instance id (the one in `AGENTS.md` belongs to another machine):

    pwsh -NoProfile -Command "& 'C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\Tools\Launch-VsDevShell.ps1' -Arch amd64 -HostArch amd64 -SkipAutomaticLocation; msbuild -restore Files.slnx -p:Configuration=Debug -p:Platform=x64 -v:quiet -clp:ErrorsOnly"

Two local traps worth knowing:

- If both `http_proxy` and `HTTP_PROXY` are set, the Roslyn build task aborts with a
  duplicate-key error. Unset one form before building.
- Building with `-p:ForkAppxVersion=9.9.9.9` is the quick way to prove the package identity
  injection still works: `AppxManifest.xml` under `bin/x64/Debug/net10.0-windows*/win-x64/`
  must show that version and the fork's `Publisher`.
