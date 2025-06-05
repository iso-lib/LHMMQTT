using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json.Linq;
using Serilog;

namespace LHMMQTT {
    public class Device : IDisposable {
        private Computer? _computer;
        private bool _disposed = false;
        public string Name { get; }
        public string Identifier { get; }
        internal HashSet<Sensor>? HaSensors;

        private JObject? _haDevice;
        

        public Device() {
            if (Settings.Current == null || Settings.Current.Sensors == null)
            {
                throw new InvalidOperationException("Sensor settings are not loaded or are incomplete. Please configure the application first.");
            }

            HaSensors = new HashSet<Sensor>();

            Name = getSafeName();
            Identifier = Name; //TODO Better way to identify the device?

            // Use config to determine what sensors we're interested in
            _computer = new Computer {
                IsCpuEnabled = Settings.Current.Sensors.CPU,
                IsGpuEnabled = Settings.Current.Sensors.GPU,
                IsMemoryEnabled = Settings.Current.Sensors.Memory,
                IsMotherboardEnabled = Settings.Current.Sensors.Motherboard,
                IsControllerEnabled = Settings.Current.Sensors.Controller,
                IsNetworkEnabled = Settings.Current.Sensors.Networking,
                IsStorageEnabled = Settings.Current.Sensors.Storage
            };

            _computer.Open();

            // Create an MQTT device (the computer) so that all sensors can be attached to it
            _haDevice = new JObject();
            _haDevice.Add("name", Name);
            _haDevice.Add("identifiers", new JArray { Identifier });
            _haDevice.Add("model", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture.ToString()})");
            _haDevice.Add("manufacturer", "LHMMQTT");

            // Make sure the sensors are updated on creation or the values will be incorrect and/or missing
            UpdateSensors();
        }

        // 实现Dispose模式
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 释放托管资源
                    if (_computer != null)
                    {
                        try
                        {
                            // 先尝试停止所有可能运行的硬件监控更新操作
                            foreach (IHardware hardware in _computer.Hardware) 
                            {
                                try 
                                {
                                    // IHardware接口没有Close方法，暂时移除
                                    // hardware.Close();
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, $"Error handling hardware: {hardware.Name}");
                                }
                            }
                            
                            // 然后关闭Computer对象
                            _computer.Close();
                            Log.Information("Hardware monitoring resources closed");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error closing computer hardware resources");
                        }
                        
                        // 确保Computer对象被垃圾回收
                        _computer = null;
                    }

                    // 清空传感器集合
                    if (HaSensors != null)
                    {
                        HaSensors.Clear();
                        HaSensors = null;
                    }
                    
                    // 清空其他资源
                    _haDevice = null;
                }

                // 释放非托管资源（如果有）
                _disposed = true;
                
                // 强制垃圾回收
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        // 保留析构函数作为安全网
        ~Device() {
            Dispose(false);
        }

        public async Task InitializeSensors(MQTTClient client) {
            // MQTT client is now passed as a parameter
            // Ensure client is connected before proceeding
            if (!client.IsConnected()) {
                await client.Connect();
            }

            // Iterate over hardware sensors and initialize them
            if (_computer != null) // Add null check for _computer
            {
                foreach (IHardware hdw in _computer.Hardware) {
                    foreach (ISensor hdwSensor in hdw.Sensors) {
                        Sensor sensor = new Sensor(hdw, hdwSensor, _haDevice!);
                        await sensor.Configure(client);
                        HaSensors?.Add(sensor);
                    }
                }
            }

            // Disconnection should be handled by the caller or when the service stops
        }

        public IList<IHardware> UpdateSensors() {
            if (_disposed)
            {
                Log.Warning("Attempted to update sensors on disposed Device");
                return new List<IHardware>(); // 返回空列表
            }
            
            // Get updated sensors
            if (_computer != null)
            {
                _computer.Accept(new UpdateVisitor());
                return _computer.Hardware;
            }
            else
            {
                Log.Warning("Attempted to update sensors on a null Computer object.");
                return new List<IHardware>();
            }
        }

        public void ReinitializeComputer() {
            if (_disposed)
            {
                Log.Warning("Attempted to reinitialize computer on disposed Device");
                return;
            }
            
            if (_computer != null) // Explicit null check for CS8602 on line 162
            {
                _computer.Close(); // Close existing instance if open
            }
            _computer = new Computer {
                IsCpuEnabled = Settings.Current.Sensors.CPU,
                IsGpuEnabled = Settings.Current.Sensors.GPU,
                IsMemoryEnabled = Settings.Current.Sensors.Memory,
                IsMotherboardEnabled = Settings.Current.Sensors.Motherboard,
                IsControllerEnabled = Settings.Current.Sensors.Controller,
                IsNetworkEnabled = Settings.Current.Sensors.Networking,
                IsStorageEnabled = Settings.Current.Sensors.Storage
            };
            _computer.Open();
            HaSensors?.Clear(); // Clear old sensors, they will be re-initialized by MqttUpdateService
            Log.Information("Computer hardware reinitialized based on new sensor settings.");
        }

        string getSafeName() {
            return Regex.Replace(System.Environment.MachineName!, @"[^a-zA-Z0-9]", "");
        }
    }
}
