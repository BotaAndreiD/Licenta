#include <Arduino.h>
#include <NimBLEDevice.h>
#include <LittleFS.h>

#define SERVICE_UUID        "12345678-1234-1234-1234-123456789abc"
#define CHARACTERISTIC_UUID "abcdefab-1234-1234-1234-abcdefabcdef"

NimBLECharacteristic* pCharacteristic = nullptr;
bool deviceConnected = false;
File ecgFile;

class ServerCallbacks : public NimBLEServerCallbacks {
    void onConnect(NimBLEServer* pServer) override {
        deviceConnected = true;
        Serial.println("Client conectat!");
    }
    void onDisconnect(NimBLEServer* pServer) override {
        deviceConnected = false;
        Serial.println("Client deconectat!");
        NimBLEDevice::startAdvertising();
    }
};

void setup() {
    Serial.begin(115200);
    delay(3000);
    Serial.println("=== START ===");

    if (!LittleFS.begin(true)) {
        Serial.println("Eroare LittleFS!");
        return;
    }
    Serial.println("LittleFS OK");

    ecgFile = LittleFS.open("/ecg_ble.txt", "r");
    if (!ecgFile) {
        Serial.println("Fisier nu gasit!");
        return;
    }
    Serial.println("Fisier ECG deschis!");
    ecgFile.readStringUntil('\n');

    NimBLEDevice::init("ECGMonitor");
    NimBLEServer* pServer = NimBLEDevice::createServer();
    pServer->setCallbacks(new ServerCallbacks());

    NimBLEService* pService = pServer->createService(SERVICE_UUID);
    pCharacteristic = pService->createCharacteristic(
        CHARACTERISTIC_UUID,
        NIMBLE_PROPERTY::NOTIFY
    );
    pService->start();

    NimBLEAdvertising* pAdvertising = NimBLEDevice::getAdvertising();
    pAdvertising->addServiceUUID(SERVICE_UUID);
    pAdvertising->start();

    Serial.println("BLE pornit — ECGMonitor");
}
void loop() {
    if (!deviceConnected) {
        delay(100);
        return;
    }

    if (!ecgFile.available()) {
        ecgFile.seek(0);
        ecgFile.readStringUntil('\n');
        return;
    }

    String line = ecgFile.readStringUntil('\n');
    line.trim();

    if (line.length() > 0) {
        pCharacteristic->setValue(line.c_str());
        pCharacteristic->notify();
        Serial.println(line);
    }

    delay(8); // 128Hz
}