#include <Arduino.h>
#include <NimBLEDevice.h>
#include <LittleFS.h>

#define SERVICE_UUID "12345678-1234-1234-1234-123456789abc"
#define CHARACTERISTIC_UUID "abcdefab-1234-1234-1234-abcdefabcdef"

NimBLECharacteristic *pCharacteristic = nullptr;
File ecgFile;

class ServerCallbacks : public NimBLEServerCallbacks
{
    void onConnect(NimBLEServer *pServer) override
    {
        Serial.println("Client conectat!");
        pCharacteristic->setValue((const uint8_t *)"CONNECTED", 9);
    }
    void onDisconnect(NimBLEServer *pServer) override
    {
        Serial.println("Client deconectat!");
        NimBLEDevice::startAdvertising();
    }
};

void setup()
{
    Serial.begin(115200);
    delay(1000);
    Serial.println("START");

    bool fsOk = LittleFS.begin(false);
    Serial.println(fsOk ? "LittleFS OK" : "LittleFS FAIL");

    if (fsOk)
    {
        ecgFile = LittleFS.open("/ecg_ble.txt", "r");
        Serial.println(ecgFile ? "Fisier OK" : "Fisier FAIL");
        if (ecgFile)
            ecgFile.readStringUntil('\n');
    }

    NimBLEDevice::init("ECGMonitor");
    NimBLEServer *pServer = NimBLEDevice::createServer();
    pServer->setCallbacks(new ServerCallbacks());

    NimBLEService *pService = pServer->createService(SERVICE_UUID);
    pCharacteristic = pService->createCharacteristic(
        CHARACTERISTIC_UUID,
        NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::NOTIFY | NIMBLE_PROPERTY::INDICATE);
    pService->start();

    NimBLEAdvertising *pAdvertising = NimBLEDevice::getAdvertising();
    NimBLEAdvertisementData advData;
    advData.setName("ECGMonitor");
    advData.setCompleteServices(NimBLEUUID(SERVICE_UUID));
    pAdvertising->setAdvertisementData(advData);
    NimBLEAdvertisementData scanData;
    scanData.setName("ECGMonitor");
    pAdvertising->setScanResponseData(scanData);
    pAdvertising->start();

    Serial.println("BLE pornit!");
}

void loop()
{
    if (!ecgFile || !ecgFile.available())
    {
        if (ecgFile)
        {
            ecgFile.seek(0);
            ecgFile.readStringUntil('\n');
        }
        delay(100);
        return;
    }

    String line = ecgFile.readStringUntil('\n');
    line.trim();

    if (line.length() > 0)
    {
        pCharacteristic->setValue((const uint8_t *)line.c_str(), line.length());
        pCharacteristic->notify();
        Serial.println("Trimis: " + line.substring(0, 15));
    }

    delay(8);
}