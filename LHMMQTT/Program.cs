using System.Diagnostics;
using LHMMQTT;
using LibreHardwareMonitor.Hardware;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LHMMQTT {
    // The main entry point is now App.xaml.cs
    // This Program.cs can be kept for utility functions or removed if not needed.
    // For now, we will keep the doUpdate logic here, but it needs to be callable from the GUI / background service.

    public static class MqttUpdateService {
        private static Device? _hardwareDevice;
        private static MQTTClient? _mqttClient;
        private static bool _isRunning = false;
        private static CancellationTokenSource? _cancellationTokenSource;
        private static Task? _updateLoopTask; // Added to track the DoUpdateLoop task

        // Returns true if service started successfully, false otherwise.
        public static async Task<bool> StartService(Device hardware) {
            if (_isRunning) {
                Log.Information("MqttUpdateService.StartService: Service is already running.");
                return true;
            }

            _hardwareDevice = hardware;
            _mqttClient = new MQTTClient();

            _cancellationTokenSource = new CancellationTokenSource();
            Log.Information("Attempting to start MQTT Update Service...");
            
            // Store the task for DoUpdateLoop
            _updateLoopTask = DoUpdateLoop(_cancellationTokenSource.Token);
            
            // Give some time for initial connection and sensor setup within DoUpdateLoop
            // This relies on DoUpdateLoop setting _isRunning to true internally upon successful init.
            try {
                await Task.Delay(5000, _cancellationTokenSource.Token); // Use the token here too
            } catch (OperationCanceledException) {
                Log.Warning("MqttUpdateService.StartService: Startup delay was canceled. Service might not have started.");
                // _isRunning should be false if DoUpdateLoop was cancelled before setting it true
                if (!_isRunning) { // Double check, as DoUpdateLoop might have been cancelled very early
                     if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested) {
                        _cancellationTokenSource.Cancel(); // Ensure it's cancelled
                    }
                    // Cleanup potentially partially started resources if any were created before loop start.
                    // (Currently _mqttClient is created before _updateLoopTask starts)
                    _mqttClient?.Dispose();
                    _mqttClient = null;
                    _hardwareDevice = null; // Device is passed in, not created here.
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                    _updateLoopTask = null; // Clear the task
                    return false;
                }
            }


            if (!_isRunning) {
                Log.Error("MQTT Update Service failed to start properly after initial delay. Check logs (DoUpdateLoop might have failed and set _isRunning to false).");
                // Ensure cancellation if startup failed and loop might be stuck retrying without setting _isRunning
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }
                // Wait for the potentially failed/stuck loop to exit
                if (_updateLoopTask != null) {
                    try {
                        await _updateLoopTask.WaitAsync(TimeSpan.FromSeconds(2)); // Brief wait
                    } catch { /* Ignore exceptions here, just trying to let it terminate */ }
                }
                _mqttClient?.Dispose(); // Clean up client if loop failed
                _mqttClient = null;
                _hardwareDevice = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _updateLoopTask = null;
                return false; // Indicate startup failure
            }
            
            Log.Information("MQTT Update Service confirmed running after initial setup.");
            return true; // Indicate startup success
        }

        public static async Task StopServiceAsync() {
            if (!_isRunning && _cancellationTokenSource == null && _updateLoopTask == null) {
                 Log.Information("MqttUpdateService.StopServiceAsync: Service not running or already stopping.");
                 return;
            }

            Log.Information("Stopping MQTT Update Service...");
            Task? loopTaskToAwait = _updateLoopTask; // Capture task locally
            
            try {
                // 1. Signal cancellation
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested) {
                    Log.Information("MqttUpdateService: Requesting cancellation for DoUpdateLoop.");
                    _cancellationTokenSource.Cancel();
                }
                
                // 2. Wait for DoUpdateLoop to complete
                if (loopTaskToAwait != null && !loopTaskToAwait.IsCompleted) {
                    Log.Information("MqttUpdateService: Waiting for DoUpdateLoop to complete...");
                    try {
                        await loopTaskToAwait.WaitAsync(TimeSpan.FromSeconds(10)); // Wait up to 10 seconds
                        Log.Information("MqttUpdateService: DoUpdateLoop task completed or timed out.");
                    } catch (TimeoutException) {
                        Log.Warning("MqttUpdateService: Timeout waiting for DoUpdateLoop to complete. Proceeding with resource disposal.");
                    } catch (Exception ex) {
                        Log.Error(ex, "MqttUpdateService: Exception while waiting for DoUpdateLoop to complete.");
                    }
                } else if (loopTaskToAwait != null && loopTaskToAwait.IsCompleted) {
                    Log.Information("MqttUpdateService: DoUpdateLoop task was already completed.");
                }


                // 3. Clean up resources (MQTT client, hardware device)
                // MQTT Client cleanup
                if (_mqttClient != null) {
                    Log.Information("MqttUpdateService: Cleaning up MQTT client...");
                        if (_mqttClient.IsConnected()) {
                        Log.Information("MqttUpdateService: Disconnecting MQTT client...");
                                try {
                            await _mqttClient.Disconnect().WaitAsync(TimeSpan.FromSeconds(3)); 
                            Log.Information("MqttUpdateService: MQTT client disconnected.");
                        } catch (TimeoutException) {
                            Log.Warning("MqttUpdateService: Timeout disconnecting MQTT client.");
                    } catch (Exception ex) {
                            Log.Error(ex, "MqttUpdateService: Error during MQTT client disconnect.");
                    }
                    }
                    Log.Information("MqttUpdateService: Disposing MQTT client...");
                    try {
                        _mqttClient.Dispose(); // MQTTClient.Dispose is now more robust
                    } catch (Exception ex) {
                        Log.Error(ex, "MqttUpdateService: Error disposing MQTT client.");
                    }
                }
                
                // Hardware Device cleanup
                if (_hardwareDevice != null) {
                    Log.Information("MqttUpdateService: Disposing hardware device...");
                    try {
                        _hardwareDevice.Dispose();
                    } catch (Exception ex) {
                        Log.Error(ex, "MqttUpdateService: Error disposing hardware device.");
                    }
                }
                
                // Token Source cleanup
                if (_cancellationTokenSource != null) {
                     Log.Information("MqttUpdateService: Disposing CancellationTokenSource.");
                    try {
                        _cancellationTokenSource.Dispose();
                    } catch (Exception ex) {
                        Log.Error(ex, "MqttUpdateService: Error disposing cancellation token source.");
                    }
                }
            }
            catch (Exception ex) {
                Log.Error(ex, "MqttUpdateService: Error during service shutdown process.");
            }
            finally {
                _cancellationTokenSource = null;
                _mqttClient = null;
                _hardwareDevice = null;
                _updateLoopTask = null; // Clear the stored task
                _isRunning = false; // Set service as not running
                Log.Information("MQTT Update Service formally stopped and resources nulled.");
            }
        }

        // 保留同步版本作为向后兼容，但内部使用异步版本
        public static void StopService() {
            Log.Information("MqttUpdateService.StopService (sync): Triggered. Will run StopServiceAsync.");
            try {
                // Task.Run().ConfigureAwait(false).GetAwaiter().GetResult() can also make it sync
                // but just calling the async version and not waiting (fire and forget from a sync method)
                // is what the original code did with Task.Run.
                // If true synchronous stop is needed, this should be Task.Run(...).Wait() or similar,
                // but that would block the caller.
                // For now, keeping behavior of original Task.Run without explicit wait on the task itself.
                Task.Run(async () => await StopServiceAsync()).ConfigureAwait(false);
            }
            catch (Exception ex) {
                Log.Error(ex, "MqttUpdateService.StopService (sync): Error starting async service shutdown task.");
                // Attempt to force state if an error occurs dispatching the async stop
                _isRunning = false; 
            }
        }

        public static bool IsServiceRunning()
        {
            return _isRunning;
        }

        private static async Task DoUpdateLoop(CancellationToken cancellationToken) {
            if (_hardwareDevice == null || _mqttClient == null) {
                Log.Error("DoUpdateLoop: Device or MQTT client not initialized for update loop.");
                // _isRunning = false; // This _isRunning should be set by StartService if init fails.
                                     // If we reach here, StartService will evaluate _isRunning after a delay.
                return;
            }

            // Capture non-nullable references after the null check
            var hardwareDevice = _hardwareDevice;
            var mqttClient = _mqttClient;

            try {
                // Initial setup phase
                Log.Information("DoUpdateLoop: Attempting initial MQTT connection...");
                Log.Information($"DoUpdateLoop: MQTT Client state before Connect(): IsConnected={mqttClient.IsConnected()}, IsDisposed={(typeof(MQTTClient).GetField("_disposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mqttClient) ?? "N/A")}");
                
                // Add a timeout specifically for the Connect operation
                var connectTask = mqttClient.Connect(); 
                try {
                    // Wait for connectTask for a certain period, e.g., 10 seconds
                    // Or use Task.WhenAny with a delay task if you want to handle timeout explicitly
                    await connectTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); 
                    Log.Information("DoUpdateLoop: mqttClient.Connect() call completed.");
                } catch (TimeoutException tex) {
                    Log.Error(tex, "DoUpdateLoop: Timeout during mqttClient.Connect().");
                    _isRunning = false;
                    return;
                } catch (OperationCanceledException oce) {
                    // This might catch cancellation from the CancellationToken passed to WaitAsync
                    Log.Error(oce, "DoUpdateLoop: OperationCanceledException during mqttClient.Connect() or its wait.");
                    _isRunning = false;
                    return; 
                } catch (Exception connectEx) {
                    Log.Error(connectEx, "DoUpdateLoop: Exception directly from mqttClient.Connect() or its wait.");
                    _isRunning = false;
                    return;
                }
                
                Log.Information($"DoUpdateLoop: MQTT Client state after Connect() call: IsConnected={mqttClient.IsConnected()}");

                if (cancellationToken.IsCancellationRequested) { 
                    Log.Information("DoUpdateLoop: Cancellation requested immediately after MQTT connect call."); 
                    // If cancelled, we might not be truly connected or stable.
                    if (mqttClient.IsConnected()) { await mqttClient.Disconnect(); } // Attempt to disconnect if connected.
                    _isRunning = false; // Ensure service isn't marked as running.
                    return; 
                }

                if (!mqttClient.IsConnected()) {
                    Log.Error("DoUpdateLoop: Failed to connect to MQTT broker after Connect() call. Client reports not connected.");
                    _isRunning = false; // Signal startup failure
                    return; 
                }

                Log.Information("DoUpdateLoop: Attempting initial sensor initialization...");
                if (hardwareDevice.HaSensors.Count == 0) { // Assuming HaSensors is the collection to check
                    await hardwareDevice.InitializeSensors(mqttClient); // Assuming mqttClient is still needed here
                    if (cancellationToken.IsCancellationRequested) { Log.Information("DoUpdateLoop: Cancellation requested during sensor init."); return; }
                    if (hardwareDevice.HaSensors.Count == 0) {
                        Log.Error("DoUpdateLoop: Failed to initialize sensors initially.");
                        if (mqttClient.IsConnected()) await mqttClient.Disconnect(); 
                        _isRunning = false; // Signal startup failure
                        return; 
                    }
                }
                Log.Information("DoUpdateLoop: Initial sensor initialization successful.");

                _isRunning = true; // Signal that service is now properly running (startup successful)
                Log.Information("MQTT Update Service (DoUpdateLoop) is now running.");

                Stopwatch stopwatch = new Stopwatch();
                while (!cancellationToken.IsCancellationRequested) {
                    stopwatch.Restart();
                    Log.Information("Updating sensors...");
                    try {
                        // Update and publish sensor data
                        // This call updates the ISensor instances in LibreHardwareMonitor
                        IList<IHardware> updatedHardwareCollection = hardwareDevice.UpdateSensors(); 
                        if (cancellationToken.IsCancellationRequested) break; 

                        List<Task> publishTasks = new List<Task>();

                        foreach (var hdw in updatedHardwareCollection) {
                            if (cancellationToken.IsCancellationRequested) break;
                            foreach (var underlyingSensor in hdw.Sensors) {
                                if (cancellationToken.IsCancellationRequested) break;
                                
                                string uniqueId = Sensor.CalculateUniqueId(
                                    hardwareDevice.Name, 
                                    hdw.Name,             
                                    underlyingSensor.Name, 
                                    underlyingSensor.SensorType 
                                );

                                Sensor? wrapperSensor = hardwareDevice.HaSensors.FirstOrDefault(s => s.UniqueId == uniqueId);

                                if (wrapperSensor != null) {
                                    if (underlyingSensor.Value.HasValue) {
                                        // Add the publish task to a list to be awaited together
                                        publishTasks.Add(wrapperSensor.SetValue(mqttClient, underlyingSensor.Value.Value));
                                    } else {
                                        Log.Debug($"Sensor {uniqueId} has no value. Skipping publish.");
                                    }
                                } else {
                                    // This case should ideally not happen if InitializeSensors correctly populates HaSensors
                                    // for all sensors found by LibreHardwareMonitor.
                                    Log.Warning($"Could not find matching LHMMQTT.Sensor wrapper for uniqueId {uniqueId} derived from hardware scan. It might not have been configured during InitializeSensors, or there's a discrepancy in UniqueId calculation/matching.");
                                }
                            }
                            if (cancellationToken.IsCancellationRequested) break; 
                        }
                        
                        if (cancellationToken.IsCancellationRequested) break; 

                        // Await all publish tasks concurrently
                        if (publishTasks.Any()) {
                            await Task.WhenAll(publishTasks);
                        }
                        
                        Log.Information($"Sensor update and publish took {stopwatch.ElapsedMilliseconds}ms");
                    } catch (Exception ex) {
                        Log.Error(ex, "Failed to update or publish sensor data.");
                        // Consider if retry logic or specific error handling is needed here
                    }

                    try {
                        // Wait for the next update interval, respecting cancellation
                        int updateInterval = Settings.Current?.Updates?.Delay * 1000 ?? 10000;
                        if (updateInterval <= 0) updateInterval = 10000; // Ensure positive delay
                        await Task.Delay(updateInterval, cancellationToken);
                    } catch (OperationCanceledException) {
                        Log.Information("DoUpdateLoop: Task.Delay was canceled. Exiting loop.");
                        break; // Exit loop if delay is cancelled
                    }
                }
                // Loop exited.
                Log.Information($"DoUpdateLoop: Loop terminated. Cancellation requested: {cancellationToken.IsCancellationRequested}.");
                // No explicit resource cleanup (_mqttClient.Disconnect) here. StopServiceAsync handles it.
                // No _isRunning = false here. StopServiceAsync handles it.

                        } catch (OperationCanceledException) {
                Log.Information("DoUpdateLoop: Operation canceled during initial setup or main loop's sensitive operations.");
                _isRunning = false; // If setup was ongoing or an operation couldn't complete due to cancellation.
                } catch (Exception ex) {
                // Enhanced logging for diagnosing silent exceptions
                Log.Fatal("!!!!!!!!!! DoUpdateLoop: Entered general catch block. This indicates an unexpected exception. !!!!!!!!!!");
                try {
                    Log.Fatal($"!!!!!!!!!! Exception Type: {ex.GetType().FullName} !!!!!!!!!!");
                    Log.Fatal($"!!!!!!!!!! Exception Message: {ex.Message} !!!!!!!!!!");
                    if (ex.InnerException != null) {
                        Log.Fatal($"!!!!!!!!!! Inner Exception Type: {ex.InnerException.GetType().FullName} !!!!!!!!!!");
                        Log.Fatal($"!!!!!!!!!! Inner Exception Message: {ex.InnerException.Message} !!!!!!!!!!");
                    }
                    // Log a condensed version of the stack trace if possible, avoiding very long strings if that's an issue for logging.
                    string stackTrace = ex.StackTrace ?? "No stack trace available";
                    Log.Fatal($"!!!!!!!!!! StackTrace (first 500 chars): {stackTrace.Substring(0, Math.Min(stackTrace.Length, 500))} !!!!!!!!!!");
                } catch (Exception logEx) {
                    Log.Fatal($"!!!!!!!!!! CRITICAL: Failed to log exception details: {logEx.Message} !!!!!!!!!!");
                }

                // Original log line that might be failing for the specific exception
                Log.Error(ex, "DoUpdateLoop: Unhandled exception during DoUpdateLoop execution (original log call attempt).");
                
                _isRunning = false; // Signal that the loop/service has critically failed.
                // Attempt to disconnect if an unexpected error occurs and client might be connected
                if (mqttClient != null && mqttClient.IsConnected()) {
                    try {
                        Log.Information("DoUpdateLoop: Attempting emergency disconnect due to unhandled exception.");
                        await mqttClient.Disconnect().WaitAsync(TimeSpan.FromSeconds(1)); // Quick disconnect attempt
                    } catch (Exception disconnectEx) { 
                        Log.Error(disconnectEx, "DoUpdateLoop: Error during emergency disconnect.");
                    }
                }
            } finally {
                Log.Information("DoUpdateLoop: Finalizing.");
                // If _isRunning is true here, it means the loop ran successfully and was asked to stop.
                // If _isRunning is false, it means it failed at some point (startup or during run).
                // The authoritative _isRunning = false for a clean shutdown is done by StopServiceAsync.
            }
        }
    }
}
