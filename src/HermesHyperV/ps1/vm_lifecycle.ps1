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

    # Linux: golden child vhd + NoCloud seed disk (FAT volume labelled
    # 'cidata' holding user-data/meta-data/network-config) + static IP on the
    # internal switch; SecureBoot off; boot disk-first. One self-contained
    # action: child -> VM -> NIC (read MAC) -> seed (needs MAC) -> start.
    'linux_create' {
      $name=[string]$req.name; $root=[string]$req.root
      $golden=[string]$req.golden; $memMb=[int]$req.mem_mb; $cpu=[int]$req.cpu
      $switch=[string]$req.switch; $tag=[string]$req.tag
      $ip=[string]$req.ip; $hostname=[string]$req.hostname
      $pubkey=[string]$req.pubkey; $diskGb=[int]$req.disk_gb
      if (-not (Test-Path $golden)) { throw "missing golden linux image $golden" }
      if (-not (Get-VMSwitch -Name $switch -ErrorAction SilentlyContinue)) {
        New-VMSwitch -Name $switch -SwitchType Internal | Out-Null
      }
      # host needs an IP on the internal LAN to ssh into guests. NB: the
      # ManagementOS adapter's Name is 'SandboxLAN' but the NetAdapter's Name
      # is 'vEthernet (SandboxLAN)' — match on THAT, not DeviceName.
      $nadap = Get-VMNetworkAdapter -ManagementOS -Name $switch -EA SilentlyContinue
      if ($nadap) {
        $ifn = (Get-NetAdapter | Where-Object { $_.Name -eq ('vEthernet (' + $switch + ')') } | Select-Object -First 1).Name
        if ($ifn -and -not (Get-NetIPAddress -InterfaceAlias $ifn -IPAddress 192.168.50.1 -EA SilentlyContinue)) {
          New-NetIPAddress -InterfaceAlias $ifn -IPAddress 192.168.50.1 -PrefixLength 24 `
            -AddressFamily IPv4 -EA SilentlyContinue | Out-Null
        }
      }
      $vhd = Join-Path $root 'disk.vhdx'   # Gen-2 requires VHDX
      $seed = Join-Path $root 'seed.vhdx'
      if (Test-Path $root) {
        Stop-VM -Name $name -TurnOff -Force -EA SilentlyContinue
        Remove-VM -Name $name -Force -EA SilentlyContinue
        Start-Sleep 1
        Remove-Item $root -Recurse -Force -EA SilentlyContinue
      }
      New-Item -ItemType Directory -Force -Path $root | Out-Null

      # child (VHD differencing children MUST match the parent size exactly —
      # Hyper-V rejects any -SizeBytes other than the parent's)
        New-VHD -Path $vhd -ParentPath $golden -Differencing | Out-Null
        if (-not (Test-Path $vhd)) { throw "child vhd not created" }
      $vm = $null
      # newvm
        $vm = New-VM -Name $name -MemoryStartupBytes ($memMb * 1MB) -VHDPath $vhd -Generation 2 -Path $root
        Connect-VMNetworkAdapter -VMName $name -SwitchName $switch
        Set-VMProcessor -VM $vm -Count $cpu
        Set-VMFirmware -VM $vm -EnableSecureBoot Off
        Set-VM -VM $vm -Notes $tag
      # mac
        # The adapter's MacAddress reads 00:00:00:00:00:00 until Hyper-V
        # commits the pool MAC at first start; the pool is deterministic
        # (00:15:5D + last 3 bytes of the VM guid), so compute it.
        $vg = [Guid](Get-VM -Name $name | Select-Object -Expand Id)
        $vb = $vg.ToByteArray()
        $script:mac = ('00155D{0:X2}{1:X2}{2:X2}' -f $vb[13], $vb[14], $vb[15]).ToLower()
        $macFmt = ($script:mac -replace '(..)(?!$)', '$1:')
      # seed
        if (Test-Path $seed) { Remove-Item $seed -Force }
        New-VHD -Path $seed -SizeBytes 64MB -Fixed | Out-Null
        $pre = @(Get-Disk | ForEach-Object { $_.Number })
        Mount-VHD -Path $seed | Out-Null; Start-Sleep 1
        $d = @(Get-Disk | Where-Object { $pre -notcontains $_.Number })[0].Number
        Initialize-Disk -Number $d -PartitionStyle MBR | Out-Null
        $p = New-Partition -DiskNumber $d -UseMaximumSize
        Format-Volume -Partition $p -FileSystem FAT -NewFileSystemLabel 'cidata' -Confirm:$false | Out-Null
        $m = Join-Path $root 'seedmnt'
        New-Item -ItemType Directory -Force -Path $m | Out-Null
        Add-PartitionAccessPath -DiskNumber $d -PartitionNumber $p.PartitionNumber -AccessPath ($m + '\')
        $enc = New-Object System.Text.UTF8Encoding($false)
        $ud = @(
          '#cloud-config'
          "hostname: $hostname"
          'manage_etc_hosts: true'
          'users:'
          '  - name: sandbox'
          '    groups: [sudo]'
          '    sudo: ALL=(ALL) NOPASSWD:ALL'
          '    shell: /bin/bash'
          '    lock_passwd: true'
          "    ssh_authorized_keys: [`"$pubkey`"]"
          'runcmd:'
          '  - echo "ready $(date -Is)" > /var/lib/sandbox-ready && chmod 644 /var/lib/sandbox-ready'
        ) -join "`n"
        $md = "instance-id: $name`nlocal-hostname: $hostname`n"
        $nc = @(
          'version: 2'
          'ethernets:'
          '  primary:'
          '    match:'
          "      name: 'en*'"
          '    set-name: eth0'
          '    dhcp4: false'
          "    addresses: [`"$ip/24`"]"
          '    routes:'
          '      - to: default'
          '        via: 192.168.50.1'
        ) -join "`n"
        [IO.File]::WriteAllText((Join-Path $m 'user-data'), $ud, $enc)
        [IO.File]::WriteAllText((Join-Path $m 'meta-data'), $md, $enc)
        [IO.File]::WriteAllText((Join-Path $m 'network-config'), $nc, $enc)
        Start-Sleep 1
        Remove-PartitionAccessPath -DiskNumber $d -PartitionNumber $p.PartitionNumber -AccessPath ($m + '\')
        Dismount-VHD -Path $seed
        Remove-Item $m -Recurse -Force -EA SilentlyContinue
      # boot: seed rides along as a second SCSI disk (NoCloud scans every
      # block device for a 'cidata' volume; a DVD slot would reject vhdx media)
        Add-VMHardDiskDrive -VMName $name -Path $seed | Out-Null
        $hdd = @(Get-VMHardDiskDrive -VMName $name)[0]
        Set-VMFirmware -VM $vm -BootOrder @($hdd)
        # serial console on COM1 -> named pipe (host-side debugging, no windows)
        Set-VMComPort -VMName $name -Number 1 -Path ('\\.\pipe\' + $name + 'com1') -EA SilentlyContinue | Out-Null
        [IO.File]::WriteAllText((Join-Path $root 'meta.json'),
          ('{"linux":true,"ip":"' + $ip + '"}'), (New-Object Text.UTF8Encoding($false)))
        Start-VM -VM $vm
        emit @{ name=$name; state=[string](Get-VM -Name $name).State; ip=$ip; mac=$script:mac; folder=$root }
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
      $tag = [string]$req.tag; $vmroot = [string]$req.vmroot
      Get-VM -ErrorAction SilentlyContinue | Where-Object { $_.Notes -eq $tag } | ForEach-Object {
        $linux = Test-Path (Join-Path $vmroot ($_.VMName + '\seed.vhdx'))
        $ips = @((Get-VMNetworkAdapter -VMName $_.VMName -ErrorAction SilentlyContinue).IpAddresses)
        $lip = ''
        if ($linux) {
          $meta = Join-Path $vmroot ($_.VMName + '\meta.json')
          if (Test-Path $meta) {
            try { $lip = [string]((Get-Content $meta -Raw | ConvertFrom-Json).ip) } catch { }
          }
        }
        emit @{
          name=$_.VMName; state=[string]$_.State; cpu_count=$_.ProcessorCount
          mem_mb=[math]::Round($_.MemoryAssigned/1MB); uptime=[string]$_.Uptime
          heartbeat=[string]$_.Heartbeat; ips=$ips
          linux=$linux; ip=$lip
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
