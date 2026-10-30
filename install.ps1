<#
  Installs the Document Highlighter extension into one Studio Pro version (10.24.1 or newer).

  -Mode Ide (default, recommended)
      Shows the Studio Pro versions installed on this machine and asks which one to install into
      (or pass -Version 11.12.6 to skip the question). Copies the extension into that version's own folder:
        C:\Program Files\Mendix\<version>\modeler\extensions\DocHighlighter
      Nothing is written inside any app folder, so nothing can ever be committed.
      Needs administrator rights (you will get a UAC prompt). Works for every app you open in that version.
      Run it again to add the extension to another version.

  -Mode App -AppPath <app folder>
      Copies the extension into <app>\extensions\DocHighlighter and hides it from Git through
      .git\info\exclude (a local-only file that is never committed or pushed).
      Studio Pro must then be started with --enable-extension-development.

  Highlights themselves are always stored outside the app, in %LOCALAPPDATA%\MendixDocHighlighter.
#>
param(
    [ValidateSet("Ide", "App")]
    [string]$Mode = "Ide",
    [string]$AppPath,
    # Studio Pro version to install into, e.g. 11.12.6 or 10.24.10 (the start of the folder name is enough).
    # Without it, the script asks.
    [string]$Version,
    # Folder that holds the Studio Pro versions.
    [string]$MendixRoot = (Join-Path ${env:ProgramFiles} "Mendix"),
    # Used internally for the elevated window, so an error stays readable instead of the window closing.
    [switch]$PauseOnError
)
$ErrorActionPreference = "Stop"
trap {
    Write-Host "Install failed: $_" -ForegroundColor Red
    if ($PauseOnError) { Read-Host "Press Enter to close this window" | Out-Null }
    exit 1
}
. (Join-Path $PSScriptRoot "tools\StudioPro.ps1")
$source = Join-Path $PSScriptRoot "out\DocHighlighter"
$dll = Join-Path $source "DocHighlighter.MendixExtension.dll"
$excludeLine = "/extensions/$ExtensionFolder/"

if (-not (Test-Path $dll)) {
    & (Join-Path $PSScriptRoot "build.ps1")
}

if ($Mode -eq "Ide") {
    $required = Get-RequiredApiVersion $dll
    $all = Get-StudioProInstalls $MendixRoot
    $supported = @($all | Where-Object { $_.ApiVersion -ge $required })
    $tooOld = @($all | Where-Object { $_.ApiVersion -lt $required })
    if ($tooOld.Count -and -not $Version) {
        Write-Host "Not supported (older than $($required.ToString(3))): $(($tooOld | ForEach-Object Name) -join ', ')" -ForegroundColor DarkGray
    }
    if ($Version -and ($tooOld | Where-Object { $_.Name -eq $Version -or $_.Name.StartsWith("$Version.") })) {
        throw "Studio Pro $Version is too old; the extension needs $($required.ToString(3)) or newer."
    }

    $install = Select-StudioPro $supported $Version "install into"
    if (-not $install) { Write-Host "Cancelled; nothing was installed."; exit 0 }

    $target = Join-Path $install.ModelerDir "extensions\$ExtensionFolder"
    Assert-StudioProClosed $install
    if (-not (Test-Admin)) {
        Write-Host "Administrator rights are needed to write to $($install.ModelerDir). Requesting elevation..."
        $elevatedArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Mode Ide -Version `"$($install.Name)`" -MendixRoot `"$MendixRoot`" -PauseOnError"
        $process = Start-Process powershell.exe -Verb RunAs -ArgumentList $elevatedArgs -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "The elevated install reported an error (exit code $($process.ExitCode))." }
        Write-Host "Installed to $target"
        Write-Host "Start Studio Pro $($install.Name) normally; right-click anything in the App Explorer > Highlight."
        exit 0
    }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item "$source\*" $target -Force
    Write-Host "Installed to $target"
    Write-Host "Start Studio Pro $($install.Name) normally; right-click anything in the App Explorer > Highlight."
    exit 0
}

# App mode
if (-not $AppPath) { throw "-AppPath is required for -Mode App" }
$AppPath = (Resolve-Path $AppPath).Path
if (-not (Get-ChildItem $AppPath -Filter *.mpr)) { throw "$AppPath does not contain an .mpr file" }
$target = Join-Path $AppPath "extensions\$extensionFolder"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item "$source\*" $target -Force
Write-Host "Installed to $target"

$gitDir = Join-Path $AppPath ".git"
if (Test-Path $gitDir) {
    $exclude = Join-Path $gitDir "info\exclude"
    New-Item -ItemType Directory -Force (Split-Path $exclude) | Out-Null
    $lines = if (Test-Path $exclude) { Get-Content $exclude } else { @() }
    if ($lines -notcontains $excludeLine) {
        Add-Content -Path $exclude -Value $excludeLine -Encoding ascii
        Write-Host "Added '$excludeLine' to .git\info\exclude (local only, never committed)."
    }
    Push-Location $AppPath
    try {
        $status = git status --porcelain --untracked-files=all -- "extensions/$extensionFolder" 2>$null
        if ($status) { Write-Warning "Git still sees the extension files:`n$status" }
        else { Write-Host "Verified: git does not see the extension files." }
    }
    finally { Pop-Location }
}
elseif (Test-Path (Join-Path $AppPath ".svn")) {
    Write-Warning "This is an SVN app. SVN has no local-only ignore, so do NOT commit the 'extensions' folder; prefer -Mode Ide."
}

Write-Host "Start Studio Pro with:  studiopro.exe --enable-extension-development `"$((Get-ChildItem $AppPath -Filter *.mpr | Select-Object -First 1).FullName)`""
