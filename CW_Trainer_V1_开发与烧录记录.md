# CW Trainer V1：开发、烧录与故障排查记录

> 项目：ESP32-S3 独立式 CW 摩斯码练习器  
> 文档版本：V1 开发记录  
> 整理日期：2026-11-08  
> 当前阶段：硬件输入/蜂鸣器已实测；Arduino 编译、烧录和 USB CDC 串口已实测；自动键 V1 固件已提供，仍需按测试清单验证算法。

---

## 1. 项目目标与 V1 范围

本项目使用 ESP32-S3 读取 DIY 电键，并由开发板直接控制实体蜂鸣器产生 CW 声音。电脑端软件、OLED、LED、外接功放等先不纳入 V1，先保证设备脱离电脑软件也能独立练习。

### V1 目标

- 读取左右两个电键触点。
- 使用实体蜂鸣器输出点、划声音。
- 根据 WPM 计算点、划和元素间隔。
- 提供直键（Straight Key）与双桨自动键的基础模式。
- 为 Iambic A / Iambic B 留出固件实现入口。
- 不依赖电脑网页或电脑端程序运行。

### 暂不属于 V1 的功能

- 电脑端实时界面和 USB 双向控制协议。
- OLED 本地菜单与速度/模式实体按键。
- 真正可调音调、可调音量的音频输出。
- 自动发送英文文本、完整 CW 解码、训练统计。
- MAX98357A 功放和双线扬声器输出。

---

## 2. 已有硬件与当前使用设备

### 当前主控

- ESP32-S3-N16R8 开发板。
- N16 通常表示 16MB Flash；R8 表示 8MB OPI PSRAM。具体板卡仍应以实物丝印和模组规格为准。
- 使用开发板原生 USB 接口连接 Windows 电脑。

### 当前已接入的硬件

- DIY 双桨电键（两个独立触点 + 公共端）。
- 高电平触发蜂鸣器模块。
- 杜邦线。
- USB 数据线。

### 暂未接入

- OLED 小屏幕。
- LED 灯珠。
- 两线扬声器。
- 3.5mm TRS 电键插座/转接板。
- MAX98357A I2S 功放。

### 安全注意

1. 电键本身是无源开关，不需要给电键触点供电。
2. 电键公共端接 ESP32 GND；两个独立触点分别接 GPIO，并由 `INPUT_PULLUP` 使用内部上拉。
3. 不要把 5V 接入 ESP32 GPIO。
4. 裸 LED 必须串联限流电阻，不能直接接 GPIO。
5. 两线扬声器不能直接接 GPIO，需经功放驱动。
6. 蜂鸣器模块的 VCC 是否能接 3.3V，应以模块规格为准；不要仅凭外观判断。

---

## 3. 最终电键方向约定与 GPIO 分配

**最终操作约定：左手为 DIT（点 `.`），右手为 DAH（划 `-`）。**

这是本项目后续代码、软件界面、接线标注统一遵守的逻辑定义。

### GPIO 分配

| 功能 | GPIO | 电气含义 |
|---|---:|---|
| 左手 DIT（点） | GPIO5 | 触点闭合时 GPIO 读 LOW |
| 右手 DAH（划） | GPIO4 | 触点闭合时 GPIO 读 LOW |
| 蜂鸣器 | GPIO6 | 高电平触发，HIGH 响、LOW 停 |
| 电键公共端 | GND | 左右两个触点共用 |

> **方向修正记录：** 早期测试代码曾使用 GPIO4=DIT、GPIO5=DAH。用户指出实际左右手定义与原定义相反。后续逻辑统一调整为 GPIO5=左手 DIT、GPIO4=右手 DAH。若实物导线实际接法不是这样，应优先按万用表/杜邦线测试确认，再修改代码中的宏定义；不要只看线的颜色猜测。

### 电键输入逻辑

代码使用：

```cpp
pinMode(PIN_DIT, INPUT_PULLUP);
pinMode(PIN_DAH, INPUT_PULLUP);
```

因此输入逻辑为低电平有效：

| 状态 | GPIO5（左 DIT） | GPIO4（右 DAH） |
|---|---|---|
| 两侧均松开 | HIGH | HIGH |
| 左手按下 DIT | LOW | HIGH |
| 右手按下 DAH | HIGH | LOW |
| 双桨同时按下 | LOW | LOW |

