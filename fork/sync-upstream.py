#!/usr/bin/env python3
"""Sync this fork with upstream.

Mirrors `main` to `upstream/main`, rebases the patch series on `fork/main` on top of it, and
reports what the rebase changed (so a lost or mangled resolution is visible immediately).

    python fork/sync-upstream.py           # fetch + mirror + rebase + range-diff (no push)
    python fork/sync-upstream.py --push    # ... then force-push fork/main

`git rerere` is enabled in this repo, so a conflict that was resolved once is replayed
automatically.  Never `git checkout --ours/--theirs` a whole file: it silently drops the other
side's non-conflicting changes.  Resolve per hunk, then re-run with the script or continue the
rebase by hand.
"""
import subprocess
import sys

UPSTREAM = "upstream"
MIRROR = "main"
WORK = "fork/main"


def git(*args, check=True):
    """Run a git command, echoing it, and return its exit code."""
    print("$ git " + " ".join(args), flush=True)
    proc = subprocess.run(["git", *args])
    if check and proc.returncode:
        sys.exit(proc.returncode)
    return proc.returncode


def out(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True).stdout.strip()


def main():
    push = "--push" in sys.argv[1:]

    git("fetch", UPSTREAM, "--prune")
    before_base = out("rev-parse", f"{UPSTREAM}/{MIRROR}")
    before_tip = out("rev-parse", WORK)

    print(f"\n==> mirror: {MIRROR} -> {UPSTREAM}/{MIRROR} ({before_base[:9]})")
    git("switch", MIRROR)
    git("merge", "--ff-only", f"{UPSTREAM}/{MIRROR}")

    print(f"\n==> rebase: {WORK} -> {UPSTREAM}/{MIRROR}")
    git("switch", WORK)
    if git("rebase", f"{UPSTREAM}/{MIRROR}", check=False):
        print(
            "\nThe rebase stopped on a conflict.  Resolve each file hunk by hunk (keep your\n"
            "intent, take upstream where it already did the same thing), `git add` it, then:\n"
            "\n    git rebase --continue      # or: git rebase --abort\n"
            "\nrerere will replay any resolution it has seen before.\n"
        )
        return 1

    print("\n==> what the rebase changed (should be upstream adaptation only):")
    git("range-diff", f"{before_base}..{before_tip}", f"{UPSTREAM}/{MIRROR}..{WORK}", check=False)

    print("\n==> patch series now:")
    print(out("log", "--oneline", f"{UPSTREAM}/{MIRROR}..{WORK}"))

    print(
        "\nNext: build (msbuild -restore Files.slnx -p:Configuration=Debug -p:Platform=x64 "
        "-v:quiet -clp:ErrorsOnly; see FORK.md for the VS DevShell setup)."
    )
    if push:
        print(f"\n==> pushing {WORK}")
        git("push", "--force-with-lease", "origin", WORK)
    else:
        print(f"Then: git push --force-with-lease origin {WORK}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
