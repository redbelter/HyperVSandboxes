# Build the GOLDEN image for an ISO/edition: a bootable, sysprepped-for-OOBE
# Windows VHDX that is NEVER booted. Runs once per (iso, edition); every
# create_vm afterwards is a differencing child of this disk + a Panther
# unattend. Deliberately does NOT write any per-VM identity (no ComputerName,
# no Panther file) — those land on each child.
# Request: { golden, iso, edition, disk_gb }
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
  $golden = [string]$req.golden     # full path to golden.vhdx
  $iso = [string]$req.iso
  $edition = [string]$req.edition
  $diskGb = [int]$req.disk_gb

  $dir = Split-Path $golden -Parent
  $seed = Join-Path $dir 'seed'      # persistent mount-point dirs (NTFS junctions), per-run

  # mount points are directories, so concurrent runs can't collide:
  $sysM = Join-Path $seed 'sys'
  $osM  = Join-Path $seed 'os'

  step 'cleanup' {
    # detach anything still bound to OUR seed dirs (crashed prior run)
    foreach ($dd in @(Get-Disk -ErrorAction SilentlyContinue | Where-Object { $_.Location -like ($dir + '*') })) {
      foreach ($p in @(Get-Partition -DiskNumber $dd.Number -ErrorAction SilentlyContinue)) {
        foreach ($ap in @($p.AccessPaths)) {
          if ($ap -like ($seed + '*')) {
            Remove-PartitionAccessPath -DiskNumber $dd.Number -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue
          }
        }
      }
    }
    if (Test-Path $golden) { Dismount-VHD -Path $golden -ErrorAction SilentlyContinue; Start-Sleep 2 }
    if ((Get-DiskImage -ImagePath $iso -ErrorAction SilentlyContinue).Attached) {
      Dismount-DiskImage -ImagePath $iso | Out-Null
    }
    if (Test-Path $seed) { Remove-Item $seed -Recurse -Force -ErrorAction SilentlyContinue }
  }
  step 'wipe' { if (Test-Path $golden) { Remove-Item $golden -Force } }
  step 'mkdir' {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    New-Item -ItemType Directory -Force -Path $sysM, $osM | Out-Null
  }
  step 'newvhd' {
    New-VHD -Path $golden -SizeBytes ($diskGb * 1GB) -Dynamic | Out-Null
    if (-not (Test-Path $golden)) { throw "VHD not created: $golden" }
  }

  # Global mount lock — see child_disk.ps1: Get-Disk diff discovery is
  # host-global, so mounts must serialize across ALL hidden child processes.
  $script:mtx = New-Object Threading.Mutex($false, 'Global\HermesHyperVMount')
  function Lock-Mount {
    try { if (-not $script:mtx.WaitOne(300000)) { throw 'mount lock timeout (5 min)' } }
    catch [Threading.AbandonedMutexException] { }   # previous holder died; lock is ours now
  }
  function Unlock-Mount { try { $script:mtx.ReleaseMutex() } catch {} }
  $script:mounted = $false
  step 'mount' {
    Lock-Mount
    try {
      $pre = @(Get-Disk | ForEach-Object { $_.Number })
      Mount-VHD -Path $golden | Out-Null
      $script:mounted = $true
      Start-Sleep 2
      $new = @(Get-Disk | Where-Object { $pre -notcontains $_.Number })
      if ($new.Count -ne 1) { throw "mounted disk discovery: got $($new.Count)" }
      $script:d = $new[0].Number
    } catch { Unlock-Mount; throw }
  }
  step 'init' { Initialize-Disk -Number $d -PartitionStyle GPT | Out-Null }

  $script:sysp = $null; $script:osp = $null
  step 'partitions' {
    $script:sysp = New-Partition -DiskNumber $d -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
    New-Partition -DiskNumber $d -Size 16MB -GptType '{e3c9e316-0b5c-4db8-b817-10d5a83cf460}' | Out-Null
    $script:osp = New-Partition -DiskNumber $d -UseMaximumSize
  }
  step 'format' {
    Format-Volume -Partition $sysp -FileSystem FAT32 -NewFileSystemLabel 'System' -Confirm:$false | Out-Null
    Format-Volume -Partition $osp -FileSystem NTFS -NewFileSystemLabel 'Windows' -Confirm:$false | Out-Null
  }
  step 'mountpoints' {
    # bind the volumes to folders instead of drive letters: per-VM namespace,
    # invisible to Explorer, collision-free
    Add-PartitionAccessPath -DiskNumber $d -PartitionNumber $sysp.PartitionNumber -AccessPath ($sysM + '\')
    Add-PartitionAccessPath -DiskNumber $d -PartitionNumber $osp.PartitionNumber  -AccessPath ($osM + '\')
    if (-not (Test-Path ($osM + '\'))) { throw 'os mount point not visible' }
  }

  step 'mountiso' {
    $null = Mount-DiskImage -ImagePath $iso -PassThru
    $vol = Get-DiskImage -ImagePath $iso | Get-Volume
    $letter = [string]$vol.DriveLetter
    if (-not $letter) {
      $part = Get-Partition -SupportVirtual -ErrorAction SilentlyContinue |
        Where-Object { $_.DiskNumber -eq (Get-DiskImage -ImagePath $iso).Number } | Select-Object -First 1
      if (-not $part) { throw 'ISO mounted but no volume/partition found' }
      $isoM = Join-Path $seed 'iso'
      New-Item -ItemType Directory -Force -Path $isoM | Out-Null
      Add-PartitionAccessPath -DiskNumber $part.DiskNumber -PartitionNumber $part.PartitionNumber -AccessPath ($isoM + '\')
      $script:isoRoot = $isoM + '\'
    } else {
      $script:isoRoot = "$letter" + ':\'
    }
    $script:wim = $isoRoot + 'sources\install.wim'
    if (-not (Test-Path $wim)) { $script:wim = $isoRoot + 'sources\install.esd' }
    if (-not (Test-Path $wim)) { throw 'install.wim/esd not found in ISO' }
  }
  step 'wimindex' {
    $img = Get-WindowsImage -ImagePath $wim | Where-Object ImageName -eq $edition
    if (-not $img) { throw "edition '$edition' not in image" }
    $script:idx = [int]$img.ImageIndex
    emit @{ step = 'wim'; path = $wim; idx = $idx }
  }
  step 'apply' {
    $log = Join-Path $dir 'dism.log'
    $p = Start-Process -FilePath "$env:windir\System32\dism.exe" -ArgumentList @(
      '/English', '/Apply-Image', "/ImageFile:$wim", "/Index:$idx", ("/ApplyDir:" + $osM + '\'), "/LogPath:$log"
    ) -NoNewWindow -Wait -PassThru `
      -RedirectStandardOutput (Join-Path $dir 'dism.out') -RedirectStandardError (Join-Path $dir 'dism.err')
    if ($p.ExitCode -ne 0) {
      $tail = if (Test-Path $log) { (Get-Content $log -Tail 20) -join ' | ' } else { '' }
      throw "dism exit $($p.ExitCode): $tail"
    }
    Remove-Item (Join-Path $dir 'dism.log'),(Join-Path $dir 'dism.out'),(Join-Path $dir 'dism.err') -ErrorAction SilentlyContinue
  }
  step 'unmountiso' { Dismount-DiskImage -ImagePath $iso | Out-Null }
  step 'bcdboot' {
    $p = Start-Process -FilePath "$env:windir\System32\bcdboot.exe" -ArgumentList @(
      ($osM + '\Windows'), '/s', ($sysM + '\'), '/f', 'UEFI' ) -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "bcdboot exit $($p.ExitCode)" }
  }
  step 'dismount' {
    foreach ($v in @(Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_Partition -Filter "DiskNumber=$d" -ErrorAction SilentlyContinue)) {
      foreach ($ap in @($v.AccessPaths)) {
        if ($ap -like ($seed + '*')) {
          Invoke-CimMethod -InputObject $v -MethodName RemoveAccessPath -Arguments @{ AccessPath = $ap } | Out-Null
        }
      }
    }
    try {
      Dismount-VHD -Path $golden | Out-Null
      $script:mounted = $false
      Remove-Item $seed -Recurse -Force -ErrorAction SilentlyContinue
    } finally { Unlock-Mount }
  }
  $g = Get-Item $golden
  emit @{ step = 'golden'; path = $golden; size_mb = [math]::Round($g.Length / 1MB) }
} catch {
  try { if ($script:mounted) { Dismount-VHD -Path $golden -ErrorAction SilentlyContinue | Out-Null } } catch { }
  try { Unlock-Mount } catch { }
  try { Dismount-DiskImage -ImagePath $iso -ErrorAction SilentlyContinue | Out-Null } catch { }
  $result.ok = $false
  $result.error = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
}
$result.data = @($global:__OUT)
ConvertTo-Json $result -Depth 8 -Compress
