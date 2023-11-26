/*
 * ESP Environment Sensor System
 * 
 * This program is designed to run on ESP32 or ESP8266 boards, collecting data from various sensors
 * and publishing it via MQTT. It supports OTA updates and can be configured via a web server interface.
 * 
 * Author: Chris Willis
 * License: MIT
 */


#include <EEPROM.h>
#include <Wire.h>
#include <DNSServer.h>


// Configurable Variables
char ssid[32];
char password[32];
char mqttServer[40];
int mqttPort;
char mqttUsername[32];
char mqttPassword[32];
char deviceId_network[32];
char mqttTopicBase[50];  // Adjust size as needed
unsigned long lastReconnectAttempt = 0;

// Sensor selection
#define SENSOR_DHT11 1
#define SENSOR_DHT22 2
#define SENSOR_SHT20 3
#define SENSOR_AHT20 4

int sensorType;  // Default sensor type

// Include Libraries and Conditional Compilation for ESP32/ESP8266
#if defined(ESP32)
#include <WiFi.h>
#include <WebServer.h>
#elif defined(ESP8266)
#include <ESP8266WiFi.h>
#include <ESP8266WebServer.h>
#else
#error "This code is intended to run only on ESP32 or ESP8266 boards."
#endif

#include <PubSubClient.h>
#include <ArduinoOTA.h>
#include "SHT2x.h"
#include "Adafruit_AHTX0.h"
#include <DHT.h>

// MQTT Topics
char tempTopic[50];
char humidityTopic[50];
char moistureTopic[50];
char alarmTopic[50];

// Network Clients
#if defined(ESP32)
WebServer server(80);
#elif defined(ESP8266)
ESP8266WebServer server(80);
#endif
WiFiClient espClient;
PubSubClient client(espClient);

// Timing and Data Accumulation
unsigned long lastSampleTime = 0;
const long sampleInterval = 2000;  // 2 seconds in milliseconds
unsigned long lastPublishTime = 0;
const long publishInterval = 300000;  // 5 minutes in milliseconds
float accumulatedTemperature = 0;
float accumulatedHumidity = 0;
int accumulatedMoisture = 0;
int sampleCount = 0;

// Calibration Values for Soil Moisture Sensor
const int AIR_VALUE = 3300;    // Replace with your sensor's value in air
const int WATER_VALUE = 1000;  // Replace with your sensor's value in water

// Thresholds and Last Sensor Data
const float humidityThreshold = 90.0;
const float temperatureChangeThreshold = 5.0;
struct SensorData {
  float temperature;
  float humidity;
  int moisture;
};
SensorData lastSensorData = { 0, 0, 0 };

// Global Variables for Sensor Pins and I2C
int dhtSensorPin;           // Variable for DHT sensor pin
int sdaPin;                 // Variable for SDA pin
int sclPin;                 // Variable for SCL pin
int soilMoistureSensorPin;  // Variable for soil moisture sensor pin

// EEPROM Addresses for Sensor Pins and I2C Pins
const int DHT_SENSOR_PIN_ADDR = 268;
const int SDA_PIN_ADDR = 272;
const int SCL_PIN_ADDR = 276;
const int SOIL_MOISTURE_SENSOR_PIN_ADDR = 280;

// EEPROM Addresses for Configurable Variables
const int EEPROM_SIZE = 512;
const int SSID_ADDR = 0;
const int PASSWORD_ADDR = 32;
const int MQTT_SERVER_ADDR = 64;
const int MQTT_PORT_ADDR = 104;
const int MQTT_USERNAME_ADDR = 108;
const int MQTT_PASSWORD_ADDR = 140;
const int DEVICE_ID_ADDR = 172;
const int SENSOR_TYPE_ADDR = 204;
const int INIT_FLAG_ADDR = 250;
const int MQTT_TOPIC_BASE_ADDR = 284;  // This address is right after SOIL_MOISTURE_SENSOR_PIN_ADDR
int wifiConnectAttempts = 0;

const byte INIT_FLAG = 0xA5;  // Arbitrary flag value

// Sensor objects
DHT* dht = nullptr;  // Pointer to DHT object
SHT2x sht20;
Adafruit_AHTX0 aht;

DNSServer dnsServer;
const byte DNS_PORT = 53;