电键是触点开关：公共端 COM 接 GND；左触点接 GPIO5；右触点接 GPIO4。左右活动金属件之间必须绝缘，不能通过金属底座/螺丝意外短接。

---

## 4. Arduino 开发环境安装记录

### 最终使用的环境

| 项目 | 当前配置 |
|---|---|
| Arduino IDE | 2.3.6 |
| ESP32 Arduino Core | 2.0.17-cn |
| 开发板 | ESP32S3 Dev Module |
| Windows 端口 | 曾识别为 COM6（实际以设备管理器当前显示为准） |
| USB 连接 | ESP32-S3 原生 USB |

### 为什么使用 2.0.17-cn

最初 Arduino IDE 1.8.x 通过普通 ESP32 包下载时，仍然跳转 GitHub，国内网络下载失败。仅添加中国区 JSON 索引并不代表普通版本包的下载地址会自动被替换。

后来改用 Arduino IDE 2.3.6，并使用乐鑫中国区镜像索引，选择 `2.0.17-cn`。日志已证实实际安装路径为：

```text
C:\Users\Administrator\AppData\Local\Arduino15\packages\esp32\hardware\esp32\2.0.17-cn
```

中国区索引地址：

```text
https://jihulab.com/esp-mirror/espressif/arduino-esp32/-/raw/gh-pages/package_esp32_index_cn.json
```

### 添加开发板管理器网址

1. 打开 Arduino IDE 2.3.6。
2. 进入「文件 → 首选项」。
3. 在「附加开发板管理器网址」中添加上面的中国区 JSON 地址。
4. 原来若有其他网址，可以保留并用换行分隔；排查时也可以临时只保留这条 ESP32 中国区索引。
5. 打开左侧「开发板管理器」图标，搜索 `esp32`。
6. 找到 `esp32 by Espressif Systems`。
7. 选择 `2.0.17-cn`，不要误选普通 `2.0.17`。

### 安装下载失败时的识别方法

查看错误里具体下载 URL：

- 若仍是 `github.com/espressif/arduino-esp32/releases/download/2.0.17/...`，说明选到普通版本或使用了普通索引。
- 中国区包通常带 `-cn` 版本后缀，索引会提供对应的国内下载地址。
- 开发板核心包之外，编译器、esptool 等依赖也需要安装；总下载体积会大于单个核心压缩包。

---

## 5. ESP32-S3 开发板参数

当前日志里 FQBN 为：

```text
esp32:esp32:esp32s3:UploadSpeed=115200,CDCOnBoot=cdc,FlashSize=16M,PartitionScheme=app3M_fat9M_16MB,PSRAM=opi
```

编译命令也出现了：

```text
-DARDUINO_USB_MODE=1
-DARDUINO_USB_CDC_ON_BOOT=1
-DARDUINO_USB_MSC_ON_BOOT=0
-DARDUINO_USB_DFU_ON_BOOT=0
```

这证明 USB Mode 和 USB CDC On Boot 设置已经实际传入编译参数。

### Arduino「工具」菜单建议配置

| 菜单项 | 当前建议值 |
|---|---|
| Board | ESP32S3 Dev Module |
| Port | 当前设备管理器显示的 COM 端口，例如 COM6 |
| USB Mode | Hardware CDC and JTAG |
| USB CDC On Boot | Enabled |
| USB DFU On Boot | Disabled |
| USB Firmware MSC On Boot | Disabled |
| CPU Frequency | 240MHz（WiFi） |
| Flash Mode | QIO 80MHz（若板卡不稳定再按板卡规格调整） |
| Flash Size | 16MB (128Mb) |
| Partition Scheme | 16M Flash (3MB APP/9.9MB FATFS) |
| PSRAM | OPI PSRAM |
| Upload Mode | UART0 / Hardware CDC |
| Upload Speed | 115200 |
| Core Debug Level | None |
| Erase All Flash Before Sketch Upload | Disabled |

> 不同 2.0.17-cn 构建包或开发板 variant 的工具菜单项目可能略有差异。优先选择实物板卡支持的选项。不要为了照抄表格而选择不存在或不匹配的设置。

---

## 6. 首次识别开发板与烧录

### 确认 COM 端口

