# Installs the Soundstage APO onto a playback device.
#
#   Run as Administrator:
#     powershell -ExecutionPolicy Bypass -File install-apo.ps1
#     powershell -ExecutionPolicy Bypass -File install-apo.ps1 -Device "AV Receiver"
#     powershell -ExecutionPolicy Bypass -File install-apo.ps1 -Uninstall
#
# Two separate things happen here, and it helps to keep them apart:
#
#   1. COM registration - teaching Windows that our CLSID lives in this DLL. Machine-wide, done once,
#      handled by the DLL's own DllRegisterServer.
#   2. Endpoint attachment - telling one specific playback device to run our plugin. Per device, and
#      the reason this script needs to ask which device you mean.
#
# On permissions, which are stranger here than you would expect. The endpoint keys under MMDevices
# are owned by SYSTEM and grant Administrators only *SetValue and ReadKey* - not Full Control. So
# even a fully elevated process can change a value on these keys but CANNOT create a subkey under
# them, and cannot open them with the usual KEY_WRITE (which implies CreateSubKey). Two consequences
# shape the code below:
#
#   * every access asks for exactly SetValue/QueryValues rather than "write", and
#   * backups live under HKLM\SOFTWARE\Soundstage, not in a subkey beside the values themselves.
#
# Backups matter because the effect properties are how the manufacturer's own audio software hooks
# in. Overwriting them with no way back is how a device quietly loses its features, so every value is
# saved before it is touched and -Uninstall puts them all back.

[CmdletBinding()]
param(
    [switch]$Uninstall,
    # Substring of the device name, e.g. "AV Receiver". Omit to be shown a list.
    [string]$Device
)

$ErrorActionPreference = "Stop"

$CLSID      = "{6F3C9A21-4E7B-4B36-9E1D-2A55C0D8E401}"
# Under SOFTWARE\Microsoft\Windows\CurrentVersion, not SYSTEM\CurrentControlSet\Control - the audio
# endpoint store is a Windows component, not a driver service.
$RenderBase = "SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"
$BackupBase = "SOFTWARE\Soundstage\EndpointBackup"
$FxGuid     = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}"
# Two properties, because either alone is ambiguous. The description is the short label Windows shows
# ("Speakers"), which is not unique - a PC with onboard audio and a virtual device has two of them.
# The friendly name carries the adapter ("Realtek(R) Audio"), which is what tells them apart.
$DescProp     = "{a45c254e-df1c-4efd-8020-67d146a850e0},2"
$FriendlyProp = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6"

# Windows consults several property ids for effects. This pair is the "mode effect" slot - post-mix,
# so it sees the final multichannel stream rather than one app's stereo. 6 is the original id and 14
# the one added in Windows 10; both are still honoured, and which one applies depends on the driver's
# own registration style, so shipping APOs write both.
$ModeFxProps = @("$FxGuid,6", "$FxGuid,14")

# PKEY_FX_Association - which pin the effect belongs to. All-zero GUID: apply regardless.
$AssocProp = "$FxGuid,0"
$AssocAny  = "{00000000-0000-0000-0000-000000000000}"

# Exactly the rights these keys grant Administrators. Asking for more fails outright.
$FxRights = [System.Security.AccessControl.RegistryRights]"SetValue,QueryValues,EnumerateSubKeys,Notify"

function Assert-Admin {
    $p = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run as Administrator."
    }
}

function Open-Fx($endpointId, [switch]$Writable) {
    $path = "$RenderBase\$endpointId\FxProperties"
    if ($Writable) {
        return [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
            $path, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, $FxRights)
    }
    return [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path)
}

