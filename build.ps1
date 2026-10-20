param(
    [string]$GamePath = 'W:\Games\Welcome to the Game II',
    [string]$MonoPath = 'W:\Mono',
    [string]$OutputPath = (Join-Path $PSScriptRoot 'bin'),
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $MonoPath 'lib\mono\4.5\mcs.exe'
$taskMono = Join-Path $MonoPath 'bin\mono.exe'
$taskCore = Join-Path $GamePath 'BepInEx\core'
$taskManaged = Join-Path $GamePath 'WTTG2_Data\Managed'
$taskOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskDll = Join-Path $taskOutput 'WTTG2RememberSaveFix.dll'
$taskCompileArgs = @('-sdk:2', '-target:library', "-out:$taskDll")
foreach ($taskRef in @((Join-Path $taskCore 'BepInEx.dll'), (Join-Path $taskCore '0Harmony.dll'),
    (Join-Path $taskManaged 'Assembly-CSharp.dll'), (Join-Path $taskManaged 'UnityEngine.dll'),
    (Join-Path $taskManaged 'UnityEngine.CoreModule.dll'), (Join-Path $MonoPath 'lib\mono\2.0-api\System.Core.dll')))
{ $taskCompileArgs += "-r:$taskRef" }
$taskCompileArgs += (Join-Path $PSScriptRoot 'RememberScopePolicy.cs'), (Join-Path $PSScriptRoot 'WTTG2RememberSaveFix.cs')
& $taskMono $taskCompiler @taskCompileArgs
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
if ($Test) {
    $taskTestExe = Join-Path $taskOutput 'RememberFixTests.exe'
    & $taskMono $taskCompiler '-sdk:2' "-out:$taskTestExe" "-r:$(Join-Path $MonoPath 'lib\mono\2.0-api\System.Core.dll')" (Join-Path $PSScriptRoot 'RememberScopePolicy.cs') (Join-Path $PSScriptRoot 'RememberFixTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $taskTestExe $GamePath
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
Get-FileHash -LiteralPath $taskDll -Algorithm SHA256