1. 使用支持数据传输的 USB 线连接开发板原生 USB 接口。
2. 打开 Windows「设备管理器 → 端口（COM 和 LPT）」。
3. 识别 ESP32-S3 对应端口，例如 `USB Serial Device (COM6)`。
4. 在 Arduino IDE「工具 → 端口」中选中同一个 COM 端口。

### 首次烧录不连接时：进入 ROM 下载模式

若上传日志停在 `Connecting...`：

1. 按住开发板 BOOT。
2. 按一下 RESET（若板上有 RST/EN 按钮）。
3. 松开 RESET。
4. 松开 BOOT。
5. 重新查看设备管理器，ROM 下载模式可能会出现新的 COM 端口。
6. 在 Arduino IDE 里选择新出现的端口，再点上传。

有些开发板的自动下载电路可以自动处理，不必每次都手动按 BOOT。

### 烧录成功的标志

上传日志出现写入进度、`Hash of data verified`，最后出现 `Hard resetting via RTS pin...` 或 IDE 提示 `Done uploading`，表示烧录流程完成。

本项目实际烧录日志确认：

```text
Chip is ESP32-S3 (revision v0.2)
Features: WiFi, BLE
Crystal is 40MHz
...
Hash of data verified.
Leaving...
Hard resetting via RTS pin...
```

---

## 7. 第一阶段：GPIO 与蜂鸣器硬件测试

### 测试目标

- DIT / DAH 输入可以被 ESP32 识别。
- GPIO6 能控制蜂鸣器响/停。
- 两个触点同时闭合时，输入状态仍可分别识别。

### 第一份硬件测试代码（早期 GPIO 映射）

早期测试时曾使用 GPIO4=DIT、GPIO5=DAH。以下代码仅作为历史记录；当前方向约定已经改为左 DIT=GPIO5、右 DAH=GPIO4。

```cpp
#define PIN_DIT     4
#define PIN_DAH     5
#define PIN_BUZZER  6

bool lastDit = false;
bool lastDah = false;

void setup() {
  pinMode(PIN_DIT, INPUT_PULLUP);
  pinMode(PIN_DAH, INPUT_PULLUP);
  pinMode(PIN_BUZZER, OUTPUT);
  digitalWrite(PIN_BUZZER, LOW);

  Serial.begin(115200);
  Serial.println("CW TRAINER Hardware Test Ready");
}

void loop() {
  bool dit = digitalRead(PIN_DIT) == LOW;
  bool dah = digitalRead(PIN_DAH) == LOW;

  digitalWrite(PIN_BUZZER, (dit || dah) ? HIGH : LOW);

  if (dit != lastDit || dah != lastDah) {
    Serial.print("DIT: ");
    Serial.print(dit ? "PRESSED" : "RELEASED");
    Serial.print(" | DAH: ");
    Serial.println(dah ? "PRESSED" : "RELEASED");
    lastDit = dit;
    lastDah = dah;
  }
  delay(1);
}
```

### 硬件测试结果

用户已确认第一阶段完成：电键的点和划都能正常控制蜂鸣器发声。该结果证明当时接线、GPIO 输入和蜂鸣器输出已经打通。

---

## 8. USB CDC 串口问题与解决记录

### 最初遇到的现象

串口监视器只看到 ESP-ROM 启动信息，例如：

```text
ESP-ROM:esp32s3-20210327
Build: Mar 27 2021
rst:0x1 (POWERON),boot:0x2b (SPI_FAST_FLASH_BOOT)
SPIWP:0xee
mode:DIO, clock div:1
load:0x3fce3808,len:0x4bc
load:0x403c9700,len:0xbd8
load:0x403cc700,len:0x2a0c
entry 0x403c98d0
```

这些是 ESP32-S3 芯片 ROM 的启动日志，不是 Arduino Sketch 自己打印的内容。单独看到 ROM 日志，不能证明应用程序串口已经工作，也不等于烧录失败。

### 排查过程

1. 确认 USB CDC On Boot 为 Enabled。
2. 确认 USB Mode 为 Hardware CDC and JTAG。
3. 重新烧录后关闭并重新打开串口监视器。
4. 重新确认当前 COM 端口；USB 设备重新枚举后，端口可能变化。
5. 打开「文件 → 首选项」，勾选编译时显示详细输出，重新验证代码。
6. 检查编译参数是否包含：

```text
-DARDUINO_USB_MODE=1
-DARDUINO_USB_CDC_ON_BOOT=1
```

