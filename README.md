# TLSnipPin — Windows 截图贴图工具

轻量级 Windows 11 截图工具：区域截图、标注、把截图"钉"在桌面随意拖动。

> 技术栈：C# + WPF（.NET 8） · 设计文档见 `../截图工具设计文档.md`

## 当前进度

- ✅ 单实例 + Generic Host（DI 容器）
- ✅ 系统托盘（**左键打开历史主窗口** / 右键菜单；X 关闭窗口后台常驻）
- ✅ **截图历史主窗口**：启动即显示
  - 卡片流缩略图（时间/尺寸/来源），悬停显示操作（📌钉住/💾保存/🗑单条删除）
  - **多选模式**：☑ 多选 → 点选任意卡片（✓ 徽标）→ 🗑 删除所选，Esc 退出
  - **清空历史**：全部 / **指定日期之前**（日期选择器）
  - **存储目录可自定义**（设置 → 截图历史 → 数据目录，修改后自动迁移）
  - **无删除上限**：超过活跃上限（默认 200，可调，0=永不归档）时最旧的自动**归档到 archive.zip**，标题栏显示已归档数量并可直接打开归档位置
  - 顶栏显示当前热键，点击跳转设置修改
- ✅ 全局热键（F1 区域截图 / F2 钉住剪贴板 / F3 全屏 / F4 显隐贴图，可在设置中自定义）
- ✅ 区域截图：全屏遮罩 + 拖拽框选 + 尺寸提示 + 选区实时清晰显示
- ✅ **智能窗口识别**：框选时悬停自动吸附窗口边界，点击直接选中整窗，拖开转手动
- ✅ **标注编辑器**（框选后进入编辑态）
  - 工具：选择/移动、矩形、椭圆、箭头、画笔、文字、序号、**马赛克**、**高亮**
  - 属性：8 预设色、粗细三档（马赛克粗细映射为块大小）
  - 交互：Shift 画正方形/45° 吸附、选中虚线高亮、Delete 删除
  - 撤销/重做（Ctrl+Z / Ctrl+Y / 工具条按钮），绘制、删除、移动全部入栈
  - 输出：✅完成(复制) 📌钉住 💾保存 📋复制 ✕取消（Esc）
- ✅ 贴图悬浮窗：置顶、拖动、双击/Esc 关闭、Ctrl+滚轮缩放、Shift+滚轮旋转、Alt+滚轮透明度、右键菜单
- ✅ **贴图会话持久化**：退出时保存所有贴图（位置/缩放/旋转/透明度），下次启动恢复（可在设置关闭）
- ✅ 保存到本地 + 复制剪贴板 + 另存为（PNG/JPG/BMP、自动命名）
- ✅ **设置界面**：热键自定义（按键捕获）、保存目录/命名/格式/质量、截图选项、贴图选项、开机自启（HKCU 注册表）
- ⏳ 放大镜、滚动长截图 — 后续

## 环境要求

- **.NET 8 SDK**
- Windows 10 1809+ / Windows 11

## 构建与运行

```powershell
cd D:\WindowsApp\SnipPin
dotnet run
```

运行后程序常驻系统托盘（蓝色图标）：
- 按 `F1` 或左键点托盘图标 → 悬停窗口自动吸附 / 拖拽框选 → **标注编辑器**（矩形/箭头/文字/马赛克/高亮等，可撤销）→ 点 ✓/📌/💾/📋 输出
- 按 `F3` → 全屏截图并钉住
- 按 `F2` → 把剪贴板里的图片钉住
- 按 `F4` → 显示/隐藏所有贴图
- 托盘右键 → 设置：自定义热键、保存目录、遮罩暗度、会话恢复、开机自启等

## 发布单文件

```powershell
dotnet publish -c Release
```
产物在 `bin\Release\net8.0-windows\win-x64\publish\TLSnipPin.exe`

## 目录结构

```
SnipPin/
├── SnipPin.csproj            # 工程文件（WPF / DI / 托盘包）
├── app.manifest              # Per-Monitor V2 DPI 感知
├── Program.cs                # 入口：单实例 + Host(DI)
├── MainWindow.cs             # 隐藏主窗体：托盘 + 热键钩子 + 会话恢复/持久化
├── Settings/
│   └── SettingsWindow.cs     # 设置窗体（含开机自启注册表写入）
├── History/
│   └── HistoryWindow.cs      # 截图历史主窗口（缩略图/删除/重新钉住）
└── Core/
    ├── Configuration/
    │   └── ConfigService.cs  # 配置模型 + 读写 config.json
    ├── Native/
    │   └── NativeMethods.cs  # Win32 P/Invoke（热键/截屏/窗口枚举）
    ├── Services/
    │   ├── IServices.cs           # 服务接口
    │   ├── ScreenCapturer.cs      # GDI 屏幕捕获
    │   ├── HotkeyService.cs       # 全局热键
    │   ├── StorageService.cs      # 保存/剪贴板
    │   ├── HistoryService.cs      # 截图历史（记录/删除/清空）
    │   ├── CaptureService.cs      # 截图流程协调
    │   ├── CaptureOverlayWindow.cs# 框选遮罩窗（含智能窗口吸附）
    │   ├── PinService.cs          # 贴图管理 + 会话持久化
    │   ├── PinWindow.cs           # 贴图悬浮窗
    │   └── PinState.cs            # 贴图会话状态模型
    └── Annotations/
        ├── Annotation.cs              # 标注对象模型（矩形/椭圆/箭头/画笔/文字/序号/马赛克/高亮）
        ├── Commands.cs                # 命令模式撤销/重做栈
        ├── Pixelator.cs               # 马赛克像素化处理
        ├── AnnotationCompositor.cs    # 底图+标注 烘焙为最终位图
        ├── AnnotationToolbar.cs       # 标注工具条控件
        └── AnnotationEditorWindow.cs  # 标注编辑窗（绘制/选中/移动/撤销交互）
```

## 设计原则

- 所有下层依赖通过**构造函数注入**，禁止内部 `new()` 下层服务
- 标注为矢量对象，命令模式实现撤销/重做
- 注释/日志/文档统一中文

