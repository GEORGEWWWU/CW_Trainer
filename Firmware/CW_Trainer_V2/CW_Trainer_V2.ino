/*
  CW TRAINER V3 - ESP32-S3-N16R8          CW训练器 V3 - ESP32-S3-N16R8
  Arduino-ESP32 Core: 2.0.17-cn          Arduino-ESP32 核心库版本：2.0.17-cn

  Wiring (the final left/right convention):    接线定义（最终左右键约定）：
    GPIO5 = LEFT paddle = DIT (dot)            GPIO5 = 左键 = 点（DIT）
    GPIO4 = RIGHT paddle = DAH (dash)          GPIO4 = 右键 = 划（DAH）
    GPIO6 = active-HIGH buzzer input           GPIO6 = 高电平有效蜂鸣器控制脚
    GND   = paddle common                      GND   = 电键公共地

  USB protocol: newline-delimited ASCII commands/events at 115200.
  USB通信协议：波特率115200，换行分隔ASCII指令与事件
  PC commands:                                 上位机指令：
    BYE                                        BYE
    HELLO,1                                    HELLO,1
    PING                                       PING
    GET                                        GET
    SET,OUTPUT,BUZZER|PC|BOTH                  SET,OUTPUT,BUZZER|PC|BOTH
    SET,WPM,5..60                              SET,WPM,5..60
    SET,MODE,STRAIGHT|IAMBIC_A|IAMBIC_B        SET,MODE,STRAIGHT|IAMBIC_A|IAMBIC_B
    SEND,BEGIN                                 开始一个发送任务（清空队列）
    SEND,TEXT,<ascii>                          追加文本，单行约 80 字节
    SEND,END                                   文本给完，开始按当前 WPM 拍发
    SEND,STOP                                  中止发送并清空队列

  Device events:                               设备上报事件：
    READY,1,CWTRAINER,3.0.0                    READY,1,CWTRAINER,3.0.0
    STATUS,WPM,15,MODE,IAMBIC_B,OUTPUT,BUZZER  STATUS,WPM,15,MODE,IAMBIC_B,OUTPUT,BUZZER
    KEY,DIT,1 / KEY,DIT,0                      KEY,DIT,1 / KEY,DIT,0
    KEY,DAH,1 / KEY,DAH,0                      KEY,DAH,1 / KEY,DAH,0
    ELEMENT,DIT,80000     (duration in microseconds)
    ELEMENT,DIT,80000     （时长单位：微秒）
    TONE,ON / TONE,OFF    (straight-key mode)
    TONE,ON / TONE,OFF    （直键模式）
    ACK,... / PONG                             ACK,... / PONG
    SEND,START,<n>                             开始拍发，n = 待发字符数
    SENDING,<remaining>                        每发完一个字符上报剩余
    SEND,DONE                                  队列发完
    SEND,ABORTED,PADDLE|STOP|MODE              被手键 / 指令 / 模式切换中止
    WARN,CHAR,<c>                              字符不在电码表里，已跳过

  Important:                                   重要说明：
  - The MCU remains responsible for keyer timing. The PC never schedules
    paddle timing; it only renders ELEMENT events as computer audio.
  - 电键时序由单片机主控负责。上位机不参与电键时序生成，仅根据ELEMENT事件播放电脑端音频。
  - OUTPUT defaults to BUZZER at boot and falls back to BUZZER if host
    heartbeat is lost for 6 seconds.
  - 上电默认输出为蜂鸣器；若上位机心跳丢失超过6秒，自动切回蜂鸣器输出。

  V2.1 host lease:                               V2.1 主机租约：
  - The host holds a *lease*, not a latch. HELLO / PING / GET / SET all
    renew it; BYE or lease expiry revokes it immediately. The buzzer level
    is recomputed from the lease every loop, so no command sequence can
    leave the keyer permanently mute.
  - 上位机持有的是“租约”而非一次性开关：HELLO / PING / GET / SET 都会续租，
    BYE 或租约到期立即失效。蜂鸣器电平每个主循环都按租约重算，
    因此任何命令顺序都不可能让电键永久静音。
  - While the lease is expired, nothing is written to USB at all, so a
    closed/unread CDC port can never block loop().
  - 租约失效期间完全不向 USB 写数据，关闭或无人读取的 CDC 端口不会阻塞 loop()。

  V3.0 text sending:                              V3.0 文本发送：
  - The host only supplies characters; this MCU still generates every element
    and gap, so USB jitter can never reach the keying waveform.
  - 上位机只提供字符，元素与间隔全部由本机生成，USB 抖动不会影响拍发波形。
  - Queue holds 256 characters; a paddle press aborts sending immediately.
  - 队列 256 字符；按任意桨立即中止发送，手动键控优先级最高。

  V2.2 settings:                                 V2.2 参数持久化：
  - WPM and keyer mode are stored in NVS (namespace "cwtrainer") and restored
    at boot, so the keyer keeps its speed across power cycles.
  - WPM 与键控模式存入 NVS（命名空间 cwtrainer），上电自动恢复。
  - Writes are debounced 300 ms so dragging the speed slider does not wear
    out flash; BYE forces an immediate flush.
  - 写盘延迟 300 毫秒，拖动速度滑块不会磨损 flash；BYE 时会立即落盘。
*/


