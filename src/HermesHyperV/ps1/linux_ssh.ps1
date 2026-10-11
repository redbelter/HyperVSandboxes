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

  # Canonical CommandLineToArgvW quoting via character loop — regex -replace
  # with backslashes drifts silently across embedding layers, this cannot.
  function Q-Arg([string]$a) {
    if ($a -eq '') { return '""' }
    if ($a -notmatch '[ \t"]') { return $a }
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('"')
    $bs = 0
    foreach ($ch in $a.ToCharArray()) {
      if ($ch -eq [char]92) { $bs++; continue }
      if ($ch -eq [char]34) {
        [void]$sb.Append([string][char]92 * ($bs * 2 + 1)); [void]$sb.Append([char]34)
      } else {
        if ($bs) { [void]$sb.Append([string][char]92 * $bs); $bs = 0 }
        [void]$sb.Append($ch)
      }
    }
    if ($bs) { [void]$sb.Append([string][char]92 * ($bs * 2)) }
    [void]$sb.Append('"')
    $sb.ToString()
  }
  function Invoke-Ssh([string]$h, [string]$command, [int]$timeoutSec) {
    $argl = @('-i', ($key),
              '-o','BatchMode=yes',
              '-o','StrictHostKeyChecking=no',
              '-o','UserKnownHostsFile=NUL',
              '-o','ConnectTimeout=8',
              '-o','ServerAliveInterval=5','-o','ServerAliveCountMax=3',
              ('sandbox@' + $h), $command)
    # [Diagnostics.Process], NOT Start-Process: under PS5.1 the PassThru object
    # reports ExitCode=NULL even after WaitForExit in this host session, which
    # silently lost the guest's real exit code. Raw Process always has it.
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $ssh
    $psi.Arguments = (($argl | ForEach-Object { Q-Arg $_ }) -join ' ')
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [Diagnostics.Process]::Start($psi)
    $oTask = $proc.StandardOutput.ReadToEndAsync()
    $eTask = $proc.StandardError.ReadToEndAsync()
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $timedOut = $false
    while (-not $proc.WaitForExit(500)) {
      if ((Get-Date) -gt $deadline) {
        $timedOut = $true
        try { $proc.Kill($true) } catch { try { $proc.Kill() } catch { } }
        break
      }
    }
    $text = ''; $errtext = ''
    if (-not $timedOut) {
      # [void] the Wait() bool — inside an if-block both it and Result flow on
      # the pipeline, turning $text into an array and poisoning ConvertTo-Json.
      $null = $oTask.Wait(3000); $text = $oTask.Result
      $null = $eTask.Wait(3000); $errtext = $eTask.Result
    }
    $code = if ($timedOut) { -1 } else { try { $proc.ExitCode } catch { -1 } }
    if ($null -eq $code) { $code = -1 }
    return @{ exit = $code; output = $text; stderr = $errtext; timed_out = $timedOut }
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
