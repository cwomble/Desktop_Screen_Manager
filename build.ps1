param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'Desktop_Screen_Manager.cs'
$outputDirectory = Join-Path $PSScriptRoot 'dist'
if (-not $OutputPath) { $OutputPath = Join-Path $outputDirectory 'Desktop_Screen_Manager.exe' }
$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $compilerPath) { throw 'The .NET Framework C# compiler was not found.' }
if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Source file not found: $sourcePath" }

New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
& $compilerPath /nologo /target:winexe /platform:anycpu "/out:$OutputPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll $sourcePath
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }
Write-Output "Built $OutputPath"