#include <Arduino.h>
#include <Preferences.h>
#include <stdlib.h>
#include <string.h>

// ---------------- Pins ----------------
static constexpr uint8_t PIN_DIT = 5;      // LEFT paddle: dot
static constexpr uint8_t PIN_DAH = 4;      // RIGHT paddle: dash
static constexpr uint8_t PIN_BUZZER = 6;   // Active HIGH buzzer module

// ---------------- Limits ----------------
static constexpr uint32_t DEBOUNCE_US = 4000UL;
static constexpr uint32_t HOST_TIMEOUT_MS = 6000UL;
static constexpr uint16_t DEFAULT_WPM = 15;
static constexpr uint16_t MIN_WPM = 5;
static constexpr uint16_t MAX_WPM = 60;

// 参数落盘延迟：拖动 WPM 滑块会连续下发 SET，立刻写会把 flash 写爆。
static constexpr uint32_t SETTINGS_SAVE_DELAY_MS = 300;

// ---------------- Enums ----------------
enum KeyerMode : uint8_t {
  MODE_STRAIGHT = 0,
  MODE_IAMBIC_A = 1,
  MODE_IAMBIC_B = 2
};

enum OutputMode : uint8_t {
  OUTPUT_BUZZER = 0,
  OUTPUT_PC = 1,
  OUTPUT_BOTH = 2
};

enum KeyerState : uint8_t {
  STATE_IDLE = 0,
  STATE_MARK = 1,
  STATE_GAP = 2
};

struct PaddleInput {
  uint8_t pin;
  bool candidatePressed;
  bool stablePressed;
  uint32_t candidateSinceUs;
};

static PaddleInput ditInput;
static PaddleInput dahInput;

static KeyerMode keyerMode = MODE_IAMBIC_B;
static OutputMode outputMode = OUTPUT_BUZZER;
static uint16_t cwWpm = DEFAULT_WPM;

// ---------------- Host lease ----------------
// 主机租约。0 表示从未连接 / 已失效。
static uint32_t hostLeaseUntilMs = 0;
static bool hostWasActive = false;
static uint8_t buzzerLevel = 0;

static bool hostActive() {
  // 0 是“从未连接 / 已撤销”的哨兵，避免 millis() 回绕后误判为有效租约。
  if (hostLeaseUntilMs == 0) return false;
  return (int32_t)(millis() - hostLeaseUntilMs) < 0;
}

static void refreshHostLease() {
  hostLeaseUntilMs = millis() + HOST_TIMEOUT_MS;
}

static void revokeHostLease() {
  hostLeaseUntilMs = 0;
}

static KeyerState keyerState = STATE_IDLE;
static uint32_t stateDeadlineUs = 0;
static bool ditMemory = false;
static bool dahMemory = false;
static bool lastElementWasDit = false;
static bool hasLastElement = false;
static bool squeezeSeen = false;
static bool iambicBExtra = false;
static bool straightToneActive = false;

