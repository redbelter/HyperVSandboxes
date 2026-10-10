# VM lifecycle + queries via the proven Hyper-V cmdlets, run hidden by the
# service (LocalSystem). One spawn per operation, JSON in / JSON out.
# Request: { action, ... }   actions: status | ensure_switch | create |
#                                   power | delete | purge | editions
$ErrorActionPreference = 'Stop'
$global:__OUT = New-Object System.Collections.ArrayList
function emit { param($o) $global:__OUT.Add($o) | Out-Null }
$result = [ordered]@{ ok = $true; data = @(); error = $null }

try {
  $req = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:HV_REQ)) | ConvertFrom-Json
  $action = [string]$req.action

  switch ($action) {

    'status' {
      $svc = Get-Service VMMS -ErrorAction SilentlyContinue
      $hp = (Get-CimInstance Win32_ComputerSystem).HypervisorPresent
      $media = [string]$req.media
      $isos = @(Get-ChildItem $media -Filter '*.iso' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
      $du = Get-PSDrive ($media.Substring(0,1)) -ErrorAction SilentlyContinue
      emit @{
        root=[string]$req.root; hypervisor_present=$hp
        vmms_service=$(if ($svc) { $svc.Status.ToString().ToLower() } else { 'not-installed' })
        isos=$isos
        disks=@{ ($media.Substring(0,1) + ':') = @{ total_gb=[math]::Round(($du.Free+$du.Used)/1GB,1); free_gb=[math]::Round($du.Free/1GB,1) } }
      }
    }

    'ensure_switch' {
      $switch = [string]$req.switch
      if (-not (Get-VMSwitch -Name $switch -ErrorAction SilentlyContinue)) {
        New-VMSwitch -Name $switch -SwitchType Internal | Out-Null
      }
      emit @{ switch = $switch; present = $true }
    }

    'create' {
      $name=[string]$req.name; $root=[string]$req.root; $vhd=Join-Path $root 'disk.vhdx'
      $memMb=[int]$req.mem_mb; $maxMb=[int]$req.max_mb; $cpu=[int]$req.cpu
      $switch=[string]$req.switch; $tag=[string]$req.tag
      if (-not (Test-Path $vhd)) { throw "missing $vhd (provisioning failed?)" }
      if (-not (Get-VMSwitch -Name $switch -ErrorAction SilentlyContinue)) {
        New-VMSwitch -Name $switch -SwitchType Internal | Out-Null
      }
      $vm = New-VM -Name $name -MemoryStartupBytes ($memMb * 1MB) -VHDPath $vhd `
                   -Generation 2 -Path $root
      $na = @(Get-VMNetworkAdapter -VMName $name -ErrorAction SilentlyContinue)
      if ($na.Count -gt 0) { Connect-VMNetworkAdapter -VMName $name -SwitchName $switch }
      else { New-VMNetworkAdapter -VMName $name -SwitchName $switch | Out-Null }
      Set-VMProcessor -VM $vm -Count $cpu
      if ($maxMb -gt $memMb) {
        $minMb = [Math]::Min(1024, $memMb)
        Set-VMMemory -VM $vm -DynamicMemoryEnabled $true `
          -MinimumBytes ($minMb * 1MB) -MaximumBytes ($maxMb * 1MB)
      }
      Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate 'MicrosoftWindows'
      Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
      Enable-VMTPM -VM $vm
      Enable-VMIntegrationService -VMName $name -Name 'Guest Service Interface'
      Set-VM -VM $vm -Notes $tag
      $hdd = Get-VMHardDiskDrive -VMName $name
      Set-VMFirmware -VM $vm -BootOrder @($hdd)
      Start-VM -VM $vm
      emit @{ name=$name; state=[string](Get-VM -Name $name).State; folder=$root; vhd=$vhd }
    }

    'power' {
      $name=[string]$req.name; $want=[string]$req.want
      $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
      if (-not $vm) { throw "VM $name not found" }
      if ($want -eq 'start') { if ($vm.State -ne 'Running') { Start-VM -VM $vm } }
      elseif ($want -eq 'stop') {
        if ($vm.State -ne 'Off') { Stop-VM -VM $vm -Force }
      } else { throw "bad power action $want" }
      emit @{ state=[string](Get-VM -Name $name).State }
    }

    'delete' {
      $name=[string]$req.name; $tag=[string]$req.tag; $root=[string]$req.root
      $oobe=[string]$req.oobe
      $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
      if (-not $vm) { emit @{ deleted=$false; reason='not-found' } }
      elseif ($vm.Notes -ne $tag) { throw "refusing: $name is not a sandbox VM (wrong tag)" }
      else {
        if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
        $paths = @(Get-VMHardDiskDrive -VMName $name).Path + @(Get-VMDvdDrive -VMName $name).Path
        Remove-VM -VM $vm -Force
        foreach ($p in $paths) {
          if ($p -and (Test-Path $p) -and ($p -notlike '*.iso')) {
            Remove-Item $p -Force -ErrorAction SilentlyContinue
          }
        }
        if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        if ($oobe -and (Test-Path $oobe)) { Remove-Item $oobe -Force -ErrorAction SilentlyContinue }
        emit @{ deleted=$true; folder=$root }
      }
    }

    'purge' {
      $tag=[string]$req.tag; $vmroot=[string]$req.vmroot; $media=[string]$req.media
      $names = @(Get-VM -ErrorAction SilentlyContinue | Where-Object { $_.Notes -eq $tag } | ForEach-Object { $_.VMName })
      $deleted = @()
      foreach ($n in $names) {
        $vm = Get-VM -Name $n
        if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
        $root = Join-Path $vmroot $n
        Remove-VM -VM $vm -Force
        if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        $deleted += $n
      }
      Get-ChildItem $media -Filter '*-oobe.xml' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
      Get-ChildItem $media -Filter '*-seed.iso' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
      emit @{ deleted = $deleted }
    }

    'editions' {
      $iso = [string]$req.iso
      $m = Mount-DiskImage -ImagePath $iso -PassThru
      try {
        $vol = $m | Get-Volume
        $isoP = ($vol.Path).TrimEnd('\') + '\'
        $wim = $isoP + 'sources\install.wim'
        if (-not (Test-Path $wim)) { $wim = $isoP + 'sources\install.esd' }
        Get-WindowsImage -ImagePath $wim | ForEach-Object { emit @{ index=$_.ImageIndex; name=$_.ImageName } }
      } finally {
        Dismount-DiskImage -ImagePath $iso | Out-Null
      }
    }

    'list' {
      $tag = [string]$req.tag
      Get-VM -ErrorAction SilentlyContinue | Where-Object { $_.Notes -eq $tag } | ForEach-Object {
        $ips = @((Get-VMNetworkAdapter -VMName $_.VMName -ErrorAction SilentlyContinue).IpAddresses)
        emit @{
          name=$_.VMName; state=[string]$_.State; cpu_count=$_.ProcessorCount
          mem_mb=[math]::Round($_.MemoryAssigned/1MB); uptime=[string]$_.Uptime
          heartbeat=[string]$_.Heartbeat; ips=$ips
        }
      }
    }

    'wait' {
      $name=[string]$req.name; $want=[string]$req.want; $timeout=[int]$req.timeout_s
      $deadline=(Get-Date).AddSeconds($timeout)
      $state=''
      while ((Get-Date) -lt $deadline) {
        $state=[string](Get-VM -Name $name -ErrorAction SilentlyContinue).State
        if ($state -eq $want) { break }
        Start-Sleep -Milliseconds 1000
      }
      emit @{ state=$state; reached=($state -eq $want) }
    }

    default { throw "unknown action '$action'" }
  }
} catch {
  $result.ok = $false
  $result.error = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
}
$result.data = @($global:__OUT)
ConvertTo-Json $result -Depth 8 -Compress
