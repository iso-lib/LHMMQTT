using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HiveMQtt.MQTT5.Types;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json.Linq;
using Serilog;

namespace LHMMQTT {
    internal class Sensor {
        public string Name { get; }
        public string StateTopic { get; }
        public DeviceClass DeviceClass { get; }
        public string UniqueId { get; }
        public JObject HaDevice { get; }
        private JObject? _sensor;

        public Sensor(IHardware hdw, ISensor hdwSensor, JObject haDevice) {
            HaDevice = haDevice;
            Name = $"{hdw.Name} {hdwSensor.Name}";
            UniqueId = CalculateUniqueId(HaDevice.GetValue("name")?.ToString() ?? string.Empty, hdw.Name, hdwSensor.Name, hdwSensor.SensorType);
            StateTopic = $"lhmmqtt/{UniqueId}{hdwSensor.Identifier}/state";

            // Figure out the device class and unit of measurement
            if (Enum.TryParse(hdwSensor.SensorType.ToString(), ignoreCase: true, out DeviceClass deviceClass)) {
                DeviceClass = deviceClass;
            }
            else {
                Log.Information($"Unknown SensorType '{hdwSensor.SensorType.ToString()}'");
            }
        }

        public async Task Configure(MQTTClient client) {
            // Only build the sensor once as it shouldn't change during runtime
            if (_sensor == null) {
                _sensor = new JObject();
                _sensor.Add("name", Name);
                _sensor.Add("state_topic", StateTopic);
                if (!DeviceClass.GetSensorClass().Equals("")) {
                    _sensor.Add("device_class", DeviceClass.GetSensorClass());
                }
                _sensor.Add("unit_of_measurement", DeviceClass.GetUnit());
                _sensor.Add("unique_id", UniqueId);
                _sensor.Add("expire_after", Settings.Current.Updates.Delay * 3); // Miss 3 updates and we'll consider the sensor unavailable
                _sensor.Add("device", HaDevice);
            }

            // 确保 _sensor 在这里是非空的，可以使用局部变量来帮助编译器理解
            JObject currentSensor = _sensor!;
            if (currentSensor == null)
            {
                Log.Error($"Configure sensor '{Name}': _sensor is unexpectedly null after initialization attempt.");
                return; // 或者抛出异常
            }

            Log.Information($"Configure sensor '{Name}' ({DeviceClass})");
            await client.Publish($"homeassistant/sensor/{HaDevice.GetValue("name")?.ToString() ?? string.Empty}/{UniqueId}/config",
                currentSensor.ToString());
        }

        public async Task SetValue(MQTTClient client, dynamic value) {
            Log.Information($"Set sensor '{Name}' to value '{String.Format(DeviceClass.GetValueFormat(), value)}'");
            await client.Publish(StateTopic, String.Format(DeviceClass.GetValueFormat(), value), QualityOfService.AtLeastOnceDelivery);
        }

        public static string CalculateUniqueId(string haDeviceName, string hdwName, string hdwSensorName, SensorType sensorType) {
            return
                $"{haDeviceName}_{Regex.Replace(hdwName, @"[^a-zA-Z0-9]", "")}_{Regex.Replace(hdwSensorName, @"[^a-zA-Z0-9]", "")}_{sensorType}";
        }
    }
}