# Dock 材质

在 Dock 背景空白处右键，打开“材质设置”：

- **玻璃 · 实时清透**：直接透出后方窗口，保留细边缘和高光。
- **磨砂 · 实时模糊**：模糊后方的真实窗口内容，保留背景颜色；应用图标保持清晰。

选择会保存，重启后继续使用。1.6.1 曾将 Dock 强制设为玻璃，升级后可以重新选择磨砂。

## 实现与范围

Dock 使用独立的 Windows Composition 背景层，由 `CreateHostBackdropBrush` 读取后方合成内容，在同一合成层使用圆角几何遮罩。无需循环截屏，也不读取或保存屏幕图像。背景层跟随 Dock 的位置、尺寸和显隐；关闭或切换到玻璃时释放。

这里仅改变 Dock 材质。收纳框等桌面组件保留原有静态壁纸磨砂；启动台保持实时透明。

合成背景的透明效果受 Windows 透明效果设置和电源策略影响。当前本机 Windows 11 已验证背景透色、圆角与材质切换；Windows 10 使用兼容接口开启背景采样，尚未在 Windows 10 真机验证。系统不支持所需接口时不会建立模糊层，玻璃仍可使用。

开发依据：[Microsoft CreateHostBackdropBrush](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)、[CompositionRoundedRectangleGeometry](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositionroundedrectanglegeometry)。
