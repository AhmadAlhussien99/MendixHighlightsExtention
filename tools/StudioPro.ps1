<#
  Shared by build.ps1, install.ps1 and uninstall.ps1 (dot-sourced): finds the Studio Pro versions installed
  on this machine and lets the user pick one.
#>

$ExtensionFolder = "DocHighlighter"
$DefaultMendixRoot = Join-Path ${env:ProgramFiles} "Mendix"
# The oldest Extensions API that has everything the extension uses (MenuExtension, the untyped model API).
# 10.3 and older lack them; 10.24.1 is the oldest version the code is known to compile against.
$MinimumApiVersion = [version]"10.24.1.0"   # four parts, to compare equal to assembly versions

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-StudioProClosed($install) {
    $exePath = Join-Path $install.ModelerDir "studiopro.exe"
    $running = Get-Process studiopro -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath }
    if ($running) { throw "Close Studio Pro $($install.Name) first; it keeps the extension DLL locked." }
}

<# Every Studio Pro under $MendixRoot, newest first, with the version of its Extensions API. #>
function Get-StudioProInstalls([string]$MendixRoot = $DefaultMendixRoot) {
    if (-not (Test-Path $MendixRoot)) { return @() }
    $installs = foreach ($dir in Get-ChildItem $MendixRoot -Directory) {
        $modeler = Join-Path $dir.FullName "modeler"
        $api = Join-Path $modeler "Mendix.StudioPro.ExtensionsAPI.dll"
        if (-not (Test-Path $api)) { continue }
        [pscustomobject]@{
            Name       = $dir.Name
            ModelerDir = $modeler
            ApiVersion = [Reflection.AssemblyName]::GetAssemblyName($api).Version
            Installed  = Test-Path (Join-Path $modeler "extensions\$ExtensionFolder")
        }
    }
    @($installs | Sort-Object ApiVersion -Descending)
}

<#
  The Extensions API version the built DLL was compiled against. .NET only loads the DLL in a Studio Pro whose API
  is this version or newer, so this is the real minimum for the build at hand.
#>
function Get-RequiredApiVersion([string]$dllPath) {
    if ($PSVersionTable.PSEdition -eq "Core") {
        $stream = [IO.File]::OpenRead($dllPath)
        try {
            $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader([Reflection.PortableExecutable.PEReader]::new($stream))
            foreach ($handle in $reader.AssemblyReferences) {
                $reference = $reader.GetAssemblyReference($handle)
                if ($reader.GetString($reference.Name) -eq "Mendix.StudioPro.ExtensionsAPI") { return $reference.Version }
            }
        }
        finally { $stream.Dispose() }
    }
    else {
        $reference = [Reflection.Assembly]::ReflectionOnlyLoadFrom($dllPath).GetReferencedAssemblies() |
            Where-Object Name -eq "Mendix.StudioPro.ExtensionsAPI"
        if ($reference) { return $reference.Version }
    }
    throw "$dllPath does not reference the Mendix Extensions API"
}

<#
  Returns the one install the user wants. With -Version it is picked without asking: an exact folder name
  (11.12.6) or the start of one (10.24.10 for 10.24.10.83478). Otherwise a numbered list is shown.
#>
function Select-StudioPro($installs, [string]$Version, [string]$action) {
    if ($installs.Count -eq 0) { throw "No Studio Pro version found to $action." }

    if ($Version) {
        $exact = @($installs | Where-Object Name -eq $Version)
        # @() around the whole if: PowerShell unwraps a one-item array, and a lone object has no Count in 5.1.
        $found = @(if ($exact.Count) { $exact } else { $installs | Where-Object { $_.Name.StartsWith("$Version.") } })
        if ($found.Count -eq 1) { return $found[0] }
        $names = ($installs | ForEach-Object Name) -join ", "
        if ($found.Count -eq 0) { throw "Studio Pro '$Version' is not available to $action. Available: $names" }
        throw "'$Version' matches several versions ($(($found | ForEach-Object Name) -join ', ')); use the full name."
    }

    Write-Host ""
    for ($i = 0; $i -lt $installs.Count; $i++) {
        $note = if ($installs[$i].Installed) { "  (extension installed)" } else { "" }
        Write-Host ("  [{0}] Studio Pro {1}{2}" -f ($i + 1), $installs[$i].Name, $note)
    }
    Write-Host ""
    while ($true) {
        $answer = Read-Host "Which version do you want to ${action}? Enter its number (or press Enter to cancel)"
        if ([string]::IsNullOrWhiteSpace($answer)) { return $null }
        $number = 0
        if ([int]::TryParse($answer.Trim(), [ref]$number) -and $number -ge 1 -and $number -le $installs.Count) {
            return $installs[$number - 1]
        }
        Write-Host "Please enter a number from 1 to $($installs.Count)." -ForegroundColor Yellow
    }
}
