param([switch]$Gui)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) ('vault-checks-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$exe = Join-Path $work 'VaultChecks.exe'
$dll = Join-Path $work 'sqlite3mc.dll'
try {
    Copy-Item -LiteralPath (Join-Path $root 'sqlite3mc.dll') -Destination $dll
    $sources = @((Join-Path $root 'Store.cs'), (Join-Path $PSScriptRoot 'VaultChecks.cs'))
    if ($Gui) { $sources = @('/r:System.Windows.Forms.dll', (Join-Path $PSScriptRoot 'GuiChecks.cs')) }
    & $compiler /nologo /codepage:65001 /platform:x64 /optimize+ "/out:$exe" @sources
    if ($LASTEXITCODE -ne 0) { throw '存储测试编译失败' }
    if ($Gui) { & $exe (Join-Path $root '密笺.exe') } else { & $exe }
    if ($LASTEXITCODE -ne 0) { throw '存储测试未通过' }
} finally {
    foreach ($file in @($exe, $dll)) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    [IO.Directory]::Delete($work)
}