function Get-RenderEndpoints {
    $root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($RenderBase)
    if (-not $root) { return @() }
    try {
        foreach ($id in $root.GetSubKeyNames()) {
            $ep = $root.OpenSubKey($id)
            if (-not $ep) { continue }
            try {
                $state = $ep.GetValue("DeviceState")
                $props = $ep.OpenSubKey("Properties")
                $desc = $null; $friendly = $null
                if ($props) {
                    $desc = $props.GetValue($DescProp)
                    $friendly = $props.GetValue($FriendlyProp)
                    $props.Close()
                }
                if ($desc) {
                    $label = if ($friendly) { "$desc ($friendly)" } else { $desc }
                    [pscustomobject]@{ Id = $id; Name = $label; Active = ($state -eq 1) }
                }
            } finally { $ep.Close() }
        }
    } finally { $root.Close() }
}

function Select-Endpoint {
    $all = @(Get-RenderEndpoints | Where-Object { $_.Active })
    if ($all.Count -eq 0) { throw "No active playback devices found." }

    if ($Device) {
        $hit = @($all | Where-Object { $_.Name -like "*$Device*" })
        if ($hit.Count -eq 1) { return $hit[0] }
        if ($hit.Count -gt 1) { throw "'$Device' matches $($hit.Count) devices: $($hit.Name -join ', ')" }
        throw "No active playback device matching '$Device'. Found: $($all.Name -join ', ')"
    }

    Write-Host ""
    Write-Host "Active playback devices:"
    for ($i = 0; $i -lt $all.Count; $i++) { Write-Host ("  [{0}] {1}" -f $i, $all[$i].Name) }
    Write-Host ""
    $pick = Read-Host "Which device should Soundstage process? (number)"
    $n = 0
    if (-not [int]::TryParse($pick, [ref]$n) -or $n -lt 0 -or $n -ge $all.Count) { throw "Not a valid choice." }
    return $all[$n]
}

function Backup-Value($endpointId, $name, $current) {
    $bk = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey("$BackupBase\$endpointId")
    try {
        # Back up once only. A second install must not overwrite the true original with our own
        # value - that would make the uninstall restore Soundstage instead of removing it.
        if ($null -ne $bk.GetValue($name)) { return }
        # Empty string is the sentinel for "there was nothing here".
        $bk.SetValue($name, $(if ($null -eq $current) { "" } else { $current }))
    } finally { $bk.Close() }
}

function Restore-Endpoint($endpointId) {
    $bk = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("$BackupBase\$endpointId", $true)
    if (-not $bk) { return $false }

    $fx = Open-Fx $endpointId -Writable
    if (-not $fx) { $bk.Close(); return $false }

    try {
        foreach ($name in $bk.GetValueNames()) {
            $saved = $bk.GetValue($name)
            if ([string]::IsNullOrEmpty($saved)) {
                try { $fx.DeleteValue($name, $false) } catch {}
            } else {
                $fx.SetValue($name, $saved)
            }
        }
    } finally { $fx.Close(); $bk.Close() }

    [Microsoft.Win32.Registry]::LocalMachine.DeleteSubKeyTree("$BackupBase\$endpointId", $false)
    return $true
}

function Restart-AudioService {
    Write-Host "Restarting the audio service so the change takes effect..."
    # AudioEndpointBuilder holds Audiosrv as a dependent; -Force takes both down together.
    Restart-Service -Name AudioEndpointBuilder -Force
    Start-Sleep -Seconds 3
}

# ---------------------------------------------------------------------------------------------

Assert-Admin

$here = $PSScriptRoot
$src  = Join-Path $here "SoundstageApo.dll"
$dst  = Join-Path $env:WINDIR "System32\SoundstageApo.dll"

