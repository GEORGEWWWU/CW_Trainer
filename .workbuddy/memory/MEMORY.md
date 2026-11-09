# CW Trainer — 项目长期约定

## 硬性规则

- **每次都必须在本地交付最新发布版**。任何改动完成后，都要执行发布并把产物放在
  `I:\Arduino\CW_Trainer\dist\CWT.exe`（框架依赖单文件），方便用户随时打开测试。
  不要只在 GitHub 上留构建结果，不要留旧 exe。
- 发布产物名固定为 **`CWT.exe`**（csproj 里 `AssemblyName=CWT`）。
- **固件 `Firmware/` 未经用户明确同意不得修改**，硬件部分不动。
- 当前固件：`Firmware/CW_Trainer_V3/CW_Trainer_V3.ino`（Arduino 要求目录名与 .ino 同名，改名要成对改）。
- 根目录 `CW_Trainer_V2.ino` 是 V2.2.0 的**冻结归档**，不要修改，只作回退/对照用。
- 用户已有实机开发板（ESP32-S3，串口 COM7）。不要在 PC 上做固件仿真，直接上实板。
  测试串口时进程必须走正常关闭流程，硬杀进程会把 USB CDC 端点拖死，只能按 RST 恢复。

## 技术约定

- 桌面端：WPF / .NET 10 (`Desktop/CWTrainer.Desktop`)，自绘控件，三色令牌。
- 主题：`Themes/Light.xaml` / `Themes/Dark.xaml`，键完全一致，界面一律 `DynamicResource`。
- 偏好存 `%AppData%\CWTrainer\settings.json`（Theme / ToneHz / Volume）。
- 串口协议：`HELLO,1` / `PING` / `BYE` / `GET` / `SET,OUTPUT,...` / `SET,MODE,...` / `SET,WPM,n`；
  设备上报 `STATUS,...`、`ELEMENT,<DIT|DAH>,<us>`。
- CI：`.github/workflows/build.yml`，推 main 出 artifact，推 `v*` 标签自动发 Release。