void setup() {
  Serial.begin(115200);

  EEPROM.begin(EEPROM_SIZE);

  byte initFlag;
  EEPROM.get(INIT_FLAG_ADDR, initFlag);  // Corrected: Read the init flag into the variable

  Serial.print("Init Flag Value: ");
  Serial.println(initFlag, HEX);
  if (initFlag != INIT_FLAG) {
    Serial.println("Initializing EEPROM with default values...");
    initEEPROM();
  } else {
    Serial.println("EEPROM already initialized.");

    // Only read other values if EEPROM is initialized
    EEPROM.get(SSID_ADDR, ssid);
    EEPROM.get(PASSWORD_ADDR, password);
    EEPROM.get(MQTT_SERVER_ADDR, mqttServer);
    EEPROM.get(MQTT_PORT_ADDR, mqttPort);
    EEPROM.get(MQTT_USERNAME_ADDR, mqttUsername);
    EEPROM.get(MQTT_PASSWORD_ADDR, mqttPassword);
    EEPROM.get(DEVICE_ID_ADDR, deviceId_network);
    EEPROM.get(SENSOR_TYPE_ADDR, sensorType);
    EEPROM.get(DHT_SENSOR_PIN_ADDR, dhtSensorPin);
    EEPROM.get(SDA_PIN_ADDR, sdaPin);
    EEPROM.get(SCL_PIN_ADDR, sclPin);
    EEPROM.get(SOIL_MOISTURE_SENSOR_PIN_ADDR, soilMoistureSensorPin);
    EEPROM.get(MQTT_TOPIC_BASE_ADDR, mqttTopicBase);
  }

  printEEPROMContents();

  // Sensor initialization based on the type
  switch (sensorType) {
    case SENSOR_DHT11:
      dht = new DHT(dhtSensorPin, DHT11);
      dht->begin();
      Serial.println("DHT11 sensor initialized.");
      break;
    case SENSOR_DHT22:
      dht = new DHT(dhtSensorPin, DHT22);
      dht->begin();
      Serial.println("DHT22 sensor initialized.");
      break;
    case SENSOR_SHT20:
      Wire.begin(sdaPin, sclPin);
      sht20.begin();
      Serial.println("SHT20 sensor initialized.");
      break;
    case SENSOR_AHT20:
      Wire.begin(sdaPin, sclPin);
      if (!aht.begin()) {
        Serial.println("Failed to initialize AHT20!");
      } else {
        Serial.println("AHT20 sensor initialized.");
      }
      break;
    default:
      Serial.println("No valid sensor type selected.");
      break;
  }

  // Initialize other components
  connectToWiFi();
  setupMQTT();
  handleMQTTConnection();
  setupOTA();
  setupWebServer();
}

void loop() {
  // Regular tasks
  handleOTA();
  handleWebServer();
  handleSensorSampling();
  handleMQTTPublishing();
    dnsServer.processNextRequest();

}

