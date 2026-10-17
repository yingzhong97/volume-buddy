# 音量助手 VolumeBuddy

一个轻量的 Windows 本地音量调节小工具，常驻系统托盘，左键点图标即弹出面板。

## 下载

前往 [Releases](https://github.com/yingzhong97/volume-buddy/releases/latest) 下载
`VolumeBuddy-v1.0-win-x64.zip`，解压后双击 `VolumeBuddy.exe` 即可。

运行需要 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0/runtime/desktop)（Windows 11 一般已自带）。

## 功能

- **输出设备**：列出所有播放设备，每台设备带独立的总音量滑条与静音；点击设备名一键切换系统默认输出设备
- **输入设备**：麦克风/输入设备列表，同样支持切换默认与独立音量
- **空间音效**：自动检测本机可用的空间音效格式（关闭 / Windows Sonic / 杜比全景声 / DTS），一键切换
- **应用音量**：类似系统音量合成器，每个正在发声的应用可单独调音量、单独静音
- **界面**：Win11 风格横向三分区，跟随系统深浅色；托盘弹出带淡入/收起动画，面板贴任务栏上方，不遮挡托盘图标
- **轻量**：托盘待命时停止全部轮询，工作集自动收缩；主程序约 200KB（依赖系统已装的 .NET 8 桌面运行时）

## 使用

1. 双击 `dist/VolumeBuddy.exe`，程序缩到托盘
2. 左键点击托盘图标打开面板，再次点击收起
3. 面板右上角齿轮进入设置：打开方式（托盘弹出 / 独立窗口）、开机自启

详见 [dist/使用说明.txt](dist/使用说明.txt)。

## 构建

需要 .NET 8 SDK：

```bash
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

## 项目结构

```
Interop/AudioInterop.cs   WASAPI COM 互操作（设备枚举 / 端点音量 / 会话 / PolicyConfig）
Audio/AudioService.cs     设备枚举、每设备独立音量、默认设备切换
Audio/AudioSessionService.cs  原生音量合成器（会话枚举 / 单应用音量与静音）
Audio/VolumeWriter.cs     音量写入的后台线程（合并最新值，避免阻塞 UI）
Audio/SpatialService.cs   空间音效检测与切换
Services/AppSettings.cs   设置持久化、开机自启、主题检测
Services/TrayHost.cs      托盘宿主（启动只驻留托盘，窗口惰性创建）
MainWindow.xaml(.cs)      主界面与交互
```

## 技术说明

- 设备枚举、每设备独立音量、切换默认设备：Windows Core Audio（WASAPI）原生接口
- **应用音量**：原生 `IAudioSessionManager2` 枚举会话，QI `ISimpleAudioVolume` 直接读写（约 0.14ms/次），拖动实时生效
- **空间音效**：通过 [NirSoft SoundVolumeView](https://www.nirsoft.net/utils/sound_volume_view.html) 的命令行切换；`SoundVolumeView.exe` 为原版未修改文件，版权归 NirSoft 所有，按其免费软件许可随本项目分发，仅用于个人本地使用
- 空间音效切换使用格式 GUID（中文系统下按名称匹配会失败），如 Windows Sonic `{B53D940C-B846-4831-9F76-D102B9B725A0}`

## 说明

本项目为个人自用工具，仅在 Windows 11 上测试过。使用前建议先备份系统音频设置。
