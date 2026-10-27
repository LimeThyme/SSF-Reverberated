param(
 [Parameter(Mandatory=$true)][string]$UnityEditorDirectory,
 [Parameter(Mandatory=$true)][string]$HarmonyAssembly
)
$ErrorActionPreference='Stop'
$compiler=Join-Path $UnityEditorDirectory 'Data/Tools/Roslyn/csc.exe'
$mono=Join-Path $UnityEditorDirectory 'Data/MonoBleedingEdge/bin/mono.exe'
$refs=Join-Path $UnityEditorDirectory 'Data/MonoBleedingEdge/lib/mono/4.7.1-api'
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'SpriteSoundFramework-main'
$results=Join-Path $PSScriptRoot 'TestResults/Harmony'
New-Item -ItemType Directory -Force -Path $results | Out-Null
if ([Reflection.AssemblyName]::GetAssemblyName($HarmonyAssembly).Version.ToString() -ne '2.4.2.0') {throw 'Tests require the real Harmony 2.4.2 assembly.'}
Copy-Item -LiteralPath $HarmonyAssembly -Destination (Join-Path $results '0Harmony.dll') -Force
foreach ($variant in @('Vanilla','NPCFR')) {
 $exe=Join-Path $results ($variant+'.exe')
 $arguments=@('/nologo','/target:exe',"/out:$exe","/reference:$refs/mscorlib.dll","/reference:$refs/System.dll","/reference:$refs/System.Core.dll","/reference:$HarmonyAssembly",(Join-Path $PSScriptRoot 'HarmonyMigrationTests.cs.txt'),"$source/SpriteSoundHarmonyBootstrap.cs","$source/SpriteSound_NPCFRPatch.cs")
 if ($variant -eq 'NPCFR') {$arguments+='/define:NPCFR'}
 & $compiler @arguments
 if ($LASTEXITCODE -ne 0) {throw 'Migration test compilation failed.'}
 & $mono $exe | Tee-Object -FilePath (Join-Path $results ($variant+'.txt'))
 if ($LASTEXITCODE -ne 0) {throw "Migration tests failed: $variant"}
}
