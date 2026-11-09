# CW Trainer V2（ESP32-S3 + C# Windows 桌面端）

本包实现 V2 的核心目标：**连接电脑并完成握手后，可选择关闭 ESP32 上的实体蜂鸣器，改由 Windows 电脑扬声器发声。**

V2 的职责划分：

- ESP32-S3：读取电键、消抖、点划时序、直键/自动键逻辑、控制实体蜂鸣器、通过 USB CDC 发送事件。
- C# 桌面端：USB 串口握手、WPM/模式/输出控制、点划预览、电脑音调和音量、通信日志。
- 电脑音频不是负责计算 CW 时序；ESP32 仍然是时序主控。电脑只按 ESP32 发来的 `ELEMENT` 事件播放对应长度的声音。

> 本交付是可继续迭代的 V2 原型工程。当前自动键 A/B 实现用于功能联调，不应视为经过示波器、标准电键测试套件验证的商用 keyer。实际高速度挤压/释放表现需要用户测试后继续校准。

---

## 1. 当前硬件与引脚约定

按最近一次确定的左右手定义：

| 功能 | ESP32-S3 引脚 | 约定 |
|---|---:|---|
| DIT（点） | GPIO5 | 左手桨片 |
| DAH（划） | GPIO4 | 右手桨片 |
| 高电平触发蜂鸣器 I/O | GPIO6 | HIGH 响、LOW 静音 |
| 电键公共端 | GND | 两个触点共用 |

电键输入使用 `INPUT_PULLUP`，按下时对应 GPIO 与 GND 接通，读到 LOW。

**重要：** 如果你实际杜邦线仍是 GPIO4 接左边 DIT、GPIO5 接右边 DAH，不要盲目改线，只需在 `.ino` 顶部交换 `PIN_DIT` 和 `PIN_DAH` 两个定义，让代码与实际接线一致。蜂鸣器仍为 GPIO6。

蜂鸣器模块供电应沿用已验证正常的接法；不要把 5V 接入 ESP32 GPIO。V2 不改变你的电源接线。

---

## 2. Arduino 固件烧录

文件：

`Firmware/CW_Trainer_V3/CW_Trainer_V3.ino`

使用你已经成功安装的：

- Arduino IDE 2.3.6
- `esp32 by Espressif Systems`：2.0.17-cn
- Board：`ESP32S3 Dev Module`

工具菜单沿用你已经验证成功的配置：

| 参数 | 设置 |
|---|---|
| USB Mode | Hardware CDC and JTAG |
| USB CDC On Boot | Enabled |
| CPU Frequency | 240MHz |
| Flash Mode | QIO 80MHz（沿用已能成功烧录的值） |
| Flash Size | 16MB |
| Partition Scheme | 16M Flash (3MB APP/9.9MB FATFS) |
| PSRAM | OPI PSRAM |
| Upload Mode | UART0 / Hardware CDC |
| Upload Speed | 115200 |

操作：

1. Arduino IDE → 文件 → 打开，打开上面的 `.ino` 文件。
2. 确认开发板为 `ESP32S3 Dev Module`，端口选开发板当前 COM 端口。
3. 确认 GPIO 定义与实际左右桨片接线相符。
4. 点击“验证”（对勾），等待编译完成。
5. 点击“上传”（向右箭头）。
6. 若卡在 `Connecting...`，按住 BOOT，点按 RESET，松开 RESET，再松开 BOOT，然后重新上传。
7. 上传完成后按一下 RST。

上传前后，Arduino 串口监视器可以用于调试，但运行 C# 软件时必须关闭串口监视器，因为一个 COM 口通常不能同时被两个程序打开。

### 固件顶部可调参数

V2 默认值：

- WPM：15
- 模式：Iambic B
- 声音输出：实体蜂鸣器
- 心跳丢失超时：6 秒后恢复实体蜂鸣器

WPM、模式与输出在电脑连接后由桌面端设置。固件重启时回到默认值。

---

## 3. C# 桌面端：使用 Visual Studio 2026

项目文件：

`Desktop/CWTrainer.Desktop/CWTrainer.Desktop.csproj`

这是 WPF 桌面程序，目标框架为 `.NET 10.0-windows`，使用 `System.IO.Ports` NuGet 包与 COM 端口通信。电脑声音直接使用 Windows 自带 `winmm.dll` 的 `PlaySound` 播放内存 WAV，不需要 NAudio。

### 打开工程

1. 启动 Visual Studio 2026。
2. 选择“打开项目或解决方案”。
3. 选择 `Desktop/CWTrainer.Desktop/CWTrainer.Desktop.csproj`。
4. 等待 NuGet 还原依赖。需要恢复 `System.IO.Ports` 10.0.0。
5. 如果 Visual Studio 提示缺少 .NET 10 桌面开发组件，在 Visual Studio Installer 中安装“.NET 桌面开发”工作负载和 .NET 10 SDK/Runtime。
6. 选择 `Debug` 或 `Release`、`x64`，按 F5 运行。

