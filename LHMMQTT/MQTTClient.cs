using HiveMQtt.Client;
using HiveMQtt.Client.Options;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LHMMQTT {
    public class MQTTClient : IDisposable {
        private HiveMQClient _client;
        private HiveMQClientOptions _connectionOptions = new HiveMQClientOptions();
        private bool _disposed = false;

        public MQTTClient() {
            if (Settings.Current == null || Settings.Current.MQTT == null)
            {
                throw new InvalidOperationException("MQTT settings are not loaded or are incomplete. Please configure the application first.");
            }

            _connectionOptions.Host = Settings.Current.MQTT.Hostname;
            _connectionOptions.Port = Settings.Current.MQTT.Port;
            _connectionOptions.UserName = Settings.Current.MQTT.Username;
            _connectionOptions.Password = Settings.Current.MQTT.Password;

            _client = new HiveMQClient(_connectionOptions);
        }

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
                    if (_client != null && _client.IsConnected())
                    {
                        try
                        {
                            // 使用后台任务断开连接，避免阻塞当前线程
                            Task.Run(async () => {
                                try {
                                    await _client.DisconnectAsync().ConfigureAwait(false);
                                    Log.Information("MQTT client disconnected during disposal");
                                } catch (Exception ex) {
                                    Log.Error(ex, "Error during async MQTT disconnect in disposal");
                                }
                            });
                            // 给定很短的时间让断开连接操作开始
                            Thread.Sleep(50);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error initiating MQTT client disconnect during disposal");
                        }
                    }
                }

                _disposed = true;
            }
        }
        
        ~MQTTClient()
        {
            Dispose(false);
        }

        public bool IsConnected() {
            if (_disposed) return false;
            return _client.IsConnected();
        }

        public async Task Connect() {
            if (_disposed)
            {
                Log.Warning("Attempted to connect with disposed MQTT client");
                return;
            }
            
            if (!_client.IsConnected()) {
                await _client.ConnectAsync();
            }
        }

        public async Task Disconnect() {
            if (_disposed) return;
            
            if (_client.IsConnected()) {
                await _client.DisconnectAsync();
            }
        }

        public async Task Publish(string topic, string payload) {
            if (_disposed)
            {
                Log.Warning("Attempted to publish with disposed MQTT client");
                return;
            }
            
            await Publish(topic, payload, HiveMQtt.MQTT5.Types.QualityOfService.ExactlyOnceDelivery);
        }

        public async Task Publish(string topic, string payload, HiveMQtt.MQTT5.Types.QualityOfService qos) {
            if (_disposed)
            {
                Log.Warning("Attempted to publish with disposed MQTT client");
                return;
            }
            
            await _client.PublishAsync(topic, payload, qos);
        }
    }
}