static char commandBuffer[96];
static size_t commandLength = 0;

// ---------------- Settings (NVS) ----------------
static Preferences prefs;
static bool prefsReady = false;

// 已落盘的影子值，用来避免重复写同一个值
static uint16_t savedWpm = DEFAULT_WPM;
static uint8_t savedMode = (uint8_t)MODE_IAMBIC_B;

static bool settingsDirty = false;
static uint32_t settingsDirtySinceMs = 0;

// ---------------- Utility ----------------
static uint32_t dotDurationUs() {
  return 1200000UL / cwWpm;
}

static bool timeReached(uint32_t now, uint32_t deadline) {
  return (int32_t)(now - deadline) >= 0;
}

static const char* modeName(KeyerMode mode) {
  switch (mode) {
    case MODE_STRAIGHT: return "STRAIGHT";
    case MODE_IAMBIC_A: return "IAMBIC_A";
    case MODE_IAMBIC_B: return "IAMBIC_B";
    default: return "UNKNOWN";
  }
}

static const char* outputName(OutputMode mode) {
  switch (mode) {
    case OUTPUT_BUZZER: return "BUZZER";
    case OUTPUT_PC: return "PC";
    case OUTPUT_BOTH: return "BOTH";
    default: return "UNKNOWN";
  }
}

// 实际生效的输出模式：租约失效时强制回落到实体蜂鸣器。
// 这样“上位机请求 PC 发声”与“上位机还活着”两件事不会互相残留。
static OutputMode effectiveOutput() {
  return hostActive() ? outputMode : OUTPUT_BUZZER;
}

static bool hardwareOutputEnabled() {
  const OutputMode mode = effectiveOutput();
  return mode == OUTPUT_BUZZER || mode == OUTPUT_BOTH;
}

// 定义在发送引擎里（这里只前置声明，避免静态变量顺序问题）
static bool txMarkActive();

static bool currentMarkActive() {
  if (txMarkActive()) return true;                 // 自动拍发
  if (keyerMode == MODE_STRAIGHT) return straightToneActive;
  return keyerState == STATE_MARK;
}

// 电平按“当前状态”重算，只有变化时才写引脚。
static void updateBuzzerOutput() {
  const uint8_t level = (hardwareOutputEnabled() && currentMarkActive()) ? 1 : 0;
  if (level == buzzerLevel) return;
  buzzerLevel = level;
  digitalWrite(PIN_BUZZER, level ? HIGH : LOW);
}

static void sendLine(const char* line) {
  // 租约失效时一次都不写 USB：端口被关闭或无人读取也不会阻塞主循环。
  if (!hostActive()) return;
  // All protocol lines are short; the keying engine never waits for a reply.
  if (Serial.availableForWrite() >= (int)strlen(line) + 2) {
    Serial.println(line);
  }
}

static void sendStatus() {
  char line[96];
  snprintf(line, sizeof(line), "STATUS,WPM,%u,MODE,%s,OUTPUT,%s",
           (unsigned)cwWpm, modeName(keyerMode), outputName(effectiveOutput()));
  sendLine(line);
}


static void sendKeyEvent(const char* paddle, bool pressed) {
  char line[40];
  snprintf(line, sizeof(line), "KEY,%s,%u", paddle, pressed ? 1U : 0U);
  sendLine(line);
}

static void sendToneEvent(bool on) {
  sendLine(on ? "TONE,ON" : "TONE,OFF");
}

static void sendElementEvent(bool isDit, uint32_t durationUs) {
  char line[48];
  snprintf(line, sizeof(line), "ELEMENT,%s,%lu",
           isDit ? "DIT" : "DAH", (unsigned long)durationUs);
  sendLine(line);
}

