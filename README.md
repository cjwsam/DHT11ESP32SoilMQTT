
# ESP-Based Environment Sensor System

## About
This repository contains code for an ESP8266/ESP32-based environment sensor system, capable of measuring temperature, humidity, and soil moisture. It includes features like MQTT for data communication, OTA updates, and a captive portal for WiFi configuration.

## Author
Chris Willis

## Features
- Supports DHT11, DHT22, SHT20, and AHT20 sensors.
- MQTT for data publishing.
- Over-the-Air (OTA) updates.
- Captive portal for WiFi configuration.
- EEPROM for storing configurable variables.

## Hardware Requirements
- ESP8266/ESP32 board.
- Supported environmental sensors (DHT11/DHT22/SHT20/AHT20).
- Soil moisture sensor (optional).

## Installation
1. Clone the repository.
2. Open the code in Arduino IDE.
3. Modify the configuration as per your hardware setup.
4. Upload the code to your ESP8266/ESP32 board.

## Usage
- On first boot, the device will create a WiFi access point.
- Connect to this access point and configure your WiFi credentials and MQTT settings.
- The device will then connect to your WiFi network and start sending sensor data to the configured MQTT server.

## License
This project is licensed under the MIT License - see the LICENSE file for details.