if ($Uninstall) {
    Write-Host "Removing Soundstage from all playback devices..."

    $touched = 0
    foreach ($ep in Get-RenderEndpoints) {
        if (Restore-Endpoint $ep.Id) {
            Write-Host "  detached from: $($ep.Name)"
            $touched++
        }
    }
    Write-Host "  $touched device(s) restored."

    if (Test-Path $dst) {
        Start-Process regsvr32.exe -ArgumentList "/s", "/u", "`"$dst`"" -Wait
        Write-Host "  unregistered the COM class."
    }

    Restart-AudioService

    # Delete last - audiodg has to let go of it first.
    if (Test-Path $dst) {
        try {
            [System.IO.File]::Delete($dst)
            Write-Host "  removed $dst"
        } catch {
            Write-Warning "Could not delete $dst (still loaded). Reboot and delete it by hand."
        }
    }

    Write-Host ""
    Write-Host "Done. Your devices are back to how they were."
    return
}

if (-not (Test-Path $src)) { throw "SoundstageApo.dll not found. Run build.ps1 first." }

$target = Select-Endpoint
Write-Host ""
Write-Host "Installing Soundstage onto: $($target.Name)   [$($target.Id)]"

# Only copy when the file is actually different. Attaching a SECOND device would otherwise fail:
# audiodg already has the DLL loaded from the first one and holds it open, so the copy is refused -
# even though the bytes are identical and there is nothing to do.
$needCopy = $true
if (Test-Path $dst) {
    try {
        $a = (Get-FileHash $src -Algorithm SHA256).Hash
        $b = (Get-FileHash $dst -Algorithm SHA256).Hash
        if ($a -eq $b) { $needCopy = $false }
    } catch { }
}

if (-not $needCopy) {
    Write-Host "  plugin already in System32 and up to date"
} else {
    $copied = $false
    try {
        Copy-Item $src $dst -Force
        $copied = $true
    } catch {
        # Held open by the audio engine. Stopping the service makes it let go; the endpoint work
        # below still needs to happen either way.
        Write-Host "  plugin is in use - stopping the audio service to replace it"
        Stop-Service Audiosrv -Force
        Start-Sleep -Seconds 2
        foreach ($i in 1..8) {
            try { Copy-Item $src $dst -Force; $copied = $true; break } catch { Start-Sleep -Seconds 2 }
        }
        Start-Service Audiosrv
        Start-Sleep -Seconds 2
    }
    if (-not $copied) { throw "Could not replace $dst - reboot and run this again." }
    Write-Host "  copied the plugin to System32"
}

# audiodg.exe runs stripped of privileges. It must be able to read the DLL, and System32's own
# permissions already allow that - but a file copied out of a user profile can arrive carrying
# inherited ACLs that do not. Re-inheriting from System32 fixes it.
$acl = Get-Acl $dst
$acl.SetAccessRuleProtection($false, $false)
Set-Acl -Path $dst -AclObject $acl

$reg = Start-Process regsvr32.exe -ArgumentList "/s", "`"$dst`"" -Wait -PassThru
if ($reg.ExitCode -ne 0) { throw "regsvr32 failed ($($reg.ExitCode))." }
Write-Host "  registered the COM class"

$fx = Open-Fx $target.Id -Writable
if (-not $fx) { throw "Could not open FxProperties for write on $($target.Name)." }
try {
    foreach ($p in @($ModeFxProps + $AssocProp)) {
        Backup-Value $target.Id $p $fx.GetValue($p)
    }
    foreach ($p in $ModeFxProps) { $fx.SetValue($p, $CLSID) }
    $fx.SetValue($AssocProp, $AssocAny)
} finally { $fx.Close() }
Write-Host "  attached to the device (previous values saved under HKLM\$BackupBase)"

Restart-AudioService

Write-Host ""
Write-Host "Installed."
Write-Host ""
Write-Host "What to expect:"
Write-Host "  * Play something. If you hear audio, the plugin loaded and is passing sound through."
Write-Host "  * Open Soundstage and move a control - it publishes settings to the plugin live."
Write-Host "  * To undo everything:"
Write-Host "      powershell -ExecutionPolicy Bypass -File install-apo.ps1 -Uninstall"
Write-Host ""
Write-Host "  Windows disables an effect that misbehaves rather than breaking your sound. If the"
Write-Host "  plugin stops loading, check Event Viewer under Windows Logs > System for 'audiodg'."