// ---------------- Settings persistence ----------------
static void loadSettings() {
  prefsReady = prefs.begin("cwtrainer", false);

  if (!prefsReady) return;

  const uint16_t storedWpm = prefs.getUShort("wpm", DEFAULT_WPM);
  const uint8_t storedMode = prefs.getUChar("mode", (uint8_t)MODE_IAMBIC_B);

  // 越界或损坏一律回默认，不让脏数据把设备卡死。
  if (storedWpm >= MIN_WPM && storedWpm <= MAX_WPM) cwWpm = storedWpm;
  if (storedMode <= (uint8_t)MODE_IAMBIC_B) keyerMode = (KeyerMode)storedMode;

  savedWpm = cwWpm;
  savedMode = (uint8_t)keyerMode;
}

static void markSettingsDirty() {
  if (!prefsReady) return;
  settingsDirty = true;
  settingsDirtySinceMs = millis();
}

// force = true 时立刻写（退出前用），否则等 SETTINGS_SAVE_DELAY_MS 静默后再写。
static void flushSettingsIfDue(bool force) {
  if (!prefsReady || !settingsDirty) return;
  if (!force && (uint32_t)(millis() - settingsDirtySinceMs) < SETTINGS_SAVE_DELAY_MS) return;

  settingsDirty = false;

  if (cwWpm != savedWpm) {
    prefs.putUShort("wpm", cwWpm);
    savedWpm = cwWpm;
  }

  const uint8_t modeNow = (uint8_t)keyerMode;
  if (modeNow != savedMode) {
    prefs.putUChar("mode", modeNow);
    savedMode = modeNow;
  }
}

// ---------------- Paddle debounce ----------------
static void initPaddle(PaddleInput& input, uint8_t pin) {
  input.pin = pin;
  pinMode(pin, INPUT_PULLUP);
  const bool pressed = (digitalRead(pin) == LOW);
  input.candidatePressed = pressed;
  input.stablePressed = pressed;
  input.candidateSinceUs = micros();
}

// Returns true when the debounced stable state changes.
static bool updatePaddle(PaddleInput& input, uint32_t nowUs) {
  const bool sampledPressed = (digitalRead(input.pin) == LOW);

  if (sampledPressed != input.candidatePressed) {
    input.candidatePressed = sampledPressed;
    input.candidateSinceUs = nowUs;
  }

  if (input.candidatePressed != input.stablePressed &&
      (uint32_t)(nowUs - input.candidateSinceUs) >= DEBOUNCE_US) {
    input.stablePressed = input.candidatePressed;
    return true;
  }
  return false;
}

// ---------------- Keyer engine ----------------
static void startElement(bool isDit, uint32_t nowUs) {
  const uint32_t durationUs = isDit ? dotDurationUs() : dotDurationUs() * 3UL;

  if (isDit) ditMemory = false;
  else dahMemory = false;

  lastElementWasDit = isDit;
  hasLastElement = true;
  keyerState = STATE_MARK;
  stateDeadlineUs = nowUs + durationUs;

  updateBuzzerOutput();
  sendElementEvent(isDit, durationUs);
}

static void resetKeyerForModeChange() {
  if (straightToneActive) {
    straightToneActive = false;
    sendToneEvent(false);
  }
  keyerState = STATE_IDLE;
  ditMemory = false;
  dahMemory = false;
  squeezeSeen = false;
  iambicBExtra = false;
  updateBuzzerOutput();
}

static void updateStraightMode(bool ditChanged) {
  const bool shouldBeOn = ditInput.stablePressed;
  if (shouldBeOn != straightToneActive) {
    straightToneActive = shouldBeOn;
    updateBuzzerOutput();
    sendToneEvent(straightToneActive);
  } else if (ditChanged) {
    updateBuzzerOutput();
  }
}