void printEEPROMContents() {
  Serial.println("EEPROM Contents:");

  // Print SSID
  Serial.print("SSID: ");
  for (int i = SSID_ADDR; i < SSID_ADDR + sizeof(ssid); i++) {
    char ch = (char)EEPROM.read(i);
    if (ch == '\0') break;
    Serial.print(ch);
  }
  Serial.println();

  // Print Password
  Serial.print("Password: ");
  for (int i = PASSWORD_ADDR; i < PASSWORD_ADDR + sizeof(password); i++) {
    char ch = (char)EEPROM.read(i);
    if (ch == '\0') break;
    Serial.print(ch);
  }
  Serial.println();

  // Print MQTT Server
  Serial.print("MQTT Server: ");
  for (int i = MQTT_SERVER_ADDR; i < MQTT_SERVER_ADDR + sizeof(mqttServer); i++) {
    char ch = (char)EEPROM.read(i);
    if (ch == '\0') break;
    Serial.print(ch);
  }
  Serial.println();

  // Print MQTT Port
  Serial.print("MQTT Port: ");
  int mqttPortValue;
  EEPROM.get(MQTT_PORT_ADDR, mqttPortValue);
  Serial.println(mqttPortValue);

  // Print MQTT Username
  Serial.print("MQTT Username: ");
  for (int i = MQTT_USERNAME_ADDR; i < MQTT_USERNAME_ADDR + sizeof(mqttUsername); i++) {
    char ch = (char)EEPROM.read(i);
    if (ch == '\0') break;
    Serial.print(ch);
  }
  Serial.println();

  // Print MQTT Password
  Serial.print("MQTT Password: ");
  for (int i = MQTT_PASSWORD_ADDR; i < MQTT_PASSWORD_ADDR + sizeof(mqttPassword); i++) {
    char ch = (char)EEPROM.read(i);
    if (ch == '\0') break;
    Serial.print(ch);
  }
  Serial.println();

  // Print Device ID
  Serial.print("Device ID: ");
  for (int i = DEVICE_ID_ADDR; i < DEVICE_ID_ADDR + sizeof(deviceId_network); i++) {
    char ch = (char)EEPROM.read(i);
    if (ch == '\0') break;
    Serial.print(ch);
  }
  Serial.println();
  Serial.print("MQTT Topic Base: ");
  for (int i = MQTT_TOPIC_BASE_ADDR; i < MQTT_TOPIC_BASE_ADDR + sizeof(mqttTopicBase); i++) {
    char ch = (char)EEPROM.read(i);
    if (ch == '\0') break;
    Serial.print(ch);
  }
  Serial.println();

  // Print Sensor Type
  Serial.print("Sensor Type: ");
  int sensorTypeValue;
  EEPROM.get(SENSOR_TYPE_ADDR, sensorTypeValue);
  Serial.println(sensorTypeValue);

  // Print DHT Sensor Pin
  Serial.print("DHT Sensor Pin: ");
  int dhtSensorPinValue;
  EEPROM.get(DHT_SENSOR_PIN_ADDR, dhtSensorPinValue);
  Serial.println(dhtSensorPinValue);

  // Print SDA Pin
  Serial.print("SDA Pin: ");
  int sdaPinValue;
  EEPROM.get(SDA_PIN_ADDR, sdaPinValue);
  Serial.println(sdaPinValue);

  // Print SCL Pin
  Serial.print("SCL Pin: ");
  int sclPinValue;
  EEPROM.get(SCL_PIN_ADDR, sclPinValue);
  Serial.println(sclPinValue);

  // Print Soil Moisture Sensor Pin
  Serial.print("Soil Moisture Sensor Pin: ");
  int soilMoistureSensorPinValue;
  EEPROM.get(SOIL_MOISTURE_SENSOR_PIN_ADDR, soilMoistureSensorPinValue);
  Serial.println(soilMoistureSensorPinValue);
}

void initEEPROM() {
// Set default values for configurable variables
const char* defaultSSID = "YourDefaultSSID";
const char* defaultPassword = "YourDefaultPassword";
const char* defaultMqttServer = "mqtt.example.com";
const int defaultMqttPort = 1883;
const char* defaultMqttUsername = "mqttUser";
const char* defaultMqttPassword = "mqttPassword";
const char* defaultdeviceId_network = "defaultDeviceId";
const int defaultSensorType = SENSOR_DHT11; // Choose a common default
  const int defaultDhtSensorPin = 22;
  const int defaultSdaPin = 21;
  const int defaultSclPin = 22;
  const int defaultSoilMoistureSensorPin = 32;
const char* defaultMqttTopicBase = "home/sensors";


  strncpy(ssid, defaultSSID, sizeof(ssid) - 1);
  strncpy(password, defaultPassword, sizeof(password) - 1);
  strncpy(mqttServer, defaultMqttServer, sizeof(mqttServer) - 1);
  mqttPort = defaultMqttPort;
  strncpy(mqttUsername, defaultMqttUsername, sizeof(mqttUsername) - 1);
  strncpy(mqttPassword, defaultMqttPassword, sizeof(mqttPassword) - 1);
  strncpy(deviceId_network, defaultdeviceId_network, sizeof(deviceId_network) - 1);
  sensorType = defaultSensorType;
  dhtSensorPin = defaultDhtSensorPin;
  sdaPin = defaultSdaPin;
  sclPin = defaultSclPin;
  soilMoistureSensorPin = defaultSoilMoistureSensorPin;


  strncpy(mqttTopicBase, defaultMqttTopicBase, sizeof(mqttTopicBase) - 1);
  mqttTopicBase[sizeof(mqttTopicBase) - 1] = '\0';

  EEPROM.put(INIT_FLAG_ADDR, INIT_FLAG);
  EEPROM.put(SSID_ADDR, ssid);
  EEPROM.put(PASSWORD_ADDR, password);
  EEPROM.put(MQTT_SERVER_ADDR, mqttServer);
  EEPROM.put(MQTT_PORT_ADDR, mqttPort);
  EEPROM.put(MQTT_USERNAME_ADDR, mqttUsername);
  EEPROM.put(MQTT_PASSWORD_ADDR, mqttPassword);
  EEPROM.put(DEVICE_ID_ADDR, deviceId_network);
  EEPROM.put(SENSOR_TYPE_ADDR, sensorType);
  EEPROM.put(DHT_SENSOR_PIN_ADDR, dhtSensorPin);
  EEPROM.put(SDA_PIN_ADDR, sdaPin);
  EEPROM.put(SCL_PIN_ADDR, sclPin);
  EEPROM.put(SOIL_MOISTURE_SENSOR_PIN_ADDR, soilMoistureSensorPin);
  EEPROM.put(INIT_FLAG_ADDR, INIT_FLAG);
  EEPROM.put(MQTT_TOPIC_BASE_ADDR, mqttTopicBase);  // Choose an appropriate address for MQTT_TOPIC_BASE_ADDR

  EEPROM.commit();
}

