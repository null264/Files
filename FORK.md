# Fork notes

This is a personal fork of [files-community/Files](https://github.com/files-community/Files).
It carries a small set of local changes that are **not** intended for upstream. They are kept
as a patch series on top of upstream `main`, so syncing upstream is a **rebase**, not a merge
(no merge commits, no "re-apply my change"-style fixup commits).

## Branch model

| branch | role |
| --- | --- |
| `main` | Pristine mirror of `upstream/main`. Fast-forward only — never commit here. |
| `build/custom` | `upstream/main` + the patch series below. Build and release from here. |
| `backup/*` | Snapshots of the pre-restructure state (2026-10-11) and the old stash. |

Exactly one branch carries the patch series. The same commits living on two branches and each
branch being rebased is what duplicates history: the two copies get different SHAs, so a later
merge treats them as distinct commits. Rebase `build/custom` only, and let a work branch follow
it with `--onto` (below) — never merge a work branch into it.

## Syncing upstream

    python fork/sync-upstream.py            # fetch + mirror + rebase + range-diff report
    python fork/sync-upstream.py --push     # ... and force-push build/custom at the end

By hand it is:

    git fetch upstream
    git switch main          && git merge --ff-only upstream/main   # keep the mirror clean
    git switch build/custom  && git rebase upstream/main            # replay the patch series
    git range-diff upstream/main@{1}..build/custom@{1} upstream/main..build/custom
    git push --force-with-lease origin build/custom                 # replace the rewritten series

Because the series exists on one branch only, it does not matter how late the rebase happens:
`git rebase upstream/main` replays it exactly once.

- Work that needs its own branch: branch off `build/custom`, commit only that change, then

      git switch build/custom && git rebase upstream/main
      git switch fix/xxx      && git rebase --onto build/custom <sha it forked from>
      git switch build/custom && git merge --ff-only fix/xxx && git branch -d fix/xxx

  A plain `git rebase upstream/main` on the work branch replays the whole series a second time —
  that is the duplicate-history trap, and it is why the merge that follows cannot be avoided
  afterwards.
- Never `git checkout --ours/--theirs` a whole file: it silently drops the other side's
  non-conflicting changes. Resolve per hunk.
- After a rebase, prefer `git range-diff` over eyeballing: it shows whether the rebase changed
  anything beyond adapting to upstream.

## Local setup (once per clone)

    git config rerere.enabled true
    git config rerere.autoupdate true

A local pre-push guard (kept outside this repository and excluded through `.git/info/exclude`)
refuses a push whose range contains merge commits or the same patch twice — the two fingerprints
of a series that was rebased on two branches. Override deliberately with
`PREPUSH_ALLOW_HISTORY=1 git push ...`.

## Patch series (`upstream/main..build/custom`)

| # | commit | what / why | upstreamable? |
| --- | --- | --- | --- |
| 1 | Feature: Use a dynamic STA worker pool | Replaces per-call STA `Thread` creation with a bounded, self-sizing pool (`STATaskPool`, `STATaskWorkItem`) that reuses STA workers and retires idle ones; the public `STATask.Run` overloads keep upstream's signatures. | The pool alone could be proposed upstream. |
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


## CI

`.github/workflows/fork-build.yml` is fork-owned and purely additive (never edit upstream's
workflow files). It builds a signed `.msixbundle`, uploads it as an artifact, and publishes a
GitHub release when a `v*` tag is pushed. It runs on `windows-2025-vs2026` (the VS 18 image) and
uses the same msbuild recipe as upstream's CD pipelines:

    msbuild src/Files.App/Files.App.csproj -t:Build `
      -p:Configuration=Release -p:Platform=x64 `
      -p:AppxBundle=Always -p:AppxBundlePlatforms=x64 `
      -p:GenerateAppxPackageOnBuild=true -p:UapAppxPackageBuildMode=SideloadOnly `
      -p:AppxPackageDir=artifacts\AppxPackages\ `
      -p:AppxPackageSigningEnabled=true `
      -p:PackageCertificateKeyFile=<pfx> -p:PackageCertificatePassword=<from env>

### One-time setup: get the signing certificate into the runner

The package publisher must equal the signing certificate subject exactly, and the certificate must
be exportable together with its private key.

1. Find the certificate this fork signs with:

       Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -like '*Nullsoft*' |
         Format-List Subject, Thumbprint, NotAfter, HasPrivateKey

2. If there is none, create a self-signed one whose subject matches `ForkAppxPublisher` in
   `fork/Fork.props`:

       $subject = "CN=Null's FilesDev Fork, O=Nullsoft, C=CN"
       New-SelfSignedCertificate -Type Custom -Subject $subject `
         -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
         -FriendlyName 'Files fork sideload' -CertStoreLocation Cert:\CurrentUser\My `
         -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

3. Export it including the private key:

       $pw = ConvertTo-SecureString -String '<password>' -Force -AsPlainText
       Export-PfxCertificate -Cert Cert:\CurrentUser\My\<thumbprint> -FilePath fork-signing.pfx -Password $pw

4. Base64 the `.pfx` and store both values as repository secrets
   (Settings -> Secrets and variables -> Actions):

       [Convert]::ToBase64String([IO.File]::ReadAllBytes('fork-signing.pfx')) | Set-Clipboard
       # secret   FORK_PFX_BASE64
       # secret   FORK_PFX_PASSWORD
       # variable FORK_APPX_PUBLISHER   (only if the subject differs from fork/Fork.props)

   Keep the `.pfx` out of the repository (it is gitignored anyway): a leaked signing key is permanent.

5. A machine that installs the package has to trust the certificate once:

       Export-Certificate -Cert Cert:\CurrentUser\My\<thumbprint> -FilePath fork-signing.cer
       Import-Certificate -FilePath fork-signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople

### Using it

    git tag v4.2.39.1 && git push origin v4.2.39.1   # build + sign + release

After an upstream sync, dispatch `Fork build` by hand (or push a `v*` tag) to get a package
artifact as verification — pushing `build/custom` does not trigger the workflow. Without the
secrets the workflow warns and produces an unsigned package instead of failing.


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
