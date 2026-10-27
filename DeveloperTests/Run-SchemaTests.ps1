param(
 [Parameter(Mandatory=$true)][string]$UnityEditorDirectory,
 [Parameter(Mandatory=$true)][string]$DfuProjectDirectory
)
$ErrorActionPreference='Stop'
$compiler=Join-Path $UnityEditorDirectory 'Data/Tools/Roslyn/csc.exe'
$mono=Join-Path $UnityEditorDirectory 'Data/MonoBleedingEdge/bin/mono.exe'
$api=Join-Path $UnityEditorDirectory 'Data/MonoBleedingEdge/lib/mono/4.7.1-api'
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'SpriteSoundFramework-main'
$results=Join-Path $PSScriptRoot 'TestResults/Schema'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$exe=Join-Path $results 'SchemaTests.exe'
$arguments=@('/nologo',"/out:$exe","/reference:$api/mscorlib.dll","/reference:$api/System.dll","/reference:$api/System.Core.dll")
foreach ($file in Get-ChildItem (Join-Path $UnityEditorDirectory 'Data/Managed/UnityEngine') -Filter 'UnityEngine*.dll') {
 $arguments+="/reference:$($file.FullName)"
}
foreach ($name in @('Assembly-CSharp','Assembly-CSharp-firstpass')) {
 $arguments+="/reference:$DfuProjectDirectory/Library/ScriptAssemblies/$name.dll"
}
$arguments+="/reference:$DfuProjectDirectory/Assets/Resources/FullSerializer - Unity.dll"
$arguments+=(Join-Path $PSScriptRoot 'SchemaTests.cs.txt')
& $compiler @arguments
if ($LASTEXITCODE -ne 0) {throw 'Schema test compilation failed.'}
& $mono $exe $source $DfuProjectDirectory $UnityEditorDirectory | Tee-Object -FilePath (Join-Path $results 'results.txt')
if ($LASTEXITCODE -ne 0) {throw 'DFU schema tests failed.'}
