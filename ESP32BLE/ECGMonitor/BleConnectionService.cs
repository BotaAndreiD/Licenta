using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ECGMonitor
{
    public static class BleConnectionService
    {
        private const string ServiceUuid = "12345678-1234-1234-1234-123456789abc";
        private const string DeviceName = "ECGMonitor";

        public static BluetoothLEDevice? Device { get; private set; }
        public static GattCharacteristic? Characteristic { get; private set; }

        public static async Task<bool> ConnectAsync(Action<string> onStatus)
        {
            try
            {
                onStatus("Se scanează BLE...");

                var tcs = new TaskCompletionSource<ulong>();
                var watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active,
                };

                watcher.Received += (w, args) =>
                {
                    string name = args.Advertisement.LocalName;
                    if (string.IsNullOrEmpty(name))
                    {
                        foreach (var s in args.Advertisement.DataSections)
                        {
                            if (s.DataType == 0x09)
                            {
                                var reader = DataReader.FromBuffer(s.Data);
                                name = reader.ReadString(s.Data.Length);
                            }
                        }
                    }
                    if (name == DeviceName)
                        tcs.TrySetResult(args.BluetoothAddress);
                };

                watcher.Start();
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(15000));
                watcher.Stop();

                if (completed != tcs.Task)
                {
                    onStatus($"{DeviceName} negăsit în 15 secunde.");
                    return false;
                }

                ulong address = tcs.Task.Result;
                onStatus("Dispozitiv găsit. Conectare...");
                await Task.Delay(1000);

                var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (device == null)
                {
                    onStatus("Nu s-a putut obține dispozitivul.");
                    return false;
                }
                Device = device;

                onStatus("Se descoperă serviciile...");
                var allServices = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                if (allServices.Status != GattCommunicationStatus.Success)
                {
                    onStatus($"Servicii negăsite: {allServices.Status}");
                    return false;
                }

                GattDeviceService? targetService = null;
                foreach (var svc in allServices.Services)
                {
                    if (svc.Uuid.ToString() == ServiceUuid)
                    {
                        targetService = svc;
                        break;
                    }
                }

                if (targetService == null)
                {
                    onStatus("Serviciu ECG negăsit.");
                    return false;
                }

                var charsResult = await targetService.GetCharacteristicsAsync(
                    BluetoothCacheMode.Uncached
                );
                if (
                    charsResult.Status != GattCommunicationStatus.Success
                    || charsResult.Characteristics.Count == 0
                )
                {
                    onStatus($"Caracteristici negăsite: {charsResult.Status}");
                    return false;
                }

                var characteristic = charsResult.Characteristics[0];

                onStatus("Se activează notificările...");
                await characteristic.GetDescriptorsForUuidAsync(
                    GattDescriptorUuids.ClientCharacteristicConfiguration,
                    BluetoothCacheMode.Uncached
                );

                await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify
                );

                Characteristic = characteristic;
                onStatus("Conectat — semnal live.");
                return true;
            }
            catch (Exception ex)
            {
                onStatus($"Eroare: {ex.Message}");
                return false;
            }
        }
    }
}
