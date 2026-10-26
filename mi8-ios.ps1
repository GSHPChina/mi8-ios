#Requires -Version 5.1
<#
.SYNOPSIS
    小米8 (dipper / 骁龙845) 跑 iOS 16 —— 一键自动化工具

.DESCRIPTION
    把「装环境 → 下载 IPSW → 构建 iOS 根文件系统 → 编译 XNU 内核 → 生成机型配置 →
    打包 → 刷入真机」整条流水线封装成一条命令。所有重活在 WSL2 Ubuntu 里跑，
    Windows 侧只负责 fastboot/adb 和调度。

    全程不改动手机 boot/recovery/modem/persist 分区；iOS root 只写入 userdata。

.EXAMPLE
    .\mi8-ios.ps1              # 智能模式：体检 + 自动推进到下一步
    .\mi8-ios.ps1 status       # 只看进度
    .\mi8-ios.ps1 demo         # QEMU 里看 iOS 跑起来（不需要手机，零风险）
    .\mi8-ios.ps1 setup        # 装环境
    .\mi8-ios.ps1 ipsw         # 下载 IPSW（断点续传）
    .\mi8-ios.ps1 rootfs       # 构建 iOS 根文件系统镜像
    .\mi8-ios.ps1 kernel       # 编译 XNU 内核
    .\mi8-ios.ps1 build        # 从你的 boot.img 生成 dipper 配置 + 打包引导镜像
    .\mi8-ios.ps1 backup       # 备份手机关键分区
    .\mi8-ios.ps1 flash        # 刷入并启动
    .\mi8-ios.ps1 restore      # 打印一键回滚步骤

.PARAMETER Work
    工作目录。默认自动选一个剩余空间 >= 60GB 的盘，否则用 %USERPROFILE%\mi8-ios

.PARAMETER BootImg
    你手机的原厂 boot.img 路径（build 步骤需要）。也会自动到 $Work\phone\boot.img 找。

.PARAMETER Yes
    跳过破坏性操作的二次确认（flash / backup 仍然会单独要求输入 YES）
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('', 'auto', 'status', 'doctor', 'setup', 'ipsw', 'rootfs',
                 'kernel', 'demo', 'build', 'backup', 'flash', 'restore', 'dump', 'help')]
    [string]$Command = 'auto',

    [string]$Work = '',
    [string]$BootImg = '',
    [switch]$Yes
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ============================== 配置 ==============================

$RepoUrl    = 'https://github.com/edi194/ios-fajita'          # 小米8 移植基座（骁龙845，已跑通到主屏幕）
$HakiosUrl  = 'https://github.com/oosexgang/hackios-xnu-kernel' # 用于 demo：QEMU 里看真 iOS
$IpswName   = 'iPhone10,3,iPhone10,6_16.7.16_20H392_Restore.ipsw'
$IpswUrl    = 'https://updates.cdn-apple.com/2025FallFCS/fullrestores/122-77006/' +
              '1158E502-D76D-4226-AA06-ED5E30CD1111/' + $IpswName
$IpswBuild  = '20H392'
$Distro     = 'Ubuntu'
$WslUser    = 'mi8'

# ============================== UI ==============================

function Write-Head([string]$t) {
    Write-Host ''
    Write-Host ('=' * 62) -ForegroundColor DarkCyan
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ('=' * 62) -ForegroundColor DarkCyan
}
function Ok([string]$t)   { Write-Host "  [OK]   $t" -ForegroundColor Green }
function Info([string]$t) { Write-Host "  [ .. ] $t" -ForegroundColor Gray }
function Tip([string]$t)  { Write-Host "  [提示] $t" -ForegroundColor Yellow }
function Warn([string]$t) { Write-Host "  [注意] $t" -ForegroundColor Yellow }
function Die([string]$t, [int]$code = 1) {
    Write-Host "  [失败] $t" -ForegroundColor Red
    exit $code
}

# ============================== 路径 ==============================

function Get-FreeGB([string]$drive) {
    try { [math]::Round(((Get-PSDrive -Name $drive -PSProvider FileSystem).Free) / 1GB, 1) }
    catch { -1 }
}

function Resolve-Work {
    if ($Work) { return (New-Object IO.DirectoryInfo($Work)).FullName }
    $homeDrive = $env:SystemDrive.TrimEnd(':')
    if ((Get-FreeGB $homeDrive) -ge 60) { return (Join-Path $env:USERPROFILE 'mi8-ios') }
    # 家目录盘空间不够，挑剩余空间最大的固定盘
    $best = $null; $bestFree = 0
    foreach ($d in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Name -match '^[A-Z]$' })) {
        $f = Get-FreeGB $d.Name
        if ($f -gt $bestFree) { $bestFree = $f; $best = $d.Name }
    }
    if ($best -and $bestFree -ge 60) { return "${best}:\mi8-ios" }
    return (Join-Path $env:USERPROFILE 'mi8-ios')
}

function To-Wsl([string]$p) {
    if ($p -match '^([A-Za-z]):[\\/](.*)$') {
        return '/mnt/' + $Matches[1].ToLower() + '/' + ($Matches[2] -replace '\\', '/')
    }
    return $p
}

# ============================== WSL ==============================

# 调用原生 exe。PS 5.1 在 EAP=Stop 下，只要原生命令往 stderr 写了东西、
# 而我们又重定向了 stderr，就会抛 NativeCommandError 把脚本打死 —— 这里统一压掉。
function Invoke-Native {
    param([string]$Exe, [string[]]$ArgList)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try {
        # 2>&1：fastboot/gh 这类工具把正常信息写在 stderr，需要一起收
        $out = & $Exe @ArgList 2>&1
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = -1 }
    } catch {
        $out = $null; $code = -1
    } finally {
        $ErrorActionPreference = $prev
    }
    [pscustomobject]@{
        Output   = @($out | ForEach-Object { "$_" })
        ExitCode = [int]$code
    }
}

# wsl.exe 的 -l / --status 在「父进程没有可读 stdin」（GUI、计划任务等无控制台场景）
# 时会永远阻塞等输入（实测挂死 >8s）。查询类调用统一经 cmd 从 NUL 喂空输入，40ms 内必回。
function Invoke-WslQuery([string]$WslArgs) {
    return Invoke-Native 'cmd.exe' @('/d', '/c', ('wsl.exe ' + $WslArgs + ' < NUL'))
}