### 一次代码 API 错误

曾经给出的诊断代码使用了：

```cpp
Serial.begin();
```

这在当前 ESP32 Arduino Core 2.0.17-cn 的 `HardwareSerial` 接口上会编译失败，因为 `begin()` 要求传入波特率。应改为：

```cpp
Serial.begin(115200);
```

错误信息示例：

```text
error: no matching function for call to 'HardwareSerial::begin()'
candidate expects 7 arguments, 0 provided
```

### 最终成功的串口诊断代码

```cpp
#include <Arduino.h>

#define PIN_BUZZER 6

unsigned long lastReport = 0;
unsigned long lastBeep = 0;

void setup() {
  pinMode(PIN_BUZZER, OUTPUT);
  digitalWrite(PIN_BUZZER, LOW);

  Serial.begin(115200);
  delay(1500);

  Serial.println();
  Serial.println("==============================");
  Serial.println("     CW TRAINER DIAGNOSTIC");
  Serial.println("     ESP32-S3 IS RUNNING");
  Serial.println("==============================");
}

void loop() {
  unsigned long now = millis();

  if (now - lastBeep >= 2000) {
    lastBeep = now;
    digitalWrite(PIN_BUZZER, HIGH);
    delay(80);
    digitalWrite(PIN_BUZZER, LOW);
  }

  if (millis() - lastReport >= 1000) {
    lastReport = millis();
    Serial.print("ALIVE | Uptime: ");
    Serial.print(millis() / 1000);
    Serial.println(" seconds");
  }
  delay(5);
}
```

### 串口最终验证结果

用户最终成功看到持续递增的输出：

```text
ALIVE | Uptime: 23 seconds
ALIVE | Uptime: 24 seconds
ALIVE | Uptime: 25 seconds
...
ALIVE | Uptime: 33 seconds
```

这证明：

- Arduino 应用程序确实在执行。
- ESP32-S3 原生 USB CDC 可以向电脑输出串口数据。
- 串口监视器选择了可用的通信端口，115200 设置正常。

因此，**当前串口问题已解决**，不需要继续修改 USB Mode 或反复重装开发板核心。

---

## 9. CW 时序基础

CW 的基本单位由 WPM 决定：

\[
T_{dot}=\frac{1200}{WPM}\text{ ms}
\]

固件若使用微秒，则：

\[
T_{dot}=\frac{1,200,000}{WPM}\text{ µs}
\]

基本时序：

| 项目 | 长度 |
|---|---:|
| 点（DIT） | 1 个单位 |
| 划（DAH） | 3 个单位 |
| 点划之间的静音 | 1 个单位 |
| 字符之间的间隔 | 3 个单位 |
| 单词之间的间隔 | 7 个单位 |

示例：

| WPM | 点 | 划 | 元素间静音 |
|---:|---:|---:|---:|
| 10 | 120ms | 360ms | 120ms |
| 15 | 80ms | 240ms | 80ms |
| 20 | 60ms | 180ms | 60ms |
| 25 | 48ms | 144ms | 48ms |
| 30 | 40ms | 120ms | 40ms |

V1 当前固件代码没有实现字符间隔、单词间隔、完整解码或 Farnsworth 间隔；它当前只涉及电键元素（点/划）以及元素之间的间隔。

---

## 10. CW Trainer V1 固件代码（按最终左右手映射修正）

### 当前参数

- 左手 DIT：GPIO5。
- 右手 DAH：GPIO4。
- 蜂鸣器：GPIO6，高电平触发。
- 默认速度：15 WPM。
- 默认模式：Iambic B（注意：当前算法为基础原型，尚未通过完整标准 Iambic A/B 行为测试，详见下一节）。

### 代码

