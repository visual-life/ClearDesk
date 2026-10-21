# 第三方许可

根目录 MIT 许可证仅覆盖 ClearDesk 自有代码。以下项目保留其原作者声明与独立许可证。

## lunar-csharp

- 上游：https://github.com/6tail/lunar-csharp
- 作者：6tail。
- 许可：MIT，保留在 `third_party/lunar/LICENSE`。
- 仓库包含随 ClearDesk 1.5.1 发布的农历源代码，未修改其实现；不将这些代码署名为 ClearDesk 作者。

## TranslucentTB

- 上游及固定版本：https://github.com/TranslucentTB/TranslucentTB/releases/tag/2026.2
- 许可：GPL-3.0，保留在 `third_party/translucenttb/LICENSE.md`。
- 作为独立可选进程运行，仓库不包含其二进制或将其合并到 ClearDesk 程序。
- `scripts/prepare-taskbar.ps1` 同时准备对应上游源码压缩包、完整许可证和所需资源；分发包含该组件的应用包时须保留这些材料。
- `third_party/translucenttb/settings.json` 是 ClearDesk 使用的组件配置；`verified.sha256` 是启动时校验清单。

## QQ 歌词缓存解码

- [WXRIW/QQMusicDecoder](https://github.com/WXRIW/QQMusicDecoder)：Copyright (c) 2023 WXRIW，MIT。适配其 DESHelper 至 .NET Framework，许可见 `third_party/qrc/LICENSE-WXRIW`。
- [jixunmoe-go/qrc](https://github.com/jixunmoe-go/qrc)：Copyright (c) 2023 Jixun Wu，MIT。本地缓存的 QMC 掩码和容器布局据此适配，许可见 `third_party/qrc/LICENSE-Jixun`。
- 固定上游修订与改动说明见 `third_party/qrc/README.md`；仅包括本地歌词解码，不包括账号接口或第三方歌词内容。

## 构建工具

Roslyn 编译器通过固定版本 NuGet 包按需获取，并校验 SHA-256；工具缓存不提交到仓库。其许可归各自上游项目所有。
