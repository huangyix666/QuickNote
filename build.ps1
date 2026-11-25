$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not (Test-Path -LiteralPath '.\sqlite3mc.dll')) { throw '缺少 sqlite3mc.dll' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:密笺.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Program.cs MainForm.cs Store.cs
exit $LASTEXITCODE