void connectToWiFi() {
  WiFi.hostname("The_ESP_New_Zealand");
  WiFi.begin(ssid, password);
  
  int attempt = 0;
  while (WiFi.status() != WL_CONNECTED && attempt < 3) {
    delay(500);
    Serial.print(".!");
    attempt++;
  }

  if (WiFi.status() == WL_CONNECTED) {
    Serial.println("\nWiFi connected. IP Address: " + WiFi.localIP().toString());
    wifiConnectAttempts = 0; // Reset the counter on successful connection
  } else {
    Serial.println("\nFailed to connect to WiFi. Entering AP mode.");
    setupAccessPoint();
  }
}

void setupSensor() {
  // Initialize the selected sensor based on the configured 'sensorType'

  Serial.print("Sensor Type: ");
  Serial.println(sensorType);

  switch (sensorType) {
    case SENSOR_DHT11:
      dht = new DHT(dhtSensorPin, DHT11);
      Serial.println("Initializing DHT11 sensor...");
      dht->begin();

      break;
    case SENSOR_DHT22:
      dht = new DHT(dhtSensorPin, DHT22);
      Serial.println("Initializing DHT22 sensor...");
      dht->begin();
      break;
    case SENSOR_SHT20:
      // Initialize the SHT20 sensor
      Serial.println("Initializing SHT20 sensor...");
      sht20.begin();
      Serial.println("SHT20 sensor initialized.");
      break;
    case SENSOR_AHT20:
      // Initialize the AHT20 sensor
      Serial.println("Initializing AHT20 sensor...");
      if (!aht.begin()) {
        Serial.println("Failed to initialize AHT20!");
      } else {
        Serial.println("AHT20 sensor initialized.");
      }
      break;
  }
}

void setupAccessPoint() {
  const char* apSSID = "ESP_AP";
  const char* apPassword = "12345678";
  IPAddress apIP(192, 168, 4, 1);
  IPAddress netMsk(255, 255, 255, 0);

  WiFi.softAP(apSSID, apPassword);
  WiFi.softAPConfig(apIP, apIP, netMsk);
  dnsServer.start(DNS_PORT, "*", apIP);

  server.on("/", HTTP_GET, [apIP]() {
    server.send(302, "text/html",  generateConfigPage());
  });

  server.on("/config", HTTP_GET, [apIP]() {
    server.send(200, "text/html", generateConfigPage());
  });

  server.on("/generate_204", HTTP_GET, [apIP]() {
    server.send(302, "text/html",  generateConfigPage());
  });

  server.on("/hotspot-detect.html", HTTP_GET, [apIP]() {
    server.send(302, "text/html",  generateConfigPage());
  });

  server.onNotFound([apIP]() {
    server.send(302, "text/html",  generateConfigPage());
  });

  server.begin();
}

void setupMQTT() {
  client.setServer(mqttServer, mqttPort);
  snprintf(tempTopic, sizeof(tempTopic), "%s/temperature", mqttTopicBase, deviceId_network);
  snprintf(humidityTopic, sizeof(humidityTopic), "%s/humidity", mqttTopicBase, deviceId_network);
  snprintf(moistureTopic, sizeof(moistureTopic), "%s/moisture", mqttTopicBase, deviceId_network);
  snprintf(alarmTopic, sizeof(alarmTopic), "%s/alarm", mqttTopicBase, deviceId_network);
}

void setupOTA() {
  ArduinoOTA.begin();
  // Add OTA update safeguards here (e.g., checksum verification)
}

