using System;
using System.Threading.Tasks;
using System.Windows;
using Serilog;

namespace LHMMQTT {
    public partial class App : System.Windows.Application {
        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);

            // Configure logger
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .WriteTo.File("logs/log-.txt",
                    rollingInterval: RollingInterval.Day)
                .MinimumLevel.Debug()
                .CreateLogger();

            // Make sure logger is closed when application exits
            Current.Exit += OnApplicationExit;
            Current.SessionEnding += OnSessionEnding;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            // Load app settings for configuration values
            if (!Settings.LoadFromConfig()) {
                Log.Information("Please correct issues with config before running again!");
                // Optionally, show a message to the user or open the config window directly
                // For now, we'll let the MainWindow handle showing an error or default values
            }

            // The main MQTT logic will now be started from MainWindow or a dedicated service class
            // For simplicity in this GUI conversion, we'll assume the core logic might be triggered
            // after settings are confirmed or on a button press in the GUI.
        }
        
        private void OnApplicationExit(object? sender, ExitEventArgs e)
        {
            EnsureCleanShutdown();
        }
        
        private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
        {
            EnsureCleanShutdown();
        }
        
        private void OnProcessExit(object? sender, EventArgs e)
        {
            EnsureCleanShutdown();
        }
        
        private void EnsureCleanShutdown()
        {
            try
            {
                // 确保服务停止
                if (MqttUpdateService.IsServiceRunning())
                {
                    Log.Information("Stopping MQTT service on application exit...");
                    Task.Run(async () => {
                        try {
                            await MqttUpdateService.StopServiceAsync();
                        }
                        catch (Exception ex) {
                            Log.Error(ex, "Error stopping service during application shutdown");
                        }
                    }).Wait(2000); // 给2秒时间停止服务
                }
                
                // 确保垃圾回收
                GC.Collect();
                GC.WaitForPendingFinalizers();
                
                // 关闭日志
                Log.CloseAndFlush();
            }
            catch (Exception ex)
            {
                // 尝试记录异常，但此时可能日志系统已经关闭
                try {
                    Log.Error(ex, "Error during application shutdown cleanup");
                }
                catch { }
            }
        }
    }
}