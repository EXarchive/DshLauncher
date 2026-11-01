# Rescue DSH

A snapshot of the harness that boots and repairs data **without the machine's
main DSH install**.

## Why it exists

The launcher's whole job is repairing DSH, so it must not depend on the DSH
install it repairs. If any of these is corrupted or half-upgraded:

- `D:\Program Files\nodejs\node_global\node_modules\@deepseek-ai\dsh` (the CLI)
- the shared `@deepseek-ai` core layer that a profile resolves through
  `%DSH_HOME%\profiles\node_modules`

...then `dsh --profile web` is exactly the thing that will not start, and a
repair tool built on top of it is useless. The rescue is a **copy** of the
harness under `rescue\`, run directly by `node.exe`. It reads nothing from the
main install and never imports from it.

## Commands

```
pwsh -File tools\rescue-dsh.ps1 create                 # snapshot (+ manifest)
pwsh -File tools\rescue-dsh.ps1 status                 # what is in the snapshot
pwsh -File tools\rescue-dsh.ps1 verify                 # check it, then boot it
pwsh -File tools\rescue-dsh.ps1 run "your task here"   # answer one task
pwsh -File tools\rescue-dsh.ps1 repair                 # scan + repair sessions
pwsh -File tools\rescue-dsh.ps1 repair -DryRun         # report only, no writes
```

Useful switches (all commands):

| Switch | Meaning |
| --- | --- |
| `-DryRun` | Zero side effects. `create` prints the plan; `repair` asks `session-manager.mjs` to compute without writing. |
| `-DshHome <path>` | The harness home to read config from and to repair sessions in. Defaults to `$env:DSH_HOME`, then `%USERPROFILE%\.dsh`. |
| `-NodeExe <path>` | Absolute `node.exe` path. Defaults to `D:\Program Files\nodejs\node.exe`. PATH is never used. |

**Do not write `run -- "task"`.** Under `pwsh -File`, PowerShell itself consumes
the bare `--` and the script fails parameter binding. Quote the task instead:
`run "回答一个字：好"`. A `--` separator is accepted and ignored when the script
is called through `&` or `pwsh -Command`.

Typical rescue session:

```
pwsh -File tools\rescue-dsh.ps1 status      # is the rescue there and usable?
pwsh -File tools\rescue-dsh.ps1 verify      # does it still boot?
pwsh -File tools\rescue-dsh.ps1 repair -DryRun   # how bad are the logs?
pwsh -File tools\rescue-dsh.ps1 repair      # truncate the torn trailers
pwsh -File tools\rescue-dsh.ps1 run "read C:\Users\Administrator\.dsh\logs and find why web fails"
```

## Layout, and why it is this layout

```
rescue\
  dsh\dsh\             copy of the @deepseek-ai\dsh CLI package (no node_modules)
  dsh\node_modules\    the dependency closure: 484 packages, incl. node_modules\@deepseek-ai\*
  profile\             the rescue profile (package.json + cordis.patch.yml)
  home\                an ISOLATED $DSH_HOME used only by the rescue
  home\profiles\rescue junction -> rescue\profile
  tools\               copies of dsh-session-log.mjs + session-manager.mjs
  manifest.json        what was snapshotted, sizes, versions