void setupWebServer() {
  server.on("/", HTTP_GET, []() {
    server.send(200, "text/html", generateHTMLStatus());
  });
  server.on("/config", HTTP_GET, []() {
    server.send(200, "text/html", generateConfigPage());
  });
  server.on("/updateConfig", HTTP_POST, []() {
    updateConfig();
    server.sendHeader("Location", "/config", true);
    server.send(302, "text/plain", "");
    delay(3000);
    ESP.restart();
  });
  server.on("/refresh", HTTP_GET, []() {
    sampleSensors();                                      // Trigger sensor sampling
    server.send(200, "text/html", generateHTMLStatus());  // Send updated status
  });

  server.begin();
}

String generateHTMLStatus() {
  return "<html><head><style>"
         "body {font-family: 'Arial', sans-serif; background-color: #e9eff1; text-align: center; margin: 0; padding: 20px;}"
         "h1 {color: #2a3f54; font-size: 2.5em; margin-bottom: 0.5em;}"
         ".sensor-data {background: #ffffff; border-radius: 10px; box-shadow: 0 4px 8px rgba(0,0,0,0.1); padding: 20px; margin: 20px auto; width: fit-content;}"
         "p {color: #555; font-size: 1.2em;}"
         "button, a {background-color: #4caf50; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px; cursor: pointer; border: none; font-size: 1em; transition: background-color 0.3s;}"
         "button:hover, a:hover {background-color: #388e3c;}"
         "</style></head><body>"
         "<h1>Environment Sensor Dashboard</h1>"
         "<div class='sensor-data'>"
         "<p>Temperature: <strong>" + String(lastSensorData.temperature) + " °C</strong></p>"
         "<p>Humidity: <strong>" + String(lastSensorData.humidity) + " %</strong></p>"
         "<p>Soil Moisture: <strong>" + String(lastSensorData.moisture) + " %</strong></p>"
         "</div>"
         "<button onclick=\"window.location.href='/refresh'\">Refresh</button><br><br>"
         "<a href=\"/config\">Configure</a>"
         "</body></html>";
}

