# ky-ai-terminal

A shell **you** drive normally while an AI agent joins the **same live session** over MCP. You
keep your terminal, your control, and your credentials; the agent gets exactly the visibility and
power you grant it (read-only / suggest / auto).

It works like the other KY.AI tools — [`ky-ai-ng`](../Ng/README.md) /
[`ky-ai-dotnet`](../Net/README.md): you run it, the agent connects over MCP (port **5103**), and
everything stays on your machine.

---

# For Humans

## Why

Getting AI help with an interactive shell task (configuring Docker over SSH, debugging a box)
otherwise forces a bad trade-off: copy/paste commands and output by hand, or hand a coding agent
your SSH keys and lose all control. This is the third option — the agent rides along in your
already-authenticated session, you see every keystroke it sends, and it can do only what the mode
allows.

## Usage

```
ky-ai-terminal       [--shell pwsh|cmd|powershell|bash|ssh|<exe>] [--mode read|suggest|auto]
                     [--name <id>] [--scrollback N] [--prefix <chord>] [--audit <file>]
                     [--no-tui] [--hub-port N] [--no-hub] [-- <shell args>]
```

Just run `ky-ai-terminal` (the `run` word is optional) — it prints a short welcome and starts a
session, auto-starting a hub if none is up. So usually you just:

```
ky-ai-terminal                                       # start a session in the default shell
ky-ai-terminal --shell ssh -- user@host              # then authenticate yourself
ky-ai-terminal shutdown                              # stop the hub + every session it supervises
ky-ai-terminal init [-y] [--dir <path>]              # wire it into your agent
```

Defaults: shell auto-detected (`pwsh` → `powershell` → `cmd`), mode **read**, scrollback **5000**
lines, approve chord prefix **Ctrl-B**. `--no-tui` falls back to the plain transparent passthrough
(no composer/status bar). `--audit <file>` mirrors the in-memory injection log to disk.

## Modes

| Mode | Agent can… |
|---|---|
| `read` (default) | read the screen and scrollback only |
| `suggest` | also `propose_command` — staged in your terminal; **you** run it with the chord |
| `auto` | also `send_text` / `send_keys` directly (only when the shell is idle at a prompt) |

Switch modes with **Shift+Tab**. Approve a staged proposal with **Ctrl+E**, dismiss with **Esc**.
Mode gating is enforced **server-side**,
so a misbehaving agent can't bypass it, and `approve`/`dismiss` are deliberately **not** MCP tools —
only you (via the chord) approve.

## Interface (TUI)

A Claude-CLI-style layout: shell output scrolls in the top region, a persistent **input-line
composer** sits at the bottom (type a command, **Enter** sends it to the shell), a **status bar**
shows the mode below it, and agent suggestions pop out just above the input line. Full-screen apps
(vim/htop) and password prompts **auto-switch to raw passthrough** and back.

## Connect your agent

**`ky-ai-terminal init`** does the MCP wiring for Claude Code automatically: it walks up to the
nearest `.mcp.json` and `.claude/`, adds the server, and allows the `mcp__ky-ai-terminal__*`
commands (idempotent; `-y` skips the prompts). To do it by hand, add the server and allow the tools:

```json
{ "mcpServers": { "ky-ai-terminal": { "type": "http", "url": "http://127.0.0.1:5103/mcp" } } }
```

## Security

- **Credentials never reach the agent** — you run `ssh`/`sudo`; the agent only drives the shell.
- **Password-prompt guard** — while a password/passphrase prompt is detected, agent input is refused
  (best-effort detection from prompt text; with echo off the secret never enters the output stream
  the agent can read).
- **Audit log** — every agent injection is recorded (in memory; `--audit <file>` to mirror to disk).
- **Loopback-only** — all HTTP binds `127.0.0.1`. The control API is hosted even with `--no-hub`
  (loopback only); `--no-hub` just skips hub registration.

---

# For Agents

Server: `http://127.0.0.1:5103/mcp`. Every tool except `list` takes a `session` name (from `list`).
Allow-list each as `mcp__ky-ai-terminal__<name>`. **Mode gating is enforced server-side** — write
tools return `ok:false` when the mode (or idle state) doesn't allow them.

| Tool | Args | Purpose |
|---|---|---|
| `list` | — | terminal sessions registered with the hub, each with shell, mode and status. **Call first.** |
| `status` | `session?` | one session (or all if omitted): name, shell, pid, running, mode, screen size, cursor position, scrollback size |
| `read_screen` | `session` | the current visible character grid + cursor — use for full-screen / TUI output (editors, pagers, REPLs) |
| `read_scrollback` | `session`, `lines?` | last N lines of scrollback transcript (`0` = everything kept) — use for ordinary command output that scrolled off screen |
| `get_mode` | `session` | the permission mode (read/suggest/auto), whether the shell is idle, and any pending proposal |
| `set_mode` | `session`, `mode` | set the mode: `read` \| `suggest` \| `auto` |
| `propose_command` | `session`, `text` | **(suggest mode)** stage a command as a banner for the human to approve via the chord; returns the proposal id. You cannot approve your own proposal |
| `send_text` | `session`, `text` | **(auto mode)** type literal text at the prompt — does **not** press Enter (follow with `send_keys "Enter"`). Idle-gated: waits briefly then returns `ok:false` if the shell is busy or the human is typing |
| `send_keys` | `session`, `keys` | **(auto mode)** named keys: `Enter`, `Tab`, `Esc`, arrows, `Home/End`, `Backspace`, `Ctrl-C`, `Ctrl-D`, or a sequence like `"Up Up Enter"`. Not idle-gated, so it can interrupt a running command or drive a TUI |
| `resize` | `session`, `cols`, `rows` | advisory screen resize; a console-attached session may re-sync to the host window size |

**Reading output:** use `read_screen` for full-screen/TUI state (the rendered grid), `read_scrollback`
for line-oriented command output that has scrolled past the visible screen.

---

## Implementation notes / current limitations

- **Pseudoconsole** via Windows ConPTY (`ConPty/*`); requires Windows 10 1809+. The shell tree is
  held in a Job Object so it dies with the tool however it's stopped.
- **Screen model** (`VtScreen`) is a self-contained, minimal VT parser — exact for line-oriented
  shells, approximate for complex full-screen TUIs. It sits behind a small API so a fuller engine
  (e.g. VtNetCore) could replace it later.
- **Approve chord** defaults to a tmux-style `Ctrl-B` prefix, which is reliable across conhost and
  Windows Terminal. The composer also maps **Shift+Tab → mode-cycle** (CSI Z, via the win32-input
  decoder). True `Ctrl+Enter` needs win32-input-mode and is not yet wired (`--keys`, deferred).
- **Idle detection** uses output-quiet + human-idle timers (heuristic). OSC 133 shell-integration
  markers — which would make "at the prompt" and exit codes exact — are not yet parsed.