```cpp
/*
 * ============================================
 * CW TRAINER V1 - PROTOTYPE
 * ESP32-S3-N16R8
 * Arduino ESP32 Core 2.0.17-cn
 * ============================================
 *
 * GPIO5 : LEFT DIT paddle
 * GPIO4 : RIGHT DAH paddle
 * GPIO6 : Active HIGH buzzer
 *
 * NOTE:
 * This is a first keyer prototype, not yet a
 * fully validated standards-compliant Iambic
 * A/B implementation.
 */

#define MODE_STRAIGHT  0
#define MODE_IAMBIC_A  1
#define MODE_IAMBIC_B  2

// Select keyer mode here:
#define KEYER_MODE MODE_IAMBIC_B

// Morse speed: 5-60 WPM
#define CW_WPM 15

// Final hand mapping:
#define PIN_DIT     5   // LEFT hand: dot
#define PIN_DAH     4   // RIGHT hand: dash
#define PIN_BUZZER  6

#define DEBOUNCE_US 4000UL

#if CW_WPM < 5 || CW_WPM > 60
#error "CW_WPM must be between 5 and 60"
#endif

#if KEYER_MODE < MODE_STRAIGHT || KEYER_MODE > MODE_IAMBIC_B
#error "Invalid keyer mode"
#endif

const uint32_t DOT_US = 1200000UL / CW_WPM;

struct PaddleInput {
  uint8_t pin;
  bool rawPressed;
  bool stablePressed;
  bool lastStable;
  uint32_t rawChangedAt;
};

PaddleInput ditInput;
PaddleInput dahInput;

void initPaddle(PaddleInput &input, uint8_t pin) {
  input.pin = pin;
  pinMode(pin, INPUT_PULLUP);
  bool initial = (digitalRead(pin) == LOW);
  input.rawPressed = initial;
  input.stablePressed = initial;
  input.lastStable = initial;
  input.rawChangedAt = micros();
}

bool updatePaddle(PaddleInput &input, uint32_t now) {
  bool sample = (digitalRead(input.pin) == LOW);

  if (sample != input.rawPressed) {
    input.rawPressed = sample;
    input.rawChangedAt = now;
  }

  if ((uint32_t)(now - input.rawChangedAt) >= DEBOUNCE_US) {
    input.stablePressed = input.rawPressed;
  }

  bool pressedEdge = input.stablePressed && !input.lastStable;
  input.lastStable = input.stablePressed;
  return pressedEdge;
}

enum KeyerState {
  KEYER_IDLE,
  KEYER_MARK,
  KEYER_GAP
};

KeyerState keyerState = KEYER_IDLE;

bool ditMemory = false;
bool dahMemory = false;
bool lastElementWasDit = false;
bool hasLastElement = false;
bool squeezeSeen = false;

uint32_t nextDeadline = 0;

bool timeReached(uint32_t now, uint32_t target) {
  return (int32_t)(now - target) >= 0;
}

void startElement(bool isDit, uint32_t now) {
  if (isDit) {
    ditMemory = false;
  } else {
    dahMemory = false;
  }

  lastElementWasDit = isDit;
  hasLastElement = true;

  digitalWrite(PIN_BUZZER, HIGH);
  keyerState = KEYER_MARK;

  uint32_t duration = isDit ? DOT_US : DOT_US * 3UL;
  nextDeadline = now + duration;
}

void updateKeyer(uint32_t now) {
  bool ditEdge = updatePaddle(ditInput, now);
  bool dahEdge = updatePaddle(dahInput, now);

  if (ditEdge) ditMemory = true;
  if (dahEdge) dahMemory = true;

  if (KEYER_MODE == MODE_STRAIGHT) {
    digitalWrite(PIN_BUZZER,
      ditInput.stablePressed ? HIGH : LOW);
    return;
  }

  bool ditPressed = ditInput.stablePressed;
  bool dahPressed = dahInput.stablePressed;

  if (KEYER_MODE == MODE_IAMBIC_A) {
    if (ditPressed && dahPressed) {
      squeezeSeen = true;
    }

    if (squeezeSeen && !ditPressed && !dahPressed) {
      ditMemory = false;
      dahMemory = false;
      squeezeSeen = false;
    }
  }

  if (keyerState == KEYER_MARK &&
      timeReached(now, nextDeadline)) {
    digitalWrite(PIN_BUZZER, LOW);
    keyerState = KEYER_GAP;
    nextDeadline += DOT_US;
  }

  if (keyerState == KEYER_GAP &&
      timeReached(now, nextDeadline)) {
    keyerState = KEYER_IDLE;
  }

  if (keyerState == KEYER_IDLE) {
    bool wantDit = ditMemory || ditPressed;
    bool wantDah = dahMemory || dahPressed;

    bool sendDit = false;
    bool shouldSend = false;

    if (wantDit && wantDah) {
      if (!hasLastElement || !lastElementWasDit) {
        sendDit = true;
      } else {
        sendDit = false;
      }
      shouldSend = true;
    } else if (wantDit) {
      sendDit = true;
      shouldSend = true;
    } else if (wantDah) {
      sendDit = false;
      shouldSend = true;
    }

    if (shouldSend) {
      startElement(sendDit, now);
    }
  }
}

void setup() {
  pinMode(PIN_BUZZER, OUTPUT);
  digitalWrite(PIN_BUZZER, LOW);
  initPaddle(ditInput, PIN_DIT);
  initPaddle(dahInput, PIN_DAH);
}

void loop() {
  uint32_t now = micros();
  updateKeyer(now);
}
```

