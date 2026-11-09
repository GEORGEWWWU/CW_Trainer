#include "PanelDisplay.h"

#include <Arduino.h>
#include <Wire.h>
#include <Adafruit_SSD1306.h>
#include <freertos/queue.h>
#include <freertos/task.h>
#include <string.h>

namespace {
constexpr int PIN_OLED_SDA = 8;
constexpr int PIN_OLED_SCL = 9;
constexpr int OLED_WIDTH = 128;
constexpr int OLED_HEIGHT = 32;
constexpr uint8_t OLED_ROTATION = 2;  // 180 degrees for upside-down mounting.
constexpr uint32_t OLED_SPLASH_MS = 1000;
constexpr uint32_t OLED_LOADING_MS = 1000;
constexpr uint32_t OLED_REFRESH_MS = 50;
constexpr int LABEL_GLYPH_SIZE = 12;
constexpr int LABEL_Y = 19;
constexpr int LABEL_ASCII_Y = LABEL_Y + 2;
constexpr int DECODE_Y = 1;
constexpr int DECODE_CHAR_ADVANCE = 11;
constexpr int DECODE_WORD_SPACE = 4;

// Row-major 1-bit glyphs for the fixed Chinese labels.
constexpr uint8_t ZHI[] = {  // 直
  0x04,0x00,0xFF,0xF0,0x04,0x00,0x3F,0xC0,0x20,0x40,0x3F,0xC0,
  0x20,0x40,0x3F,0xC0,0x20,0x40,0x3F,0xC0,0x20,0x40,0xFF,0xF0
};
constexpr uint8_t JIAN[] = {  // 键
  0x00,0x00,0x40,0x80,0x4F,0xE0,0x74,0xA0,0x8B,0xF0,0xF8,0xA0,
  0x6F,0xE0,0xF4,0xA0,0x6F,0xE0,0x7C,0x80,0x6C,0x80,0x53,0xF0
};
constexpr uint8_t SHUANG[] = {  // 双
  0x00,0x00,0xFF,0xE0,0x0A,0x20,0x4A,0x20,0x4A,0x20,0x3A,0x40,
  0x31,0x40,0x11,0xC0,0x38,0x80,0x69,0xC0,0x46,0x60,0x8C,0x30
};
constexpr uint8_t JIANG[] = {  // 桨
  0x00,0x00,0x19,0x00,0x5B,0xF0,0x3E,0x20,0x3D,0xC0,0x79,0x80,
  0x1E,0x00,0x06,0x00,0xFF,0xF0,0x0F,0x00,0x36,0xC0,0xC6,0x30
};
constexpr uint8_t DAI[] = {  // 待
  0x00,0x00,0x11,0x00,0x2F,0xE0,0x41,0x00,0x11,0x00,0x2F,0xF0,
  0x60,0x40,0xEF,0xF0,0x20,0x40,0x24,0x40,0x20,0x40,0x21,0xC0
};
constexpr uint8_t JI[] = {  // 机
  0x00,0x00,0x27,0xC0,0x24,0x40,0xFC,0x40,0x24,0x40,0x74,0x40,
  0x6C,0x40,0xA4,0x40,0xA4,0x50,0x24,0x50,0x28,0x50,0x28,0x60
};

QueueHandle_t frames = nullptr;
Adafruit_SSD1306 oled(OLED_WIDTH, OLED_HEIGHT, &Wire, -1, 400000, 400000);

bool beginOled() {
  for (uint8_t address = 0x3C; address <= 0x3D; ++address) {
    Wire.beginTransmission(address);
    if (Wire.endTransmission() == 0) {
      return oled.begin(SSD1306_SWITCHCAPVCC, address, false, false);
    }
  }
  return false;
}

void drawGlyph(int x, const uint8_t* glyph) {
  oled.drawBitmap(x, LABEL_Y, glyph, LABEL_GLYPH_SIZE, LABEL_GLYPH_SIZE, SSD1306_WHITE);
}

int decodeAdvance(char c) {
  return c == ' ' ? DECODE_WORD_SPACE : DECODE_CHAR_ADVANCE;
}

int decodeWidth(const char* text) {
  int width = 0;
  for (; *text; ++text) width += decodeAdvance(*text);
  return width;
}

void drawDecodeText(const char* text, int offset) {
  int x = -offset;
  for (; *text && x < OLED_WIDTH; ++text) {
    if (*text != ' ') {
      oled.drawChar(x, DECODE_Y, *text, SSD1306_WHITE, SSD1306_WHITE, 2);
    }
    x += decodeAdvance(*text);
  }
}

void drawPractice(const PanelFrame& frame, bool loading, int offset) {
  char speed[8];
  snprintf(speed, sizeof(speed), "%u WPM", (unsigned)frame.wpm);
  const int speedLength = (int)strlen(speed);
  if (loading) {
    oled.setTextSize(2);
    oled.setCursor((OLED_WIDTH - 12 * speedLength) / 2, DECODE_Y);
    oled.print(speed);
    if (frame.tx) {
      drawGlyph(OLED_WIDTH - LABEL_GLYPH_SIZE, JIAN);
    } else {
      drawGlyph(OLED_WIDTH - 2 * LABEL_GLYPH_SIZE, DAI);
      drawGlyph(OLED_WIDTH - LABEL_GLYPH_SIZE, JI);
    }
  } else {
    drawDecodeText(frame.decoded, offset);
    oled.setTextSize(1);
    oled.setCursor(OLED_WIDTH - 6 * speedLength, LABEL_ASCII_Y);
    oled.print(speed);
  }

  const bool straight = strcmp(frame.keyerMode, "STRAIGHT") == 0;
  drawGlyph(0, straight ? ZHI : SHUANG);
  drawGlyph(LABEL_GLYPH_SIZE, straight ? JIAN : JIANG);
  if (!straight) {
    oled.setTextSize(1);
    oled.setCursor(2 * LABEL_GLYPH_SIZE + 2, LABEL_ASCII_Y);
    oled.print(strcmp(frame.keyerMode, "IAMBIC_A") == 0 ? 'A' : 'B');
  }
}

void drawQso(const PanelFrame& frame) {
  char title[16];
  const unsigned long hz = frame.frequencyHz;
  snprintf(title, sizeof(title), "%lu.%03lu.%03lu", hz / 1000000UL,
           hz / 1000UL % 1000UL, hz % 1000UL);

  oled.setTextSize(2);
  oled.setCursor((OLED_WIDTH - 12 * (int)strlen(title)) / 2, 0);
  oled.print(title);

  oled.setTextSize(1);
  oled.setCursor(0, 22);
  oled.print(frame.mode);
  oled.setCursor(24, 22);
  oled.print(frame.tx ? "TX" : frame.rx ? "RX" : "--");
  oled.setCursor(42, 22);
  if (frame.signalDbm > -73) {
    oled.print("S9+");
    oled.print(frame.signalDbm + 73);
  } else {
    oled.print('S');
    oled.print((frame.signalDbm + 130) / 6);
  }
  const int fill = constrain((frame.signalDbm + 121) * 49 / 108, 0, 49);
  oled.drawRect(77, 22, 51, 8, SSD1306_WHITE);
  if (fill > 0) oled.fillRect(78, 23, fill, 6, SSD1306_WHITE);
}

void displayTask(void*) {
  while (!Wire.begin(PIN_OLED_SDA, PIN_OLED_SCL, 400000)) {
    vTaskDelay(pdMS_TO_TICKS(2000));
  }
  Wire.setTimeOut(20);
  while (!beginOled()) {
    vTaskDelay(pdMS_TO_TICKS(2000));
  }
  oled.setRotation(OLED_ROTATION);
  oled.setTextColor(SSD1306_WHITE);
  oled.setTextWrap(false);

  constexpr char splash[] = "CWT V3";
  oled.clearDisplay();
  oled.setTextSize(3);
  oled.setCursor((OLED_WIDTH - 18 * (sizeof(splash) - 1)) / 2, (OLED_HEIGHT - 24) / 2);
  oled.print(splash);
  oled.display();
  vTaskDelay(pdMS_TO_TICKS(OLED_SPLASH_MS));

  const uint32_t loadingStartedAt = millis();
  bool loading = true;
  int offset = 0;
  int target = 0;
  PanelFrame frame{};
  while (true) {
    const TickType_t wait = loading || offset < target ?
        pdMS_TO_TICKS(OLED_REFRESH_MS) : portMAX_DELAY;
    xQueueReceive(frames, &frame, wait);
    if (!frame.keyerMode) continue;
    if (loading) loading = millis() - loadingStartedAt < OLED_LOADING_MS;
    const bool qso = frame.qso && !loading;
    target = qso || loading ? 0 : max(0, decodeWidth(frame.decoded) - OLED_WIDTH);
    offset = min(offset + 4, target);
    oled.clearDisplay();
    if (qso) drawQso(frame);
    else drawPractice(frame, loading, offset);
    oled.display();
    vTaskDelay(pdMS_TO_TICKS(OLED_REFRESH_MS));
  }
}
}  // namespace

void beginPanelDisplay() {
  frames = xQueueCreate(1, sizeof(PanelFrame));
  if (frames && xTaskCreate(displayTask, "CW OLED", 4096, nullptr, tskIDLE_PRIORITY,
                            nullptr) != pdPASS) {
    vQueueDelete(frames);
    frames = nullptr;
  }
}

void publishPanelFrame(const PanelFrame& frame) {
  if (frames) xQueueOverwrite(frames, &frame);
}
