param()
$ErrorActionPreference = 'Stop'
$taskProject = Join-Path $PSScriptRoot 'GameDailyWidget.csproj'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskVersion = ([xml](Get-Content -Raw -Encoding UTF8 -LiteralPath $taskProject)).Project.PropertyGroup.Version
$taskRelease = Join-Path $taskWorkspace "双游戏日常组件-v$taskVersion"
$taskDist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $taskRelease,$taskDist -Force | Out-Null
dotnet publish $taskProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $taskRelease
if ($LASTEXITCODE -ne 0) { throw '合并版发布失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $taskRelease '使用说明.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.txt') -Destination (Join-Path $taskRelease 'THIRD_PARTY_NOTICES.txt')
$taskVerification = Join-Path $PSScriptRoot 'verification\合并验证说明.md'
if (Test-Path -LiteralPath $taskVerification) { Copy-Item -LiteralPath $taskVerification -Destination (Join-Path $taskRelease '初版合并验证说明.md') }
$taskStreakVerification = Join-Path $PSScriptRoot 'verification\连续天数的数据来源.md'
if (Test-Path -LiteralPath $taskStreakVerification) { Copy-Item -LiteralPath $taskStreakVerification -Destination (Join-Path $taskRelease '连续天数的数据来源.md') }
$taskResetVerification = Join-Path $PSScriptRoot 'verification\终末地换日隔离测试.md'
if (Test-Path -LiteralPath $taskResetVerification) { Copy-Item -LiteralPath $taskResetVerification -Destination (Join-Path $taskRelease '终末地换日隔离测试.md') }
Compress-Archive -LiteralPath $taskRelease -DestinationPath (Join-Path $taskDist "双游戏日常组件-v$taskVersion-win-x64.zip") -Force
& (Join-Path $PSScriptRoot 'build-source-package.ps1')
Write-Output $taskRelease
