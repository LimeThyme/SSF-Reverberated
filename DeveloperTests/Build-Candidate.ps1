param(
 [Parameter(Mandatory=$true)][string]$UnityEditorDirectory,
 [Parameter(Mandatory=$true)][string]$DfuProjectDirectory,
 [Parameter(Mandatory=$true)][string]$HarmonyAssembly
)
# Compile outside Assets so Unity/Mod Builder cannot accidentally bundle Harmony.
# The generated staging folder contains only SSF's assembly, settings and manifest.
# It is input to Mod Builder, NOT an installable .dfmod or a tested platform build.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$src=Join-Path $root 'SpriteSoundFramework-main'
$out=Join-Path $root 'BuildOutput'
$stage=Join-Path $out 'SSF-Reverberated-142'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$compiler=Join-Path $UnityEditorDirectory 'Data/Tools/Roslyn/csc.exe'
$engine=Join-Path $UnityEditorDirectory 'Data/Managed/UnityEngine'
$api=Join-Path $UnityEditorDirectory 'Data/MonoBleedingEdge/lib/mono/4.7.1-api'
if ([Reflection.AssemblyName]::GetAssemblyName($HarmonyAssembly).Version.ToString() -ne '2.4.2.0') {throw 'Use a Harmony 2.4.2.0 reference (Lib.Harmony 2.4.2, net472).'}
$assembly=Join-Path $stage 'SpriteSoundFramework.dll.bytes'
# Remove a stale build only within this fixed package output location. A failed
# compile must not leave a previous DLL looking like a newly successful build.
if (Test-Path -LiteralPath $assembly) {Remove-Item -LiteralPath $assembly}
$argsList=@('/nologo','/target:library','/optimize+','/nowarn:0436',"/out:$assembly")
foreach ($f in Get-ChildItem -LiteralPath $engine -Filter 'UnityEngine*.dll') {$argsList+="/reference:$($f.FullName)"}
foreach ($name in @('mscorlib','System','System.Core')) {$argsList+="/reference:$api/$name.dll"}
foreach ($name in @('Assembly-CSharp','Assembly-CSharp-firstpass')) {$argsList+="/reference:$DfuProjectDirectory/Library/ScriptAssemblies/$name.dll"}
$argsList+="/reference:$HarmonyAssembly"
foreach ($f in Get-ChildItem -LiteralPath $src -Filter '*.cs') {$argsList+=$f.FullName}
& $compiler @argsList
if ($LASTEXITCODE -ne 0) {throw 'SSF compilation failed; do not package this output.'}
Copy-Item -LiteralPath (Join-Path $src 'modsettings.json') -Destination $stage -Force
$manifest=Get-Content -LiteralPath (Join-Path $src 'SpriteSoundFramework.dfmod.json') -Raw | ConvertFrom-Json
$manifest.Files=@('Assets/Untracked/ModBuilder/SSF-Reverberated-142/SpriteSoundFramework.dll.bytes','Assets/Untracked/ModBuilder/SSF-Reverberated-142/modsettings.json')
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $stage 'SpriteSoundFramework.dfmod.json')
Write-Output "Compilation passed. Stage for Mod Builder: $stage"
Write-Output 'Harmony was referenced, not copied. Build platform asset bundles separately in Unity.'