String generateConfigPage() {
  return "<html><head><title>Device Configuration</title><style>"
         "body {font-family: 'Arial', sans-serif; background-color: #f8f9fa; text-align: center; padding: 20px;}"
         "form {background-color: #fff; padding: 20px; border-radius: 10px; box-shadow: 0 4px 8px rgba(0,0,0,0.1); max-width: 500px; margin: 20px auto;}"
         "input[type='text'], input[type='password'], input[type='number'], select {padding: 12px; margin: 10px 0; border: 1px solid #ddd; border-radius: 5px; width: calc(100% - 24px);}"
         "input[type='button'] {background-color: #28a745; color: white; padding: 12px 20px; border: none; border-radius: 5px; cursor: pointer; font-size: 1em; transition: background-color 0.3s;}"
         "input[type='button']:hover {background-color: #218838;}"
         "h2 {color: #2a3f54; font-size: 2em; margin-bottom: 0.5em;}"
         "label {display: block; margin-top: 20px; font-size: 1.1em; color: #4a4a4a;}"
         "</style>"
         "<script>"
         "function updateSensorConfig() {"
         "  var sensorType = document.getElementById('sensorType').value;"
         "  var i2cConfig = document.getElementById('i2cConfig');"
         "  var dhtConfig = document.getElementById('dhtConfig');"
         "  i2cConfig.style.display = (sensorType == '3' || sensorType == '4') ? 'block' : 'none';"
         "  dhtConfig.style.display = (sensorType == '1' || sensorType == '2') ? 'block' : 'none';"
         "}"
         "function submitForm() {"
         "  var xhr = new XMLHttpRequest();"
         "  xhr.open('POST', '/updateConfig', true);"
         "  xhr.setRequestHeader('Content-type', 'application/x-www-form-urlencoded');"
         "  var ssid = document.getElementById('ssid').value;"
         "  var pass = document.getElementById('password').value;"
         "  var mqttServer = document.getElementById('mqttServer').value;"
         "  var mqttPort = document.getElementById('mqttPort').value;"
         "  var mqttTopicBase = document.getElementById('mqttTopicBase').value;"
         "  var mqttUser = document.getElementById('mqttUsername').value;"
         "  var mqttPass = document.getElementById('mqttPassword').value;"
         "  var deviceId_network = document.getElementById('deviceId_network').value;"
         "  var sensorType = document.getElementById('sensorType').value;"
         "  var dhtSensorPin = document.getElementById('dhtSensorPin').value;"
         "  var sdaPin = document.getElementById('sdaPin').value;"
         "  var sclPin = document.getElementById('sclPin').value;"
         "  var soilMoistureSensorPin = document.getElementById('soilMoistureSensorPin').value;"
         "  var data = 'ssid=' + ssid + '&password=' + pass + '&mqttServer=' + mqttServer + '&mqttPort=' + mqttPort + '&mqttTopicBase=' + mqttTopicBase + '&mqttUsername=' + mqttUser + '&mqttPassword=' + mqttPass + '&deviceId_network=' + deviceId_network + '&sensorType=' + sensorType + '&dhtSensorPin=' + dhtSensorPin + '&sdaPin=' + sdaPin + '&sclPin=' + sclPin + '&soilMoistureSensorPin=' + soilMoistureSensorPin;"
         "  xhr.send(data);"
         "  xhr.onload = function() {"
         "    alert('Configuration Updated. Rebooting...');"
         "    setTimeout(function(){ location.reload(); }, 3000);"
         "  };"
         "}"
         "document.addEventListener('DOMContentLoaded', function() {"
         "  updateSensorConfig();"
         "}, false);"
         "</script></head><body>"
         "<h2>Device Configuration</h2>"
         "<form>"
         "<label for='ssid'>SSID:</label><input id='ssid' name='ssid' type='text' value='" + String(ssid) + "'><br>"
         "<label for='password'>Password:</label><input id='password' name='password' type='password' value='" + String(password) + "'><br>"
         "<label for='mqttServer'>MQTT Server:</label><input id='mqttServer' name='mqttServer' type='text' value='" + String(mqttServer) + "'><br>"
         "<label for='mqttPort'>MQTT Port:</label><input id='mqttPort' name='mqttPort' type='number' value='" + String(mqttPort) + "'><br>"
         "<label for='mqttTopicBase'>MQTT Topic Base:</label><input id='mqttTopicBase' name='mqttTopicBase' type='text' value='" + String(mqttTopicBase) + "'><br>"
         "<label for='mqttUsername'>MQTT Username:</label><input id='mqttUsername' name='mqttUsername' type='text' value='" + String(mqttUsername) + "'><br>"
         "<label for='mqttPassword'>MQTT Password:</label><input id='mqttPassword' name='mqttPassword' type='password' value='" + String(mqttPassword) + "'><br>"
         "<label for='deviceId_network'>Device ID:</label><input id='deviceId_network' name='deviceId_network' type='text' value='" + String(deviceId_network) + "'><br>"
         "<label for='sensorType'>Sensor Type:</label><select id='sensorType' name='sensorType' onchange='updateSensorConfig()'>"
         "<option value='1'" + (sensorType == SENSOR_DHT11 ? " selected" : "") + ">DHT11</option>"
         "<option value='2'" + (sensorType == SENSOR_DHT22 ? " selected" : "") + ">DHT22</option>"
         "<option value='3'" + (sensorType == SENSOR_SHT20 ? " selected" : "") + ">SHT20</option>"
         "<option value='4'" + (sensorType == SENSOR_AHT20 ? " selected" : "") + ">AHT20</option>"
         "</select><br>"
         "<div id='i2cConfig' style='display:none;'>"
         "I2C SDA Pin: <input id='sdaPin' name='sdaPin' type='number' value='" + String(sdaPin) + "'><br>"
         "I2C SCL Pin: <input id='sclPin' name='sclPin' type='number' value='" + String(sclPin) + "'><br>"
         "</div>"
         "<div id='dhtConfig' style='display:none;'>"
         "DHT Sensor Pin: <input id='dhtSensorPin' name='dhtSensorPin' type='number' value='" + String(dhtSensorPin) + "'><br>"
         "</div>"
         "Soil Moisture Sensor Pin: <input id='soilMoistureSensorPin' name='soilMoistureSensorPin' type='number' value='" + String(soilMoistureSensorPin) + "'><br>"
         "<input type='button' value='Save' onclick='submitForm()'>"
         "</form></body></html>";
}

