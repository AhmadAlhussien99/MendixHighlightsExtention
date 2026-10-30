<#
  Removes the Document Highlighter extension.
    .\uninstall.ps1                       # asks which Studio Pro version to remove it from (UAC prompt)
    .\uninstall.ps1 -Version 11.12.6      # removes it from that version without asking
    .\uninstall.ps1 -Mode App -AppPath X  # remove the app-level install and its .git\info\exclude line
  Add -RemoveHighlights to also delete the saved highlights in %LOCALAPPDATA%\MendixDocHighlighter.
#>
param(
    [ValidateSet("Ide", "App")]
    [string]$Mode = "Ide",
    [string]$AppPath,
    # Studio Pro version to remove the extension from, e.g. 11.12.6 or 10.24.10. Without it, the script asks.
    [string]$Version,
    # Folder that holds the Studio Pro versions.
    [string]$MendixRoot = (Join-Path ${env:ProgramFiles} "Mendix"),
    [switch]$RemoveHighlights,
    # Used internally for the elevated window, so an error stays readable instead of the window closing.
    [switch]$PauseOnError
)
$ErrorActionPreference = "Stop"
trap {
    Write-Host "Uninstall failed: $_" -ForegroundColor Red
    if ($PauseOnError) { Read-Host "Press Enter to close this window" | Out-Null }
    exit 1
}
. (Join-Path $PSScriptRoot "tools\StudioPro.ps1")
$excludeLine = "/extensions/$ExtensionFolder/"

if ($Mode -eq "Ide") {
    $installed = @(Get-StudioProInstalls $MendixRoot | Where-Object Installed)
    if ($installed.Count -eq 0) {
        Write-Host "The extension is not installed in any Studio Pro version under $MendixRoot."
    }
    elseif ($install = Select-StudioPro $installed $Version "remove the extension from") {
        $target = Join-Path $install.ModelerDir "extensions\$ExtensionFolder"
        Assert-StudioProClosed $install
        if (Test-Admin) {
            if (Test-Path $target) { Remove-Item $target -Recurse -Force }
            Remove-LegacyExtensions (Split-Path $target)
        }
        else {
            $elevatedArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Mode Ide -Version `"$($install.Name)`" -MendixRoot `"$MendixRoot`" -PauseOnError"
            $process = Start-Process powershell.exe -Verb RunAs -ArgumentList $elevatedArgs -Wait -PassThru
            if ($process.ExitCode -ne 0) { throw "The elevated uninstall reported an error (exit code $($process.ExitCode))." }
        }
        Write-Host "Removed $target"
    }
    else {
        Write-Host "Cancelled; nothing was removed."
    }
}
else {
    if (-not $AppPath) { throw "-AppPath is required for -Mode App" }
    $target = Join-Path $AppPath "extensions\$extensionFolder"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force; Write-Host "Removed $target" }
    Remove-LegacyExtensions (Split-Path $target)
    $extensionsDir = Join-Path $AppPath "extensions"
    if ((Test-Path $extensionsDir) -and -not (Get-ChildItem $extensionsDir -Force)) { Remove-Item $extensionsDir }
    $exclude = Join-Path $AppPath ".git\info\exclude"
    if (Test-Path $exclude) {
        $lines = Get-Content $exclude
        if ($lines -contains $excludeLine) {
            $lines | Where-Object { $_ -ne $excludeLine } | Set-Content $exclude -Encoding ascii
            Write-Host "Removed '$excludeLine' from .git\info\exclude"
        }
    }
}

if ($RemoveHighlights) {
    $store = Join-Path $env:LOCALAPPDATA "MendixDocHighlighter"
    if (Test-Path $store) { Remove-Item $store -Recurse -Force; Write-Host "Removed $store" }
}
