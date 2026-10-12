# HyperVSandboxes

Disposable sandboxes on a single Hyper-V host — **Windows Server / Windows 11**
and **Debian 13** — controlled by an agent over **MCP** or by a human over a
**web dashboard with a real SSH terminal**.

A local Windows service (`LocalSystem`) that:

- **offline-provisions** guests into VHDX files on the host (no ISO boot
  prompts, no interactive setup). Windows: `dism /Apply-Image` + `bcdboot` +
  an auto-OOBE unattend. Linux: a golden Debian cloud image + a per-VM
  differencing disk + a cloud-init `cidata` seed disk. Children boot in ~20 s
  from the golden cache, or ~5–10 min on a cold golden build;
- runs commands **inside the guest** — PowerShell Direct (VMBus) for Windows,
  SSH for Linux — and tells the truth about it: `output`, `stderr`, and the
  guest's real `exit` code;
- proves *readiness*, not just power: heartbeat `Ok` does not mean the desktop
  is usable. Windows readiness is `C:\Sandbox\ready.txt` written on the
  desktop session; Linux readiness is `/var/lib/sandbox-ready` proven over
  SSH. `wait_ready` blocks until the guest is actually usable;
- gives humans a **real interactive terminal in the browser** for Linux
  sandboxes (xterm.js ⇄ WebSocket ⇄ `ssh -tt` ⇄ bash) — the dashboard adapts
  to the selected VM's OS: Debian shows the SSH terminal, Windows shows
  PowerShell Direct, never both;
- tags every VM it creates (`mcp-*` + notes tag) so `delete_vm`/`purge_all`
  can only ever touch its own sandboxes;
- exposes the whole thing twice:
  - **MCP streamable-HTTP** at `http://127.0.0.1:5003/mcp` for agents
    (`create_vm`, `run_ps`, `wait_ready`, `delete_vm`, `purge_all`, …)
  - a dark-mode **dashboard** at `http://127.0.0.1:5003/dashboard.html` for
    humans, backed by the same code path (`/api/*`).

Zero visible console windows — every PowerShell/dism/bcdboot/ssh child is
spawned hidden (`CREATE_NO_WINDOW`). No UAC prompts, no scheduled-task
elevation: the service *is* the privileged component.

## Why

Hyper-V's normal bootstrap is a keypress prompt ("Press any key to boot from
CD…") that no client-Windows Hyper-V build lets you press programmatically,
and scripting the whole stack from user-level PowerShell means either UAC
prompts or a window storm. This service inverts it: the host builds a fully
bootable disk offline, the guest only ever sees an unattended first boot, and
the privileged surface is one small, auditable C# service instead of an army
of elevated shell children.

## Requirements

- Windows 10/11 Pro+ or Server with the **Microsoft-Hyper-V** role (reboot
  once after enabling),
- .NET 10 runtime (ASP.NET Core runtime for the web bits),
- media under `<root>\Media` (default root `C:\HyperV`):
  - **Server 2025 eval ISO** —
    <https://www.microsoft.com/en-us/evalcenter/download-windows-server-2025>
  - **Windows 11 Enterprise eval ISO** (optional, catalog `win11ent-26h2`)
  - **Debian 13 generic cloud image** (optional, catalog `debian-13`) —
    `debian-13-genericcloud-amd64.raw` from
    <https://cloud.debian.org/images/cloud/> (SHA-512-verified).

## Quick start

```powershell
# build + publish
dotnet publish src/HermesHyperV -c Release -r win-x64 --self-contained false -o publish

# install as a Windows service (from an elevated shell)
sc.exe create HermesHyperV binPath= "C:\path\to\publish\HermesHyperV.exe" start= auto
sc.exe description HermesHyperV "LocalSystem Hyper-V sandbox: MCP server on /mcp + dashboard REST"
sc.exe failure HermesHyperV reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start HermesHyperV
```

Then:

- humans: `http://127.0.0.1:5003/dashboard.html`
- agents: add MCP server `http://127.0.0.1:5003/mcp` (streamable HTTP)

## Tools (MCP)

| tool | what it does |
|---|---|
| `sandbox_status` | VMMS / hypervisor / media / free-disk readiness |
| `list_vms`, `vm_status` | tagged VMs with state, **heartbeat**, IP, phase, guest login |
| `list_editions` | catalog entries (SKUs, media present) |
| `create_vm` | provision + create + start a throwaway Gen-2 VM (golden cache hit ~20 s) |
| `run_ps` | one-shot command in the guest — PS Direct (Windows) or SSH/bash (Linux); returns output/stderr/exit |
| `wait_ready` | block until the guest is *actually* usable (desktop marker / SSH marker), not just powered |
| `start_vm`, `stop_vm` | power |
| `delete_vm` | destroy one sandbox (VM + VHDXs + folder) — tag-guarded |
| `purge_all` | destroy every tagged sandbox on the host |

Guest accounts: Windows `Sandbox`, Linux `sandbox`. Passwords default to a
generated `guest.key`, or are settable per-VM (`password` on `create_vm` /
`POST /api/create`), are shown on the dashboard card (click-to-copy), and are
written to `creds.json` next to the VM — nothing credential-shaped is ever
stored in the repo or in logs.

## Agents: installing software in a sandbox

The intended loop is `create_vm` → `wait_ready` → `run_ps` (repeat) →
`delete_vm`. Permissions are not the limiter: the Linux `sandbox` user has
`sudo` NOPASSWD (root), and `run_ps` returns stderr and the real exit code so
agents see failures instead of silence.

Network installs (`apt`, `pip`, `curl`, `wget`, `npm`) **work** — see "Guest
egress" below. Verified end-to-end: `sudo apt-get install -y hello` prints
`Hello, world!`; `pip3 install requests` then
`requests.get("https://pypi.org/simple/")` returns 200 from inside the VM.
Windows guests have every in-box tool available offline; for online installs
on Windows, set `http_proxy=http://192.168.50.1:8899` — the same proxy
serves them.

## Guest egress (how isolated VMs get `apt`/`pip`)

Linux guests live on an **Internal** Hyper-V switch (`SandboxLAN`,
`192.168.50.0/24`, static IPs, no DHCP, no NAT) — deliberately un-routable.
The one controlled door out is a tiny **HTTP/CONNECT proxy**
(`Engine/SandboxProxy.cs`) that:

- binds **only** to the host's SandboxLAN vNIC (`192.168.50.1:8899`) — the
  physical LAN can't use it as an open proxy;
- installs its own scoped Windows Firewall allow rule at service start
  (inbound TCP 8899 from `192.168.50.0/24` only — the service is LocalSystem,
  so no elevation dance);
- is preconfigured inside every Linux guest by the cloud-init seed: apt
  `http(s)_proxy` config, `/etc/profile.d/sandbox-proxy.sh`, `/etc/pip.conf`
  — so `apt-get install` in the SSH terminal or an exec session just works
  (`run_ps` sessions get the proxy env injected by the SSH wrapper too).

This is a convenience door for disposable sandboxes, not a security boundary.
Tighten it (host allowlist, drop plain-HTTP forwarding) if your threat model
needs that.

## Dashboard

- VM cards (state, phase, IP, uptime, guest login/password click-to-copy);
- clicking a card opens the **adaptive guest console**: live SSH terminal for
  Ready Debian (auto-connects), PowerShell Direct one-shot for Ready Windows,
  locked with the honest reason while installing;
- create-VM form (OS picker from the catalog, name, RAM, CPUs, password),
  job queue, per-step log, start/stop/delete/purge.

## Configuration (env vars on the service)

| var | default | meaning |
|---|---|---|
| `HYPERV_ROOT` | `C:\HyperV` | VMs under `<root>\VMs`, media under `<root>\Media` |
| `HYPERV_SVC_PORT` | `5003` | HTTP port (loopback only) |
| `HYPERV_GUEST_PASSWORD` | *(generated)* | fixed guest password, else `guest.key` is generated once |

## How it works

```
Program.cs            WebApplication + UseWindowsService + MCP on /mcp
Tools/                [McpServerTool] surface (same names as the REST API)
Engine/SandboxEngine  product logic: golden cache, differencing children,
                      lifecycle, guest exec, readiness probes + cache
Engine/JobStore       background jobs + activity log (dashboard polls these)
Engine/SandboxProxy   guests' egress door: HTTP/CONNECT on 192.168.50.1:8899
Api/TerminalWs.cs     /ws/term — browser xterm.js ⇄ hidden ssh -tt (PTY)
Engine/PowerShellRunner  ONE hidden powershell.exe child per call; embedded
                      .ps1 read request JSON from env HV_REQ (base64), answer
                      one compact JSON on stdout: {ok, data, error}
ps1/*.ps1             the only PowerShell left, because Hyper-V cmdlets,
                      storage cmdlets and PowerShell Direct have no in-proc
                      equivalent on client Windows:
                        provision_disk.ps1   VHDX -> bootable Windows disk
                        golden_image.ps1     golden VHDX builder (per Windows SKU)
                        child_disk.ps1       differencing child + unattend
                        vm_lifecycle.ps1     create/power/delete/status +
                                             Linux seed (cloud-init cidata)
                        linux_golden.ps1     Debian raw -> fixed VHDX golden
                        linux_ssh.ps1        ssh key bootstrap + exec (exit codes!)
                        guest_exec.ps1       Invoke-Command -VMName (PS Direct)
wwwroot/              dashboard + vendored xterm.js
```

Linux specifics:

- `New-VHD` fixed VHDX golden from the signed raw image (block-copied offline
  via `PHYSICALDRIVE` — Gen-2 refuses `.vhd`, so no dynamic/VHD formats);
- per-VM differencing child + a second SCSI disk: the `cidata` seed
  (FAT32 VHDX with cloud-init `meta-data`/`user-data`/`network-config`);
- netplan matches the NIC **by driver name** (`match: name: 'en*'`), not MAC
  — Hyper-V's MAC pool only commits at first start, so MAC-match seeds
  silently fail;
- static IP `192.168.50.x` assigned by the service, host at `.1`;
- COM1 is redirected to a named pipe (`\\.\pipe\mcp-<vm>com1`) — boot-log
  forensics with zero windows.

## Security posture

- binds `127.0.0.1` only;
- `delete`/`purge` refuse anything not tagged as this service's sandbox;
- the egress proxy binds only to the sandbox vNIC and is firewall-scoped to
  the sandbox subnet;
- guest passwords live in `creds.json` next to the VM (the dashboard shows
  them — the dashboard is local-admin surface anyway) and in the per-VM
  cloud-init seed, never in the repo;
- the service runs as LocalSystem because Hyper-V administration is the point;
  treat the HTTP surface as local-admin-equivalent and do not port-forward it.

## Not included

- No ISO or cloud image is shipped; only Microsoft eval center and
  `cloud.debian.org` are referenced.
- `pip`/`python3-pip` are not baked into the Debian golden (one
  `apt-get install python3-pip` away, through the proxy).
- No snapshot/revert — delete + recreate is the reset story (golden cache
  makes it ~20 s).