static void updatePaddleKeyer(uint32_t nowUs, bool ditChanged, bool dahChanged) {
  const bool ditPressed = ditInput.stablePressed;
  const bool dahPressed = dahInput.stablePressed;

  // Paddle memories preserve a short press made during an element or gap.
  if (ditChanged && ditPressed) ditMemory = true;
  if (dahChanged && dahPressed) dahMemory = true;

  if (ditPressed && dahPressed) squeezeSeen = true;

  // A squeeze is considered released only after BOTH paddles are open.
  if (squeezeSeen && !ditPressed && !dahPressed) {
    // Clear squeeze-generated pending memories. The currently sounding
    // element is allowed to finish. Iambic B schedules one final opposite
    // element; Iambic A does not.
    ditMemory = false;
    dahMemory = false;
    if (keyerMode == MODE_IAMBIC_B && keyerState != STATE_IDLE && hasLastElement) {
      iambicBExtra = true;
    }
    squeezeSeen = false;
  }

  // End the current mark and begin the one-unit inter-element gap.
  if (keyerState == STATE_MARK && timeReached(nowUs, stateDeadlineUs)) {
    keyerState = STATE_GAP;
    stateDeadlineUs += dotDurationUs();
    updateBuzzerOutput();
  }

  // Finish the one-unit gap.
  if (keyerState == STATE_GAP && timeReached(nowUs, stateDeadlineUs)) {
    keyerState = STATE_IDLE;
  }

  // Select the next element only while idle.
  if (keyerState == STATE_IDLE) {
    const bool wantDit = ditPressed || ditMemory;
    const bool wantDah = dahPressed || dahMemory;
    bool sendDit = false;
    bool shouldSend = false;

    if (wantDit && wantDah) {
      // Alternate while both paddles are requested. First simultaneous
      // squeeze starts with DIT.
      sendDit = !hasLastElement || !lastElementWasDit;
      shouldSend = true;
    } else if (wantDit) {
      sendDit = true;
      shouldSend = true;
    } else if (wantDah) {
      sendDit = false;
      shouldSend = true;
    } else if (iambicBExtra) {
      sendDit = !lastElementWasDit;
      shouldSend = true;
      iambicBExtra = false;
    }

    if (shouldSend) startElement(sendDit, nowUs);
  }
}

// ---------------- Morse table (TX) ----------------
struct MorseRow {
  char symbol;
  const char* code;
};

static const MorseRow MORSE_TABLE[] = {
  {'A', ".-"},     {'B', "-..."},   {'C', "-.-."},   {'D', "-.."},
  {'E', "."},      {'F', "..-."},   {'G', "--."},    {'H', "...."},
  {'I', ".."},     {'J', ".---"},   {'K', "-.-"},    {'L', ".-.."},
  {'M', "--"},     {'N', "-."},     {'O', "---"},    {'P', ".--."},
  {'Q', "--.-"},   {'R', ".-."},    {'S', "..."},    {'T', "-"},
  {'U', "..-"},    {'V', "...-"},   {'W', ".--"},    {'X', "-..-"},
  {'Y', "-.--"},   {'Z', "--.."},
  {'0', "-----"},  {'1', ".----"},  {'2', "..---"},  {'3', "...--"},
  {'4', "....-"},  {'5', "....."},  {'6', "-...."},  {'7', "--..."},
  {'8', "---.."},  {'9', "----."},
  {'.', ".-.-.-"}, {',', "--..--"}, {'?', "..--.."}, {'\'', ".----."},
  {'!', "-.-.--"}, {'/', "-..-."},  {'(', "-.--."},  {')', "-.--.-"},
  {'&', ".-..."},  {':', "---..."}, {';', "-.-.-."}, {'=', "-...-"},
  {'+', ".-.-."},  {'-', "-....-"}, {'_', "..--.-"}, {'"', ".-..-."},
  {'$', "...-..-"}, {'@', ".--.-."}
};

static const char* morseLookup(char c) {
  if (c >= 'a' && c <= 'z') c = (char)(c - 32);

  for (size_t i = 0; i < sizeof(MORSE_TABLE) / sizeof(MORSE_TABLE[0]); i++) {
    if (MORSE_TABLE[i].symbol == c) return MORSE_TABLE[i].code;
  }
  return nullptr;
}

