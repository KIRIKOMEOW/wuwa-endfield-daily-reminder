param()
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskDist = Join-Path $PSScriptRoot 'dist'
# A fresh stage keeps prior source-build bin/obj files out of repeat archives.
$taskVersion = ([xml](Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $PSScriptRoot 'GameDailyWidget.csproj'))).Project.PropertyGroup.Version
$taskStage = Join-Path $taskDist ("source-v$taskVersion-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
$taskExtensions = @('.cs','.csproj','.xaml','.md','.txt','.ico','.manifest','.ps1','.sln')
# The shared Endfield sources and their checks live inside GameDailyWidget (Endfield\, tests\).
foreach ($taskName in @('GameDailyWidget','WuwaLauncherProbe')) {
    $taskRoot = Join-Path $taskWorkspace $taskName
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskRoot -Recurse -File) {
        $taskRelative = $taskFile.FullName.Substring($taskRoot.Length + 1)
        if ($taskRelative -match '(^|\\)(bin|obj|dist|design|verification|artifacts|\.git)(\\|$)') { continue }
        if ($taskName -eq 'GameDailyWidget' -and $taskFile.Name -like 'Codex*提示词*.md') { continue }
        if ($taskFile.Extension -notin $taskExtensions -and $taskFile.Name -ne '.gitignore') { continue }
        $taskTarget = Join-Path (Join-Path $taskStage $taskName) $taskRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget -Force
    }
}
$taskVerification = Join-Path $taskStage 'GameDailyWidget\verification'
New-Item -ItemType Directory -Path $taskVerification -Force | Out-Null
$taskEvidence = Join-Path $PSScriptRoot 'verification'
Copy-Item -LiteralPath (Join-Path $taskEvidence 'fixtures') -Destination $taskVerification -Recurse
foreach ($taskFile in Get-ChildItem -LiteralPath $taskEvidence -File) {
    if ($taskFile.Name -like 'v1.6.2-*.json' -or $taskFile.Name -like 'v1.6.1-*.json' -or $taskFile.Name -like 'v1.6.0-*.json' -or $taskFile.Name -like 'v1.5.3-*.json' -or $taskFile.Name -like 'v1.5.2-*.json' -or $taskFile.Name -like 'v1.5.1-*.json' -or $taskFile.Name -like 'v1.5.0-*.json' -or $taskFile.Name -like 'v1.4-*' -or $taskFile.Name -like 'v1.4.1-*' -or $taskFile.Name -like 'v1.4.2-*' -or $taskFile.Name -like 'v1.4.3-*' -or $taskFile.Name -like 'v1.4.4-*' -or $taskFile.Name -in @('接口字段核对.md','限时活动开发记录.md')) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskVerification
    }
}
foreach ($taskName in @('v1.3.6贴边误展开修复.md','v1.3.6-integration-checks.json','v1.3.6-lifecycle-checks.json','v1.3.6-reset-window-checks.json','v1.3.6-animation-frames-checks.json','v1.3.6-original-integrity.json','v1.3.6-portable-smoke.json','v1.3.6-published-lifecycle-checks.json','v1.3.6-deployment.json')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
foreach ($taskName in @('v1.3.5动画流畅度与验证.md','v1.3.5-before-animation-frames.json','v1.3.5-after-animation-frames.json','v1.3.5-integration-checks.json','v1.3.5-lifecycle-checks.json','v1.3.5-reset-window-checks.json','v1.3.5-original-integrity.json','v1.3.5-portable-smoke.json','v1.3.5-published-animation-frames.json','v1.3.5-published-lifecycle-checks.json','v1.3.5-deployment.json')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
foreach ($taskName in @('v1.3.4-published-lifecycle-checks.json','v1.3.4-migration.json','v1.3.4-deployment.json')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
foreach ($taskName in @('v1.3.4即时收起与提醒延时.md','v1.3.4-integration-checks.json','v1.3.4-lifecycle-checks.json','v1.3.4-reset-window-checks.json','v1.3.4-original-integrity.json','v1.3.4-original-checks.json','v1.3.4-portable-smoke.json','v1.3.4-native-toast.json','v1.3.4-settings-menu.png','v1.3.4-reminder-delay.png')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
foreach ($taskName in @('v1.3.2移除桌面固定与验证.md','v1.3.2-integration-checks.json','v1.3.2-lifecycle-checks.json','v1.3.2-reset-window-checks.json','v1.3.2-original-integrity.json','v1.3.2-portable-smoke.json','v1.3.2-settings-menu.png')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
foreach ($taskName in @('v1.3.1界面验收与修复.md','v1.3.1-computer-use.json','v1.3.1-integration-checks.json','v1.3.1-lifecycle-checks.json','v1.3.1-reset-window-checks.json','v1.3.1-native-toast.json','v1.3.1-original-integrity.json','v1.3.1-portable-smoke.json')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
foreach ($taskName in @('v1.3.0更新与验证.md','终末地换日隔离测试.md','v1.3.0-reset-window-checks.json','v1.3.0-integration-checks.json','v1.3.0-lifecycle-checks.json','v1.3.0-original-checks.json','v1.3.0-original-integrity.json','v1.3.0-portable-smoke.json')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
foreach ($taskName in @('v1.2.1更新与验证.md','v1.2.1-integration-checks.json','v1.2.1-lifecycle-checks.json','v1.2.1-native-toast.json')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
foreach ($taskName in @('v1.2更新与验证.md','v1.2-integration-checks.json','v1.2-lifecycle-checks.json','v1.2-edge-animation-checks.json','v1.2-native-toast.json','v1.2-portable-smoke.json','v1.2-original-integrity.json','labels-wuwa-agent.png','labels-endfield-agent.png','v1.2-menu-settings.png')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
foreach ($taskName in @('v1.1更新与验证.md','连续天数的数据来源.md','v1.1-original-checks.json','v1.1-integration-checks.json','v1.1-lifecycle-checks.json','v1.1-edge-window-checks.json','v1.1-portable-smoke.json','v1.1-original-integrity.json','v1.1-preview-integrity.json','face-wuwa-agent.png','face-endfield-agent.png','details-wuwa-agent.png','details-endfield-agent.png','menu-settings-agent.png','menu-size-agent-v2.png','menu-opacity-agent.png','menu-wuwa-agent.png','menu-wuwa-refresh-agent.png','menu-endfield-agent.png','menu-endfield-refresh-agent.png')) {
    $taskSource = Join-Path (Join-Path $PSScriptRoot 'verification') $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $taskVerification -Force }
}
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $taskStage | Select-Object -ExpandProperty FullName) -DestinationPath (Join-Path $taskDist "双游戏日常组件-v$taskVersion-源码.zip") -Force
Write-Output $taskStage
