# KY.AI.Updater

One command to keep the whole [KY.AI](../../README.md) tool suite current. `ky-ai-updater` updates
**itself first**, then finds every other installed `ky-ai-*` tool and updates each one — so you don't
have to run `ky-ai-ng update`, `ky-ai-dotnet update`, `ky-ai-browser update`, … by hand.

```bash
dotnet tool install --global KY.AI.Updater
```

## Usage

```powershell
ky-ai-updater            # update ky-ai-updater itself, then every other installed ky-ai-* tool
ky-ai-updater --all      # update only the OTHER installed ky-ai-* tools (skip self)
ky-ai-updater list       # show which ky-ai-* tools are installed (and how)
```

Just run `ky-ai-updater` — that's the whole story.

## How it works

A running process can't replace its own files while it's running (a hard rule on Windows, and not
worth assuming elsewhere). So the update runs in **two phases**:

1. **Self-update.** The work is handed to a separate step that waits for the current process to exit,
   runs `dotnet tool update --global KY.AI.Updater --no-cache`, and then re-invokes the freshly
   installed `ky-ai-updater --all` for everything else. On **Windows** that step is a new console
   window (the file lock there is real); on **macOS/Linux** the binary is replaced in place, so it
   runs inline and you watch it happen.

2. **Update the rest.** `ky-ai-updater --all` discovers the installed `ky-ai-*` tools dynamically —
   from the dotnet global-tool store and from npm globals — and updates each by delegating to its
   **own `update` command**. That's deliberate: each tool already knows how it was installed (dotnet
   vs npm) and stops its own running instances first so the files aren't locked. Keeping that logic
   in the tool, not duplicated here, means a tool added to the suite later is picked up with no change
   to the updater.

> On Windows, each tool's own `update` schedules its real work in a new window and returns
> immediately, so you'll see a window per tool — let them finish.

`--no-cache` matters for the dotnet path: without it, `dotnet tool update` consults NuGet's local
HTTP cache and can report "already up to date" for a while after a new version ships. Disabling the
cache forces a fresh feed query and the real latest version.

## Notes

- Stand-alone by design: this tool references nothing else in the suite (not even `KY.AI.Serve`), so
  the one tool whose job is to replace the others carries no shared, lockable dependencies.
- It does no network or MCP work of its own — it only shells out to `dotnet` / `npm` and to the other
  tools' `update` commands. Nothing is exposed off the machine.

See the [project README](../../README.md) for the full suite.
