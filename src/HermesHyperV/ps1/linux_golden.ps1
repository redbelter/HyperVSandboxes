# Build a Linux golden from a raw cloud image WITHOUT qemu:
#   New-VHD fixed VHDX (Hyper-V lays out the VHDX format) -> mount -> take the
#   disk offline -> byte-copy the raw image onto \\.\PHYSICALDRIVE<n> -> online
#   -> mount read-only to verify the partition table parsed.
# Gen-2 VMs reject .vhd/conectix media outright, so we produce real VHDX.
# Request: { raw, vhd }
$ErrorActionPreference = 'Stop'
$global:__OUT = New-Object System.Collections.ArrayList
function emit { param($o) $global:__OUT.Add($o) | Out-Null }
$result = [ordered]@{ ok = $true; data = @(); error = $null }
try {
  $req = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:HV_REQ)) | ConvertFrom-Json
  $raw = [string]$req.raw; $vhd = [string]$req.vhd
  if (-not (Test-Path $raw)) { throw "missing raw image $raw" }
  $size = (Get-Item $raw).Length
  if ($size % 512 -ne 0) { throw "raw size $size not sector-aligned" }
  $dir = Split-Path $vhd; if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $tmp = $vhd + '.tmp.vhdx'
  foreach ($f in @($vhd, $tmp)) { if (Test-Path $f) { Remove-Item $f -Force } }

  # fixed VHDX sized exactly to the raw image
    New-VHD -Path $tmp -SizeBytes $size -Fixed | Out-Null
    if (-not (Test-Path $tmp)) { throw 'fixed vhdx not created' }

  # raw -> physical drive  (global mount lock: a concurrent mount elsewhere
  # would see THIS disk in its Get-Disk diff — see child_disk.ps1)
    $script:mtx = New-Object Threading.Mutex($false, 'Global\HermesHyperVMount')
    function Lock-Mount {
      try { if (-not $script:mtx.WaitOne(300000)) { throw 'mount lock timeout (5 min)' } }
      catch [Threading.AbandonedMutexException] { }
    }
    function Unlock-Mount { try { $script:mtx.ReleaseMutex() } catch {} }
    Lock-Mount
    try {
    $pre = @(Get-Disk | ForEach-Object { $_.Number })
    Mount-VHD -Path $tmp -ErrorAction Stop | Out-Null
    Start-Sleep 1
    $d = @(Get-Disk | Where-Object { $pre -notcontains $_.Number })
    $d = if ($d.Count -eq 1) { $d[0] } else { throw "mounted disk discovery: got $($d.Count)" }
    $dn = [int]$d.Number
    try {
      Set-Disk -Number $dn -IsOffline $true -EA Stop
      Start-Sleep 1
      $src = [IO.File]::Open($raw, 'Open', 'Read', 'Read')
      $dst = [IO.File]::Open("\\.\PHYSICALDRIVE$dn", 'Open', 'Write', 'None')
      try {
        $buf = New-Object byte[] 4194304          # 4 MiB chunks
        $copied = [long]0
        while ($true) {
          $n = $src.Read($buf, 0, $buf.Length)
          if ($n -le 0) { break }
          $dst.Write($buf, 0, $n)
          $copied += $n
        }
        $dst.Flush()
        if ($copied -ne $size) { throw "short copy: $copied of $size" }
      } finally { $src.Close(); $dst.Close() }
      Set-Disk -Number $dn -IsOffline $false -EA SilentlyContinue
      Start-Sleep 1
    } finally { Dismount-VHD -Path $tmp -ErrorAction SilentlyContinue }

  # verify: partitions parse when re-mounted
    $pre2 = @(Get-Disk | ForEach-Object { $_.Number })
    Mount-VHD -Path $tmp -ReadOnly -ErrorAction Stop | Out-Null
    Start-Sleep 1
    try {
      $d2 = @(Get-Disk | Where-Object { $pre2 -notcontains $_.Number })
      if ($d2.Count -ne 1) { throw "verify mount discovery: got $($d2.Count)" }
      $d2 = $d2[0]
      $np = @(Get-Partition -DiskNumber $d2.Number -EA SilentlyContinue).Count
      if ($np -lt 1) { throw 'no partitions visible in golden vhdx (write suspect)' }
      emit @{ partitions = $np; host_size = $d2.Size }
    } finally { Dismount-VHD -Path $tmp -ErrorAction SilentlyContinue }
    } finally { Unlock-Mount }

  Move-Item $tmp $vhd -Force
  emit @{ vhd = $vhd; bytes = $size }
} catch {
  $result.ok = $false
  $result.error = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
  if ($tmp -and (Test-Path $tmp)) { Remove-Item $tmp -Force -EA SilentlyContinue }
}
$result.data = @($global:__OUT)
ConvertTo-Json $result -Depth 8 -Compress