### 第一次连接

1. 确认 ESP32-S3 已烧录 V2 固件并通过 USB 数据线连接电脑。
2. 关闭 Arduino IDE 的串口监视器、其他串口调试器。
3. 启动 CW Trainer 桌面程序。
4. 点击“刷新端口”。
5. 选择 ESP32-S3 对应 COM 口（例如 COM6；实际以设备管理器为准）。
6. 点击“连接练习器”。
7. 程序会发送 `HELLO,1` 并等待开发板回复 `READY,1,CWTRAINER,2.0.0`。只有握手成功，界面才显示已连接并启用控制项。

如果连接失败：

- 先检查 COM 口是否被 Arduino 串口监视器占用。
- 设备管理器中确认运行时的 USB COM 口。
- 按一下开发板 RST，再刷新端口并重试。
- 确认已烧录 V2 固件，而不是 V1 测试程序。
- 不要同时打开两个串口终端。

### 关闭实体蜂鸣器，改用电脑发声

连接成功后，在左侧「声音输出」的三段式选择器里切换：

| 选项 | 效果 |
|---|---|
| 实体 | 只让开发板蜂鸣器响（`SET,OUTPUT,BUZZER`） |
| 电脑 | 关闭实体蜂鸣器，由电脑播放（`SET,OUTPUT,PC`） |
| 双路 | 两边同时响，注意可能比较吵（`SET,OUTPUT,BOTH`） |

选择后立即生效，不需要再点确认按钮。选「电脑」时 ESP32 立即把 GPIO6 拉低；之后每产生一个点或划，就发送类似 `ELEMENT,DIT,80000` 的事件，桌面端据此播放 80ms 的电脑音。

电脑音调与音量滑块只影响电脑扬声器。当前实体有源蜂鸣器无法通过软件调节音高/音量。

### 调节速度和模式

- WPM：5–60，拖动滑块即生效（停手约 0.3 秒后下发 `SET,WPM`，避免拖动过程中刷命令）。
- 模式：直键 / Iambic A / Iambic B，点选即下发 `SET,MODE`。
- 滑块下方会实时显示当前速度对应的点划时长，例如 15 WPM 时「点 80 · 划 240 ms」。
- Straight Key 模式使用左侧 DIT 输入（GPIO5）作为直键输入；右侧 DAH 输入不参与直键发声。

### 界面与设计规范

界面按一套明确的规范实现，不是随手堆控件：

- **配色只有三种**：墨黑 `#0F0F10`、纸白 `#FFFFFF`（配 `#F4F4F3` 浅灰画布和中性灰阶）、信号橙 `#FF5A1F`。橙色只用在滑块进度、当前点划、状态点和图标上，不参与大面积着色。
- **平面化**：卡片靠浅灰画布与白卡片之间的色差分层，不使用投影，也不画描边（避免「幽灵边框」）。
- **8pt 栅格**：间距只用 4 / 8 / 12 / 16 / 20 / 24 / 32 几档，卡片内边距 20，卡片之间 20。
- **同心圆角**：卡片 20 → 内部控件 12 → 分段控件内块 9（外层圆角减去内边距）。
- **控件全部自绘**：按钮、下拉框、滑块、分段选择器、列表都是自定义模板，没有用系统默认外观。
- **图标统一**：所有图标画在 24 网格内，再用 Viewbox 统一输出 20（按钮内）或 14（窗口按钮），因此大小和描边粗细一致。
- **不留页面滚动条**：整页是固定 1200×760 版面，外层用等比缩放适配任意窗口尺寸。窗口可以拉到很小（最小 900×570）或很大，内容永远完整显示，不会出现裁切或滚动条。这一点也是为了方便以后移植到安卓——一套版面适配所有屏幕。
- **应用图标**：`Desktop/CWTrainer.Desktop/Assets/app.ico`，由 `.workbuddy/tools/make_icon.py` 生成（黑底 + 橙色点划，即摩斯码 A 的 `.-`），多尺寸内嵌，同时用于任务栏和窗口。

### 单文件 EXE 发布

本工程默认设置为 Windows x64、框架依赖型单文件发布：体积较小，但目标电脑需要安装 .NET 10 Desktop Runtime。

Visual Studio 操作：

1. 在解决方案资源管理器中右键 `CWTrainer.Desktop` 项目。
2. 选择“发布（Publish）”。
3. 选择“文件夹（Folder）”作为目标。
4. 打开发布配置设置（Show all settings / 编辑）。
5. 配置：
   - Configuration：Release
   - Target Framework：net10.0-windows
   - Deployment mode：Framework-dependent
   - Target Runtime：win-x64
   - Produce single file：勾选
6. 保存并点击“发布”。
7. 单文件 EXE 位于项目的 `bin\Release\net10.0-windows\win-x64\publish\` 目录。

命令行等效方式（在项目目录打开终端）：

```powershell
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

