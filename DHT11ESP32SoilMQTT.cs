  /* Code mai https://github.com/cjwsam  (Tama Ausetalia mai Samoa i Norge)

  Feel free to use this code it can be used with any esp32 board BUT its designed to be used with a DHTSoilESP32 
  This code in a nutshell takes humidity, tempreature and soil wetness mesurements
  and publishes it to a MQTT broker using json format (*easy intergration with Home Automation )
  then goes to deep sleep for another 15 min (Your plants wont dehydrate in 15 min lol and deep sleep saves power) 

  Du er velkommen til å bruke denne koden, den kan brukes med alle esp32-kort men den er designet for å bli brukt 
  med en DHTSoilESP32-innsats
  Denne koden i et nøtteskall tar fuktighet, tempreature og jordfuktighetsmålinger og publiserer den til en 
  MQTT-megler ved hjelp av json-format (* enkel intergration med hjemmevirksomhet)
  deretter går den i dvalemodus for 15 min. 
  (Dine planter vil ikke dehydrere i 15 minutter og dyp søvn sparer strøm)

  E mafai ona e fa'aaugaina le nei polokalame i so'o se ESP32 Board ae ave ese le DHT 
  ma le SoilPIN laga o le na sensor e le maua i ESP32 board masagi. 
  O le uiga o le nei polokalame o le asiasiga o le vevela ma le susu o le ea, ma le susu o le eleele 
  ma le uploadiga o le ga data i se MQTT server, pe a uma, e moe le lea mo se 15 minute
  se'i sefeina le charge a le ma'a. ( e le pepee ou laau i se 15 minute) 

  */ 

  #include <WiFi.h>
  #include <PubSubClient.h>
  #include "DHT.h"
  #include <ArduinoJson.h>

  #define uS_TO_S_FACTOR 1000000 // Conversion factor for micro seconds to seconds for deep sleep 
  #define TIME_TO_SLEEP  900 // 900 seconds is 15 min 

  #define MQTT_VERSION MQTT_VERSION_3_1_1

  RTC_DATA_ATTR int bootCount = 0;

  // Wifi: SSID and password
  const char* WIFI_SSID = "Pretty Fly For A WiFi";
  const char* WIFI_PASSWORD = "server13";

  // MQTT: ID, server IP, port, username and password
  const PROGMEM char* MQTT_CLIENT_ID = "DHTSteak0";
  const PROGMEM char* MQTT_SERVER_IP = "192.168.1.6";
  const PROGMEM uint16_t MQTT_SERVER_PORT = 1883;

  // MQTT: topic
  const PROGMEM char* MQTT_SENSOR_TOPIC = "plants/DHTStake0";

  // DHT - D1/GPIO22 type 11
  #define DHTPIN 22
  #define DHTTYPE DHT11

  //Soil Pin 
  const int soilPin = 32;
  static int waterlevel;

  DHT dht(DHTPIN, DHTTYPE);
  WiFiClient wifiClient;
  PubSubClient client(wifiClient);

  // function called to publish the temperature and the humidity *YOU MUST USE ARDUINOJSON v5 LIBs 
  //or youll get an error*
  void publishData(float p_temperature, float p_humidity, float p_soil) {
    
    // create a JSON object
    // doc : https://github.com/bblanchon/ArduinoJson/wiki/API%20Reference
    StaticJsonBuffer<200> jsonBuffer;
    JsonObject& root = jsonBuffer.createObject();
    
    // the data must be converted into a string; a problem sometimes occurs when using floats (too big)...
    root["temperature"] = (String)p_temperature;
    root["humidity"] = (String)p_humidity;
    root["soil"] = (String)p_soil;
    Serial.println("");
    char data[200];
    root.printTo(data, root.measureLength() + 1);
    client.publish(MQTT_SENSOR_TOPIC, data, true);
    yield();
  }

  // function called when a MQTT message arrived
  void callback(char* p_topic, byte* p_payload, unsigned int p_length) {
  }

  void MQTT() {
    // Loop until we're reconnected
    while (!client.connected()) {
      Serial.println();
    Serial.println("Attempting MQTT connection...");
    // Attempt to connect
    if (client.connect(MQTT_CLIENT_ID)) {
      Serial.println();
      Serial.println("MQTT connected");
    } else {
      Serial.print("ERROR: failed, rc=");
      Serial.print(client.state());
      Serial.println("DEBUG: try again in 5 seconds");
      // Wait 5 seconds before retrying
      delay(5000);
    }
    }
  }

