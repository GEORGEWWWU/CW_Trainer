#pragma once

#include <stdint.h>

constexpr uint8_t PANEL_TEXT_CAPACITY = 48;

struct PanelFrame {
  uint32_t frequencyHz;
  const char* mode;
  const char* keyerMode;
  uint16_t wpm;
  int8_t signalDbm;
  bool qso;
  bool tx;
  bool rx;
  char decoded[PANEL_TEXT_CAPACITY + 1];

  bool operator==(const PanelFrame& other) const {
    return frequencyHz == other.frequencyHz && mode == other.mode &&
           keyerMode == other.keyerMode && wpm == other.wpm && signalDbm == other.signalDbm &&
           qso == other.qso && tx == other.tx && rx == other.rx;
  }
};

void beginPanelDisplay();
void publishPanelFrame(const PanelFrame& frame);