function Get-WslDistro {
    $env:WSL_UTF8 = '1'
    $r = Invoke-WslQuery '-l -q'
    if ($r.ExitCode -ne 0 -or -not $r.Output) { return $null }
    $names = @($r.Output | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($names.Count -eq 0) { return $null }
    foreach ($n in $names) { if ($n -eq $Distro) { return $Distro } }
    return $names[0]
}

function Test-WslOk {
    $env:WSL_UTF8 = '1'
    return ((Invoke-WslQuery '--status').ExitCode -eq 0)
}

function Install-WslDistro {
    Write-Head '安装 WSL2 + Ubuntu'
    if (-not (Test-WslOk)) {
        Info '启用 WSL 功能（可能弹出 UAC，请点“是”）...'
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -ArgumentList @(
            '-NoProfile', '-Command',
            'wsl --install --no-distribution | Out-Null; exit 0'
        )
        if (-not (Test-WslOk)) { Die 'WSL 启用失败，请手动执行: wsl --install --no-distribution' 10 }
    }
    $d = Get-WslDistro
    if (-not $d) {
        Info "下载 $Distro 镜像（约 500MB，请耐心等）..."
        $r = Invoke-Native 'wsl.exe' @('--install', '-d', $Distro, '--no-launch')
        if ($r.ExitCode -ne 0) { Die "安装 $Distro 失败，可手动执行: wsl --install -d $Distro --no-launch" 11 }
        Info '创建免密用户 mi8...'
        $u = @(
            'useradd -m -G sudo ' + $WslUser + ' 2>/dev/null || true'
            "echo '$WslUser ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/$WslUser"
            "printf '[user]\ndefault=$WslUser\n[interop]\nappendWindowsPath=true\n' > /etc/wsl.conf"
        ) -join '; '
        [void](Invoke-Native 'wsl.exe' @('-d', $Distro, '-u', 'root', '--', 'bash', '-c', $u))
        [void](Invoke-Native 'wsl.exe' @('--terminate', $Distro))
        $d = Get-WslDistro
        if (-not $d) { Die 'Ubuntu 装好了但没识别到，请重启电脑后重试' 12 }
    }
    Ok "WSL 发行版: $d"
    $script:Distro = $d
}

# 运行 lib/<name>.sh。-AllowExit 里的退出码不视为失败，直接返回该码。
function Invoke-Lib([string]$Name, [int[]]$AllowExit = @()) {
    $path = Join-Path (Join-Path $Work 'lib') "$Name.sh"
    if (-not (Test-Path $path)) { Die "缺少脚本 $path（先跑 setup）" 20 }
    Write-Host ''
    $wslPath = To-Wsl $path
    & wsl.exe -d $Distro -- bash $wslPath
    $code = $LASTEXITCODE
    if ($code -eq 0) { return 0 }
    if ($AllowExit -contains $code) { return $code }
    Die "步骤 $Name 失败 (exit $code)，日志见上方" 21
}

function Write-Lib([string]$Name, [string]$Body) {
    $dir = Join-Path $Work 'lib'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $header = @'
#!/usr/bin/env bash
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
export PATH="$HOME/.local/bin:$PATH"
W='__W__'
REPO="$W/repo"
HAKIOS="$W/hakios"
OUT="$W/out"
IPSW="$W/ipsw/__IPSW__"
IPSW_URL='__IPSWURL__'
REPO_URL='__REPO__'
HAKIOS_URL='__HAKIOS__'
# 国内网络：github.com 常年被重置，但 api.github.com / codeload / ssh.github.com 可用
GH_API='https://api.github.com'
GH_PROXY='https://gh-proxy.com/https://github.com'
mkdir -p "$W/ipsw" "$OUT" "$W/phone" "$W/dtb" "$W/xnu"
step(){ printf '\n\033[36m== %s ==\033[0m\n' "$*"; }
die(){ printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# 拉源码：直连 -> 镜像 -> 源码包，三种依次降级
clone_repo(){
  local url="$1" dest="$2" tar
  if [ -d "$dest/.git" ]; then echo "已存在: $dest"; return 0; fi
  if [ -d "$dest" ] && [ -n "$(ls -A "$dest" 2>/dev/null)" ]; then echo "已存在(非git): $dest"; return 0; fi
  mkdir -p "$(dirname "$dest")"
  if timeout 12 curl -sI -o /dev/null "https://github.com" 2>/dev/null; then
    echo "github.com 可直连"
    if timeout 90 git clone --depth 1 "$url" "$dest"; then return 0; fi
    rm -rf "$dest"
  fi
  echo "改走镜像 $GH_PROXY"
  if timeout 180 git clone --depth 1 "$GH_PROXY/${url#https://github.com/}" "$dest"; then return 0; fi
  rm -rf "$dest"
  echo "镜像 clone 失败，改用源码包"
  tar="$dest.tgz"
  if timeout 180 curl -fL --retry 3 -o "$tar" \
       "$GH_PROXY/${url#https://github.com/}/archive/refs/heads/main.tar.gz" || \
     timeout 180 curl -fL --retry 3 -o "$tar" \
       "https://codeload.github.com/${url#https://github.com/}/tar.gz/refs/heads/main"; then
    mkdir -p "$dest"
    tar -xzf "$tar" --strip-components=1 -C "$dest"
    rm -f "$tar"
    ( cd "$dest" && git init -q && git add -A && \
      git -c user.email=mi8@local -c user.name=mi8 commit -qm "import" )
    return 0
  fi
  rm -f "$tar"
  die "拉不到 $url（直连/镜像/源码包都失败）"
}

# gh CLI：apt 没有时从镜像拉官方二进制（github.com 被墙，走 gh-proxy）
install_gh(){
  command -v gh >/dev/null 2>&1 && return 0
  sudo -n apt-get install -y -qq gh >/dev/null 2>&1 && command -v gh >/dev/null 2>&1 && return 0
  echo "apt 没有 gh，从镜像下载官方二进制"
  local ver
  ver=$(curl -fsSL --max-time 20 "$GH_API/repos/cli/cli/releases/latest" \
        | python3 -c 'import json,sys;print(json.load(sys.stdin)["tag_name"].lstrip("v"))' 2>/dev/null || echo "2.103.2")
  curl -fL --retry 3 --max-time 300 -o /tmp/gh.tgz \
    "$GH_PROXY/cli/cli/releases/download/v${ver}/gh_${ver}_linux_amd64.tar.gz" || die "gh 下载失败"
  mkdir -p "$HOME/.local/tmp" "$HOME/.local/bin"
  tar -xzf /tmp/gh.tgz -C "$HOME/.local/tmp"
  find "$HOME/.local/tmp" -name gh -type f -path '*/bin/*' -exec cp {} "$HOME/.local/bin/gh" \;
  chmod +x "$HOME/.local/bin/gh"; rm -rf "$HOME/.local/tmp" /tmp/gh.tgz
  grep -q '.local/bin' "$HOME/.bashrc" 2>/dev/null || echo 'export PATH="$HOME/.local/bin:$PATH"' >> "$HOME/.bashrc"
  command -v gh >/dev/null 2>&1 || die "gh 装不上"
  echo "gh: $(gh --version | head -1)"
}
'@
    $map = [ordered]@{
        '__W__'       = (To-Wsl $Work)
        '__IPSW__'    = $IpswName
        '__IPSWURL__' = $IpswUrl
        '__REPO__'    = $RepoUrl
        '__HAKIOS__'  = $HakiosUrl
    }
    foreach ($k in $map.Keys) { $header = $header.Replace($k, $map[$k]) }
    # bash 脚本写成 UTF-8 无 BOM：BOM 会让 bash 报语法错，中文 echo 才不会乱码
    [System.IO.File]::WriteAllText(
        (Join-Path $dir "$Name.sh"),
        ($header + "`n" + $Body),
        ([System.Text.UTF8Encoding]::new($false)))
}

# ============================== 依赖安装（Windows 侧） ==============================

function Ensure-WinTool([string]$wingetId, [string]$exe, [string]$hint) {
    if (Get-Command $exe -ErrorAction SilentlyContinue) { return $true }
    Info "安装 $exe ($wingetId)..."
    [void](Invoke-Native 'winget.exe' @('install', '--id', $wingetId, '-e',
                                         '--accept-source-agreements',
                                         '--accept-package-agreements', '--silent'))
    if (Get-Command $exe -ErrorAction SilentlyContinue) { Ok "$exe 已安装"; return $true }
    Warn "$hint"
    return $false
}

# ============================== 各步骤的 bash 体 ==============================

$BodySetup = @'
step "1/4  安装 Linux 依赖"
sudo -n true 2>/dev/null || { echo "ERROR: sudo 需要密码 / 用户不在 sudoers"; exit 40; }
sudo -n apt-get update -y -qq
PKGS="build-essential git curl wget unzip python3 python3-pip gcc-aarch64-linux-gnu
binutils-aarch64-linux-gnu cpio flex bison libssl-dev libelf-dev bc dwarves
libncurses-dev hfsprogs rsync android-sdk-libsparse-utils libfuse3-dev libbz2-dev
zlib1g-dev libattr1-dev cmake file lz4 jq device-tree-compiler qemu-system-arm
qemu-efi-aarch64 xz-utils"
sudo -n apt-get install -y -qq --no-install-recommends $PKGS
sudo -n apt-get install -y -qq 7zip 2>/dev/null || sudo -n apt-get install -y -qq p7zip-full 2>/dev/null || true
sudo -n apt-get install -y -qq gh 2>/dev/null || echo "warning: gh 不在 apt，内核编译步骤会用到"

if ! command -v 7z >/dev/null 2>&1 && ! command -v 7zz >/dev/null 2>&1; then
  echo "ERROR: 找不到 7z，无法解压 IPSW"; exit 41
fi
Z=$(command -v 7z || command -v 7zz)
ZV=$("$Z" 2>&1 | head -1 || true)
echo "7zip: $ZV"
V=$(printf '%s' "$ZV" | grep -oE '[0-9]{2,3}' | head -1 || true)
if [ -z "$V" ]; then V=0; fi
if [ "$V" -lt 23 ] 2>/dev/null; then
  echo "WARNING: 7-Zip 低于 23，解 APFS 可能失败（demo 步骤才需要）"
fi

step "2/4  拉取 ios-fajita 源码"
clone_repo "$REPO_URL" "$REPO"
echo "clone 完成"

step "3/4  项目自带环境脚本（依赖 + apfs-fuse + mkbootimg）+ gh CLI"
install_gh
cd "$REPO"
# setup-wsl.sh 自己还要 clone apfs-fuse / mkbootimg / WSL 内核源码 -> 全局改走镜像（只读）
git config --global url."https://gh-proxy.com/https://github.com/".insteadOf "https://github.com/"
echo "github.com 已全局改走 gh-proxy 镜像"
# 先跑一遍非编译版：它会装 linux-modules-extra 并 modprobe hfsplus，够用就不编内核
bash tools/setup-wsl.sh || true
if ! grep -qw hfsplus /proc/filesystems 2>/dev/null; then
  echo "内核缺 hfsplus -> 编译 WSL2 内核（20-40 分钟，只需一次）"
  bash tools/setup-wsl.sh --wsl-kernel || true
fi

step "4/4  结果检查"
MISS=""
command -v 7z >/dev/null 2>&1 || command -v 7zz >/dev/null 2>&1 || MISS="$MISS 7z"
command -v apfs-fuse >/dev/null 2>&1 || MISS="$MISS apfs-fuse"
command -v mkbootimg >/dev/null 2>&1 || MISS="$MISS mkbootimg"
command -v img2simg >/dev/null 2>&1 || MISS="$MISS img2simg"
command -v dtc >/dev/null 2>&1 || MISS="$MISS dtc"
if [ -n "$MISS" ]; then
  echo "ERROR: 关键工具还缺:$MISS"
  echo "       重跑 .\\mi8-ios.ps1 setup，或看上面 setup-wsl.sh 的报错"
  exit 42
fi
echo "关键工具齐全"
if grep -qw hfsplus /proc/filesystems 2>/dev/null; then
  echo "HFSPLUS_OK"
else
  echo "HFSPLUS_MISSING"
  echo "需要把编译出的内核写进 .wslconfig 并 wsl --shutdown，工具会自动处理"
fi
echo "SETUP_SCRIPT_DONE"
'@

$BodyIpsw = @'
step "下载 iOS 16.7.16 ($IPSW)"
if [ -f "$IPSW" ]; then
  echo "已存在: $(du -h "$IPSW" | cut -f1)，跳过（如需重下先删掉它）"
  exit 0
fi
echo "源: $IPSW_URL"
echo "约 7.4 GB，支持断点续传，中断了重跑本命令即可"
# -C - 只在 .part 已存在时加：全新下载带 -C - 会让部分 curl 报 "range not multit"
RESUME=""; [ -f "$IPSW.part" ] && RESUME="-C -"
# shellcheck disable=SC2086
curl -L $RESUME --retry 8 --retry-delay 3 --retry-all-errors \
     --connect-timeout 30 -o "$IPSW.part" "$IPSW_URL"
mv -f "$IPSW.part" "$IPSW"
ls -lh "$IPSW"
echo "IPSW_DONE"
'@

$BodyRootfs = @'
step "1/3  解压 IPSW 中的根文件系统镜像"
X="$W/ipsw/x"
mkdir -p "$X"
if [ ! -f "$X/BuildManifest.plist" ]; then
  Z=$(command -v 7z || command -v 7zz)
  # 7z 退出码 1 = warning（APFS 里常有），在 set -e 下必须吞掉
  "$Z" x -y "-o$X" "$IPSW" '*.dmg' BuildManifest.plist '*.plist' >/dev/null || true
fi
ls -lh "$X"/*.dmg 2>/dev/null || { echo "ERROR: 没解出 dmg"; exit 50; }

step "2/3  对应 OS / SystemOS / AppOS"
python3 - "$X" > "$W/ipsw/map.env" <<'PYEOF'
import glob, os, plistlib, sys
d = sys.argv[1]
man = {}
mp = os.path.join(d, 'BuildManifest.plist')
if os.path.exists(mp):
    try:
        m = plistlib.load(open(mp, 'rb'))
        man = m['BuildIdentities'][0].get('Manifest', {})
    except Exception as e:
        sys.stderr.write('BuildManifest 解析失败: %s\n' % e)
paths = {}
for k, v in man.items():
    info = (v or {}).get('Info') or {}
    p = info.get('Path')
    if p and p.lower().endswith('.dmg') and os.path.exists(os.path.join(d, os.path.basename(p))):
        paths[k] = os.path.basename(p)
def pick(key):
    for k in paths:
        if k.lower() == key.lower():
            return paths[k]
    return None
os_p, sys_p, app_p = pick('OS'), pick('SystemOS'), pick('AppOS')
if not (os_p and sys_p and app_p):
    fs = sorted(glob.glob(os.path.join(d, '*.dmg')), key=lambda f: -os.path.getsize(f))
    if len(fs) < 3:
        sys.stderr.write('dmg 数量不足 3 个\n'); sys.exit(51)
    sys.stderr.write('WARNING: BuildManifest 没匹配全，按体积排序兜底\n')
    os_p, sys_p, app_p = [os.path.basename(f) for f in fs[:3]]
print('OS=%s' % os_p)
print('SYSTEMOS=%s' % sys_p)
print('APPOS=%s' % app_p)
print('D=%s' % d)
PYEOF
cat "$W/ipsw/map.env"
. "$W/ipsw/map.env"
echo "OS=$OS  SYSTEMOS=$SYSTEMOS  APPOS=$APPOS"

step "3/3  构建 16GB HFS+ 根文件系统"
cd "$REPO"
OUTIMG="$OUT/ios16-root.img"
if [ -f "$OUTIMG" ]; then echo "已存在，跳过（要重建先删掉它）"; exit 0; fi
bash tools/mkiosroot.sh "$D/$OS" "$D/$SYSTEMOS" "$D/$APPOS" "$OUTIMG" 16G
sudo fsck.hfsplus -fy "$OUTIMG" >/dev/null 2>&1 || true
sudo mkdir -p /mnt/iosroot
sudo mount -t hfsplus -o loop,force "$OUTIMG" /mnt/iosroot
sudo python3 tools/sweet-root-tweaks.py /mnt/iosroot || {
  sudo umount /mnt/iosroot; echo "ERROR: root-tweaks 失败"; exit 52; }
sudo umount /mnt/iosroot
command -v img2simg >/dev/null || { echo "ERROR: 缺 img2simg"; exit 53; }
img2simg "$OUTIMG" "$OUT/ios16-root.simg"
ls -lh "$OUT"
echo "ROOTFS_DONE"
'@

$BodyKernel = @'
step "1/4  检查 GitHub CLI 登录"
cd "$REPO"
command -v gh >/dev/null 2>&1 || die "没有 gh（重跑 setup）"
if ! gh auth status >/dev/null 2>&1; then
  echo "NEED_AUTH"
  exit 61
fi
# 推送走 ssh.github.com:443（github.com 的 HTTPS 出方向被重置），需要账号上有 SSH key
ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes \
    -T ssh://git@ssh.github.com:443 > /tmp/ssh.out 2>&1 || true
SSHMSG=$(cat /tmp/ssh.out 2>/dev/null || true)
echo "$SSHMSG" | grep -qi 'successfully authenticated' || {
  echo "NEED_SSH_KEY"
  echo "  $SSHMSG"
  exit 62
}

step "2/4  Fork 仓库并推送"
GHUSER=$(gh api user -q .login)
echo "GitHub 用户: $GHUSER"
for i in 1 2 3; do
  if gh repo fork edi194/ios-fajita --clone=false >/dev/null 2>&1; then break; fi
  echo "  fork 未完成，10s 后重试 ($i/3)"; sleep 10
done
git remote remove fork >/dev/null 2>&1 || true
# 直接写 SSH 地址（走 443），不改全局 insteadOf，免得影响别的 clone
git remote add fork "ssh://git@ssh.github.com:443/$GHUSER/ios-fajita.git"
git branch -M main >/dev/null 2>&1 || true
git push -f fork main >/dev/null 2>&1 || git push -f fork HEAD:main

step "3/4  触发 XNU 内核编译（GitHub Actions macOS runner，约 30-60 分钟）"
# 刚 fork/push 完，workflow 定义可能还没注册进去 -> 重试
for i in $(seq 1 12); do
  if gh workflow run xnu.yml -R "$GHUSER/ios-fajita" 2>/tmp/wf.err; then break; fi
  echo "  触发失败，10s 后重试 ($i/12)：$(tr -d '\n' < /tmp/wf.err)"
  sleep 10
done
RUN=""
for i in $(seq 1 12); do
  RUN=$(gh run list -R "$GHUSER/ios-fajita" -w xnu.yml --limit 1 \
        --json databaseId -q '.[0].databaseId' 2>/dev/null || true)
  [ -n "$RUN" ] && [ "$RUN" != "null" ] && break
  sleep 10
done
[ -n "$RUN" ] && [ "$RUN" != "null" ] || die "拿不到 run id，去 https://github.com/$GHUSER/ios-fajita/actions 看"
echo "run: $RUN   （本机 Ctrl+C 不会取消远端编译）"
gh run watch "$RUN" -R "$GHUSER/ios-fajita" --exit-status || true
CONC=$(gh run view "$RUN" -R "$GHUSER/ios-fajita" --json conclusion -q .conclusion || true)
echo "结论: ${CONC:-unknown}"
[ "$CONC" = "success" ] || die "编译没成功，去 https://github.com/$GHUSER/ios-fajita/actions 看日志"

step "4/4  下载产物"
mkdir -p "$W/xnu"
rm -rf "$W/xnu"/* 2>/dev/null || true
gh run download "$RUN" -R "$GHUSER/ios-fajita" -n xnu-kernel -D "$W/xnu" || \
  die "产物下载失败（artifact 名可能变了，去 Actions 页面手工下载解到 $W/xnu）"
find "$W/xnu" -type f -name 'kernel*' -printf '%p  %s bytes\n' || true
KERN=$(find "$W/xnu" -type f -name 'kernel*' 2>/dev/null | head -1 || true)
[ -n "$KERN" ] || die "产物里没有 kernel 文件"
echo "KERNEL_DONE"
'@

$BodyDemo = @'
step "1/4  拉取 HakiOS（用于 QEMU 体验，不碰手机）"
clone_repo "$HAKIOS_URL" "$HAKIOS"
cd "$HAKIOS"

step "2/4  下载 iOS 16.5 IPSW 并解出系统镜像（约 7.4 GB）"
make fetch

step "3/4  构建 APFS 系统镜像"
make images

step "4/4  启动 QEMU 会话（默认 20 分钟，看到苹果 logo + 锁屏即成功）"
echo "提示：鼠标按住左键 = 手指；H = Home，L = 锁屏，上下键 = 音量"
SECS=${SECS:-1200} make qemu
echo "DEMO_DONE"
'@

$BodyBuild = @'
step "1/5  找到原厂 boot.img"
BOOT="$W/phone/boot.img"
if [ ! -f "$BOOT" ]; then
  echo "ERROR: 找不到 $BOOT"
  echo "       从 MIUI 卡刷包里解出 boot.img 放进去，或先跑 backup 让工具从手机拉"
  exit 70
fi

step "2/5  解 boot.img，抽出内核"
cd "$REPO"
mkdir -p dtb/boot_unpacked
python3 - "$BOOT" "$W/dtb/kernel.bin" <<'PYEOF'
import struct, sys
src, out = sys.argv[1], sys.argv[2]
d = open(src, 'rb').read()
if d[:8] != b'ANDROID!':
    sys.stderr.write('不是 Android boot 镜像\n'); sys.exit(71)
page, = struct.unpack_from('<I', d, 36)
ver,  = struct.unpack_from('<I', d, 40)
ksize, = struct.unpack_from('<I', d, 8)
if not (0 < ksize < len(d)):
    sys.stderr.write('kernel_size 异常\n'); sys.exit(71)
open(out, 'wb').write(d[page:page + ksize])
print('header v%d  page=%d  kernel=%d bytes' % (ver, page, ksize))
PYEOF

step "3/5  提取设备树 (DTB)"
rm -f dtb/boot_unpacked/*.dtb 2>/dev/null || true
python3 tools/extract-dtb.py "$W/dtb/kernel.bin" dtb/boot_unpacked dipper
ls -l dtb/boot_unpacked/ || true
# 用 find 而不是 ls|wc：pipefail 下 ls 对通配符不匹配会返回非 0
N=$(find dtb/boot_unpacked -maxdepth 1 -name '*.dtb' 2>/dev/null | wc -l)
[ "$N" -ge 1 ] || { echo "ERROR: 没找到 DTB"; exit 72; }
if [ "$N" -gt 1 ]; then
  echo "WARNING: 有 $N 个 DTB，默认取第一个，请人工确认："
  ls dtb/boot_unpacked/
fi
# head 会提前关管道，find 收到 SIGPIPE -> 加 || true 避免 pipefail 误杀
DTB=$(find dtb/boot_unpacked -maxdepth 1 -name '*.dtb' | head -1 || true)
[ -n "$DTB" ] || { echo "ERROR: 没找到 DTB"; exit 72; }
echo "使用: $DTB"

step "4/5  生成机型配置 dipper.env"
command -v dtc >/dev/null || { echo "ERROR: 缺 dtc（重跑 setup）"; exit 73; }
dtc -I dtb -O dts -o "$W/dtb/dipper.dts" "$DTB" 2>/dev/null || true
python3 - "$W/dtb/dipper.dts" "$REPO/tools/fajita.env" "$REPO/tools/dipper.env" "$DTB" <<'PYEOF'
import os, re, sys

dts, tmpl, out, dtb = sys.argv[1:5]
txt = open(dts, encoding='utf-8', errors='replace').read() if os.path.exists(dts) else ''
dtb_rel = 'dtb/boot_unpacked/' + os.path.basename(dtb)


def cells_of(node_body):
    m = re.search(r'reg\s*=\s*<([^>]+)>', node_body)
    if not m:
        return None
    vals = []
    for tok in m.group(1).replace(',', ' ').split():
        if tok.startswith('0x') or tok.startswith('0X'):
            vals.append(int(tok, 16))
        elif tok.isdigit():
            vals.append(int(tok, 10))
    return vals


def find_high_mem():
    """从 DTB 里找出 4G 以上那段内存的基址和大小。"""
    for m in re.finditer(r'memory@([0-9a-fA-F]+)\s*\{(.*?)\n\s*\};', txt, re.S):
        base = int(m.group(1), 16)
        if base < 0x100000000:
            continue
        c = cells_of(m.group(2))
        if not c:
            continue
        if len(c) >= 4:
            return base, (c[2] << 32) | c[3]
        if len(c) == 2:
            return base, c[1]
    return None, None


base, size = find_high_mem()
tmpl_txt = open(tmpl, encoding='utf-8').read()

# 小米8 面板 1080x2248（一加6T 是 1080x2340），这几项直接按机型填
auto = {
    'DTB_DEFAULT': dtb_rel,
    'FB_W': '1080',
    'FB_H': '2248',
    'FB_STRIDE': '4320',
}
if base:
    auto['RAM_HIGH_BASE'] = '0x%x' % base
if size:
    auto['RAM_HIGH_SIZE'] = '0x%x' % size

out_lines = []
for line in tmpl_txt.splitlines():
    key = line.split('=', 1)[0].strip() if '=' in line else None
    if key in auto:
        out_lines.append('%s=%s          # <- auto' % (key, auto[key]))
    else:
        out_lines.append(line)

head = [
    '# ============================================================',
    '# dipper (Xiaomi Mi 8, SDM845) -- AUTO-GENERATED by mi8-ios.ps1',
    '# 生成时间: ' + __import__('datetime').datetime.now().isoformat(timespec='seconds'),
    '#',
    '# [已自动填入] DTB_DEFAULT / RAM_HIGH_* / 分辨率 (来自你的原厂 DTB + 机型规格)',
    '# [!! 必须人工核对 !!]',
    '#   FB_BASE / FB_BLOCK / FB_OFFSET',
    '#       ABL 留下的显存基址，不同机型不同，这里仍是 fajita 的值。',
    '#       拿不到就先用默认值试，不亮屏再从 dipper.dts 的 reserved-memory 里找。',
    '#   TOUCH_*',
    '#       一加6T 是 Synaptics RMI4。小米8 可能是另一颗 IC，',
    '#       去 dipper.dts 里搜 i2c 节点和 compatible 确认地址/GPIO/LDO。',
    '#   改完执行:  .\\mi8-ios.ps1 build   (会把这份写回 tools/fajita.env 并重新打包)',
    '# ============================================================',
    '',
]
open(out, 'w').write('\n'.join(head) + '\n'.join(out_lines) + '\n')
print('生成: %s' % out)
for k in ('DTB_DEFAULT', 'RAM_HIGH_BASE', 'RAM_HIGH_SIZE', 'FB_W', 'FB_H',
          'FB_BASE', 'FB_BLOCK', 'TOUCH_I2C_BASE', 'TOUCH_I2C_ADDR'):
    for l in out_lines:
        if l.split('=')[0].strip() == k:
            print('    ' + l)
if not base or not size:
    print('!! 没解析到内存节点，RAM_HIGH_* 沿用 fajita 的值，必须手工改 !!')
PYEOF

step "4b/5  写回工具读取的配置（原文件已备份为 fajita.env.orig）"
[ -f "$REPO/tools/fajita.env.orig" ] || cp "$REPO/tools/fajita.env" "$REPO/tools/fajita.env.orig"
cp "$REPO/tools/dipper.env" "$REPO/tools/fajita.env"
echo "配置已生效，可直接编辑: $REPO/tools/fajita.env"

step "5/5  打包引导镜像"
KERN=$(find "$W/xnu" -name 'kernel*' -type f 2>/dev/null | head -1 || true)
if [ -z "$KERN" ]; then
  echo "ERROR: 还没有内核产物，先跑 kernel 步骤"; exit 74
fi
echo "内核: $KERN"
XNU_KERNEL="$KERN" DTB="$DTB" bash tools/build-fajita.sh
ls -lh shim/out/shim-boot.img
echo "BUILD_DONE"
echo "接下来: .\\mi8-ios.ps1 backup  然后  .\\mi8-ios.ps1 flash"
'@

# ============================== 进度 ==============================

function Get-Progress {
    $lib = Join-Path $Work 'lib'
    [ordered]@{
        'setup 脚本'   = (Test-Path (Join-Path $lib 'setup.sh'))
        '源码 clone'   = (Test-Path (Join-Path $Work 'repo\.git'))
        'WSL hfsplus'  = $false
        'IPSW 下载'    = (Test-Path (Join-Path $Work ("ipsw\$IpswName")))
        'rootfs 镜像'  = (Test-Path (Join-Path $Work 'out\ios16-root.simg'))
        'XNU 内核'     = ((Test-Path (Join-Path $Work 'xnu')) -and
                          (@(Get-ChildItem -Path (Join-Path $Work 'xnu') -Recurse -Filter 'kernel*' -File -ErrorAction SilentlyContinue).Count -gt 0))
        'boot.img'     = (Test-Path (Join-Path $Work 'phone\boot.img'))
        '引导镜像'     = (Test-Path (Join-Path $Work 'repo\shim\out\shim-boot.img'))
    }
}

function Test-HfsPlus {
    $d = Get-WslDistro
    if (-not $d) { return $false }
    $env:WSL_UTF8 = '1'
    return ((Invoke-WslQuery ('-d ' + $d + ' -- grep -qw hfsplus /proc/filesystems')).ExitCode -eq 0)
}

function Show-Status {
    Write-Head '进度'
    if (-not $Work) { $script:Work = Resolve-Work }
    Info "工作目录: $Work"
    $free = Get-FreeGB ($Work.Substring(0, 1))
    if ($free -ge 0) { Info "剩余空间: ${free} GB" }
    $p = Get-Progress
    foreach ($k in @($p.Keys)) {
        if ($k -eq 'WSL hfsplus') { $p[$k] = Test-HfsPlus }
        $v = $p[$k]
        if ($v) { Write-Host ("  [x] " + $k) -ForegroundColor Green }
        else    { Write-Host ("  [ ] " + $k) -ForegroundColor DarkGray }
    }
    Write-Host ''
}

# ============================== 命令 ==============================

function Step-Doctor {
    Write-Head '环境体检'
    Info "Windows: $($PSVersionTable.PSVersion)  $([Environment]::OSVersion.VersionString)"
    if (Get-WslDistro) { Ok "WSL 发行版: $(Get-WslDistro)" } else { Warn 'WSL 没装（setup 会自动装）' }
    if (Get-Command git -ea SilentlyContinue)   { Ok 'git 已装' }   else { Warn '缺 git（setup 会装）' }
    if (Get-Command gh -ea SilentlyContinue)    { Ok 'gh 已装' }    else { Warn '缺 gh —— 只有 kernel 步骤需要' }
    foreach ($e in @('fastboot', 'adb')) {
        if (Get-Command $e -ea SilentlyContinue) { Ok "$e 已装" } else { Warn "缺 $e —— 真机步骤需要（winget install Google.PlatformTools）" }
    }
    $free = Get-FreeGB ($env:SystemDrive.Substring(0, 1))
    if ($free -ge 60) { Ok "系统盘剩余 ${free} GB" } else { Warn "系统盘只剩 ${free} GB，建议用 -Work 指到更大的盘" }
    Show-Status
    if (-not (Get-WslDistro)) { Tip '下一步: .\mi8-ios.ps1 setup' }
    else { Tip '下一步: .\mi8-ios.ps1 demo   (看效果)  或  .\mi8-ios.ps1 ipsw  (上真机)' }
}

function Step-Setup {
    Write-Head '安装环境'
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    Write-Lib 'setup' $BodySetup
    Install-WslDistro
    $script:Distro = Get-WslDistro
    Invoke-Lib 'setup'

    if (-not (Test-HfsPlus)) {
        # setup-wsl.sh 把内核拷到了 Windows 的 %USERPROFILE%\wsl-hfsplus-bzImage
        Info '把 HFS+ 内核写入 .wslconfig...'
        $winImg = Join-Path $env:USERPROFILE 'wsl-hfsplus-bzImage'
        if (-not (Test-Path $winImg)) {
            Warn '没找到编译出的内核镜像，稍后手动处理'
            Tip '它应该在 ' + $winImg
            return
        }
        $cfg = Join-Path $env:USERPROFILE '.wslconfig'
        $block = "[wsl2]`r`nkernel=$winImg`r`nmemory=6GB`r`nprocessors=$((Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors)"
        if (Test-Path $cfg) {
            $cur = Get-Content $cfg -Raw -ErrorAction SilentlyContinue
            if ($cur -notmatch [regex]::Escape($winImg)) { Add-Content -Path $cfg -Value ("`r`n" + $block) }
        } else { Set-Content -Path $cfg -Value $block }
        Ok ".wslconfig 已更新 -> $cfg"
        Info '重启 WSL 使内核生效...'
        [void](Invoke-Native 'wsl.exe' @('--shutdown'))
        Start-Sleep -Seconds 3
        $script:Distro = Get-WslDistro
        if (Test-HfsPlus) { Ok 'hfsplus 已生效' }
        else { Warn 'hfsplus 仍未生效，可能需要重启电脑后再跑一次 setup' }
    } else { Ok 'hfsplus 已可用' }
    Tip '环境就绪。下一步: .\mi8-ios.ps1 ipsw'
}

function Step-Ipsw {
    Write-Head "下载 IPSW ($IpswBuild)"
    if (-not (Get-WslDistro)) { Die '先跑 setup' 30 }
    $script:Distro = Get-WslDistro
    Write-Lib 'ipsw' $BodyIpsw
    Invoke-Lib 'ipsw'
    Tip '下一步: .\mi8-ios.ps1 rootfs'
}

function Step-Rootfs {
    Write-Head '构建 iOS 根文件系统镜像'
    if (-not (Get-WslDistro)) { Die '先跑 setup' 30 }
    if (-not (Test-Path (Join-Path $Work "ipsw\$IpswName"))) { Die '先跑 ipsw' 31 }
    if (-not (Test-HfsPlus)) { Die 'WSL 内核没有 hfsplus，先重跑 setup（mount HFS+ 必须用它）' 32 }
    $script:Distro = Get-WslDistro
    Write-Lib 'rootfs' $BodyRootfs
    Invoke-Lib 'rootfs'
    Tip '下一步: .\mi8-ios.ps1 kernel'
}

function Step-Kernel {
    Write-Head '编译 XNU 内核'
    if (-not (Get-WslDistro)) { Die '先跑 setup' 30 }
    $script:Distro = Get-WslDistro
    Write-Lib 'kernel' $BodyKernel
    $code = Invoke-Lib 'kernel' -AllowExit 61, 62
    if ($code -eq 61 -or $code -eq 62) {
        if ($code -eq 61) {
            Info '还没登录 GitHub —— 启动设备码登录（token + SSH key 一次搞定）'
            Tip '按屏幕提示在浏览器输入一次性验证码，完成后回这里'
        } else {
            Info '账号缺 SSH key —— 启动登录流程，gh 会自动生成并上传'
            Tip '按屏幕提示在浏览器输入一次性验证码'
        }
        Write-Host ''
        & wsl.exe -d $Distro -- bash -lc "cd '$(To-Wsl $Work)/repo' && gh auth login -h github.com -p ssh -w"
        Write-Host ''
        Info '登录完成，重新执行内核编译...'
        Invoke-Lib 'kernel'
    }
    Tip '下一步: .\mi8-ios.ps1 build'
}

function Step-Demo {
    Write-Head 'QEMU 体验：不碰手机，直接看 iOS 跑起来'
    Warn '这一步下载约 7.4GB + 构建，预计 1-2 小时；全程不连接手机'
    if (-not (Get-WslDistro)) { Die '先跑 setup' 30 }
    $script:Distro = Get-WslDistro
    Write-Lib 'demo' $BodyDemo
    Invoke-Lib 'demo'
}

function Step-Build {
    Write-Head '生成 dipper 配置并打包引导镜像'
    if (-not (Get-WslDistro)) { Die '先跑 setup' 30 }
    $script:Distro = Get-WslDistro
    $dst = Join-Path $Work 'phone\boot.img'
    New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
    if ($BootImg -and (Test-Path $BootImg)) {
        Copy-Item $BootImg $dst -Force
        Ok "已复制 boot.img -> $dst"
    }
    if (-not (Test-Path $dst)) {
        Die ("找不到 boot.img。请把原厂 boot.img 放到:`n       $dst`n       " +
             "（MIUI 卡刷包里解出来，或跑 backup 让工具从手机拉）") 70
    }
    Write-Lib 'build' $BodyBuild
    Invoke-Lib 'build'
    Warn '生成的 tools/dipper.env 是模板 —— 面板分辨率/触摸必须人工核对！'
    Tip '打开看一眼: ' + (Join-Path $Work 'repo\tools\dipper.env')
    Tip '下一步: .\mi8-ios.ps1 backup  然后  .\mi8-ios.ps1 flash'
}

function Get-AdbDevice {
    $r = Invoke-Native 'adb.exe' @('devices')
    $l = $r.Output | Select-String -Pattern "^\S+\s+device$"
    if ($l) { return (($l | Select-Object -First 1).ToString() -split '\s+')[0] }
    return $null
}
function Get-FastbootDevice {
    $r = Invoke-Native 'fastboot.exe' @('devices')
    $l = $r.Output | Select-String -Pattern 'fastboot'
    if ($l) { return (($l | Select-Object -First 1).ToString() -split '\s+')[0] }
    return $null
}

function Confirm-Yes([string]$what) {
    if ($Yes) { return $true }
    $a = Read-Host "  确认要$what？输入 YES 回车"
    return ($a -eq 'YES')
}

function Step-Backup {
    Write-Head '备份手机关键分区'
    if (-not (Get-Command fastboot.exe -ea SilentlyContinue)) {
        if (-not (Ensure-WinTool 'Google.PlatformTools' 'fastboot' '请手动安装: winget install Google.PlatformTools')) { Die '缺 fastboot' 80 }
        $env:Path += ";$env:LOCALAPPDATA\Android\Platform-tools"
    }
    $bdir = Join-Path $Work 'backup'
    New-Item -ItemType Directory -Force -Path $bdir | Out-Null
    if (-not (Confirm-Yes '读取并备份手机分区（不会改动手机）')) { Die '已取消' 81 }

    $fb = Get-FastbootDevice
    if (-not $fb) {
        Warn 'fastboot 没检测到设备。请: 手机进入 fastboot（关机后按 音量下+电源），用原装线直连 USB'
        Die '设备未就绪' 82
    }
    Ok "fastboot 设备: $fb"

    # A/B 设备分区名带 _a/_b，先探一下（fastboot 把 getvar 结果写在 stderr）
    $vars = (Invoke-Native 'fastboot.exe' @('getvar', 'all')).Output -join "`n"
    $suf = ''
    if ($vars -match 'partition-type-boot_a:') { $suf = '_a' }
    Info "分区后缀: '$suf'"

    foreach ($p in @('boot', 'dtbo', 'vbmeta', 'recovery')) {
        Info "拉取 ${p}${suf} ..."
        $out = Join-Path $bdir "$p.img"
        $r = Invoke-Native 'fastboot.exe' @('fetch', "${p}${suf}", $out)
        if ((Test-Path $out) -and ((Get-Item $out).Length -gt 0)) { Ok "$p.img $([math]::Round((Get-Item $out).Length/1MB,1)) MB" }
        else { Warn "$p 拉取失败（部分机型不支持 fetch）: $(($r.Output -join ' ').Trim())" }
    }
    if (-not (Test-Path (Join-Path $bdir 'boot.img'))) {
        Warn '没能自动拉 boot.img —— 请手动把原厂 boot.img 放到: ' + (Join-Path $Work 'phone\boot.img')
    } else {
        Copy-Item (Join-Path $bdir 'boot.img') (Join-Path $Work 'phone\boot.img') -Force
        Ok 'boot.img 已同步到 phone\boot.img'
    }
    Tip "备份目录: $bdir   —— 务必离线再存一份"
    Tip 'persist / modemst1 / modemst2 / fsg 含校准数据，需在 Recovery 里用 adb pull，见 README'
}

function Step-Flash {
    Write-Head '刷入并启动 iOS'
    Warn '此操作会清空 userdata（安卓数据全没），boot/recovery/modem 不动'

    $shim = Join-Path $Work 'repo\shim\out\shim-boot.img'
    $simg = Join-Path $Work 'out\ios16-root.simg'
    if (-not (Test-Path $shim)) { Die "先跑 build（缺 $shim）" 90 }
    if (-not (Test-Path $simg)) { Die "先跑 rootfs（缺 $simg）" 91 }

    if (-not (Get-Command fastboot.exe -ea SilentlyContinue)) {
        $env:Path += ";$env:LOCALAPPDATA\Android\Platform-tools"
        if (-not (Get-Command fastboot.exe -ea SilentlyContinue)) { Die '缺 fastboot' 80 }
    }
    if (-not (Get-PathBackupOk)) { Warn '没找到备份，强烈建议先跑 .\\mi8-ios.ps1 backup' }

    $fb = Get-FastbootDevice
    if (-not $fb) { Warn 'fastboot 没检测到设备（关机后 音量下+电源 进 fastboot，原装线直连）'; Die '设备未就绪' 82 }
    Ok "fastboot 设备: $fb"

    Write-Host ''
    Write-Host '  将执行:' -ForegroundColor Yellow
    Write-Host '    1. fastboot -S 512M flash userdata  ios16-root.simg   (~7 分钟)' -ForegroundColor Gray
    Write-Host '    2. fastboot boot shim-boot.img                        (临时引导，不写分区)' -ForegroundColor Gray
    if (-not (Confirm-Yes '刷入 userdata 并启动')) { Die '已取消' 92 }

    Info '刷入 userdata（千万别拔线）...'
    & fastboot.exe -S 512M flash userdata $simg
    if ($LASTEXITCODE -ne 0) {
        Warn '小米 fastboot 大分区刷完常假死：请手动 音量下+电源 重新进 fastboot 后重跑本命令'
        Die 'flash 失败' 93
    }
    Ok 'userdata 刷入完成'

    Info '启动 shim（首启在苹果 logo 停 4-5 分钟，别急）...'
    & fastboot.exe boot $shim
    Ok '已下发启动命令'
    Write-Host ''
    Tip '卡住/黑屏时：电源+音量下 回 fastboot，再跑 .\mi8-ios.ps1 restore'
}

function Get-PathBackupOk { return (Test-Path (Join-Path $Work 'backup')) }

function Step-Restore {
    Write-Head '回滚到 MIUI'
    $bdir = Join-Path $Work 'backup'
    Warn 'iOS 只写 userdata，boot/recovery 没被动过，通常重刷 MIUI 即可完全恢复'
    Write-Host ''
    Write-Host '  手动回滚步骤:' -ForegroundColor Yellow
    Write-Host '    1. 手机进 fastboot（关机后 音量下+电源）' -ForegroundColor Gray
    Write-Host '    2. （若备份里有 boot.img）fastboot flash boot_a ' + (Join-Path $bdir 'boot.img') -ForegroundColor Gray
    Write-Host '       fastboot flash boot_b ' + (Join-Path $bdir 'boot.img') -ForegroundColor Gray
    Write-Host '    3. 用小米官方线刷包/卡刷包重刷 MIUI，或:' -ForegroundColor Gray
    Write-Host '       fastboot reboot recovery  -> 卡刷包 sideload' -ForegroundColor Gray
    Write-Host '    4. 恢复出厂后 userdata 被清空，iOS 也随之消失' -ForegroundColor Gray
    Write-Host ''
    if (Get-Command fastboot.exe -ea SilentlyContinue) {
        $fb = Get-FastbootDevice
        if ($fb) {
            if (Confirm-Yes '现在就 reboot 到 bootloader（仅重启，不改动）') { & fastboot.exe reboot }
        }
    }
}

function Step-Dump {
    Write-Head '生成全部辅助脚本（仅生成，不执行）'
    Write-Lib 'setup'  $BodySetup
    Write-Lib 'ipsw'   $BodyIpsw
    Write-Lib 'rootfs' $BodyRootfs
    Write-Lib 'kernel' $BodyKernel
    Write-Lib 'demo'   $BodyDemo
    Write-Lib 'build'  $BodyBuild
    $dir = Join-Path $Work 'lib'
    Get-ChildItem $dir -Filter '*.sh' | ForEach-Object { Info "$($_.FullName)  ($($_.Length) bytes)" }
    Tip "WSL 内路径: $(To-Wsl $dir)"
}

function Show-Help {
    Write-Host @'

  小米8 跑 iOS 16 —— 一键工具                用法:  .\mi8-ios.ps1 <命令>

  智能模式（默认，自动体检并推进到下一步）
      .\mi8-ios.ps1

  体验 / 环境
      demo        QEMU 里看 iOS 跑起来（不碰手机，零风险）
      setup       装 WSL2 + Ubuntu + 全部依赖 + 拉源码
      doctor      环境体检          status     看各步骤进度

  真机流水线
      ipsw        下载 iOS 16.7.16 IPSW（断点续传）
      rootfs      构建 16GB iOS 根文件系统镜像
      kernel      编译 XNU 内核（GitHub Actions，需登录）
      build       从 boot.img 生成 dipper 配置 + 打包引导镜像
      backup      备份手机关键分区
      flash       刷入并启动          restore     回滚说明

  常用参数
      -Work D:\mi8-ios      指定工作目录（默认自动挑剩余空间 >= 60GB 的盘）
      -BootImg C:\boot.img  指定原厂 boot.img
      -Yes                  跳过二次确认

'@ -ForegroundColor Cyan
}

# ============================== 主流程 ==============================

if (-not $Work) { $Work = Resolve-Work }

switch ($Command) {
    'help'    { Show-Help; break }
    'status'  { Show-Status; break }
    'doctor'  { Step-Doctor; break }
    'setup'   { Step-Setup; break }
    'ipsw'    { Step-Ipsw; break }
    'rootfs'  { Step-Rootfs; break }
    'kernel'  { Step-Kernel; break }
    'demo'    { Step-Demo; break }
    'build'   { Step-Build; break }
    'backup'  { Step-Backup; break }
    'flash'   { Step-Flash; break }
    'restore' { Step-Restore; break }
    'dump'    { Step-Dump; break }
    'auto'    {
        Show-Status
        $p = Get-Progress
        if (-not $p['setup 脚本'])         { Step-Setup; exit 0 }
        if (-not $p['源码 clone'])       { Step-Setup; exit 0 }
        if (-not (Test-HfsPlus))         { Step-Setup; exit 0 }
        Write-Host ''
        Write-Host '  环境就绪。现在选择路线:' -ForegroundColor Cyan
        Write-Host '    .\mi8-ios.ps1 demo    先在 QEMU 看 iOS（推荐，零风险）' -ForegroundColor Gray
        Write-Host '    .\mi8-ios.ps1 ipsw    直接推进真机流水线' -ForegroundColor Gray
        Write-Host ''
        if (-not $p['IPSW 下载'])  { Tip '下一步: .\mi8-ios.ps1 ipsw';  exit }
        if (-not $p['rootfs 镜像']){ Tip '下一步: .\mi8-ios.ps1 rootfs'; exit 0 }
        if (-not $p['XNU 内核'])   { Tip '下一步: .\mi8-ios.ps1 kernel'; exit 0 }
        if (-not $p['boot.img'])   { Tip '下一步: .\mi8-ios.ps1 backup'; exit 0 }
        if (-not $p['引导镜像'])   { Tip '下一步: .\mi8-ios.ps1 build';  exit }
        Tip '流水线已全部完成。下一步: .\mi8-ios.ps1 flash'
        break
    }
    default   { Show-Help }
}

# 明确给 0：否则脚本退出码会继承最后一条原生命令（如 wsl.exe）的状态
exit 0