如果要把 EXE 拿到没有 .NET 10 Desktop Runtime 的电脑运行，把部署模式改为 Self-contained（自包含）。这样也能发布单文件，但 EXE 会明显变大，因为要把 .NET Runtime 一起打包。WPF 应用不建议开启 trimming。

---

## 4. V2 串口协议

串口参数：115200 baud，8 data bits，No parity，1 stop bit（8N1）；文本为 ASCII，每条消息以换行符结束。

### PC → ESP32

| 命令 | 作用 |
|---|---|
| `HELLO,1` | 握手 |
| `PING` | 心跳，每 1.5 秒发送一次 |
| `GET` | 请求设备状态 |
| `SET,OUTPUT,BUZZER` | 实体蜂鸣器 |
| `SET,OUTPUT,PC` | 电脑发声、实体蜂鸣器静音 |
| `SET,OUTPUT,BOTH` | 两边同时发声 |
| `SET,WPM,20` | 设置 20 WPM（范围 5–60） |
| `SET,MODE,IAMBIC_B` | 设置键控模式 |
| `SET,MODE,IAMBIC_A` | 设置键控模式 |
| `SET,MODE,STRAIGHT` | 直键模式 |

### ESP32 → PC

| 事件 | 含义 |
|---|---|
| `READY,1,CWTRAINER,2.0.0` | 握手成功 |
| `STATUS,WPM,15,MODE,IAMBIC_B,OUTPUT,BUZZER` | 当前配置 |
| `KEY,DIT,1` | 左侧点桨按下 |
| `KEY,DIT,0` | 左侧点桨松开 |
| `KEY,DAH,1` | 右侧划桨按下 |
| `KEY,DAH,0` | 右侧划桨松开 |
| `ELEMENT,DIT,80000` | 开始发送一个 80ms 的点 |
| `ELEMENT,DAH,240000` | 开始发送一个 240ms 的划 |
| `TONE,ON` / `TONE,OFF` | Straight Key 模式的连续音开始/结束 |
| `ACK,...` | 命令确认 |
| `PONG` | 心跳响应 |

关键设计：电脑不计算点划时序。ESP32 在本地生成时序，并把实际发送的每个元素通知电脑。Windows 音频播放会受 USB、系统调度和音频设备缓冲影响，所以电脑声音可能比实体蜂鸣器略有延迟；但不会反过来影响 ESP32 的电键时序。

---

## 5. 声音保护机制

- 固件启动默认 `OUTPUT_BUZZER`，因此不连接电脑时仍可独立练习。
- 只有 PC 完成握手并发送 `SET,OUTPUT,PC` 后，实体蜂鸣器才会静音。
- C# 程序每 1.5 秒发送一次 `PING`。
- 如果固件连续 6 秒收不到心跳，会自动切回 `OUTPUT_BUZZER`。
- 桌面程序主动断开时，也会先发送 `SET,OUTPUT,BUZZER`，再关闭 COM 口。

这样即使软件崩溃、USB 断开或电脑关机，设备也不会永久处于静音状态。

---

## 6. 现阶段限制与注意事项

1. 电脑端目前显示实时点划预览和通信事件；尚未实现完整的英文字符解码、QSO 记录、训练统计和电脑发文字自动发送。
2. 当前电脑发声使用 Windows 自带异步 WAV 播放接口，不依赖第三方音频库。不同声卡/Windows 音频缓冲可能带来少量起音延迟；如果将来追求低延迟、可混音和更高级音频控制，可改用 WASAPI/NAudio。
3. WPM 与键控模式目前在设备重启后恢复默认值；连接后由桌面端重新设置。后续可以加入 ESP32 NVS 参数保存。
4. Iambic A/B 当前代码是 V2 联调原型，挤压后释放行为需要真实双桨测试并进一步校准；尤其建议在 10、15、20、25 WPM 下检查双桨挤压、单侧优先、释放记忆与连续交替。
5. 高电平触发有源蜂鸣器只有开/关，不支持自由调节音调和实体音量。V2 的音调、音量设置只作用于电脑播放。
6. 用户需要先关闭 Arduino 串口监视器再让 C# 软件打开 COM 口。一个串口端口不能同时被多个常规串口程序独占打开。

---

## 7. 推荐验收顺序

1. 先只烧录 V2 固件，不运行 C# 软件；左右桨片仍应能让实体蜂鸣器按点划响。
2. 打开 C# 程序，刷新并选择 COM 口，完成握手。
3. 选择“电脑软件发声”，确认实体蜂鸣器静音。
4. 左桨按下：电脑应发短音，预览出现 `·`。
5. 右桨按下：电脑应发长音，预览出现 `—`。
6. 把音量滑块调低，确认电脑音量随之变化。
7. 选择“实体蜂鸣器”，确认实体蜂鸣器恢复。
8. 再选择“电脑软件发声”，拔掉 USB 或关闭桌面程序；等待最多约 6 秒，实体蜂鸣器应恢复。
9. 设置 20 WPM，再分别测试点和划，点/划长度应接近 1:3。
10. 最后再测试 Iambic A/B 挤压与释放行为。

