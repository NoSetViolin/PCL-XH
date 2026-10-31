# 底栏 HDR 中央柔光验证

底栏玻璃滑块按住时，SDR shader 使用中央最亮、向边缘衰减的光照，去掉
滑块 rim 和内侧暗边。HDR 层使用 FP16/scRGB DirectComposition 交换链，
仅在滑块所在显示器当前输出 HDR 时启用。光斑最高目标为显示器报告峰值与
600 nits 中较小者（scRGB 1 = 80 nits），不代表测得的屏幕亮度。
文字按原始覆盖率以正常颜色合成到柔光之上，不使用挖空、膨胀遮罩或文字暗边。
文字亮度随 Windows SDR 内容白电平匹配；高对比度仍保留系统可识别轮廓并关闭 HDR。

不创建额外输入 HWND，不改变 WPF 捕获和点击区域。按压/松手通过透明度过渡；
稳定长按复用纹理；拖动与形变时刷新小尺寸标签图像，复用交换链。
关闭 HDR、跨到 SDR 屏、图形错误、隐藏/最小化和关闭窗口均有回退/清理。
滑块跨越屏幕边界时暂时关闭 HDR，以免高光落入另一块 SDR 屏幕。
显示器能力每秒及 display/settings/DPI 消息后刷新，显卡错误冷却 15 秒后可重试。

## 自动检查

```powershell
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Release -p:Platform=x64 --filter 'FullyQualifiedName~HdrHighlightTests|FullyQualifiedName~LiquidTab'
& tools/hdr-smoke/Run.ps1
```

`Run.ps1` 使用独立数据、配置、缓存、实例锁和编译中间目录，不读取用户游戏和账号。
它只在隔离构建中注入 `HdrSmoke.cs`。普通构建不含反射入口、强制 HDR 或测试开关。
注入模式测试真实 D3D11/FP16 交换链及 GPU 读回、真实显示器能力、模拟 SDR 能力门控、
硬件加速禁用、长按/拖动/松手、鼠标捕获、最小化和资源释放；截图覆盖四种语言、
浅色/深色、810/1280 DIP。模拟 SDR 门控不能代替真实 SDR 显示器测试。

截图来自普通 SDR 捕获，用于布局和外观检查，不能证明实际 HDR 峰值。
仍需人工验证混合 HDR/SDR 跨屏、系统 HDR 实时切换、设备丢失恢复，以及
肉眼观感/亮度测量。单屏机器不把这些场景记为端到端通过。

## 临时预览

未提交源码仅构建到 `artifacts/hdr-preview/Release/`，不作为正式发布。
正式交付仍按仓库 AGENTS.md 使用准确提交、tag、SHA 和标准产出目录。
