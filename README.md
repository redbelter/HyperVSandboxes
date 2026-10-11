# HyperVSandboxes

Disposable Windows VMs on a single Hyper-V host, controlled by an agent over
**MCP** or a human over a **web dashboard**.

A local Windows service (`LocalSystem`) that:

- **offline-provisions** Windows into a VHDX on the host (no ISO boot prompts,
  no interactive setup — `dism /Apply-Image` + `bcdboot` + an auto-OOBE
  unattend), then boots the VM straight to a ready desktop in ~2–10 minutes;
- runs commands **inside the guest over PowerShell Direct** (VMBus — works
  even with no network and no domain);
- tags every VM it creates (`mcp-*` + notes tag) so `delete_vm`/`purge_all`
  can only ever touch its own sandboxes;
- exposes the whole thing twice:
  - **MCP streamable-HTTP** at `http://127.0.0.1:5003/mcp` for agents
    (`create_vm`, `run_ps`, `delete_vm`, `purge_all`, …)
  - a dark-mode **dashboard** at `http://127.0.0.1:5003/dashboard.html` for
    humans, backed by the same code path (`/api/*`).

Zero visible console windows — every PowerShell/dism/bcdboot child is spawned
hidden (`CREATE_NO_WINDOW`). No UAC prompts, no scheduled-task elevation: the
service *is* the privileged component.

## Why

Hyper-V's normal bootstrap is a keypress prompt ("Press any key to boot from
CD…") that no client-Windows Hyper-V build lets you press programmatically,
and scripting the whole stack from user-level PowerShell means either UAC
prompts or a window storm. This service inverts it: the host builds a fully
bootable VHDX offline, the guest only ever sees an unattended OOBE, and the
privileged surface is one small, auditable C# service instead of an army of
elevated shell children.

## Requirements

- Windows 10/11 Pro+ or Server with the **Microsoft-Hyper-V** role (reboot
  once after enabling),
- .NET 10 runtime (ASP.NET Core runtime for the web bits),
- a Windows eval ISO in `<root>\Media` (default root `C:\HyperV`). The built-in
  catalog entry targets the Server 2025 evaluation ISO from
  <https://www.microsoft.com/en-us/evalcenter/download-windows-server-2025>.

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
| `sandbox_status` | VMMS / hypervisor / ISO / free-disk readiness |
| `list_vms`, `vm_status` | tagged VMs with state, **heartbeat**, IPs |
| `list_editions` | editions inside an ISO |
| `create_vm` | offline-provision + create + start a throwaway Gen-2 VM (blocks ~2–10 min) |
| `run_ps` | PowerShell inside the guest via PowerShell Direct |
| `start_vm`, `stop_vm` | power |
| `delete_vm` | destroy one sandbox (VM + VHDX + folder) — tag-guarded |
| `purge_all` | destroy every tagged sandbox on the host |

Guest account is `Sandbox`; its password comes from `HYPERV_GUEST_PASSWORD`,
or from a generated `guest.key` file, and is echoed by `create_vm`.

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
Engine/SandboxEngine  product logic: provisioning, lifecycle, guest exec
Engine/JobStore       background jobs + activity log (dashboard polls these)
Engine/PowerShellRunner  ONE hidden powershell.exe child; embedded .ps1 read
                         request JSON from env HV_REQ (base64), answer one
                         compact JSON on stdout: {ok, data, error}
ps1/*.ps1             the only PowerShell left, because Hyper-V cmdlets,
                      storage cmdlets and PowerShell Direct have no in-proc
                      equivalent on client Windows:
                        provision_disk.ps1   VHDX -> bootable Windows disk
                        vm_lifecycle.ps1     create/power/delete/purge/status
                        guest_exec.ps1       Invoke-Command -VMName (PS Direct)
wwwroot/              the dashboard (plain HTML+JS, polls /api/*)
```

VM readiness is judged by the **Hyper-V heartbeat**, not IP addresses — the
sandbox switch is Internal, so guests only ever get APIPA (`169.254.x.x`)
addresses, which say nothing about install state.

## Security posture

- binds `127.0.0.1` only;
- `delete`/`purge` refuse anything not tagged as this service's sandbox;
- the guest password is never stored in the repo or in logs beyond the
  `create_vm` result echo (intentional: the caller created the machine);
- the service runs as LocalSystem because Hyper-V administration is the point;
  treat the HTTP surface as local-admin-equivalent and do not port-forward it.

## Not included

- No ISO is shipped or referenced by URL beyond Microsoft's public eval center.
- The ISO filename in `IsoCatalog` is just a name-matching hint; drop any
  Windows ISO into `<root>\Media` and pass its path/edition explicitly.
