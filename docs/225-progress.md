# 2.2.5 进度
- [完成] 磁盘检查（C 587GB/D 664GB 空闲），PC 工作树=交付的 2.2.4 源码（238 文件 0 差异），备份 backup-before-225\source-2.2.4
- [代码] AssemblyInfo、manifest、taskbar-factory.zip 外置、TB-04 退避/备用目录/占用者/签名未知、DesktopKeeper(Win+D)、FolderWidget；未编译
- [测试] 2026-10-30 21:56 v225 套件 53/53 通过（独占锁、退避、备用目录、签名“未知”、文件夹组件、Win+D 模拟）。
- [实测] 外部 ToggleDesktop（关闭 keeper = 2.2.4 行为）：Progman 升到第 8 层，组件被压在下面——复现了用户问题；开启 keeper 的对照运行在电脑离线时中断，未取得结果。
- [用户反馈 22:07] 测试运行弹出了真实 TB-04 toast（占用者是测试 ClearDesk 自己）。已改：测试/预览运行不弹托盘提示（Program.QuietRun）；全局 mutex 只让一个 ClearDesk 管理任务栏；占用者是别的 ClearDesk/TranslucentTB 时视为正常、不提示、不再启动第二份。未编译（电脑离线）。
- [设备电量 22:20] 新组件 battery + `ClearDesk.exe --battery-diag`。本机实测：罗技 PRO X 2（Lightspeed 接收器 046D:C54D，HID++ 0x1004）= 77%；2.4G Wireless Device 25A7:2420 = 不支持读取电量（无标准 HID 电量报告）；罗技 USB Receiver 046D:C539 = 接收器上无设备应答（设备关机/休眠/未配对）；蓝牙：只有 MediaTek 适配器，没有已连接的蓝牙设备，蓝牙路径未能实测。
- [Win+D 实测 22:3x] 外部 Shell.Application.ToggleDesktop（COM，无按键）：关闭 keeper（=2.2.4）时 Progman 升到第 8 层、组件在 34/39 层被盖住；开启 keeper 时组件在第 3/8 层、Progman 第 13 层 → 组件保持可见；恢复后 raises 1 / lowers 1，锁定组件回到底层。桌面已恢复。