void updateConfig() {
  bool shouldRestart = false;

  if (server.hasArg("ssid")) {
    String newSSID = server.arg("ssid");
    strncpy(ssid, newSSID.c_str(), sizeof(ssid) - 1);
    ssid[sizeof(ssid) - 1] = '\0';
    EEPROM.put(SSID_ADDR, ssid);
    Serial.println("SSID updated");
  }

  if (server.hasArg("password")) {
    String newPassword = server.arg("password");
    strncpy(password, newPassword.c_str(), sizeof(password) - 1);
    password[sizeof(password) - 1] = '\0';
    EEPROM.put(PASSWORD_ADDR, password);
    Serial.println("Password updated");
  }

  if (server.hasArg("mqttServer")) {
    String newMqttServer = server.arg("mqttServer");
    strncpy(mqttServer, newMqttServer.c_str(), sizeof(mqttServer) - 1);
    mqttServer[sizeof(mqttServer) - 1] = '\0';
    EEPROM.put(MQTT_SERVER_ADDR, mqttServer);
    Serial.println("MQTT Server updated");
  }

  if (server.hasArg("mqttPort")) {
    mqttPort = server.arg("mqttPort").toInt();
    EEPROM.put(MQTT_PORT_ADDR, mqttPort);
    Serial.println("MQTT Port updated");
  }

  if (server.hasArg("mqttUsername")) {
    String newMqttUsername = server.arg("mqttUsername");
    strncpy(mqttUsername, newMqttUsername.c_str(), sizeof(mqttUsername) - 1);
    mqttUsername[sizeof(mqttUsername) - 1] = '\0';
    EEPROM.put(MQTT_USERNAME_ADDR, mqttUsername);
    Serial.println("MQTT Username updated");
  }

  if (server.hasArg("mqttPassword")) {
    String newMqttPassword = server.arg("mqttPassword");
    strncpy(mqttPassword, newMqttPassword.c_str(), sizeof(mqttPassword) - 1);
    mqttPassword[sizeof(mqttPassword) - 1] = '\0';
    EEPROM.put(MQTT_PASSWORD_ADDR, mqttPassword);
    Serial.println("MQTT Password updated");
  }

  if (server.hasArg("deviceId_network")) {
    String newdeviceId_network = server.arg("deviceId_network");
    strncpy(deviceId_network, newdeviceId_network.c_str(), sizeof(deviceId_network) - 1);
    deviceId_network[sizeof(deviceId_network) - 1] = '\0';
    EEPROM.put(DEVICE_ID_ADDR, deviceId_network);
    Serial.println("Device ID updated");
  }

  if (server.hasArg("sensorType")) {
    sensorType = server.arg("sensorType").toInt();
    EEPROM.put(SENSOR_TYPE_ADDR, sensorType);
    Serial.println("Sensor type updated");
    shouldRestart = true;
  }

  if (server.hasArg("dhtSensorPin")) {
    dhtSensorPin = server.arg("dhtSensorPin").toInt();
    EEPROM.put(DHT_SENSOR_PIN_ADDR, dhtSensorPin);
    Serial.println("DHT Sensor Pin updated");
  }

  if (server.hasArg("sdaPin")) {
    sdaPin = server.arg("sdaPin").toInt();
    EEPROM.put(SDA_PIN_ADDR, sdaPin);
    Serial.println("SDA Pin updated");
  }

  if (server.hasArg("sclPin")) {
    sclPin = server.arg("sclPin").toInt();
    EEPROM.put(SCL_PIN_ADDR, sclPin);
    Serial.println("SCL Pin updated");
  }

  if (server.hasArg("soilMoistureSensorPin")) {
    soilMoistureSensorPin = server.arg("soilMoistureSensorPin").toInt();
    EEPROM.put(SOIL_MOISTURE_SENSOR_PIN_ADDR, soilMoistureSensorPin);
    Serial.println("Soil Moisture Sensor Pin updated");
  }
  if (server.hasArg("mqttTopicBase")) {
    String newMqttTopicBase = server.arg("mqttTopicBase");
    strncpy(mqttTopicBase, newMqttTopicBase.c_str(), sizeof(mqttTopicBase) - 1);
    mqttTopicBase[sizeof(mqttTopicBase) - 1] = '\0';
    EEPROM.put(MQTT_TOPIC_BASE_ADDR, mqttTopicBase);
    Serial.println("MQTT Topic Base updated");
  }

  EEPROM.commit();
  printEEPROMContents();
  Serial.println("Configuration updated.");

  if (shouldRestart) {
    Serial.println("Restarting ESP...");
    delay(1000);    // Short delay to allow serial message to be sent
    ESP.restart();  // Restart the ESP
  } else {
    Serial.println("No restart required.");
  }
}

