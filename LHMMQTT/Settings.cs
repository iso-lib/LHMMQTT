using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Serilog;

namespace LHMMQTT {
    public class AppSettings
    {
        public MQTT MQTT { get; set; } = new MQTT();
        public Updates Updates { get; set; } = new Updates();
        public Sensors Sensors { get; set; } = new Sensors();
        public GeneralSettings General { get; set; } = new GeneralSettings(); // Added for general settings
    }

    public class MQTT
    {
        public MQTT() {
            Hostname = "";
            Port = 1883;
            Username = "";
            Password = "";
        }
        public string Hostname { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class Updates
    {
        public Updates() { 
            Delay = 10;
        }
        public int Delay { get; set; }
    }

    public class Sensors
    {
        public Sensors() {
            CPU = false;
            GPU = false;
            Memory = false;
            Motherboard = false;
            Controller = false;
            Networking = false;
            Storage = false;
        }
        public bool CPU { get; set; }
        public bool GPU { get; set; }
        public bool Memory { get; set; }
        public bool Motherboard { get; set; }
        public bool Controller { get; set; }
        public bool Networking { get; set; }
        public bool Storage { get; set; }
    }

    // Added class for General settings
    public class GeneralSettings
    {
        public bool Startup { get; set; } // For "开机启动"
        public bool TrayIcon { get; set; } // For "最小化到托盘"
    }

    public static class Settings
    {
        public static AppSettings? Current { get; set; } // Changed private set to public set

        public static bool LoadFromConfig() {
            // Read contents of config.yaml
            string yaml = String.Empty;
            try {
                yaml = File.ReadAllText("config.yaml");
            } catch (Exception err) {
                Log.Fatal($"Failed to load config.yaml: {err.Message}");
                return false;
            }

            // Attempt to parse YAML into settings values
            try {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(PascalCaseNamingConvention.Instance)
                    .Build();
                var config = deserializer.Deserialize<Dictionary<string, AppSettings>>(yaml);

                Current = config["AppSettings"];
            } catch (Exception err) {
                Log.Fatal($"Failed to parse configuration values: {err.Message}");
                return false;
            }

            return true;
        }
    }
}