// ---------------- Text sending engine ----------------
// 队列里只存字符，元素时序仍然由本机生成：上位机只负责“给什么”，不负责“什么时候按”。
static constexpr uint16_t TX_BUFFER_SIZE = 256;
static constexpr uint8_t TX_UNITS_CHAR_GAP = 3;
static constexpr uint8_t TX_UNITS_WORD_GAP = 7;

enum TxState : uint8_t {
  TX_IDLE = 0,
  TX_MARK = 1,
  TX_ELEM_GAP = 2,
  TX_CHAR_GAP = 3
};

static char txBuffer[TX_BUFFER_SIZE];
static uint16_t txHead = 0;
static uint16_t txCount = 0;

static TxState txState = TX_IDLE;
static uint32_t txDeadlineUs = 0;
static const char* txCode = nullptr;
static uint8_t txCodeLen = 0;
static uint8_t txElemIndex = 0;
static bool txRunning = false;   // SEND,END 之后到 SEND,DONE 之间为 true

static bool txIsSending() {
  return txState != TX_IDLE;
}

static bool txMarkActive() {
  return txState == TX_MARK;
}

static bool txEnqueue(char c) {
  if (txCount >= TX_BUFFER_SIZE) return false;
  txBuffer[(txHead + txCount) % TX_BUFFER_SIZE] = c;
  txCount++;
  return true;
}

static char txPeek(uint16_t offset) {
  return txBuffer[(txHead + offset) % TX_BUFFER_SIZE];
}

static char txPop() {
  const char c = txBuffer[txHead];
  txHead = (uint16_t)((txHead + 1) % TX_BUFFER_SIZE);
  txCount--;
  return c;
}

static void txClear() {
  txHead = 0;
  txCount = 0;
  txState = TX_IDLE;
  txCode = nullptr;
  txCodeLen = 0;
  txElemIndex = 0;
  txRunning = false;
  updateBuzzerOutput();
}

// 中止发送。notify = false 用于租约失效（此时也无法回传任何东西）。
static void txAbort(const char* reason, bool notify) {
  const bool wasActive = txRunning || txState != TX_IDLE;

  txClear();

  if (notify && wasActive) {
    char line[32];
    snprintf(line, sizeof(line), "SEND,ABORTED,%s", reason);
    sendLine(line);
  }
}

static void txStartMark(uint32_t nowUs) {
  const bool isDit = (txCode[txElemIndex] == '.');
  const uint32_t durationUs = isDit ? dotDurationUs() : dotDurationUs() * 3UL;

  txState = TX_MARK;
  txDeadlineUs = nowUs + durationUs;

  updateBuzzerOutput();
  sendElementEvent(isDit, durationUs);
}

static void txReportRemaining() {
  char line[24];
  snprintf(line, sizeof(line), "SENDING,%u", (unsigned)txCount);
  sendLine(line);
}

static void txFinish() {
  txRunning = false;
  txState = TX_IDLE;
  updateBuzzerOutput();
  sendLine("SEND,DONE");
}

// 取下一个字符并发出它的第一个元素；没有可发字符就收尾。
static void txStartNextChar(uint32_t nowUs) {
  while (txCount > 0) {
    const char c = txPop();

    if (c == ' ') continue;   // 词间隔由字符间隔阶段处理，空格本身不发

    const char* code = morseLookup(c);

    if (code == nullptr) {
      char line[24];
      snprintf(line, sizeof(line), "WARN,CHAR,%c",
               (c >= 32 && c < 127) ? c : '?');
      sendLine(line);
      continue;
    }

    txCode = code;
    txCodeLen = (uint8_t)strlen(code);
    txElemIndex = 0;
    txStartMark(nowUs);
    return;
  }

  txFinish();
}

