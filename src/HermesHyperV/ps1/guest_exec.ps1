# Guest exec via PowerShell Direct (VMBus). The only step PowerShell is
# genuinely required for -- Microsoft's sole host->guest channel.
# Request: { vm, script_b64, user, password, timeout }
$ErrorActionPreference = 'Stop'
$function:emit = { param($o) $global:__OUT.Add($o) | Out-Null }
$global:__OUT = New-Object System.Collections.ArrayList
$result = [ordered]@{ ok = $true; data = @(); error = $null }
try {
  $req = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:HV_REQ)) | ConvertFrom-Json
  $sb = [ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$req.script_b64)))
  $sec = ConvertTo-SecureString ([string]$req.password) -AsPlainText -Force
  $cred = New-Object System.Management.Automation.PSCredential([string]$req.user, $sec)
  $job = Invoke-Command -VMName ([string]$req.vm) -Credential $cred -ScriptBlock $sb -AsJob
  if (Wait-Job $job -Timeout ([int]$req.timeout)) {
    $r = Receive-Job $job
    emit @{ output = ($r | Out-String); timed_out = $false }
  } else {
    Stop-Job $job -ErrorAction SilentlyContinue
    $r = Receive-Job $job -ErrorAction SilentlyContinue
    emit @{ output = ($r | Out-String); timed_out = $true }
  }
  Remove-Job $job -Force -ErrorAction SilentlyContinue
} catch {
  $result.ok = $false
  $result.error = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
}
$result.data = @($global:__OUT)
if ($result.error) { $result.data = @() }
ConvertTo-Json $result -Depth 8 -Compress
