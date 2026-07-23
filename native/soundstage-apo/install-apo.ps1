# Installs the Soundstage APO onto a playback device.
#
#   Run as Administrator:
#     powershell -ExecutionPolicy Bypass -File install-apo.ps1
#     powershell -ExecutionPolicy Bypass -File install-apo.ps1 -Uninstall
#
# Two separate things happen here, and it helps to keep them apart:
#
#   1. COM registration - teaching Windows that our CLSID lives in this DLL. Machine-wide, done once,
#      handled by the DLL's own DllRegisterServer.
#   2. Endpoint attachment - telling one specific playback device to run our plugin. Per device, and
#      the reason this script needs to ask which device you mean.
#
# Every value this script overwrites is saved first, under a Soundstage.Backup key beside it, and
# -Uninstall puts them all back. That matters because the effect properties are how the manufacturer's
# own audio software hooks in; clobbering them without a way back is how people end up with a device
# that has lost its features and no idea why.

[CmdletBinding()]
param(
    [switch]$Uninstall,
    # Substring of the device name, e.g. "Realtek" or "NVIDIA". Omit to be shown a list.
    [string]$Device
)

$ErrorActionPreference = "Stop"

$CLSID       = "{6F3C9A21-4E7B-4B36-9E1D-2A55C0D8E401}"
$RenderRoot  = "HKLM:\SYSTEM\CurrentControlSet\Control\MMDevices\Audio\Render"
$FxGuid      = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}"
$BackupKey   = "Soundstage.Backup"

# The device-name property, and the effect slots we write.
$NameProp    = "{a45c254e-df1c-4efd-8020-67d146a850e0},2"

# Windows looks for effects under several property ids. The pair below is the "mode effect" slot -
# post-mix, so it sees the final multichannel stream rather than one app's stereo. 6 is the original
# id and 14 the one added in Windows 10; both are still consulted, and writing both is what every
# shipping APO does, because which one applies depends on the driver's own registration style.
$ModeFxProps = @("$FxGuid,6", "$FxGuid,14")

# PKEY_FX_Association - which pin the effect belongs to. KSNODETYPE_ANY: apply regardless.
$AssocProp   = "$FxGuid,0"
$AssocAny    = "{00000000-0000-0000-0000-000000000000}"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = [Security.Principal.WindowsPrincipal]::new($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run as Administrator. Right-click PowerShell -> Run as administrator."
    }
}

function Get-RenderEndpoints {
    Get-ChildItem $RenderRoot -ErrorAction SilentlyContinue | ForEach-Object {
        $props = Join-Path $_.PSPath "Properties"
        $name = $null
        try { $name = (Get-ItemProperty -Path $props -Name $NameProp -ErrorAction Stop).$NameProp } catch {}
        $state = $null
        try { $state = (Get-ItemProperty -Path $_.PSPath -Name "DeviceState" -ErrorAction Stop).DeviceState } catch {}
        if ($name) {
            [pscustomobject]@{
                Id      = $_.PSChildName
                Name    = $name
                Active  = ($state -eq 1)
                Path    = $_.PSPath
                FxPath  = Join-Path $_.PSPath "FxProperties"
            }
        }
    }
}

