param([string]$OutputName = 'Miko.exe')
$ErrorActionPreference = 'Stop'
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName) { throw 'OutputName must be a filename.' }
$taskSource = $PSScriptRoot
$taskOut = Split-Path -Parent $taskSource
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$taskFacade = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET/assembly/GAC_MSIL/netstandard') -Filter netstandard.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName
$taskReferences = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Net.Http.dll','System.Security.dll',
    "$taskFramework/WPF/WindowsBase.dll", "$taskFramework/WPF/PresentationCore.dll", "$taskFramework/WPF/PresentationFramework.dll",
    'System.Windows.Forms.dll','System.Drawing.dll',"$taskOut/NAudio.Core.dll", "$taskOut/NAudio.Wasapi.dll", $taskFacade)
$taskCompilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:' + (Join-Path $taskOut $OutputName)))
$taskCompilerArgs += $taskReferences | ForEach-Object { '/reference:' + $_ }
$taskCompilerArgs += Get-ChildItem -LiteralPath $taskSource -Filter '*.xaml' | ForEach-Object { '/resource:' + $_.FullName + ',' + $_.Name }
$taskCompilerArgs += Get-ChildItem -LiteralPath $taskSource -Filter '*.cs' | ForEach-Object { $_.FullName }
& "$taskFramework/csc.exe" @taskCompilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output "Built: $taskOut/$OutputName"
