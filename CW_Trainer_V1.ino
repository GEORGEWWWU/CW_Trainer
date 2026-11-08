
/*
 * ============================================
 * CW TRAINER V1 （CW 练习器 V1 版本）
 * ESP32-S3-N16R8
 * Arduino ESP32 Core 2.0.17-cn
 * ============================================
 *
 * GPIO4 : DAH paddle
 * GPIO5 : DIT paddle
 * GPIO6 : Active HIGH buzzer
 *
 * Features:
 * - Straight Key
 * - Iambic A
 * - Iambic B
 * - Adjustable WPM
 * - Paddle debounce
 * - Paddle memory
 * - Non-blocking CW timing
 */

// ============================================
// 1. USER SETTINGS
// ============================================

// Keyer modes
#define MODE_STRAIGHT  0
#define MODE_IAMBIC_A  1
#define MODE_IAMBIC_B  2

// Change this to select your keyer mode
#define KEYER_MODE MODE_IAMBIC_B

// Adjust Morse code speed (5-60 WPM)
#define CW_WPM 15

// Hardware pins
#define PIN_DIT     5
#define PIN_DAH     4
#define PIN_BUZZER  6

// Debounce time
#define DEBOUNCE_US 4000UL

#if CW_WPM < 5 || CW_WPM > 60
#error "CW_WPM must be between 5 and 60"
#endif

#if KEYER_MODE < MODE_STRAIGHT || KEYER_MODE > MODE_IAMBIC_B
#error "Invalid keyer mode"
#endif

// One Morse timing unit in microseconds
const uint32_t DOT_US = 1200000UL / CW_WPM;


// ============================================
// 2. INPUT HANDLING
// ============================================

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

// Returns true only when a debounced press begins
bool updatePaddle(PaddleInput &input, uint32_t now) {
  bool sample = (digitalRead(input.pin) == LOW);

  if (sample != input.rawPressed) {
    input.rawPressed = sample;
    input.rawChangedAt = now;
  }

  if ((uint32_t)(now - input.rawChangedAt) >= DEBOUNCE_US) {
    input.stablePressed = input.rawPressed;
  }

  bool pressedEdge =
    input.stablePressed && !input.lastStable;

  input.lastStable = input.stablePressed;

  return pressedEdge;
}


// ============================================
// 3. KEYER STATE
// ============================================

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


// Safely compare timestamps across micros() rollover
bool timeReached(uint32_t now, uint32_t target) {
  return (int32_t)(now - target) >= 0;
}


// ============================================
// 4. START MORSE ELEMENT
// ============================================

void startElement(bool isDit, uint32_t now) {

  // Consume the selected paddle memory
  if (isDit) {
    ditMemory = false;
  } else {
    dahMemory = false;
  }

  lastElementWasDit = isDit;
  hasLastElement = true;

  // Physical buzzer ON
  digitalWrite(PIN_BUZZER, HIGH);

  keyerState = KEYER_MARK;

  // Dot = 1 unit
  // Dash = 3 units
  uint32_t duration = isDit
    ? DOT_US
    : DOT_US * 3UL;

  nextDeadline = now + duration;
}


// ============================================
// 5. KEYER ENGINE
// ============================================

void updateKeyer(uint32_t now) {

  // 1. Update paddle inputs
  bool ditEdge = updatePaddle(ditInput, now);
  bool dahEdge = updatePaddle(dahInput, now);

  if (ditEdge) {
    ditMemory = true;
  }

  if (dahEdge) {
    dahMemory = true;
  }

  // 2. Straight Key mode
  if (KEYER_MODE == MODE_STRAIGHT) {

    digitalWrite(
      PIN_BUZZER,
      ditInput.stablePressed ? HIGH : LOW
    );

    return;
  }

  bool ditPressed = ditInput.stablePressed;
  bool dahPressed = dahInput.stablePressed;

  // 3. Iambic A squeeze-release handling
  if (KEYER_MODE == MODE_IAMBIC_A) {

    if (ditPressed && dahPressed) {
      squeezeSeen = true;
    }

    if (squeezeSeen && !ditPressed && !dahPressed) {

      // A mode: discard pending elements
      // after both paddles are released
      ditMemory = false;
      dahMemory = false;

      squeezeSeen = false;
    }
  }

  // 4. Finish current sound
  if (keyerState == KEYER_MARK &&
      timeReached(now, nextDeadline)) {

    digitalWrite(PIN_BUZZER, LOW);

    keyerState = KEYER_GAP;

    // One-unit inter-element gap
    nextDeadline += DOT_US;
  }

  // 5. Finish inter-element gap
  if (keyerState == KEYER_GAP &&
      timeReached(now, nextDeadline)) {

    keyerState = KEYER_IDLE;
  }

  // 6. Select next element
  if (keyerState == KEYER_IDLE) {

    bool wantDit = ditMemory || ditPressed;
    bool wantDah = dahMemory || dahPressed;

    bool sendDit = false;
    bool shouldSend = false;

    if (wantDit && wantDah) {

      // Alternate when both paddles are requested.
      // Initial simultaneous press starts with DIT.
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


// ============================================
// 6. SETUP
// ============================================

void setup() {

  pinMode(PIN_BUZZER, OUTPUT);
  digitalWrite(PIN_BUZZER, LOW);

  initPaddle(ditInput, PIN_DIT);
  initPaddle(dahInput, PIN_DAH);
}


// ============================================
// 7. MAIN LOOP
// ============================================

void loop() {

  uint32_t now = micros();

  updateKeyer(now);
}
