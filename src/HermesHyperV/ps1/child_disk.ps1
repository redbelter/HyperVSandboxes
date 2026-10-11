# Create a VM disk as a differencing child of the golden VHDX, then drop the
# per-VM Panther unattend into its (empty) OS partition. All writes go to the
# child; the golden is opened read-only by the chain. No ISO, no dism, no
# drive letters — mount points are per-VM folders, so this scales to
# concurrent creates without a global lock.
# Request: { golden, root, unattend }
$ErrorActionPreference = 'Stop'
$global:__OUT = New-Object System.Collections.ArrayList
function emit { param($o) $global:__OUT.Add($o) | Out-Null }
$result = [ordered]@{ ok = $true; data = @(); error = $null }

function step($n, $sb) {
  try { & $sb; emit @{ step = $n } }
  catch { throw ('STEP ' + $n + ' [' + $_.Exception.GetType().Name + '] ' + $_.Exception.Message) }
}

try {
  $req = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:HV_REQ)) | ConvertFrom-Json
  $golden = [string]$req.golden; $root = [string]$req.root
  $unattend = [string]$req.unattend
  $vhd = Join-Path $root 'disk.vhdx'
  $seed = Join-Path $root 'seed'

  if (-not (Test-Path $golden)) { throw "missing golden image $golden" }
  if (-not (Test-Path $unattend)) { throw "missing unattend $unattend" }

  step 'cleanup' {
    foreach ($dd in @(Get-Disk -ErrorAction SilentlyContinue | Where-Object { $_.Location -like ($root + '*') })) {
      foreach ($p in @(Get-Partition -DiskNumber $dd.Number -ErrorAction SilentlyContinue)) {
        foreach ($ap in @($p.AccessPaths)) {
          if ($ap -like ($seed + '*') -or $ap -match '^[A-Z]:\\?$') {
            Remove-PartitionAccessPath -DiskNumber $dd.Number -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue
          }
        }
      }
    }
    if (Test-Path $vhd) { Dismount-VHD -Path $vhd -ErrorAction SilentlyContinue; Start-Sleep 1 }
    if (Test-Path $seed) { Remove-Item $seed -Recurse -Force -ErrorAction SilentlyContinue }
  }
  step 'mkdir' { New-Item -ItemType Directory -Force -Path $root, $seed | Out-Null }
  step 'child' {
    if (Test-Path $vhd) { Remove-Item $vhd -Force }
    New-VHD -Path $vhd -ParentPath $golden -Differencing | Out-Null
    if (-not (Test-Path $vhd)) { throw "child VHD not created: $vhd" }
  }

  $pre = @(Get-Disk | ForEach-Object { $_.Number })
  $script:d = $null; $script:osPart = $null
  step 'mount' {
    Mount-VHD -Path $vhd | Out-Null
    Start-Sleep 2
    $new = @(Get-Disk | Where-Object { $pre -notcontains $_.Number })
    if ($new.Count -ne 1) { throw "mounted disk discovery: got $($new.Count)" }
    $script:d = $new[0].Number
    $osM = Join-Path $seed 'os'
    New-Item -ItemType Directory -Force -Path $osM | Out-Null
    # the OS partition is the one carrying \Windows
    foreach ($p in @(Get-Partition -DiskNumber $d | Where-Object { $_.Size -gt 1GB })) {
      Add-PartitionAccessPath -DiskNumber $d -PartitionNumber $p.PartitionNumber -AccessPath ($osM + '\') -ErrorAction SilentlyContinue
      if (Test-Path (Join-Path $osM 'Windows')) {
        $script:osPart = $p.PartitionNumber
        break
      }
      Remove-PartitionAccessPath -DiskNumber $d -PartitionNumber $p.PartitionNumber -AccessPath ($osM + '\') -ErrorAction SilentlyContinue
    }
    if (-not $osPart) { throw 'OS partition not found on child disk' }
  }
  step 'panther' {
    $panther = Join-Path (Join-Path $seed 'os') 'Windows\Panther'
    New-Item -ItemType Directory -Force -Path $panther | Out-Null
    Copy-Item $unattend (Join-Path $panther 'unattend.xml') -Force
    if (-not (Test-Path (Join-Path $panther 'unattend.xml'))) { throw 'panther copy missing' }
  }
  step 'dismount' {
    Remove-PartitionAccessPath -DiskNumber $d -PartitionNumber $osPart -AccessPath ((Join-Path $seed 'os') + '\') -ErrorAction SilentlyContinue
    Dismount-VHD -Path $vhd | Out-Null
    Remove-Item $seed -Recurse -Force -ErrorAction SilentlyContinue
  }
  $c = Get-Item $vhd
  emit @{ step = 'child-disk'; vhd = $vhd; size_mb = [math]::Round($c.Length / 1MB) }
} catch {
  try { Dismount-VHD -Path $vhd -ErrorAction SilentlyContinue | Out-Null } catch { }
  $result.ok = $false
  $result.error = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
}
$result.data = @($global:__OUT)
ConvertTo-Json $result -Depth 8 -Compress