static void updateTx(uint32_t nowUs) {
  if (txState == TX_IDLE) {
    if (txRunning && txCount > 0) txStartNextChar(nowUs);
    return;
  }

  if (!timeReached(nowUs, txDeadlineUs)) return;

  switch (txState) {
    case TX_MARK: {
      // 元素结束 → 一个单位的元素间隔
      txState = TX_ELEM_GAP;
      txDeadlineUs = nowUs + dotDurationUs();
      updateBuzzerOutput();
      break;
    }

    case TX_ELEM_GAP: {
      if (txElemIndex + 1 < txCodeLen) {
        txElemIndex++;
        txStartMark(nowUs);
        break;
      }

      // 一个字符发完：先吃掉后面的空格，决定是字符间隔还是词间隔
      txCode = nullptr;

      uint8_t gapUnits = TX_UNITS_CHAR_GAP;

      while (txCount > 0 && txPeek(0) == ' ') {
        txPop();
        gapUnits = TX_UNITS_WORD_GAP;
      }

      if (txCount == 0) {
        txFinish();
        break;
      }

      txReportRemaining();

      txState = TX_CHAR_GAP;
      txDeadlineUs = nowUs + dotDurationUs() * gapUnits;
      break;
    }

    case TX_CHAR_GAP: {
      if (txCount > 0) txStartNextChar(nowUs);
      else txFinish();
      break;
    }

    default:
      txState = TX_IDLE;
      break;
  }
}

// ---------------- Protocol ----------------
static void acknowledge(const char* key, const char* value) {
  char line[64];
  snprintf(line, sizeof(line), "ACK,%s,%s", key, value);
  sendLine(line);
}

static void handleCommand(char* line) {
  if (strcmp(line, "BYE") == 0) {
    // 先把回执发出去（此时租约还有效），再立刻失效。
    outputMode = OUTPUT_BUZZER;
    updateBuzzerOutput();
    sendLine("BYE,BUZZER");
    revokeHostLease();
    hostWasActive = false;
    flushSettingsIfDue(true);   // 退出前把还没落盘的参数写掉
    return;
  }

  // 其它任何合法指令都算“上位机还活着”，续一次租约。
  refreshHostLease();

  if (strcmp(line, "HELLO,1") == 0) {
    sendLine("READY,1,CWTRAINER,3.0.0");
    sendStatus();
    return;
  }

  if (strcmp(line, "PING") == 0) {
    sendLine("PONG");
    return;
  }

  if (strcmp(line, "GET") == 0) {
    sendStatus();
    return;
  }

  // ---- 发送方向 ----
  if (strcmp(line, "SEND,BEGIN") == 0) {
    txClear();
    resetKeyerForModeChange();
    sendLine("ACK,SEND,BEGIN");
    return;
  }

  if (strncmp(line, "SEND,TEXT,", 10) == 0) {
    const char* text = line + 10;

    for (size_t i = 0; text[i] != '\0'; i++) {
      char c = text[i];
      if (c >= 'a' && c <= 'z') c = (char)(c - 32);

      if (!txEnqueue(c)) {
        sendLine("ERROR,TX_FULL");
        break;
      }
    }

    {
      char ack[40];
      snprintf(ack, sizeof(ack), "ACK,SEND,TEXT,%u", (unsigned)txCount);
      sendLine(ack);
    }
    return;
  }

  if (strcmp(line, "SEND,END") == 0) {
    if (txCount == 0) {
      sendLine("SEND,DONE");
      return;
    }

    {
      char started[40];
      snprintf(started, sizeof(started), "SEND,START,%u", (unsigned)txCount);
      sendLine(started);
    }

    resetKeyerForModeChange();   // 手键状态清零，避免和自动拍发互相踩
    txRunning = true;
    return;
  }

  if (strcmp(line, "SEND,STOP") == 0) {
    txAbort("STOP", true);
    return;
  }

  char* command = strtok(line, ",");
  char* key = strtok(nullptr, ",");
  char* value = strtok(nullptr, ",");

  if (command == nullptr || key == nullptr || value == nullptr || strcmp(command, "SET") != 0) {
    sendLine("ERROR,BAD_COMMAND");
    return;
  }

  if (strcmp(key, "OUTPUT") == 0) {
    if (strcmp(value, "BUZZER") == 0) outputMode = OUTPUT_BUZZER;
    else if (strcmp(value, "PC") == 0) outputMode = OUTPUT_PC;
    else if (strcmp(value, "BOTH") == 0) outputMode = OUTPUT_BOTH;
    else {
      sendLine("ERROR,BAD_OUTPUT");
      return;
    }
    updateBuzzerOutput();
    acknowledge("OUTPUT", outputName(outputMode));
    sendStatus();
    return;
  }

  if (strcmp(key, "WPM") == 0) {
    const long parsed = strtol(value, nullptr, 10);
    if (parsed < MIN_WPM || parsed > MAX_WPM) {
      sendLine("ERROR,WPM_RANGE_5_60");
      return;
    }
    cwWpm = (uint16_t)parsed;
    markSettingsDirty();
    acknowledge("WPM", value);
    sendStatus();
    return;
  }

  if (strcmp(key, "MODE") == 0) {
    KeyerMode newMode;
    if (strcmp(value, "STRAIGHT") == 0) newMode = MODE_STRAIGHT;
    else if (strcmp(value, "IAMBIC_A") == 0) newMode = MODE_IAMBIC_A;
    else if (strcmp(value, "IAMBIC_B") == 0) newMode = MODE_IAMBIC_B;
    else {
      sendLine("ERROR,BAD_MODE");
      return;
    }
    if (newMode != keyerMode) {
      txAbort("MODE", true);
      resetKeyerForModeChange();
      keyerMode = newMode;
      updateBuzzerOutput();
      markSettingsDirty();
    }
    acknowledge("MODE", modeName(keyerMode));
    sendStatus();
    return;
  }

  sendLine("ERROR,UNKNOWN_SETTING");
}


