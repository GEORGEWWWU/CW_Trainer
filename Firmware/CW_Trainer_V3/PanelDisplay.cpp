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
constexpr int GLYPH_SIZE = 16;

// Row-major 1-bit glyphs for the fixed Chinese labels.
constexpr uint8_t ZHI[] = {  // 直
  0x00,0x00,0x01,0x80,0xFF,0xFF,0x01,0x80,0x3F,0xFC,0x30,0x0C,0x30,0x0C,0x3F,0xFC,
  0x30,0x0C,0x3F,0xFC,0x30,0x0C,0x3F,0xFC,0x30,0x0C,0x30,0x0C,0xFF,0xFF,0x00,0x00
};
constexpr uint8_t JIAN[] = {  // 键
  0x00,0x00,0x20,0x10,0x20,0x10,0x67,0x7E,0x79,0x16,0xC2,0xFF,0xFC,0x16,0x27,0x7E,
  0x21,0x16,0xF9,0xFE,0x25,0x10,0x26,0xFE,0x2A,0x10,0x33,0x90,0x24,0xFF,0x08,0x00
};
constexpr uint8_t SHUANG[] = {  // 双
  0x00,0x00,0x00,0x00,0xFF,0xFE,0x06,0x86,0x46,0x86,0x64,0x84,0x24,0xC4,0x1C,0x4C,
  0x1C,0x48,0x0C,0x78,0x1C,0x30,0x36,0x30,0x22,0x48,0x41,0x86,0x02,0x02,0x00,0x00
};
constexpr uint8_t JIANG[] = {  // 桨
  0x0C,0x40,0x0C,0x40,0x6C,0xFE,0x2D,0x86,0x1F,0x44,0x1C,0x78,0x7C,0x30,0x4C,0xE0,
  0x0D,0x80,0x01,0x80,0xFF,0xFF,0x03,0xC0,0x0D,0xB0,0x39,0x9C,0x61,0x86,0x01,0x80
};
constexpr uint8_t DAI[] = {  // 待
  0x00,0x00,0x08,0x20,0x18,0x20,0x33,0xFE,0x60,0x20,0x48,0x20,0x1F,0xFF,0x30,0x00,
  0x30,0x08,0x77,0xFF,0x30,0x08,0x32,0x08,0x31,0x08,0x30,0x88,0x30,0x08,0x30,0x78
};
constexpr uint8_t JI[] = {  // 机
  0x00,0x00,0x10,0x00,0x11,0xF8,0x11,0x88,0xFD,0x88,0x11,0x88,0x31,0x88,0x39,0x08,
  0x35,0x08,0x51,0x08,0xD1,0x08,0x11,0x09,0x13,0x09,0x16,0x0B,0x14,0x0E,0x00,0x00
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
  oled.drawBitmap(x, 16, glyph, GLYPH_SIZE, GLYPH_SIZE, SSD1306_WHITE);
}

void drawPractice(const PanelFrame& frame, bool loading, int offset) {
  char speed[8];
  snprintf(speed, sizeof(speed), "%u WPM", (unsigned)frame.wpm);
  const int speedWidth = 12 * (int)strlen(speed);

  oled.setTextSize(2);
  if (loading) {
    oled.setCursor((OLED_WIDTH - speedWidth) / 2, 0);
    oled.print(speed);
    if (frame.tx) {
      drawGlyph(OLED_WIDTH - GLYPH_SIZE, JIAN);
    } else {
      drawGlyph(OLED_WIDTH - 2 * GLYPH_SIZE, DAI);
      drawGlyph(OLED_WIDTH - GLYPH_SIZE, JI);
    }
  } else {
    oled.setCursor(-offset, 0);
    oled.print(frame.decoded);
    oled.setCursor(OLED_WIDTH - speedWidth, 16);
    oled.print(speed);
  }

  const bool straight = strcmp(frame.keyerMode, "STRAIGHT") == 0;
  drawGlyph(0, straight ? ZHI : SHUANG);
  drawGlyph(GLYPH_SIZE, straight ? JIAN : JIANG);
  if (!straight) {
    oled.setCursor(2 * GLYPH_SIZE + 2, 16);
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
    target = qso || loading ? 0 : max(0, 12 * (int)strlen(frame.decoded) - OLED_WIDTH);
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
