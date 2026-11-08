/*
  CW TRAINER V2 - ESP32-S3-N16R8
  Arduino-ESP32 Core: 2.0.17-cn

  Wiring (the final left/right convention):
    GPIO5 = LEFT paddle = DIT (dot)
    GPIO4 = RIGHT paddle = DAH (dash)
    GPIO6 = active-HIGH buzzer input
    GND   = paddle common

  USB protocol: newline-delimited ASCII commands/events at 115200.
  PC commands:
    HELLO,1
    PING
    GET
    SET,OUTPUT,BUZZER|PC|BOTH
    SET,WPM,5..60
    SET,MODE,STRAIGHT|IAMBIC_A|IAMBIC_B

  Device events:
    READY,1,CWTRAINER,2.0.0
    STATUS,WPM,15,MODE,IAMBIC_B,OUTPUT,BUZZER
    KEY,DIT,1 / KEY,DIT,0
    KEY,DAH,1 / KEY,DAH,0
    ELEMENT,DIT,80000     (duration in microseconds)
    TONE,ON / TONE,OFF    (straight-key mode)
    ACK,... / PONG

  Important:
  - The MCU remains responsible for keyer timing. The PC never schedules
    paddle timing; it only renders ELEMENT events as computer audio.
  - OUTPUT defaults to BUZZER at boot and falls back to BUZZER if host
    heartbeat is lost for 6 seconds.
*/

#include <Arduino.h>
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

static KeyerState keyerState = STATE_IDLE;
static uint32_t stateDeadlineUs = 0;
static bool ditMemory = false;
static bool dahMemory = false;
static bool lastElementWasDit = false;
static bool hasLastElement = false;
static bool squeezeSeen = false;
static bool iambicBExtra = false;
static bool straightToneActive = false;

static bool hostConnected = false;
static uint32_t lastHostPingMs = 0;

static char commandBuffer[96];
static size_t commandLength = 0;

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

static bool hardwareOutputEnabled() {
  return outputMode == OUTPUT_BUZZER || outputMode == OUTPUT_BOTH;
}

static bool currentMarkActive() {
  if (keyerMode == MODE_STRAIGHT) return straightToneActive;
  return keyerState == STATE_MARK;
}

static void updateBuzzerOutput() {
  const bool on = hardwareOutputEnabled() && currentMarkActive();
  digitalWrite(PIN_BUZZER, on ? HIGH : LOW);
}

static void sendLine(const char* line) {
  if (!hostConnected) return;
  // All protocol lines are short; the keying engine never waits for a reply.
  if (Serial.availableForWrite() >= (int)strlen(line) + 2) {
    Serial.println(line);
  }
}

static void sendStatus() {
  if (!hostConnected) return;
  char line[96];
  snprintf(line, sizeof(line), "STATUS,WPM,%u,MODE,%s,OUTPUT,%s",
           (unsigned)cwWpm, modeName(keyerMode), outputName(outputMode));
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
  if (!hostConnected) return;
  char line[48];
  snprintf(line, sizeof(line), "ELEMENT,%s,%lu",
           isDit ? "DIT" : "DAH", (unsigned long)durationUs);
  sendLine(line);
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

// ---------------- Protocol ----------------
static void acknowledge(const char* key, const char* value) {
  char line[64];
  snprintf(line, sizeof(line), "ACK,%s,%s", key, value);
  sendLine(line);
}

static void handleCommand(char* line) {
  if (strcmp(line, "HELLO,1") == 0) {
    hostConnected = true;
    lastHostPingMs = millis();
    sendLine("READY,1,CWTRAINER,2.0.0");
    sendStatus();
    return;
  }

  if (strcmp(line, "PING") == 0) {
    hostConnected = true;
    lastHostPingMs = millis();
    sendLine("PONG");
    return;
  }

  if (strcmp(line, "GET") == 0) {
    sendStatus();
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
      resetKeyerForModeChange();
      keyerMode = newMode;
      updateBuzzerOutput();
    }
    acknowledge("MODE", modeName(keyerMode));
    sendStatus();
    return;
  }

  sendLine("ERROR,UNKNOWN_SETTING");
}

static void readSerialCommands() {
  while (Serial.available() > 0) {
    const char c = (char)Serial.read();
    if (c == '\r') continue;

    if (c == '\n') {
      commandBuffer[commandLength] = '\0';
      if (commandLength > 0) handleCommand(commandBuffer);
      commandLength = 0;
      continue;
    }

    if (commandLength < sizeof(commandBuffer) - 1) {
      commandBuffer[commandLength++] = c;
    } else {
      commandLength = 0;
      sendLine("ERROR,LINE_TOO_LONG");
    }
  }
}

static void checkHostTimeout() {
  if (hostConnected && (uint32_t)(millis() - lastHostPingMs) > HOST_TIMEOUT_MS) {
    hostConnected = false;
    // Safe fallback: if the PC app exits or USB is unplugged, restore the
    // standalone physical buzzer so the keyer never becomes permanently mute.
    outputMode = OUTPUT_BUZZER;
    updateBuzzerOutput();
  }
}

// ---------------- Arduino entry points ----------------
void setup() {
  pinMode(PIN_BUZZER, OUTPUT);
  digitalWrite(PIN_BUZZER, LOW);

  initPaddle(ditInput, PIN_DIT);
  initPaddle(dahInput, PIN_DAH);

  Serial.begin(115200);
}

void loop() {
  const uint32_t nowUs = micros();

  const bool ditChanged = updatePaddle(ditInput, nowUs);
  const bool dahChanged = updatePaddle(dahInput, nowUs);

  if (ditChanged) sendKeyEvent("DIT", ditInput.stablePressed);
  if (dahChanged) sendKeyEvent("DAH", dahInput.stablePressed);

  if (keyerMode == MODE_STRAIGHT) {
    updateStraightMode(ditChanged);
  } else {
    updatePaddleKeyer(nowUs, ditChanged, dahChanged);
  }

  readSerialCommands();
  checkHostTimeout();
}
