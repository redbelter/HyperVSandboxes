// Guest defaults. The password is NEVER stored in this repo:
//   1. $HYPERV_GUEST_PASSWORD env var wins (explicit operator choice), else
//   2. <HYPERV_ROOT>\guest.key (generated once on first run, local only), else
//   3. a fresh random password, generated + persisted to that file.
// Existing VMs created by older builds keep working by writing their password
// into guest.key once.
namespace HermesHyperV.Engine;

internal static class GuestDefaults
{
    public const string User = "Sandbox";

    static string? _cached;
    static readonly object Gate = new();

    public static string Password
    {
        get
        {
            if (_cached is not null) return _cached;
            lock (Gate)
            {
                if (_cached is not null) return _cached;
                var fromEnv = Environment.GetEnvironmentVariable("HYPERV_GUEST_PASSWORD");
                if (!string.IsNullOrWhiteSpace(fromEnv)) return _cached = fromEnv.Trim();
                var file = Path.Combine(
                    Environment.GetEnvironmentVariable("HYPERV_ROOT") ?? @"C:\HyperV", "guest.key");
                try
                {
                    if (File.Exists(file))
                        return _cached = File.ReadAllText(file).Trim();
                }
                catch { /* fall through to generate */ }
                var pwd = "Sbx-" + Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).TrimEnd('=');
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, pwd);
                }
                catch { /* ephemeral: survives until reboot; create_vm echoes it */ }
                return _cached = pwd;
            }
        }
    }
}

internal sealed record IsoEntry(string File, string Edition, int MemoryMb, int DiskGb);

internal static class IsoCatalog
{
    // 'server2025-eval' resolves to <root>\Media\<File> if that ISO is present.
    // Download: https://www.microsoft.com/en-us/evalcenter/download-windows-server-2025
    public static readonly Dictionary<string, IsoEntry> Entries = new()
    {
        ["server2025-eval"] = new(
            "26100.1742.240906-0331.ge_release_svc_refresh_SERVER_EVAL_x64FRE_en-us.iso",
            "Windows Server 2025 Datacenter Evaluation (Desktop Experience)", 4096, 64),
    };
}

internal static class Oobe
{
    // oobeSystem-only unattend (the host already built the disk; the guest
    // just finishes OOBE, creates the Sandbox admin, enables RDP+PSRemoting).
    // __COMPUTERNAME__ and __PASSWORD__ are replaced at render time.
    public const string Template = """
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
    <settings pass="specialize">
        <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
            <ComputerName>__COMPUTERNAME__</ComputerName>
        </component>
    </settings>
    <settings pass="oobeSystem">
        <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
            <AutoLogon>
                <Password><PlainText>true</PlainText><Value>__PASSWORD__</Value></Password>
                <LogonCount>5</LogonCount>
                <Username>Sandbox</Username>
                <Enabled>true</Enabled>
            </AutoLogon>
            <OOBE>
                <HideEULAPage>true</HideEULAPage>
                <HideLocalAccountScreen>true</HideLocalAccountScreen>
                <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
                <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
                <NetworkLocation>Work</NetworkLocation>
                <ProtectYourPC>3</ProtectYourPC>
                <!-- Server SKUs honour these: without them msoobe.exe stalls
                     after its WinRE task on a host with no external network
                     and never hands the console over to the auto-logon. -->
                <SkipMachineOOBE>true</SkipMachineOOBE>
                <SkipUserOOBE>true</SkipUserOOBE>
            </OOBE>
            <UserAccounts>
                <AdministratorPassword><Value>__PASSWORD__</Value><PlainText>true</PlainText></AdministratorPassword>
                <LocalAccounts>
                    <LocalAccount wcm:action="add">
                        <Password><Value>__PASSWORD__</Value><PlainText>true</PlainText></Password>
                        <Description>Sandbox admin</Description>
                        <DisplayName>Sandbox</DisplayName>
                        <Group>Administrators</Group>
                        <Name>Sandbox</Name>
                    </LocalAccount>
                </LocalAccounts>
            </UserAccounts>
            <FirstLogonCommands>
                <SynchronousCommand wcm:action="add">
                    <Order>1</Order>
                    <CommandLine>cmd.exe /c powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Set-ItemProperty -Path 'HKLM:\\System\\CurrentControlSet\\Control\\Terminal Server' -Name fDenyTSConnections -Value 0 -Force; Enable-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue; Enable-PSRemoting -Force -SkipNetworkProfileCheck; Set-Service WinRM -StartupType Automatic; New-Item -ItemType Directory -Force -Path C:\\Sandbox | Out-Null; Set-Content -Path C:\\Sandbox\\ready.txt -Value ('ready ' + (Get-Date -Format 'o'))"</CommandLine>
                    <Description>EnableRDP+PSRemoting</Description>
                </SynchronousCommand>
            </FirstLogonCommands>
        </component>
    </settings>
</unattend>

""";
}