void CommsWiFi()
{
      // init the WiFi connection
    Serial.println();
    Serial.print("Connecting to WIFI ");
    Serial.println();
    Serial.println();
    WiFi.mode(WIFI_STA);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

  while(WiFi.status() != WL_CONNECTED)
  {
    delay(1000);
    
   if(WiFi.status() == WL_CONNECTED)
   {
    Serial.println();
    Serial.print("WiFi connected");
    }
  }
}
  /*
  Method to print the reason by which ESP32
  has been awaken from sleep *refer to setup() for y this is important but not nessary once fixed 
  */
  void print_wakeup_reason()
  {
    esp_sleep_wakeup_cause_t wakeup_reason;

    wakeup_reason = esp_sleep_get_wakeup_cause();

    switch(wakeup_reason)
    {
    case ESP_SLEEP_WAKEUP_EXT0 : Serial.println("Wakeup caused by external signal using RTC_IO"); break;
    case ESP_SLEEP_WAKEUP_EXT1 : Serial.println("Wakeup caused by external signal using RTC_CNTL"); break;
    case ESP_SLEEP_WAKEUP_TIMER : Serial.println("Wakeup caused by timer"); break;
    case ESP_SLEEP_WAKEUP_TOUCHPAD : Serial.println("Wakeup caused by touchpad"); break;
    case ESP_SLEEP_WAKEUP_ULP : Serial.println("Wakeup caused by ULP program"); break;
    default : Serial.printf("Wakeup was not caused by deep sleep: %d\n",wakeup_reason); break;
    }
  }
  
  void ReadAndSend(){
    
  if (!client.connected()) 
  {
    MQTT();
  }
  client.loop();
  
   // Reading temperature or humidity takes about 250 milliseconds! SOOO SLOW
    float h = dht.readHumidity();
    float t = dht.readTemperature();

   waterlevel = analogRead(32);
   waterlevel = map(waterlevel, 0, 4095, 0, 1023);
   waterlevel = constrain(waterlevel, 0, 1023);
   float w = waterlevel;
   
    if (isnan(h) || isnan(t) || isnan(w)) {
    Serial.println("ERROR: Failed to read from sensors!");
    return;
    } else {
       publishData(t, h, w);
    }
    
  }

  void setup() {
    
    // init the serial
   Serial.begin(115200);

   // small delays around "big" operations wont hurt if this isnt a time sensitive operation 
   delay(1000);
   
    // init the DHT
    dht.begin();
    
    //start WiFi 
    CommsWiFi();
    Serial.println();
    Serial.println();

   //log the wakeup reason to serial 
   //This is useful coz i ran into problems with brownout kernal panic triggering a reboot outside of my specs 
   //This aparently is a common issue, I found using a usb cable < 60cm on usb 2.0 port or 5V2A charger fixes this 
    print_wakeup_reason();
    Serial.println();
    
    //setup sleep conditions and log 
    esp_sleep_enable_timer_wakeup(TIME_TO_SLEEP * uS_TO_S_FACTOR);
    Serial.println("Setup ESP32 to sleep for every " + String(TIME_TO_SLEEP) + " Seconds");
     
    // init the MQTT connection
    client.setServer(MQTT_SERVER_IP, MQTT_SERVER_PORT);
    client.setCallback(callback);
    
    //read sensors and send data 
    ReadAndSend();
    Serial.println("DATA SENT :)");
    Serial.println("");

    Serial.println("Going to sleep now");
    delay(1000);
    Serial.flush(); 
    esp_deep_sleep_start(); //ZZZZ.... 
    
  }

  void loop() 
  {
    
    //never gonna be called coz it turns on then connects reads sends and then sleeps
    //but if u want an "always on" script put it in here and remove most from setup() 
    //setup() runs once at boot then loop() runs in perpetuity 
    
  }