```

Two resolution facts decide this shape. Both were verified against the shipped
loader source and then by an actual boot:

1. **Bundles resolve from the running CLI's own anchor first.**
   `dsh-app-boot`'s `resolveBundleDir` tries the install anchor (the running
   package's `package.json`) before the profile directory. Node's lookup from
   `rescue\dsh\dsh\package.json` is `rescue\dsh\dsh\node_modules` then
   `rescue\dsh\node_modules` — so the core layer sits one level up, exactly at
   `rescue\dsh\node_modules`, and the CLI package does **not** carry a second
   copy of it. `rescue\dsh\dsh` is copied with `robocopy /MIR /XD node_modules`
   for that reason: a second 213 MB copy would buy nothing.

2. **The dependency closure must be a direct child of the package root.**
   A package at `rescue\dsh\node_modules\<pkg>` resolves its siblings through
   Node's parent walk, which *skips* a directory literally named
   `node_modules` and then offers `rescue\dsh\node_modules` — itself. So a flat
   closure at `rescue\dsh\node_modules` is internally consistent. Copying the
   closure to some other depth (for example `rescue\node_modules`) would break
   inter-package resolution.

3. **A profile can only be named, never pathed.** `dsh-app-boot`'s
   `resolveProfileDir` rejects any profile name containing `/` or `\`, and looks
   the name up at `<$DSH_HOME>\profiles\<name>`. So `rescue\profile` cannot be
   booted directly; the rescue home carries a **directory junction**
   `rescue\home\profiles\rescue -> rescue\profile`. A junction needs no
   privilege on Windows, keeps `rescue\profile` as the single physical copy,
   and `create`/`verify` re-create it if it is missing, so the rescue survives
   being copied to another folder.

DSH also maintains its own module fallback at `<rescue home>\profiles\node_modules`
(483 junctions: 482 into `rescue\dsh\node_modules\*`, 1 for the CLI package
itself at `rescue\dsh\dsh` — all 483 point inside `rescue\`, none at the machine
install). That is the harness rebuilding the resolution it needs from *the
snapshot*, which is the self-containment this whole design is for. Those links
are absolute and are re-healed automatically at the next boot if the rescue
folder moves.

## What it does NOT do

- **No web UI.** The rescue profile is `@deepseek-ai/dsh-base` +
  `@deepseek-ai/dsh-headless` only: one task, printed answer, exit. It never
  binds a port and never touches 3080 or the debugging ports.
- **No plugin hot reload.** `patchReload` is `startup`; no file watcher runs.
- **No supervision.** Leases, watchdogs and the supervised cluster stay the
  launcher's job. A rescue that supervised would depend on the supervision state
  it may be repairing.
- **It is not a backup of your data.** It snapshots *code*, not sessions,
  storages or workspaces. `repair` operates **in place on the real harness
  home**; the only thing the rescue takes from that home is a copy of
  `settings.yaml` and `.credentials.yaml`, so the rescue agent has a model route
  and an API key. The rescue home therefore contains a copy of that key.
- **It goes stale.** The snapshot is a copy. After a DSH upgrade the rescue is
  still the old version — re-run `create` to refresh it.
- **It does not fix node itself.** It shells out to an absolute `node.exe`; a
  broken Node runtime is out of scope.
- **It only repairs session logs.** Whatever else is broken (settings, storage
  files, the install) is reported by the rescue agent, not repaired by this
  script.

## How it fails safe

- **Reads, never writes, the patient.** Nothing under `%DSH_HOME%\profiles` is
  ever written to. Nothing under the machine's `node_global` install is ever
  written to. The only writes outside `rescue\` are `session-manager.mjs`
  repairing session logs, which backs the original up beside it
  (`session.jsonl.zstd.corrupt-<stamp>`) instead of deleting it.
- **Isolated home.** The rescue boots with `DSH_HOME=<rescue>\home`, so its own
  throwaway sessions, storages and caches never land in the real home.
- **Loud failures.** Every missing prerequisite (node, a snapshot tree, the CLI
  entry point, a bundle in the core layer, node_modules resolution) prints a
  named, actionable message and exits non-zero. `status` exits 2 when the
  snapshot is not usable.
- **`-DryRun` provably changes nothing.** `create -DryRun` measures and reports;
  `repair -DryRun` runs `scan` (read-only) and passes `--dry-run` to the repair
  step, which only counts.
- **No process is hijacked.** The only processes started are short-lived node
  boot tests and one-shot headless tasks started by this script, and it waits
  for each to exit.

## Note on `--select damaged`

`session-manager.mjs` has no `damaged` selector (its selectors are
`subagent|probe|imported|system|unlisted|archived|empty|all`), so `repair`
derives the damaged set from `scan` and passes it as explicit `--id` values. If a
`damaged` selector is ever added, the script detects it and switches to
`repair --select damaged` automatically.
