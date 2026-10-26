# ============================================================================
# build.ps1 —— 一键编译整个工具链（图标 -> 主程序 -> 安装器）
#   用法:  powershell -ExecutionPolicy Bypass -File .\build.ps1
# 产物:
#   dist\Mi8iOS\Mi8iOS.exe      图形界面主程序
#   dist\Mi8iOS\engine\*.ps1    流水线引擎
#   dist\Mi8iOSSetup.exe        安装器（自带全部文件，单文件分发）
# ============================================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$pkg  = Join-Path $dist 'Mi8iOS'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }

function Step($t) { Write-Host "==> $t" -ForegroundColor Cyan }
function Die($t)  { Write-Host "[失败] $t" -ForegroundColor Red; exit 1 }

New-Item -ItemType Directory -Force -Path $pkg, (Join-Path $pkg 'engine') | Out-Null

# ---- 1. 图标 ---------------------------------------------------------------
Step '生成图标 app.ico'
& $csc /nologo /codepage:65001 /target:exe /out:"$src\iconforge.exe" "$src\iconforge.cs"
if ($LASTEXITCODE -ne 0) { Die 'iconforge 编译失败' }
& "$src\iconforge.exe" "$src\app.ico"
if ($LASTEXITCODE -ne 0) { Die '图标生成失败' }

$common = @(
    '/nologo', '/codepage:65001', '/target:winexe',
    ('/win32manifest:' + (Join-Path $src 'app.manifest')),
    '/optimize+', '/debug-'
)
$refs = @('/r:System.dll', '/r:System.Core.dll',
          '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')

# ---- 2. 主程序 -------------------------------------------------------------
Step '编译 Mi8iOS.exe'
& $csc @common @refs `
    ('/out:' + (Join-Path $pkg 'Mi8iOS.exe')) `
    ('/res:' + (Join-Path $src 'app.ico') + ',app.ico') `
    (Join-Path $src 'Mi8iOS.GUI.cs')
if ($LASTEXITCODE -ne 0) { Die '主程序编译失败' }

# ---- 3. 引擎与说明 ---------------------------------------------------------
Step '打包引擎 mi8-ios.ps1 + README.md'
Copy-Item (Join-Path $root 'mi8-ios.ps1') (Join-Path $pkg 'engine\mi8-ios.ps1') -Force
Copy-Item (Join-Path $root 'README.md')   (Join-Path $pkg 'README.md') -Force

# 引擎必须带 UTF-8 BOM（PS 5.1 中文 Windows 否则按 GBK 解析）
$b = [IO.File]::ReadAllBytes((Join-Path $pkg 'engine\mi8-ios.ps1'))
if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
    $txt = [Text.Encoding]::UTF8.GetString($b)
    [IO.File]::WriteAllText((Join-Path $pkg 'engine\mi8-ios.ps1'), $txt, (New-Object Text.UTF8Encoding($true)))
    Write-Host '    已补 BOM'
}

# ---- 4. 安装器 -------------------------------------------------------------
Step '编译 Mi8iOSSetup.exe'
& $csc @common @refs '/r:Microsoft.CSharp.dll' `
    ('/out:' + (Join-Path $dist 'Mi8iOSSetup.exe')) `
    ('/res:' + (Join-Path $src 'app.ico') + ',app.ico') `
    ('/res:' + (Join-Path $pkg 'Mi8iOS.exe') + ',payload.gui') `
    ('/res:' + (Join-Path $pkg 'engine\mi8-ios.ps1') + ',payload.engine') `
    ('/res:' + (Join-Path $pkg 'README.md') + ',payload.readme') `
    (Join-Path $src 'Mi8iOS.Setup.cs')
if ($LASTEXITCODE -ne 0) { Die '安装器编译失败' }

# ---- 5. 汇总 ---------------------------------------------------------------
Write-Host ''
Write-Host '编译完成：' -ForegroundColor Green
Get-ChildItem $dist -Recurse -File | ForEach-Object {
    '    {0,-42} {1,8:N0} bytes' -f $_.FullName.Substring($dist.Length + 1), $_.Length
}
Write-Host ''
Write-Host '  运行界面:   dist\Mi8iOS\Mi8iOS.exe' -ForegroundColor Gray
Write-Host '  分发安装包: dist\Mi8iOSSetup.exe' -ForegroundColor Gray