### 修改速度

修改：

```cpp
#define CW_WPM 15
```

例如改成 `20` 就是 20 WPM。允许范围由代码限制为 5–60 WPM。修改后必须重新验证并上传。

### 修改模式

将：

```cpp
#define KEYER_MODE MODE_IAMBIC_B
```

改为以下之一：

```cpp
#define KEYER_MODE MODE_STRAIGHT
#define KEYER_MODE MODE_IAMBIC_A
#define KEYER_MODE MODE_IAMBIC_B
```

注意：该原型的 Straight Key 模式使用 DIT 输入 GPIO5 作为直键输入。它不会自动将双桨电键转换成独立直键接口。

---

## 11. V1 固件的已知限制与必须注意的算法问题

**很重要：上面这份自动键代码是开发原型，不应在未验证前宣称为完整、严格符合规范的 Iambic A/B keyer。**

目前代码存在以下边界：

1. `Iambic B` 的 squeeze-release 记忆处理尚未完整实现。代码目前没有在释放双桨时可靠地记录“是否需要补发最后一个元素”的状态，因此不能保证标准 Iambic B 的最后元素行为。
2. `Iambic A` 当前通过清除待发送记忆实现简化逻辑，但对“当前元素完成、释放双桨、待发元素”的边界需要进一步测试和重构。
3. 双桨交替规则目前以最近发送元素作为优先判断，尚未验证所有挤压、松开和快速交替序列。
4. 触点消抖初值为 4ms；机械触点、键距和使用速度不同，可能需要调整。
5. `loop()` 以 `micros()` 轮询；这是基础原型，不是硬件定时器中断实现。若以后增加复杂通信、显示或音频任务，需要保证时序不被阻塞。
6. `Straight Key` 只读取 DIT 端（GPIO5）。若要兼容独立直键，可能需要明确直键接线规则或增加模式配置。
7. 当前没有字符间隔、单词间隔、解码和发送缓冲区。
8. 当前有源蜂鸣器只能简单开/关；不能据此实现真正可调音调与音量。

因此建议把当前版本命名为 **V1 Prototype（原型）**。下一版应优先重写并验证 Iambic A/B 状态机，完成以下测试后再称为正式 Iambic keyer：

- 单独按住 DIT 连发点。
- 单独按住 DAH 连发划。
- 在 DIT 发声期间按下 DAH。
- 在 DAH 发声期间按下 DIT。
- 双桨同时按下并同时释放。
- 发送点时挤压后先松 DIT、再松 DAH。
- 发送划时挤压后先松 DAH、再松 DIT。
- 元素发声过程中松开双桨。
- 不同 WPM 下重复以上测试。

---

## 12. 常见问题与解决办法汇总