void handleOTA() {
  ArduinoOTA.handle();
}

void handleWebServer() {
  server.handleClient();
}

void handleSensorSampling() {
  if (millis() - lastSampleTime >= sampleInterval) {
    lastSampleTime = millis();
    sampleSensors();
  }
}

void handleMQTTPublishing() {
  if (millis() - lastPublishTime >= publishInterval) {
    lastPublishTime = millis();
    publishAveragedData();
    resetAccumulatedData();
  }
}

void handleMQTTConnection() {
  if (!client.connected()) {
    reconnectMQTT();
  }
  client.loop();
}

void sampleSensors() {
  float currentTemperature = 0.0;
  float currentHumidity = 0.0;

  switch (sensorType) {
    case SENSOR_DHT11:
      // Sample the DHT sensor
      Serial.println("Sampling DHT sensor...");
      currentTemperature = dht->readTemperature();
      currentHumidity = dht->readHumidity();
      Serial.println("DHT sensor sampled.");
      break;
    case SENSOR_DHT22:
      // Sample the DHT sensor
      Serial.println("Sampling DHT sensor...");
      currentTemperature = dht->readTemperature();
      currentHumidity = dht->readHumidity();
      Serial.println("DHT sensor sampled.");
      break;
    case SENSOR_SHT20:
      // Sample the SHT20 sensor
      Serial.println("Sampling SHT20 sensor...");
      currentTemperature = sht20.readTemperature();
      currentHumidity = sht20.readHumidity();
      Serial.println("SHT20 sensor sampled.");
      break;
    case SENSOR_AHT20:
      // Sample the AHT20 sensor
      Serial.println("Sampling AHT20 sensor...");
      sensors_event_t humidity, temp;
      aht.getEvent(&humidity, &temp);
      currentTemperature = temp.temperature;
      currentHumidity = humidity.relative_humidity;
      Serial.println("AHT20 sensor sampled.");
      break;
  }

  int sensorValue = analogRead(soilMoistureSensorPin);

  int currentMoisture = map(sensorValue, AIR_VALUE, WATER_VALUE, 0, 100);
  currentMoisture = constrain(currentMoisture, 0, 100);

  // Ensure valid sensor readings
  if (isnan(currentTemperature) || isnan(currentHumidity)) {
    Serial.println("Failed to read from sensor!");
    return;
  }

  accumulatedTemperature += currentTemperature;
  accumulatedHumidity += currentHumidity;
  accumulatedMoisture += currentMoisture;
  sampleCount++;

  if (checkForAlarm(currentTemperature, currentHumidity)) {
    publishData(currentTemperature, currentHumidity, currentMoisture, true);
    resetAccumulatedData();
  }

  lastSensorData = { currentTemperature, currentHumidity, currentMoisture };
}

bool checkForAlarm(float temperature, float humidity) {
  return humidity > humidityThreshold || abs(temperature - lastSensorData.temperature) > temperatureChangeThreshold;
}

void publishAveragedData() {
  if (sampleCount == 0) return;

  float avgTemperature = accumulatedTemperature / sampleCount;
  float avgHumidity = accumulatedHumidity / sampleCount;
  int avgMoisture = accumulatedMoisture / sampleCount;

  publishData(avgTemperature, avgHumidity, avgMoisture, false);
}

void publishData(float temperature, float humidity, int moisture, bool alarm) {
  client.publish(tempTopic, String(temperature, 1).c_str());
  client.publish(humidityTopic, String(humidity, 1).c_str());
  client.publish(moistureTopic, String(moisture).c_str());
  client.publish(alarmTopic, alarm ? "ALARM!" : "No Stress");
}

void resetAccumulatedData() {
  accumulatedTemperature = 0;
  accumulatedHumidity = 0;
  accumulatedMoisture = 0;
  sampleCount = 0;
}

void reconnectMQTT() {
  if (!client.connected()) {
    Serial.println("Connecting to MQTT...");
    client.setKeepAlive(60);  // Increase keepalive to 60 seconds
    if (client.connect(deviceId_network, mqttUsername, mqttPassword)) {
      Serial.println("MQTT Connected");
      // Subscribe to topics here if necessary
    } else {
      Serial.print("MQTT Connection failed, rc=");
      Serial.println(client.state());
    }
  }
}