static void readSerialCommands() {
  while (Serial.available() > 0) {

    const int raw = Serial.read();
    if (raw < 0) break;

    const char c = (char)raw;

    if (c == '\r') continue;

    if (c == '\n') {

      commandBuffer[commandLength] = '\0';

      if (commandLength > 0) {
        handleCommand(commandBuffer);
      }

      commandLength = 0;
      continue;
    }

    if (commandLength < sizeof(commandBuffer) - 1) {
      commandBuffer[commandLength++] = c;
    } else {
      commandLength = 0;
    }
  }
}


static void updateHostLease() {
  const bool active = hostActive();

  if (hostWasActive && !active) {
    // 租约刚到期：把请求的输出模式也一并清掉，
    // 免得下一次连接之前一直卡在“请求 PC 发声但没人发声”的静音态。
    outputMode = OUTPUT_BUZZER;

    // 上位机没了就不要再自己拍发，否则会一直发到队列结束。
    txAbort("HOST", false);
  }

  hostWasActive = active;
}

// ---------------- Arduino entry points ----------------
void setup() {
  pinMode(PIN_BUZZER, OUTPUT);
  digitalWrite(PIN_BUZZER, LOW);

  initPaddle(ditInput, PIN_DIT);
  initPaddle(dahInput, PIN_DAH);

  loadSettings();   // 恢复上次的 WPM 与键控模式

  Serial.begin(115200);
}

void loop() {
  const uint32_t nowUs = micros();

  const bool ditChanged = updatePaddle(ditInput, nowUs);
  const bool dahChanged = updatePaddle(dahInput, nowUs);

  if (ditChanged) sendKeyEvent("DIT", ditInput.stablePressed);
  if (dahChanged) sendKeyEvent("DAH", dahInput.stablePressed);

  // 手键永远优先：自动拍发过程中按任意桨，立刻中止发送并交还手动键控。
  if (txIsSending() && (ditInput.stablePressed || dahInput.stablePressed)) {
    txAbort("PADDLE", true);
  }

  if (!txIsSending()) {
    if (keyerMode == MODE_STRAIGHT) {
      updateStraightMode(ditChanged);
    } else {
      updatePaddleKeyer(nowUs, ditChanged, dahChanged);
    }
  }

  updateTx(nowUs);

  readSerialCommands();
  updateHostLease();

  // 每个主循环都按“租约 + 当前键状态”重算一次引脚，
  // 任何路径留下的状态都不会导致永久静音。
  updateBuzzerOutput();

  flushSettingsIfDue(false);
}
