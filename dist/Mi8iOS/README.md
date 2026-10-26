# mi8-ios —— 小米8 (dipper / 骁龙845) 跑 iOS 16 一键工具

单文件 PowerShell 脚本，把整条流水线封装成一条命令：

```
装环境 → 下载 IPSW → 构建 iOS 根文件系统 → 编译 XNU 内核
       → 从你的 boot.img 生成机型配置 → 打包引导镜像 → 真机刷入
```

- 所有重活（编译、解包、镜像制作）在 **WSL2 Ubuntu** 里跑，Windows 侧只负责 fastboot/adb 和调度。
- 全程**不改动** boot / recovery / modem / persist 分区，iOS 根文件系统只写进 **userdata**。
- 基座项目：[edi194/ios-fajita](https://github.com/edi194/ios-fajita)（骁龙845 一加6T，2026-10-25 已进主屏幕），小米8 同 SoC，最成熟的移植。

---

## 快速开始

```powershell
# 0) 打开 PowerShell（普通权限即可；装 WSL 时会弹一次 UAC）
cd "D:\work\mi8-ios"

# 1) 体检：看看这台机器缺什么
.\mi8-ios.ps1 doctor

# 2) 零风险先看效果：QEMU 里跑 iOS（不需要手机）
.\mi8-ios.ps1 demo

# 3) 真机流水线（一步步推，或直接回车走智能模式）
.\mi8-ios.ps1 setup
.\mi8-ios.ps1 ipsw
.\mi8-ios.ps1 rootfs
.\mi8-ios.ps1 kernel
.\mi8-ios.ps1 backup
.\mi8-ios.ps1 build
.\mi8-ios.ps1 flash
```

什么都不敲直接运行 `.\mi8-ios.ps1` 是**智能模式**：显示进度，自动推进到没做完的那一步。

---

## 图形界面版（推荐）

不想敲命令就用安装包：双击 **`dist\Mi8iOSSetup.exe`** → 「开始安装」（用户级安装，**不需要管理员**）。

装完桌面上会多一个「**Mi8 iOS 工具箱**」：

| 区域 | 说明 |
|---|---|
| 左侧「流水线进度」 | 8 步实时打勾（环境 / 源码 / WSL / IPSW / rootfs / 内核 / boot.img / 引导镜像），每 3 秒刷新 |
| 「▶ 一键下一步」 | 相当于智能模式，自动推进到没做完的那一步 |
| 下方按钮 | `doctor` `setup` `ipsw` `rootfs` `kernel` `build` `backup` `flash` `restore` `demo` `status` `help` |
| 右侧日志 | 实时流式输出并按类型着色（蓝=标题、绿=成功、红=失败、黄=注意），自动滚到底部；往上翻看历史时暂停自动滚 |
| 底部状态栏 | 运行状态、用时、日志文件名、工作目录 |
| 顶栏「设置」 | 工作目录、原厂 boot.img 路径、是否 `-Yes` |

- 每次运行都会在 `安装目录\logs\时间_命令.log` 落一份完整日志。
- `刷入启动` / `备份手机` 会弹**二次确认**才执行。
- 命令行参数：`Mi8iOS.exe --run doctor`（直接跑某条命令）、`Mi8iOS.exe --selftest`（自检，退出码 0 = 正常）。
- 卸载：控制面板「程序和功能」里的 *Mi8 iOS 工具箱*，或运行安装目录的 `Uninstall.exe`；只删程序本身，**不动**工作目录里的 IPSW / 镜像 / 编译产物。
- 安装器也支持静默方式：`Mi8iOSSetup.exe --target="D:\Mi8iOS" --silent`、`Uninstall.exe /uninstall --silent`。

源码在 `src\`（纯 C# WinForms，.NET Framework 4.x，无第三方依赖），重新编译：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

---

## 命令表

| 命令 | 作用 | 需要手机 | 风险 |
|---|---|---|---|
| （无参数） | 智能模式：体检 + 推进到下一步 | 否 | 无 |
| `doctor` | 环境体检，列出缺什么 | 否 | 无 |
| `status` | 看各步骤进度（绿叉 = 完成） | 否 | 无 |
| `demo` | QEMU 里看 iOS 跑起来（下载 7.4GB，1–2 小时） | 否 | **无，推荐先跑这个** |
| `setup` | 装 WSL2 + Ubuntu + 全部依赖 + 拉源码（含编译带 HFS+ 的 WSL 内核） | 否 | 无（会弹一次 UAC） |
| `ipsw` | 下载 iOS 16.7.16 IPSW（约 7.4GB，断点续传） | 否 | 无 |
| `rootfs` | 从 IPSW 构建 16GB HFS+ 根文件系统镜像 | 否 | 无 |
| `kernel` | fork 仓库、触发 GitHub Actions 编译 XNU、拉回产物（30–60 分钟） | 否 | 无（需要登录 GitHub） |
| `build` | 从原厂 boot.img 抽 DTB，生成 `dipper.env`，打包 `shim-boot.img` | 否 | 无 |
| `backup` | fastboot 读取并备份 boot/dtbo/vbmeta/recovery | 是 | 只读，不改手机 |
| `flash` | 刷入 userdata + 临时引导启动 | 是 | **清空安卓数据** |
| `restore` | 打印回滚到 MIUI 的步骤 | 是 | 只读说明 |
| `dump` | 只生成 `lib/*.sh` 辅助脚本，不执行（用来审阅） | 否 | 无 |
| `help` | 帮助 | 否 | 无 |

### 参数

| 参数 | 说明 |
|---|---|
| `-Work D:\mi8-ios` | 指定工作目录。默认自动挑剩余空间 ≥ 60GB 的盘（例如 `D:\mi8-ios`） |
| `-BootImg C:\path\boot.img` | 指定原厂 boot.img，`build` 会复制到 `phone\boot.img` |
| `-Yes` | 跳过二次确认（`flash` / `backup` 仍会要求输入 `YES`） |

---

## 工作目录结构

```
D:\mi8-ios\
├── lib\          由 dump/setup 生成的 bash 脚本（setup / ipsw / rootfs / kernel / demo / build）
├── repo\         ios-fajita 源码
├── hakios\       hackios-xnu-kernel（demo 用）
├── ipsw\         16.7.16 IPSW + 解出的 dmg
├── out\          ios16-root.img / ios16-root.simg
├── xnu\          GitHub Actions 编译出的内核产物
├── dtb\          从你 boot.img 抽出的内核与 dipper*.dtb / dipper.dts
├── phone\        boot.img（backup 拉回或你手动放进来）
├── backup\       fastboot 备份的分区镜像
└── out\shim\     （在 repo\shim\out\shim-boot.img）
```

---

## 这台机器的网络特殊处理（已内置）

实测本机环境：

| 目标 | 状态 | 工具怎么处理 |
|---|---|---|
| `github.com`（HTTPS） | **被重置/超时** | clone 走 `直连 → gh-proxy 镜像 → codeload tarball` 三级降级；`setup` 里把 `git` 的 github URL 全局改写到 `gh-proxy.com` |
| `api.github.com` | 通 | `gh` CLI 直接用 |
| `ssh.github.com:22/443` | 通 | `kernel` 步骤的 push 走 `ssh://git@ssh.github.com:443`（**用显式 SSH remote，不改全局 insteadOf 的写方向**） |
| Apple CDN（IPSW） | 通 | 断点续传直下 |
| Ubuntu 镜像 | 通 | apt 默认源即可 |

`gh` CLI 若 apt 装不到，会自动从镜像拉官方二进制到 `~/.local/bin`。

---

## 真机前提（工具替代不了的两件事）

1. **解锁 Bootloader**：小米官方解锁要登录账号、绑定设备，**等待 168 小时（7 天）**。不解 BL 无法 fastboot 刷任何东西。
2. **原厂 `boot.img`**：`build` 步骤必须要有它（用来抽 DTB 和核对显存基址）。两种来源：
   - 跑 `.\mi8-ios.ps1 backup`，让工具用 `fastboot fetch` 从手机拉；
   - 从对应版本的 MIUI 卡刷包 / 线刷包里解出 `boot.img`，放进 `D:\mi8-ios\phone\boot.img`，或用 `-BootImg` 指定。

### 必须手动备份的分区（不在 fastboot 里）

`persist`、`modemst1`、`modemst2`、`fsg` 存着基带校准数据，刷坏了手机就没信号。它们**只能在 Recovery 里用 adb 拉**：

```text
1. 手机进 Recovery（关机后按 音量上+电源）-> 连电脑
2. adb pull /dev/block/bootdevice/by-name/persist      persist.img
   adb pull /dev/block/bootdevice/by-name/modemst1     modemst1.img
   adb pull /dev/block/bootdevice/by-name/modemst2     modemst2.img
   adb pull /dev/block/bootdevice/by-name/fsg          fsg.img
3. 离线再存一份（U 盘 / 网盘）
```

本工具**不碰**这些分区，所以一般用不上；但只要你在玩 fastboot，就建议先拉一份。

---

## `build` 之后必须人工核对的配置

`build` 会从你的 DTB 生成 `repo\tools\dipper.env`（并自动备份原文件为 `fajita.env.orig`，把新配置写回 `tools/fajita.env` 供打包脚本读取）。

**自动填好的**：`DTB_DEFAULT`、`RAM_HIGH_BASE/SIZE`（从你的 DTB 解析）、`FB_W=1080 / FB_H=2248 / FB_STRIDE=4320`（小米8 面板规格，一加6T 是 2340）。

**必须自己核对的**：

| 项 | 为什么 | 怎么查 |
|---|---|---|
| `FB_BASE` / `FB_BLOCK` / `FB_OFFSET` | ABL 留下的显存基址，一加和小米不一样 | 先用默认值试；不亮屏就在 `D:\mi8-ios\dtb\dipper.dts` 里搜 `reserved-memory` |
| `TOUCH_*` | 一加6T 是 Synaptics RMI4，小米8 可能是另一颗触摸 IC | 在 `dipper.dts` 里搜 `i2c` 节点和 `compatible` |
| `DTB_DEFAULT` | 抽出的 dtb 可能有多个 | 看 `build` 输出里"使用: ..."那一行 |

改完重新跑一次 `.\mi8-ios.ps1 build` 让配置生效并重新打包。

---

## 刷入与回滚

```powershell
.\mi8-ios.ps1 flash
```

实际执行：

```
fastboot -S 512M flash userdata  ios16-root.simg    # ~7 分钟，只动 userdata
fastboot boot shim-boot.img                          # 临时引导，不写 boot 分区
```

- 首次启动**苹果 logo 上会停 4–5 分钟**，别急着断电。
- 卡住/黑屏：`电源 + 音量下` 回 fastboot，再看 `.\mi8-ios.ps1 restore`。
- 小米 fastboot 刷大分区结束后常"假死"：手动 `音量下 + 电源` 重进 fastboot 再重跑一次即可。

回滚（因为没动过 boot/recovery）：

```
1. 进 fastboot
2. fastboot flash boot_a backup\boot.img
   fastboot flash boot_b backup\boot.img     （A/B 分区；非 A/B 机型省略后缀）
3. 用小米官方线刷包/卡刷包重刷 MIUI
4. 恢复出厂后 userdata 被清空，iOS 也随之消失
```

---

## 常见问题

**Q：`setup` 卡在"编译 WSL2 内核"？**
正常，20–40 分钟只做一次。它在给 WSL 内核加 HFS+ 支持——`rootfs` 挂载镜像必须用。装完工具会自动写 `%USERPROFILE%\.wslconfig` 并 `wsl --shutdown` 让它生效。如果提示 `HFSPLUS_MISSING`，重启电脑再跑一次 `setup`。

**Q：`kernel` 说 `NEED_AUTH` / `NEED_SSH_KEY`？**
脚本会自动弹 `gh auth login` 的设备码流程：按提示在浏览器打开 `https://github.com/login/device` 输入一次性验证码。选 SSH 协议时 gh 会自动生成并上传 key，这正是 push 需要的。

**Q：`kernel` 触发失败一直重试？**
fork 完 GitHub 需要几秒钟注册 workflow，脚本会重试 12 次。还不行就去 `https://github.com/<你的用户名>/ios-fajita/actions` 手动点 Run workflow。

**Q：想重新做某一步？**
产物都是幂等的，删掉对应文件重跑：`ipsw` 删 `ipsw\*.ipsw`；`rootfs` 删 `out\ios16-root.img`；`kernel` 删 `xnu\` 下的文件。

**Q：这会变砖吗？**
不会。引导镜像是 `fastboot boot` 临时加载的，不写 boot 分区；userdata 被清了也只是丢安卓数据。任何时刻都能回 fastboot 刷回 MIUI——**前提是 bootloader 解过锁**。

---

## 本机体检实测结论（2026-10-26）

- 磁盘：任选一块剩余 ≥ 60GB 的盘即可（工具自动挑最大的那块，示例工作目录 `D:\mi8-ios`）
- 已装：Python 3.12、winget 1.29
- 未装：git、7z、qemu、fastboot/adb、WSL 发行版（`setup` 会装）
- 网络：见上文表格

---

## 许可证

[MIT](LICENSE) © 2026 GSHPChina —— 可自由使用、修改、分发，保留版权声明即可。