| 现象/错误 | 原因或判断 | 处理办法 |
|---|---|---|
| ESP32 Core 下载仍指向 GitHub | 选了普通 `2.0.17` 或索引并未生效 | 在中国区索引下选择 `2.0.17-cn`，检查实际下载 URL |
| 添加中国区 JSON 后仍然下载失败 | JSON 索引只提供包元数据，不保证旧普通包 URL 自动改写 | 确认版本后缀和索引；不要只看是否添加了 JSON |
| Arduino IDE 1.8.x 安装困难 | 经典 IDE 的管理器流程与网络问题叠加 | 本项目最终改用 Arduino IDE 2.3.6 |
| 上传卡在 `Connecting...` | 芯片没有进入下载模式、端口选错或 USB 线不支持数据 | BOOT 按住，点 RST，松开 RST 再松 BOOT；重新选 COM；换数据线 |
| 串口只显示 `ESP-ROM...` | 这是芯片 ROM 启动日志，不是应用日志 | 检查 USB CDC、COM 端口和程序串口输出；不要仅凭 ROM 日志判断烧录失败 |
| `Serial.begin()` 编译错误 | Core 2.0.17 的 `HardwareSerial::begin()` 需要波特率参数 | 改为 `Serial.begin(115200);` |
| USB CDC 没输出 | 可能看错 COM、监视器打开时机不对或 CDC 设置不一致 | `USB Mode=Hardware CDC and JTAG`、`USB CDC On Boot=Enabled`；重启后重新检查 COM |
| ROM 日志后没有 `ALIVE` | ROM 输出本身不证明 Arduino 程序串口已工作 | 使用每秒打印的诊断程序；检查编译宏和当前运行态 COM |
| 蜂鸣器不响 | 代码未配置蜂鸣器、GPIO 映射错误或模块电平/供电不匹配 | 检查 GPIO6、模块 VCC 规格和高电平触发逻辑 |
| 点和划左右反了 | 逻辑 DIT/DAH 与物理左右手接线映射不一致 | 固定左手 DIT、右手 DAH；本项目当前建议 GPIO5=DIT、GPIO4=DAH |
| 点划时长不对 | WPM 单位换算或当前蜂鸣器/时序逻辑问题 | `Tdot(ms)=1200/WPM`；划为 3 倍；检查 WPM 宏 |
| 双桨挤压行为不符合预期 | 简化原型的 Iambic A/B 状态机尚未完善 | 不要先改线；下一版需要完善 squeeze / release memory 状态机 |

---

## 13. 当前阶段验收状态

| 项目 | 状态 | 说明 |
|---|---|---|
| Arduino IDE 2.3.6 | 已完成 | 已使用 |
| ESP32 Core 2.0.17-cn | 已完成 | 编译日志确认 |
| ESP32-S3 编译 | 已完成 | 正常生成固件 |
| ESP32-S3 烧录 | 已完成 | Hash 校验成功 |
| USB CDC 编译配置 | 已完成 | 两个宏均为 1 |
| USB CDC 运行输出 | 已完成 | `ALIVE | Uptime` 已持续输出 |
| DIT/DAH GPIO 输入 | 已实测 | 第一阶段点划控制正常 |
| 蜂鸣器输出 | 已实测 | 高电平触发正常 |
| 左 DIT / 右 DAH 约定 | 已确定 | 后续固定此方向 |
| 自动点划 V1 原型 | 已提供 | 需要实际测试时序与手感 |
| Iambic A 标准行为 | 未完成验证 | 原型算法需完善 |
| Iambic B 标准行为 | 未完成验证 | 原型算法需完善 |
| OLED / LED | 未接入 | 后续阶段 |
| 电脑软件通信 | 尚未开发 | 后续阶段 |

---

## 14. 下一阶段建议

推荐按以下顺序继续，不要同时叠加太多功能：

1. **先确认最终接线方向**：左手 GPIO5=DIT，右手 GPIO4=DAH；蜂鸣器 GPIO6。
2. **验证基础时序**：15 WPM 时点约 80ms、划约 240ms、元素间隔约 80ms。
3. **重构 Iambic A/B 状态机**：分别处理 squeeze、release、memory 和最后元素行为，完成测试矩阵。
4. **增加参数控制**：先通过代码宏设置 WPM/模式，之后再考虑按键/旋钮或电脑控制。
5. **增加事件输出**：将 KEY down/up、ELEMENT dot/dash 等事件通过 USB CDC 传给电脑。
6. **最后开发电脑界面**：实时点划、解码文本、速度/模式显示与配置。

---

## 15. 项目关键结论（速查）

- 主控：ESP32-S3-N16R8。
- IDE：Arduino IDE 2.3.6。
- Core：ESP32 Arduino Core 2.0.17-cn。
- Board：ESP32S3 Dev Module。
- USB Mode：Hardware CDC and JTAG。
- USB CDC On Boot：Enabled。
- 左手 DIT：GPIO5。
- 右手 DAH：GPIO4。
- 蜂鸣器：GPIO6，高电平触发。
- 串口：115200 baud；已成功看到 `ALIVE | Uptime`。
- 当前状态：硬件与串口基础测试已通过；自动键 V1 为原型，Iambic A/B 算法仍需进一步完善。