function Select-Endpoint {
    $all = @(Get-RenderEndpoints | Where-Object { $_.Active })
    if ($all.Count -eq 0) { throw "No active playback devices found." }

    if ($Device) {
        $hit = @($all | Where-Object { $_.Name -like "*$Device*" })
        if ($hit.Count -eq 1) { return $hit[0] }
        if ($hit.Count -gt 1) { throw "'$Device' matches $($hit.Count) devices. Be more specific." }
        throw "No active playback device matching '$Device'."
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

function Backup-Value($fxPath, $name) {
    $backupPath = Join-Path $fxPath $BackupKey
    if (-not (Test-Path $backupPath)) { New-Item -Path $backupPath -Force | Out-Null }

    # Only ever back up once. Running install twice must not overwrite the real original with our own
    # value - that would make the uninstall restore Soundstage instead of removing it.
    $existing = $null
    try { $existing = (Get-ItemProperty -Path $backupPath -Name $name -ErrorAction Stop).$name } catch {}
    if ($null -ne $existing) { return }

    $current = ""
    try { $current = (Get-ItemProperty -Path $fxPath -Name $name -ErrorAction Stop).$name } catch {}
    New-ItemProperty -Path $backupPath -Name $name -Value $current -PropertyType String -Force | Out-Null
}

function Restore-Value($fxPath, $name) {
    $backupPath = Join-Path $fxPath $BackupKey
    $saved = $null
    try { $saved = (Get-ItemProperty -Path $backupPath -Name $name -ErrorAction Stop).$name } catch {}

    if ($null -eq $saved) { return }
    if ($saved -eq "") {
        Remove-ItemProperty -Path $fxPath -Name $name -ErrorAction SilentlyContinue
    } else {
        New-ItemProperty -Path $fxPath -Name $name -Value $saved -PropertyType String -Force | Out-Null
    }
    Remove-ItemProperty -Path $backupPath -Name $name -ErrorAction SilentlyContinue
}

function Restart-AudioService {
    Write-Host "Restarting the audio service so the change takes effect..."
    # AudioEndpointBuilder holds Audiosrv as a dependent; -Force takes both down together.
    Restart-Service -Name AudioEndpointBuilder -Force
    Start-Sleep -Seconds 2
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
        if (-not (Test-Path $ep.FxPath)) { continue }
        $isOurs = $false
        foreach ($p in $ModeFxProps) {
            $v = $null
            try { $v = (Get-ItemProperty -Path $ep.FxPath -Name $p -ErrorAction Stop).$p } catch {}
            if ($v -eq $CLSID) { $isOurs = $true }
        }
        if (-not $isOurs) { continue }

        foreach ($p in $ModeFxProps) { Restore-Value $ep.FxPath $p }
        Restore-Value $ep.FxPath $AssocProp

        $backupPath = Join-Path $ep.FxPath $BackupKey
        if (Test-Path $backupPath) {
            if (-not (Get-Item $backupPath).Property) { Remove-Item $backupPath -Force }
        }
        Write-Host "  detached from: $($ep.Name)"
        $touched++
    }
    Write-Host "  $touched device(s) restored."

    if (Test-Path $dst) {
        & regsvr32.exe /s /u $dst
        Write-Host "  unregistered the COM class."
    }

    Restart-AudioService

    # Delete last - audiodg has to let go of it first.
    if (Test-Path $dst) {
        try { Remove-Item $dst -Force; Write-Host "  removed $dst" }
        catch { Write-Warning "Could not delete $dst (still loaded). Reboot and delete it by hand." }
    }

    Write-Host ""
    Write-Host "Done. Your devices are back to how they were."
    return
}

if (-not (Test-Path $src)) { throw "SoundstageApo.dll not found. Run build.ps1 first." }

$target = Select-Endpoint

Write-Host ""
Write-Host "Installing Soundstage onto: $($target.Name)"

Copy-Item $src $dst -Force
Write-Host "  copied the plugin to System32"

# audiodg.exe runs stripped of privileges, under a restricted token. It has to be able to read the
# DLL, and the default System32 permissions already allow that - but a file copied from a user
# profile can arrive carrying inherited ACLs that don't. Re-inheriting from System32 fixes it.
$acl = Get-Acl $dst
$acl.SetAccessRuleProtection($false, $false)
Set-Acl -Path $dst -AclObject $acl

$reg = Start-Process -FilePath "regsvr32.exe" -ArgumentList "/s", "`"$dst`"" -Wait -PassThru
if ($reg.ExitCode -ne 0) { throw "regsvr32 failed ($($reg.ExitCode))." }
Write-Host "  registered the COM class"

if (-not (Test-Path $target.FxPath)) { New-Item -Path $target.FxPath -Force | Out-Null }

foreach ($p in $ModeFxProps) {
    Backup-Value $target.FxPath $p
    New-ItemProperty -Path $target.FxPath -Name $p -Value $CLSID -PropertyType String -Force | Out-Null
}
Backup-Value $target.FxPath $AssocProp
New-ItemProperty -Path $target.FxPath -Name $AssocProp -Value $AssocAny -PropertyType String -Force | Out-Null
Write-Host "  attached to the device (previous values saved)"

Restart-AudioService

Write-Host ""
Write-Host "Installed."
Write-Host ""
Write-Host "What to expect:"
Write-Host "  * Play something. If you hear audio, the plugin loaded and is passing sound through."
Write-Host "  * Open Soundstage and move a control - it publishes settings to the plugin live."
Write-Host "  * If audio goes silent or the device disappears, run:"
Write-Host "      powershell -ExecutionPolicy Bypass -File install-apo.ps1 -Uninstall"
Write-Host ""
Write-Host "  Windows disables an effect that misbehaves rather than breaking your sound. If the"
Write-Host "  plugin stops loading, check Event Viewer under Windows Logs > System for 'audiodg'."
