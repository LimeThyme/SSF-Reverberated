param(
    [Parameter(Mandatory=$true)]
    [string]$UnityEditorDirectory
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $UnityEditorDirectory 'Data\Tools\Roslyn\csc.exe'
$references = Join-Path $UnityEditorDirectory 'Data\MonoBleedingEdge\lib\mono\4.7.1-api'
$results = Join-Path $PSScriptRoot 'TestResults'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$executable = Join-Path $results 'MusicianEnsembleTests.exe'
$production = Join-Path (Split-Path $PSScriptRoot -Parent) 'SpriteSoundFramework-main\SpriteSoundEnsembles.cs'
& $compiler /nologo /target:exe "/out:$executable" "/reference:$references\mscorlib.dll" "/reference:$references\System.dll" "/reference:$references\System.Core.dll" (Join-Path $PSScriptRoot 'MusicianEnsembleTests.cs.txt') $production
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $executable | Tee-Object -FilePath (Join-Path $results 'results.txt')
if ($LASTEXITCODE -ne 0) { throw 'Conductor tests failed.' }
