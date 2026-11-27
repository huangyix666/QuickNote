$ErrorActionPreference = 'Stop'

function Get-MaintenanceItem($path) {
    try { Get-Item -LiteralPath $path -Force }
    catch [System.Management.Automation.ItemNotFoundException] { return }
}

$dataPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'QuickNoteVault'
$programPath = Join-Path $PSScriptRoot '密笺.exe'
$libraryPath = Join-Path $PSScriptRoot 'sqlite3mc.dll'
$startupPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupName = 'QuickNoteVault'

Write-Host '密笺维护'
Write-Host '  1. 清空默认资料库'
Write-Host '  2. 卸载密笺并清空默认资料库'
Write-Host '  0. 取消'
$choice = Read-Host '请选择'
if ($choice -notin '1', '2') {
    Write-Host '操作已取消。'
    return
}

$uninstall = $choice -eq '2'
$phrase = if ($uninstall) { 'UNINSTALL' } else { 'CLEAR' }
Write-Host ''
Write-Host '将永久删除默认资料库及其数据库日志。'
Write-Host $dataPath
if ($uninstall) {
    Write-Host '还会移除此目录里的密笺程序、运行库和此安装的开机启动项。'
    Write-Host $PSScriptRoot
}

if (([string](Read-Host '第一次确认，请输入 Y 继续')).Trim() -ne 'Y') {
    Write-Host '操作已取消。'
    return
}
if (([string](Read-Host "第二次确认，请输入 $phrase 执行删除")).Trim() -cne $phrase) {
    Write-Host '操作已取消。'
    return
}

try {
    if (Get-Process -Name '密笺' -ErrorAction SilentlyContinue) {
        throw '密笺仍在运行。请从 Windows 托盘菜单点击「退出密笺」，再运行本脚本。'
    }

    $dataFolder = Get-MaintenanceItem $dataPath
    if ($dataFolder -and (!$dataFolder.PSIsContainer -or
            ($dataFolder.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
        throw "资料库目录不是普通文件夹。$dataPath"
    }

    $targets = @('vault.qnv', 'vault.qnv-wal', 'vault.qnv-shm', 'vault.qnv-journal') |
        ForEach-Object { Join-Path $dataPath $_ }
    if ($uninstall) {
        $targets += $programPath, $libraryPath
    }

    # Check all existing files before changing anything.
    $files = foreach ($path in $targets) {
        $item = Get-MaintenanceItem $path
        if (!$item) { continue }
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "维护目标不是普通文件。$path"
        }
        try {
            $handle = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $handle.Dispose()
        }
        catch {
            throw "无法独占访问文件。请确认密笺已退出，并检查文件权限。$path"
        }
        $path
    }

    if ($uninstall) {
        $startupKey = Get-MaintenanceItem $startupPath
        if ($startupKey) {
            try { $startupCommand = $startupKey.GetValue($startupName) }
            finally { $startupKey.Close() }
            if ($startupCommand -eq ('"{0}" --startup' -f $programPath)) {
                Remove-ItemProperty -LiteralPath $startupPath -Name $startupName
            }
        }
    }

    foreach ($path in $files) {
        Remove-Item -LiteralPath $path -Force
    }

    if ($uninstall) { Write-Host '密笺已卸载，默认资料库已清空。' }
    else { Write-Host '默认资料库已清空。' }
}
catch {
    Write-Host "维护失败。$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
