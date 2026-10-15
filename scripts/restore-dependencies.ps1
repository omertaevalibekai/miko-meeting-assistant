param([switch]$WithLocalWhisper)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCache = Join-Path $taskRoot '.deps/downloads'
[IO.Directory]::CreateDirectory($taskCache) | Out-Null

function Get-VerifiedArtifact([string]$Name, [string]$Url, [string]$Sha256) {
    $taskFile = Join-Path $taskCache $Name
    if (!(Test-Path -LiteralPath $taskFile)) {
        $taskPartial = $taskFile + '.partial'
        Invoke-WebRequest -Uri $Url -OutFile $taskPartial -UseBasicParsing
        if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA256).Hash -ne $Sha256) {
            throw "Checksum mismatch for $Name. Download was not installed."
        }
        Move-Item -LiteralPath $taskPartial -Destination $taskFile -Force
    }
    if ((Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Checksum mismatch for cached $Name. Remove that cache file and retry."
    }
    return $taskFile
}

$taskPackages = @(
    @{ Name='naudio.core'; Dll='NAudio.Core.dll'; Hash='794645DBFD30E4880663D52D9D9224D55C301FA54228FF623C95D572C7887347' },
    @{ Name='naudio.wasapi'; Dll='NAudio.Wasapi.dll'; Hash='C8396B8DBAB86A2619EDFD9158BEAF18C21CB86677099B5594950FD8651B9D1A' }
)
foreach ($taskPackage in $taskPackages) {
    $taskName = $taskPackage.Name
    $taskZip = Get-VerifiedArtifact "$taskName.zip" "https://api.nuget.org/v3-flatcontainer/$taskName/2.2.1/$taskName.2.2.1.nupkg" $taskPackage.Hash
    $taskExtracted = Join-Path $taskRoot ".deps/$taskName"
    Expand-Archive -LiteralPath $taskZip -DestinationPath $taskExtracted -Force
    Copy-Item -LiteralPath (Join-Path $taskExtracted ('lib/netstandard2.0/' + $taskPackage.Dll)) -Destination $taskRoot -Force
}

if ($WithLocalWhisper) {
    $taskZip = Get-VerifiedArtifact 'whisper-b5130-x64.zip' 'https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-bin-x64.zip' 'F9EC6C52A2E949B62AB51FA21D0D497958F9E41C3010C157C4E42932D5316F3C'
    $taskExtracted = Join-Path $taskRoot '.deps/whisper'
    Expand-Archive -LiteralPath $taskZip -DestinationPath $taskExtracted -Force
    $taskEngine = Join-Path $taskRoot 'engine'
    $taskModels = Join-Path $taskRoot 'models'
    [IO.Directory]::CreateDirectory($taskEngine) | Out-Null
    [IO.Directory]::CreateDirectory($taskModels) | Out-Null
    $taskCli = Get-ChildItem -LiteralPath $taskExtracted -Recurse -File -Filter 'whisper-cli.exe' | Select-Object -First 1
    if (!$taskCli) { throw 'whisper-cli.exe is missing from the verified archive.' }
    Copy-Item -LiteralPath $taskCli.FullName -Destination $taskEngine -Force
    Get-ChildItem -LiteralPath $taskCli.DirectoryName -File | Where-Object { $_.Name -eq 'whisper.dll' -or $_.Name -like 'ggml*.dll' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $taskEngine -Force
    }
    $taskBase = Get-VerifiedArtifact 'ggml-base.bin' 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin' '60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE'
    $taskVad = Get-VerifiedArtifact 'ggml-silero-v6.2.0.bin' 'https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin' '2AA269B785EEB53A82983A20501DDF7C1D9C48E33AB63A41391AC6C9F7FB6987'
    Copy-Item -LiteralPath $taskBase,$taskVad -Destination $taskModels -Force
}
Write-Output 'Verified dependencies are ready.'
