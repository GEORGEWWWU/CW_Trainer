# CW Trainer

**ESP32-S3 双桨电键 + Windows 桌面端的摩斯码（CW）练习器。**

硬件负责读桨和生成点划时序，电脑负责发声、抄收和显示。电脑没连上也能单独拿开发板练习。

<img src="assets/hardware.jpg" width="620" alt="CW Trainer 实物">

---

## 目录

- [这是什么](#这是什么)
- [界面](#界面)
- [功能](#功能)
- [硬件](#硬件)
- [快速开始](#快速开始)
- [串口协议](#串口协议)
- [项目结构](#项目结构)
- [设计规范](#设计规范)
- [已知限制](#已知限制)
- [路线图](#路线图)
- [相关文档](#相关文档)

---

## 这是什么

一套自己搭的 CW 练习系统。左边的 DIY 双桨电键接到 ESP32-S3，开发板用原生 USB 连电脑。

| | 负责 |
|---|---|
| **ESP32-S3** | 读左右桨、触点消抖、生成点划时序、Iambic A/B 与直键逻辑、驱动实体蜂鸣器、通过 USB CDC 上报事件 |
| **Windows 软件** | USB 握手与心跳、下发 WPM/模式/输出、把文字交给设备拍发、播放电脑音频、实时解码抄收、通信日志 |

关键分工：**点划时序永远由单片机生成**，电脑只按收到的 `ELEMENT` 事件决定"响多久"，这样界面卡顿不会影响手键手感。

## 界面

**亮色 / 暗色**——跟随 Windows 的「应用模式」自动切换，没有开关：

<img src="assets/ui-light.png" width="640" alt="亮色">

<img src="assets/ui-dark.png" width="640" alt="暗色">

**已连接 · 电脑发声**——右侧日志里是真实设备返回的 `ACK,OUTPUT,PC`：

<img src="assets/ui-connected.png" width="720" alt="已连接">

**发送**——输入文字，设备按当前速度自动拍发；进度实时显示，按桨或点「停止」立刻打断：

<img src="assets/ui-sending.png" width="720" alt="发送">

**摩斯电码表**——抄收卡片右上角的小入口，点任意一行就按当前速度和音调播一遍：

<img src="assets/ui-dictionary.png" width="720" alt="电码表">

**通联模式（模拟）**——顶栏切换到「通联」。一台虚构的 HF 接收机：频谱 + 瀑布图、波段活动列表、S 表、白噪声，点击列表里的电台即可调谐过去抄收：

<img src="assets/ui-qso-light.png" width="720" alt="通联模式（亮色）">

<img src="assets/ui-qso-dark.png" width="720" alt="通联模式（暗色）">

**设置**——顶栏齿轮。外观三选一（跟随系统 / 浅色 / 深色），改完立刻生效并记住：

<img src="assets/ui-settings.png" width="620" alt="设置">

布局分三块：左列是**设备连接 / 声音输出 / 键控**，右侧是**实时抄收**主视区和**通信记录**。整页没有滚动条——固定版面等比缩放，窗口从 900×600 拉到全屏都能一次看全。

## 功能

- **三种声音输出**：实体蜂鸣器 / 电脑发声 / 双路同时
- **速度**：5–60 WPM，拖动即生效，旁边实时换算点划时长（15 WPM → 点 80 ms、划 240 ms）
- **键控模式**：直键、Iambic A、Iambic B
- **发送**：输入文字，由设备按当前 WPM 自动拍发。输入框只接受电码表里的字符、小写自动转大写、中文标点就地折算；可以随时停止，按一下桨就立刻打断
- **实时抄收**：解码后的文本、当前字符的点划序列、电键状态指示灯
- **摩斯电码表**：字母、数字、标点共 54 条，点一行按当前 WPM 播放，顺带练耳朵
- **通联模式（模拟）**：不接设备也能玩的虚构 HF 接收机——
  - 频谱 + 瀑布图（黑/橙/白配色，对数 dBm 刻度），扫宽 10k/25k/50k/100k 可切
  - 频率 0–30 MHz 任意调整，自动归入业余波段显示 `Band xx m`（160m–10m 九个波段）
  - 发射模式 CW / USB / LSB：SSB 电台按惯例落频——USB 在载波上方 +1.2 kHz，LSB 在下方 −1.2 kHz
  - S 表按标准刻度：S9 = −73 dBm，每格 6 dB，量程 S1 到 S9+60；同时显示 dBm 与 µV
  - 各波段有符合真实习惯的噪声底（160m 最高、10m 最低）与缓慢起伏，带限白噪声音频可开关
  - 波段活动列表列出本波段模拟电台（低端 CW、中高端 SSB），点一下自动调谐并切换模式
  - 界面为通联模式独立设计，不保留练习模式的版面；连接状态等必要设置仍在顶栏
- **深色模式**：默认跟随系统「应用模式」，也可以在设置里锁定浅色或深色
- **全屏**：标题栏右侧的展开按钮或 `F11`，`Esc` 退出。整页等比缩放，全屏后铺满整个显示器
- **设置**：顶栏齿轮打开，外观在这里切换。外观、CW 音调、音量都会记住，存在 `%AppData%\CWTrainer\settings.json`
- **电脑音频**：可指定输出设备，默认「跟随系统」——静音 4 秒后释放设备，下次发声重新取当前默认设备，插拔耳机不用重启
- **断联保护**：软件每秒心跳，3 秒无回应就在界面上标「失联」并禁用控制项；固件 2.1 起改为**主机租约**——`HELLO`/`PING`/`GET`/`SET` 续租，`BYE` 或 6 秒超时立即失效，蜂鸣器电平每个主循环按租约重算，因此任何命令顺序都不会让电键永久静音
- **通信日志**：收发的每一行协议、解码结果（`-.-. → C`）都在右侧记录

## 硬件

| 功能 | ESP32-S3 引脚 | 说明 |
|---|---:|---|
| 左桨（点 DIT） | GPIO5 | `INPUT_PULLUP`，按下读到 LOW |
| 右桨（划 DAH） | GPIO4 | `INPUT_PULLUP`，按下读到 LOW |
| 蜂鸣器 | GPIO6 | 高电平触发 |
| 电键公共端 | GND | 两侧触点共用 |

- 主控：ESP32-S3-N16R8（16 MB Flash / 8 MB PSRAM）
- 电键：亚克力底座 DIY 双桨，两个触点 + 公共端
- 声音：高电平触发的有源蜂鸣器模块
- 连接：开发板**原生 USB（OTG / USB-Serial-JTAG）**，不是板上那颗 USB-TTL

> 电键是无源开关，公共端接 GND 即可，**不要给触点供电**；也不要把 5V 接到任何 GPIO。

## 快速开始

### 1. 烧录固件

用 Arduino IDE 打开 `Firmware/CW_Trainer_V3/CW_Trainer_V3.ino`。

- Arduino IDE 2.3.6
- `esp32 by Espressif Systems` **2.0.17-cn**（中国区镜像索引）
- Board：`ESP32S3 Dev Module`

工具菜单：

| 项 | 值 |
|---|---|
| USB Mode | Hardware CDC and JTAG |
| USB CDC On Boot | Enabled |
| Flash Size | 16 MB |
| Partition Scheme | 16M Flash (3MB APP/9.9MB FATFS) |
| PSRAM | OPI PSRAM |
| Upload Speed | 115200 |

对等的 FQBN：

```text
esp32:esp32:esp32s3:UploadSpeed=115200,USBMode=default,CDCOnBoot=cdc,UploadMode=cdc,FlashSize=16M,PartitionScheme=app3M_fat9M_16MB
```

烧完按一下 RST。**上传和运行时都不要同时开串口监视器**，一个 COM 口只能被一个程序打开。

### 2. 运行软件

需要 **.NET 10 Desktop Runtime**（框架依赖，体积小）。

直接下载：到 [Releases](https://github.com/GEORGEWWWU/CW_Trainer/releases) 取最新的 `CWT.exe`，双击即可，不用安装。

自己构建（Release 出版本就是单文件，约 380 KB）：

```powershell
cd Desktop/CWTrainer.Desktop
dotnet build                                                   # 调试
dotnet publish -c Release -r win-x64 --self-contained false     # 单文件
```

产物名固定为 **`CWT.exe`**，落在 `bin/Release/net10.0-windows/win-x64/publish/CWT.exe`；
本地开发时也会同步一份到仓库根目录的 `dist/CWT.exe`（该目录不进版本库），随时可测。

目标机器需要 .NET 10 Desktop Runtime；如果希望对方什么都不用装，把 `--self-contained` 改成 `true`，代价是 exe 会变成 60 MB 左右。

### 自动构建

`.github/workflows/build.yml` 已经配好：

- 推送到 `main`：自动编译，产物作为 Actions artifact（`CWT-win-x64`）可下载
- 打 `v*` 标签（如 `v2.1.0`）：自动编译并把 `CWT.exe` 挂到 GitHub Release 上

### 3. 用起来

1. 选 COM 口（ESP32-S3 的 USB OTG 那个），点「连接设备」，状态变成「已连接 · COMx」
2. 在「声音输出」选输出方式；选「电脑」时实体蜂鸣器静音
3. 拖 WPM、点模式，改完立即下发给设备
4. 开始拍键，右侧会实时显示点划和抄收文本

## 串口协议

115200 / 8N1，ASCII，换行分隔。

**上位机 → 设备**

| 命令 | 作用 |
|---|---|
| `HELLO,1` | 握手，设备回 `READY` |
| `PING` | 心跳（每秒一次） |
| `GET` | 查询状态 |
| `SET,OUTPUT,BUZZER\|PC\|BOTH` | 声音输出 |
| `SET,WPM,5..60` | 速度（V2.2 起写入 NVS，重新上电仍生效） |
| `SET,MODE,STRAIGHT\|IAMBIC_A\|IAMBIC_B` | 键控模式（V2.2 起写入 NVS） |
| `SEND,BEGIN` | 开始一个发送任务，清空队列 |
| `SEND,TEXT,<text>` | 追加文本，单行 ≤ 80 字符，长文本由软件分片 |
| `SEND,END` | 文本给完，按当前 WPM 开始拍发 |
| `SEND,STOP` | 中止发送并清空队列 |
| `BYE` | 退出，设备交还实体蜂鸣器 |

**设备 → 上位机**

| 事件 | 说明 |
|---|---|
| `READY,1,CWTRAINER,3.0.0` | 握手成功 |
| `STATUS,WPM,15,MODE,IAMBIC_B,OUTPUT,BUZZER` | 当前配置 |
| `KEY,DIT,1` / `KEY,DAH,0` | 桨叶按下 / 松开 |
| `ELEMENT,DIT,80000` | 开始发送一个 80 ms 的点（单位微秒） |
| `TONE,ON` / `TONE,OFF` | 直键模式的连续音 |
| `SEND,START,<n>` | 开始拍发，n 为待发字符数 |
| `SENDING,<remaining>` | 每发完一个字符上报剩余数 |
| `SEND,DONE` | 队列发完 |
| `SEND,ABORTED,PADDLE\|STOP\|MODE\|HOST` | 被手键、停止指令、模式切换或心跳中断中止 |
| `WARN,CHAR,<c>` | 该字符不在电码表里，已跳过 |
| `ACK,<KEY>,<VALUE>` / `PONG` / `ERROR,<REASON>` | 应答 |

## 项目结构

```text
CW_Trainer/
├─ Firmware/CW_Trainer_V3/
│  └─ CW_Trainer_V3.ino      # ESP32-S3 固件：读桨、点划时序、协议、自动拍发
├─ Desktop/CWTrainer.Desktop/
│  ├─ Themes/                # 亮色 / 暗色两套色板（键完全一致）
│  ├─ App.xaml               # 字体、间距、全部自绘控件模板
│  ├─ MainWindow.xaml        # 界面布局
│  ├─ MainWindow.xaml.cs     # 串口、心跳、解码调度、窗口逻辑
│  ├─ MorseDecoder.cs        # 解码状态机（纯逻辑，可单测）
│  ├─ MorseText.cs           # 发送文本归一化（折标点、过滤、分片）
│  ├─ AppSettings.cs         # 偏好持久化（外观 / 音调 / 音量）
│  ├─ AudioOutput.cs         # waveOut 音频输出，支持指定设备
│  └─ Assets/app.ico         # 应用图标
├─ assets/                   # 实物图与界面截图
├─ dist/                     # 发布产物（不纳入版本管理）
├─ README_中文使用说明.md     # 详细操作手册
├─ CW_Trainer_V1_开发与烧录记录.md
├─ CW_Trainer_V2_开发历程与故障记录.md
├─ CW_Trainer_V3_规划.md
└─ CW_Trainer_V2.ino         # 归档：V2.2.0 固件最后一版，可回退/对照
```

## 设计规范

界面按一套明确的规则实现，不是随手堆控件：

- **三色**：墨黑、纸白（含中性灰阶）、信号橙。亮色下是 `#0F0F10 / #FFFFFF / #FF5A1F`，暗色下整体反转（`#F2F2F3 / #171718 / #FF6B37`），强调色只用于滑块进度、当前点划、状态点和图标。
- **主题令牌**：两套色板都在 `Themes/` 下，界面一律用 `DynamicResource` 引用，换主题时整页自动刷新，不重建控件。
- **平面化**：卡片靠画布与白卡片的色差分层，不用投影、不画描边。
- **8pt 栅格**：间距只用 4 / 8 / 12 / 16 / 20 / 24 / 32。
- **同心圆角**：卡片 20 → 控件 12 → 分段内块 9（外圆角减去内边距）。
- **自绘控件**：按钮、下拉框、滑块、分段选择器、列表都是自定义模板。
- **图标统一**：全部画在 24 网格内，用 Viewbox 统一输出 20（按钮）或 14（窗口按钮）。
- **无滚动**：固定 1200×800 版面 + 等比缩放，任何窗口尺寸都完整显示。

应用图标由 `.workbuddy/tools/make_icon.py` 生成（黑底 + 橙色点划，即摩斯码 A 的 `.-`）：

<img src="Desktop/CWTrainer.Desktop/Assets/app-512.png" width="96" alt="应用图标">

## 已知限制

1. **必须烧录 V3.0 固件**（`Firmware/CW_Trainer_V3/CW_Trainer_V3.ino`）。V2.0 的输出模式是一次性锁存，关闭或强杀软件后会永久静音、串口还可能被写阻塞拖死只能按 RST；V2.1 起改用主机租约，V2.2 加上 WPM/模式的 NVS 持久化，V3.0 加上文字发送。发送指令旧固件不认识，会回 `ERROR,BAD_COMMAND`。烧录前建议先按一次 RST。
2. **Iambic A/B 是联调原型**，挤压/释放的标准行为还没有用测试矩阵完整验证。
3. **发送队列上限 256 字符**，超出部分会被截断（软件会在日志里明确提示，不会静默丢弃）。
4. **发送中途调整 WPM 会让当前字符的时长突变**，因为固件在发每个元素时才取一次速度。要改速度建议等这批发完。
5. **电脑发声有少量延迟**（USB + 音频缓冲），只影响听感，不影响设备侧时序。
6. 有源蜂鸣器只能开关，实体侧无法调音调与音量；音调、音量只作用于电脑音频。

## 路线图

V3 的完整方案（协议、验收标准、依赖关系、推荐顺序）见 [CW_Trainer_V3_规划.md](CW_Trainer_V3_规划.md)。

- [ ] Iambic A/B 标准行为测试矩阵（只测，优先）
- [x] 设备端用 NVS 保存 WPM / 模式（V2.2 完成）
- [x] 电脑输入文字 → 设备自动拍发（发送方向，V3.0 完成）
- [x] 通联模式（模拟接收机，V3.1 完成，仅软件、固件未动）
- [ ] QSO 记录与训练统计（依赖发送方向）
- [ ] 抽 netstandard 库，为安卓移植做准备

## 相关文档

- [README_中文使用说明.md](README_中文使用说明.md) —— 详细操作与排错
- [CW_Trainer_V1_开发与烧录记录.md](CW_Trainer_V1_开发与烧录记录.md) —— 硬件、环境、烧录
- [CW_Trainer_V2_开发历程与故障记录.md](CW_Trainer_V2_开发历程与故障记录.md) —— V2 架构、故障分析与修复经过
- [CW_Trainer_V3_规划.md](CW_Trainer_V3_规划.md) —— 发送方向、Iambic 测试矩阵、NVS、统计与安卓的技术方案
