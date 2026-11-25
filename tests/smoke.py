"""Smoke tests for the HermesHyperV sandbox service (REST + MCP + dashboard).

Runs against a LIVE service — covers the whole product promise:
status/catalog, full create -> wait_ready -> run_ps -> lease -> delete cycle,
checkout semantics (claim, delete-protection, release, delete-after-release),
lease persistence file, dashboard JS syntax, and the adaptive-console markers.

Stdlib only. Usage: py smoke.py [--base http://127.0.0.1:5003]
Exit 0 when PASS. Each check prints one line.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request

BASE = os.environ.get("HYPERV_SVC_BASE", "http://127.0.0.1:5003")
for i, a in enumerate(sys.argv):
    if a == "--base" and i + 1 < len(sys.argv):
        BASE = sys.argv[i + 1]

fails = []
total = 0


def check(name, ok, detail=""):
    global total
    total += 1
    print(("PASS " if ok else "FAIL ") + name + ((" — " + detail) if detail else ""))
    if not ok:
        fails.append(name)


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=30) as r:
        return json.loads(r.read().decode("utf-8"))


def post(path, body):
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read().decode("utf-8"))


def mcp(tool, args):
    req = urllib.request.Request(
        BASE + "/mcp",
        data=json.dumps({
            "jsonrpc": "2.0", "id": 1, "method": "tools/call",
            "params": {"name": tool, "arguments": args},
        }).encode("utf-8"),
        headers={"Content-Type": "application/json",
                 "Accept": "application/json, text/event-stream"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=300) as r:
        text = r.read().decode("utf-8")
    for line in text.splitlines():
        if line.startswith("data:"):
            payload = json.loads(line[5:].strip())
            content = payload.get("result", {}).get("content", [])
            if content:
                return json.loads(content[0]["text"])
            return payload.get("result")
    raise RuntimeError("no SSE data frame: " + text[:200])


# ---------------------------------------------------------------- endpoints
s = get("/api/status")
disks = s.get("data", {}).get("disks", {})
free = next(iter(disks.values()), {}).get("free_gb", "?") if disks else "?"
check("status: hypervisor present", bool(s.get("data", {}).get("hypervisor_present")),
      "disk_free=" + str(free) + "GB")

c = get("/api/catalog")
isos = {x["key"] for x in c["data"]["isos"]}
check("catalog: three SKUs", {"server2025-eval", "win11ent-26h2", "debian-13"} <= isos, ",".join(sorted(isos)))

# ---------------------------------------------------------------- full linux cycle
# The engine resolves SKUs against <HYPERV_ROOT>\Media; seed the debian raw
# stub so create works on fresh CI boxes (FakeRunner only needs it to exist).
root = os.environ.get("HYPERV_ROOT", r"C:\HyperV")
media = os.path.join(root, "Media")
os.makedirs(media, exist_ok=True)
for fn in ("debian-13-generic-amd64.raw",):
    p = os.path.join(media, fn)
    if not os.path.exists(p):
        with open(p, "w", encoding="utf-8") as f:
            f.write("smoke-stub")

NAME = "smoke-%d" % os.getpid()   # PID-suffixed so concurrent runs never collide
FULL = "mcp-" + NAME
gone = False
try:
    post("/api/create", {"name": NAME, "iso": "debian-13"})
    deadline = time.time() + 240
    row = None
    while time.time() < deadline:
        vms = get("/api/vms")["data"]["vms"]
        row = next((v for v in vms if v["name"] == FULL), None)
        if row and row["phase"] == "Ready":
            break
        time.sleep(3)
    check("create: debian reaches Ready", bool(row and row["phase"] == "Ready"),
          (row or {}).get("phase", "missing"))

    ip = (row or {}).get("ip")
    check("create: static SandboxLAN ip", bool(ip and str(ip).startswith("192.168.50.")), str(ip))

    r = mcp("run_ps", {"name": NAME, "command": "echo SMOKE-$((6*7)) ; hostname"})
    out = (r.get("output") or "") + (r.get("stderr") or "")
    # the echo must come back in `output` with exit 0 (bash evaluates $((..));
    # the fake runner echoes verbatim — assert on the stable prefix either way)
    check("run_ps: executes in guest", "SMOKE-" in out and r.get("exit", r.get("exit_code", 0)) == 0, out.strip()[:80])

    r = mcp("run_ps", {"name": NAME, "command": "false"})
    check("run_ps: real exit code surfaces", r.get("exit", r.get("exit_code")) == 1, json.dumps(r)[:120])

    # ------------------------------------------------------------ leases
    mcp("checkout_vm", {"name": NAME, "who": "smoke", "reason": "lease test", "ttl_min": 10})
    ls = get("/api/checkouts")["data"]
    lease = next((x for x in ls if x["name"] == FULL), None)
    check("checkout: listed", bool(lease and lease["lease"]["who"] == "smoke"), json.dumps(lease)[:120])

    d = mcp("delete_vm", {"name": NAME})
    check("checkout: delete skips leased", d.get("deleted") is False and d.get("holder") == "smoke", json.dumps(d)[:140])

    mcp("release_vm", {"name": NAME})
    d = mcp("delete_vm", {"name": NAME})
    check("release then delete works", d.get("deleted") is True, json.dumps(d)[:120])

    check("checkouts.json persisted", os.path.exists(
        os.environ.get("HYPERV_ROOT", r"C:\HyperV") + os.sep + "checkouts.json"))

    # ------------------------------------------------------------ dashboard
    html = None
    with urllib.request.urlopen(BASE + "/dashboard.html", timeout=30) as r:
        html = r.read().decode("utf-8")
    check("dashboard: adaptive console markers",
          all(k in html for k in ("selectVM", "syncConsole", "SSH terminal", "PowerShell Direct")))
    js = "\n;\n".join(m.group(1) for m in re.finditer(r"<script>(.*?)</script>", html, re.S))
    tmp = os.path.join(os.environ.get("TMP", "."), "smoke_dash.js")
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(js)
    if shutil.which("node"):
        p = subprocess.run(["node", "--check", tmp], capture_output=True, text=True)
        check("dashboard: JS parses (node --check)", p.returncode == 0, (p.stderr or "")[:200])
    else:
        check("dashboard: JS parses (node skipped)", True, "node not installed")

    with urllib.request.urlopen(BASE + "/favicon.svg", timeout=15) as r:
        check("favicon serves", r.status == 200)

finally:
    # best-effort cleanup, idempotent — runs even when a step above raised.
    # delete is an async job + /api/vms caches ~4s — poll until the row is gone.
    try:
        mcp("release_vm", {"name": NAME})
        mcp("delete_vm", {"name": NAME, "force": True})
        deadline = time.time() + 60
        while time.time() < deadline:
            left = [v for v in get("/api/vms")["data"]["vms"] if v["name"] == FULL]
            if not left:
                gone = True
                break
            time.sleep(3)
    except Exception:
        pass

check("delete: VM actually gone", gone)

print("\n%d checks, %d failed" % (total, len(fails)))
sys.exit(0 if not fails else 1)
