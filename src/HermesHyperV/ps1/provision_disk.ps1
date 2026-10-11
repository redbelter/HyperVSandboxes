# Build a bootable Windows VHDX entirely on the host (no ISO boot, no keypress
# prompt). Two deliberate deviations from a pure cmdlet script:
#   Expand-WindowsImage -> dism.exe /Apply-Image (same engine, logged, hidden)
#   ISO mounted to a drive letter so dism / Get-WindowsImage see a normal path.
# Request: { root, iso, edition, unattend, disk_gb }
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
  $root = [string]$req.root; $iso = [string]$req.iso
  $edition = [string]$req.edition; $unattend = [string]$req.unattend
  $diskGb = [int]$req.disk_gb

  $vhd = Join-Path $root 'disk.vhdx'
  $stg = 'root/Microsoft/Windows/Storage'

  # release stale mounts/letters left by a crashed provision (any sandbox
  # disk under the VMs root, not just this VM's folder — a crashed sibling
  # run can leave S:/W: bound to ITS partitions)
  step 'cleanup' {
    $scan = [string]$req.vmroot; if (-not $scan) { $scan = $root }
    foreach ($dd in @(Get-Disk -ErrorAction SilentlyContinue | Where-Object { $_.Location -like ($scan + '*') })) {
      foreach ($p in @(Get-Partition -DiskNumber $dd.Number -ErrorAction SilentlyContinue)) {
        foreach ($ap in @($p.AccessPaths)) {
          if ($ap -match '^[A-Z]:\\?$') {
            Remove-PartitionAccessPath -DiskNumber $dd.Number -PartitionNumber $p.PartitionNumber -AccessPath $ap.TrimEnd('\') -ErrorAction SilentlyContinue
          }
        }
      }
    }
    if (Test-Path $vhd) { Dismount-VHD -Path $vhd -ErrorAction SilentlyContinue; Start-Sleep 2 }
    # stale ISO mount from a crashed run (ours only — never touch user ISOs)
    if ((Get-DiskImage -ImagePath $iso -ErrorAction SilentlyContinue).Attached) {
      Dismount-DiskImage -ImagePath $iso | Out-Null
    }
    foreach ($l in 'R','S','W') {
      if (Get-Volume -DriveLetter $l -ErrorAction SilentlyContinue) {
        throw "drive letter $l in use (stale mount outside our control)"
      }
    }
  }
  step 'wipe' { if (Test-Path $root) { Remove-Item $root -Recurse -Force } }
  step 'mkdir' { New-Item -ItemType Directory -Force -Path $root | Out-Null }

  step 'newvhd' {
    New-VHD -Path $vhd -SizeBytes ($diskGb * 1GB) -Dynamic | Out-Null
    if (-not (Test-Path $vhd)) { throw "VHD not created: $vhd" }
  }

  $pre = @(Get-Disk | ForEach-Object { $_.Number })
  step 'mount' {
    Mount-VHD -Path $vhd | Out-Null
    Start-Sleep 2
    $new = @(Get-Disk | Where-Object { $pre -notcontains $_.Number })
    if ($new.Count -ne 1) { throw "mounted disk discovery: got $($new.Count)" }
    $script:d = $new[0].Number
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
  step 'letters' {
    Add-PartitionAccessPath -DiskNumber $d -PartitionNumber $sysp.PartitionNumber -AccessPath 'S:'
    Add-PartitionAccessPath -DiskNumber $d -PartitionNumber $osp.PartitionNumber  -AccessPath 'W:'
    if (-not (Test-Path 'W:\')) { throw 'W: not visible' }
  }

  step 'mountiso' {
    $null = Mount-DiskImage -ImagePath $iso -PassThru
    $vol = Get-DiskImage -ImagePath $iso | Get-Volume
    $letter = [string]$vol.DriveLetter
    if (-not $letter) {
      # no auto letter on this build -> bind one explicitly
      $part = Get-Partition -SupportVirtual -ErrorAction SilentlyContinue |
        Where-Object { $_.DiskNumber -eq (Get-DiskImage -ImagePath $iso).Number } | Select-Object -First 1
      if (-not $part) { throw 'ISO mounted but no volume/partition found' }
      Add-PartitionAccessPath -DiskNumber $part.DiskNumber -PartitionNumber $part.PartitionNumber -AccessPath 'R:'
      $letter = 'R'
    }
    $script:wim = "$letter" + ':\sources\install.wim'
    if (-not (Test-Path $wim)) { $script:wim = "$letter" + ':\sources\install.esd' }
    if (-not (Test-Path $wim)) { throw 'install.wim/esd not found in ISO' }
  }
  step 'wimindex' {
    $img = Get-WindowsImage -ImagePath $wim | Where-Object ImageName -eq $edition
    if (-not $img) { throw "edition '$edition' not in image" }
    $script:idx = [int]$img.ImageIndex
    emit @{ step = 'wim'; path = $wim; idx = $idx }
  }
  step 'apply' {
    $log = Join-Path $root 'dism.log'
    # -NoNewWindow + file redirects => no console window, no stdout pollution
    $p = Start-Process -FilePath "$env:windir\System32\dism.exe" -ArgumentList @(
      '/English', '/Apply-Image', "/ImageFile:$wim", "/Index:$idx", '/ApplyDir:W:\', "/LogPath:$log"
    ) -NoNewWindow -Wait -PassThru `
      -RedirectStandardOutput (Join-Path $root 'dism.out') -RedirectStandardError (Join-Path $root 'dism.err')
    if ($p.ExitCode -ne 0) {
      $tail = if (Test-Path $log) { (Get-Content $log -Tail 20) -join ' | ' } else { '' }
      throw "dism exit $($p.ExitCode): $tail"
    }
  }
  step 'unmountiso' {
    Dismount-DiskImage -ImagePath $iso | Out-Null
  }
  step 'bcdboot' {
    $p = Start-Process -FilePath "$env:windir\System32\bcdboot.exe" -ArgumentList @(
      'W:\Windows', '/s', 'S:', '/f', 'UEFI' ) -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "bcdboot exit $($p.ExitCode)" }
  }
  step 'panther' {
    New-Item -ItemType Directory -Force -Path 'W:\Windows\Panther' | Out-Null
    Copy-Item $unattend 'W:\Windows\Panther\unattend.xml' -Force
    if (-not (Test-Path 'W:\Windows\Panther\unattend.xml')) { throw 'panther copy missing' }
  }
  step 'dismount' {
    Dismount-VHD -Path $vhd | Out-Null
    Remove-Item (Join-Path $root 'dism.log') -ErrorAction SilentlyContinue
  }
  emit @{ step = 'provisioned'; vhd = $vhd }
} catch {
  # best-effort so a failed run never leaves R:/S:/W:/VHD mounted
  try {
    Invoke-CimMethod -Namespace $stg -ClassName MSFT_DiskImage -MethodName Dismount -Arguments @{ DiskImage = $iso } | Out-Null
  } catch { }
  try { Dismount-VHD -Path $vhd -ErrorAction SilentlyContinue | Out-Null } catch { }
  $result.ok = $false
  $result.error = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
}
$result.data = @($global:__OUT)
ConvertTo-Json $result -Depth 8 -Compress
