# SSH into sandbox Linux VMs from the host (LocalSystem service). No CI, no
# passwords: host keypair created once; cloud-init seeds the pub key.
# Request: { mode: 'key' | 'exec' | 'ready', root, host?, cmd?, timeout_s? }
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$global:__OUT = New-Object System.Collections.ArrayList
function emit { param($o) $global:__OUT.Add($o) | Out-Null }
$result = [ordered]@{ ok = $true; data = @(); error = $null }
try {
  $req = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:HV_REQ)) | ConvertFrom-Json
  $mode = [string]$req.mode
  $ssh = Join-Path $env:windir 'System32\OpenSSH\ssh.exe'
  $keygen = Join-Path $env:windir 'System32\OpenSSH\ssh-keygen.exe'
  $key = Join-Path ([string]$req.root) 'linux_id_ed25519'

  if (-not (Test-Path $key)) {
    $p = Start-Process -FilePath $keygen -ArgumentList @('-t','ed25519','-N','""','-f',($key)) -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "ssh-keygen exit $($p.ExitCode)" }
  }

  function Invoke-Ssh([string]$h, [string]$command, [int]$timeoutSec) {
    $outf = Join-Path $env:TEMP ('sshsx_' + [Guid]::NewGuid().ToString('N'))
    $errf = $outf + '.err'
    $args = @('-i', ($key),
              '-o','BatchMode=yes',
              '-o','StrictHostKeyChecking=no',
              '-o','UserKnownHostsFile=NUL',
              '-o','ConnectTimeout=8',
              '-o','ServerAliveInterval=5','-o','ServerAliveCountMax=3',
              ('sandbox@' + $h), $command)
    $p = Start-Process -FilePath $ssh -ArgumentList $args -NoNewWindow -PassThru `
          -RedirectStandardOutput $outf -RedirectStandardError $errf
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $timedOut = $false
    while ($true) {
      if ($p.WaitForExit(500)) { break }
      if ((Get-Date) -gt $deadline) {
        $timedOut = $true
        Stop-Process -Id $p.Id -Force -EA SilentlyContinue
        Start-Sleep 1
        break
      }
    }
    # ReadAllText, NOT Get-Content: the latter's ETS note-properties make
    # ConvertTo-Json serialize a whole provider object graph instead of a string.
    $text = if (Test-Path $outf) { [IO.File]::ReadAllText($outf) } else { '' }
    $errtext = if (Test-Path $errf) { (([IO.File]::ReadAllLines($errf)) -join ' ') } else { '' }
    Remove-Item $outf, $errf -Force -EA SilentlyContinue
    return @{ exit = $p.ExitCode; output = $text; stderr = $errtext; timed_out = $timedOut }
  }

  switch ($mode) {
    'key' {
      emit @{ step = 'key'; path = $key; pub = (Get-Content ($key + '.pub') -Raw).Trim() }
    }
    'exec' {
      $t = [int]$req.timeout_s; if ($t -le 0) { $t = 60 }
      $r = Invoke-Ssh ([string]$req.host) ([string]$req.cmd) $t
      emit @{ step = 'exec'; exit = $r.exit; output = $r.output; stderr = $r.stderr; timed_out = [bool]$r.timed_out }
    }
    'ready' {
      # cloud-init runcmd writes /var/lib/sandbox-ready once the box is live
      $r = Invoke-Ssh ([string]$req.host) 'cat /var/lib/sandbox-ready 2>/dev/null || echo NOT-YET' 25
      emit @{ step = 'ready'; exit = $r.exit; output = $r.output; stderr = $r.stderr; timed_out = [bool]$r.timed_out }
    }
    default { throw "unknown mode $mode" }
  }
} catch {
  $result.ok = $false
  $result.error = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
}
$result.data = @($global:__OUT)
ConvertTo-Json $result -Depth 8 -Compress
