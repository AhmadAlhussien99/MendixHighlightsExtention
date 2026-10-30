<#
  Builds the extension without needing a .NET SDK.
  It uses the Roslyn compiler that ships with Studio Pro, driven by tools\mxcsc.
  Output: .\out\Highlights\ (Highlights.MendixExtension.dll + manifest.json)

  It compiles against the OLDEST installed Studio Pro that is new enough, because the DLL then also loads in every
  newer version. Pass -StudioProDir "<Mendix>\<version>\modeler" to compile against a specific one.
#>
param(
    [string]$StudioProDir
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "tools\StudioPro.ps1")

if (-not $StudioProDir) {
    $oldest = Get-StudioProInstalls | Where-Object { $_.ApiVersion -ge $MinimumApiVersion } | Select-Object -Last 1
    if (-not $oldest) { throw "Building needs Studio Pro $MinimumApiVersion or newer under $DefaultMendixRoot." }
    $StudioProDir = $oldest.ModelerDir
    Write-Host "Compiling against Studio Pro $($oldest.Name); the DLL loads in this version and newer."
    if ($oldest.ApiVersion -gt $MinimumApiVersion) {
        Write-Warning "Studio Pro $MinimumApiVersion is not installed, so this build will not load in versions older than $($oldest.Name)."
    }
}
$root = $PSScriptRoot
$src = Join-Path $root "DocHighlighter"
$out = Join-Path $root "out\$ExtensionFolder"
$tools = Join-Path $root "tools\mxcsc"
# One compiler host per Studio Pro version: each ships its own Roslyn, which needs that version's .NET runtime.
$hostDll = Join-Path $tools "bin\$(Split-Path (Split-Path $StudioProDir) -Leaf)\mxcsc.dll"

function Get-RuntimeDir([string]$framework, [int]$major) {
    $base = Join-Path ${env:ProgramFiles} "dotnet\shared\$framework"
    $dir = Get-ChildItem $base -Directory | Where-Object { $_.Name -like "$major.*" } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $dir) { throw "$framework $major.x runtime not found under $base" }
    $dir.FullName
}

function Test-Managed([string]$path) {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($path); $true } catch { $false }
}

# Reference the same .NET version this Studio Pro runs on (net8.0 for 10.24-11.5, net10.0 from 11.6).
$tfm = (Get-Content (Join-Path $StudioProDir "studiopro.runtimeconfig.json") -Raw | ConvertFrom-Json).runtimeOptions.tfm
$dotnetMajor = [int]($tfm -replace '^net(\d+)\..*$', '$1')
$coreDir = Get-RuntimeDir "Microsoft.NETCore.App" $dotnetMajor
$desktopDir = Get-RuntimeDir "Microsoft.WindowsDesktop.App" $dotnetMajor

# 1. Bootstrap the Roslyn host with the C# compiler built into Windows (only once).
if (-not (Test-Path $hostDll)) {
    New-Item -ItemType Directory -Force (Split-Path $hostDll) | Out-Null
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    $refs = "System.Private.CoreLib","System.Runtime","System.Collections","System.Linq","System.Console",
            "System.Collections.Immutable","System.Runtime.Extensions","System.IO.FileSystem" |
        ForEach-Object { "/r:$coreDir\$_.dll" }
    $refs += "/r:$StudioProDir\Microsoft.CodeAnalysis.dll", "/r:$StudioProDir\Microsoft.CodeAnalysis.CSharp.dll"
    & $csc /nologo /noconfig /nostdlib+ /nowarn:1701 /target:exe "/out:$hostDll" @refs (Join-Path $tools "Program.cs")
    if ($LASTEXITCODE -ne 0) { throw "Bootstrapping the compiler host failed" }
    Copy-Item "$StudioProDir\Microsoft.CodeAnalysis.dll", "$StudioProDir\Microsoft.CodeAnalysis.CSharp.dll" (Split-Path $hostDll)
    "{`"runtimeOptions`":{`"tfm`":`"$tfm`",`"framework`":{`"name`":`"Microsoft.NETCore.App`",`"version`":`"$dotnetMajor.0.0`"}}}" |
        Set-Content -Encoding ascii (Join-Path (Split-Path $hostDll) "mxcsc.runtimeconfig.json")
}

# 2. Reference list: .NET + WPF + the Studio Pro extensions API.
#    WindowsDesktop goes first: NETCore.App only has a WindowsBase facade, the real one is in WindowsDesktop.
$refList = Join-Path $env:TEMP "dochighlighter-refs.txt"
$seen = @{}
$refs = foreach ($file in (Get-ChildItem "$desktopDir\*.dll") + (Get-ChildItem "$coreDir\*.dll")) {
    if (-not $seen.ContainsKey($file.Name) -and (Test-Managed $file.FullName)) { $seen[$file.Name] = $true; $file.FullName }
}
$refs += "$StudioProDir\Mendix.StudioPro.ExtensionsAPI.dll", "$StudioProDir\System.ComponentModel.Composition.dll"
$refs | Set-Content -Encoding utf8 $refList

# 3. Compile.
New-Item -ItemType Directory -Force $out | Out-Null
$sources = Get-ChildItem "$src\*.cs" | ForEach-Object FullName
& dotnet exec $hostDll (Join-Path $out $ExtensionDll) $refList @sources
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item "$src\manifest.json" $out -Force
Write-Host "Built -> $out"
