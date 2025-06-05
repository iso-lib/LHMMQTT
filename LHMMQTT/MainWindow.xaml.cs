using System.Windows;
using Microsoft.Win32;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Windows.Forms; // Required for NotifyIcon
using System.Drawing; // Required for Icon
using System.Threading.Tasks; // Required for Task
using Serilog; // For logging
using System.Windows.Controls; // Required for TextBlock
using System.Windows.Navigation; // Required for Hyperlink
using System.Diagnostics; // Required for Process.Start
using System.Windows.Threading; // Required for DispatcherTimer
using System.Runtime.InteropServices; // Required for DllImport
using System.Windows.Interop; // Required for WindowInteropHelper

namespace LHMMQTT {
    public partial class MainWindow : Window {
        private Device? _hardwareDevice;
        private NotifyIcon? _notifyIcon;
        private TextBlock? _serviceStatusIndicator;
        
        // 添加静态变量跟踪最后一次服务停止的时间
        private static DateTime? _lastServiceStopTime = null;
        // 添加所需的冷却时间（毫秒）
        private static readonly int _serviceStopCooldownMs = 5000; // 设置为5秒
        // 添加标志标识服务是否正在停止中
        private bool _isServiceStopping = false;
        // 添加定时器用于倒计时
        private DispatcherTimer? _cooldownTimer;

        // Win32 API 引用
        private const int MF_BYCOMMAND = 0x00000000;
        private const int MF_ENABLED = 0x00000000;
        private const int MF_GRAYED = 0x00000001;
        private const int SC_CLOSE = 0xF060;

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        private IntPtr _windowHandle;
        private IntPtr _systemMenuHandle;

        public MainWindow() {
            InitializeComponent();
            LoadSettings();
            LoadStartupSetting();

            // 初始化冷却计时器
            InitializeCooldownTimer();

            // Find the status indicator TextBlock from XAML
            // This assumes you will add a TextBlock named ServiceStatusIndicator in your XAML
            _serviceStatusIndicator = FindName("ServiceStatusIndicator") as TextBlock;
            UpdateServiceStatusIndicator(); // Initial status update

            SaveMqttButton.Click += SaveMqttButton_Click;
            SaveSensorsButton.Click += SaveSensorsButton_Click;
            StartupCheckBox.Checked += StartupCheckBox_Changed;
            StartupCheckBox.Unchecked += StartupCheckBox_Changed;
            TrayIconCheckBox.Checked += TrayIconCheckBox_Changed;
            TrayIconCheckBox.Unchecked += TrayIconCheckBox_Changed;

            // Initialize Device and NotifyIcon
            _hardwareDevice = new Device();
            InitializeNotifyIcon();

            // Initialize button states
            StartMqttServiceButton.Content = "启动 LHMMQTT 服务";
            StopMqttServiceButton.IsEnabled = false; // Initially, stop button is disabled

            // Assign click events from XAML if not already assigned, or ensure they are correct
            // StartMqttServiceButton.Click += StartStopMqttServiceButton_Click; // Already in XAML
            // StopMqttServiceButton.Click += StopMqttServiceButton_Click; // Already in XAML

            this.StateChanged += MainWindow_StateChanged;
            this.Closing += MainWindow_Closing;
            this.SourceInitialized += MainWindow_SourceInitialized; // 获取窗口句柄
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            _systemMenuHandle = GetSystemMenu(_windowHandle, false);
        }

        private void DisableCloseButton()
        {
            if (_systemMenuHandle != IntPtr.Zero)
            {
                EnableMenuItem(_systemMenuHandle, SC_CLOSE, MF_BYCOMMAND | MF_GRAYED);
            }
        }

        private void EnableCloseButton()
        {
            if (_systemMenuHandle != IntPtr.Zero)
            {
                EnableMenuItem(_systemMenuHandle, SC_CLOSE, MF_BYCOMMAND | MF_ENABLED);
            }
        }

        private void InitializeNotifyIcon() {
            _notifyIcon = new NotifyIcon();
            // _notifyIcon.Icon = SystemIcons.Application; // Or a custom icon
            // To use the SVG icon, it needs to be converted to an .ico file first.
            // For example, using an online converter or a tool like Inkscape.
            // Once converted to 'icon.ico' and placed in Resources folder:
            try {
                _notifyIcon.Icon = new System.Drawing.Icon("Resources/icon.ico");
            }
            catch (System.Exception ex) {
                Log.Error(ex, "Failed to load tray icon from Resources/icon.ico. Ensure the file exists and is a valid .ico file.");
                // Optionally, set a default icon or leave it null if critical, but for now, we'll log and proceed.
                // If _notifyIcon.Icon remains null, it might not show, or show a default system icon depending on OS.
                // Forcing a visible default if custom fails:
                _notifyIcon.Icon = SystemIcons.Application; // Fallback to a generic system icon if custom load fails
            }
            _notifyIcon.Visible = false;
            _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("显示", null, ShowWindow_Click);
            contextMenu.Items.Add("退出", null, Exit_Click);
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private async void StartStopMqttServiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (!MqttUpdateService.IsServiceRunning())
            {
                if (_hardwareDevice != null && Settings.Current != null)
                {
                    bool serviceStartedSuccessfully = false;
                    try
                    {
                        // 更新服务状态指示器为"正在启动服务"
                        if (_serviceStatusIndicator != null)
                        {
                            _serviceStatusIndicator.Text = "正在启动服务，请稍后...";
                            _serviceStatusIndicator.Foreground = System.Windows.Media.Brushes.Orange;
                        }
                        
                        // 禁用按钮，防止重复点击
                        StartMqttServiceButton.IsEnabled = false;
                        
                        _hardwareDevice.ReinitializeComputer(); 
                        serviceStartedSuccessfully = await MqttUpdateService.StartService(_hardwareDevice); 

                        if (serviceStartedSuccessfully && MqttUpdateService.IsServiceRunning()) 
                        {
                            StartMqttServiceButton.Content = "LHMMQTT 服务运行中";
                            StartMqttServiceButton.IsEnabled = false;
                            StopMqttServiceButton.IsEnabled = true;
                            Log.Information("LHMMQTT Service started by user.");
                        }
                        else
                        {
                            System.Windows.MessageBox.Show("服务未能成功启动。请检查日志获取更多信息。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                            StartMqttServiceButton.Content = "启动 LHMMQTT 服务";
                            StartMqttServiceButton.IsEnabled = true;
                            StopMqttServiceButton.IsEnabled = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to start MQTT Service from GUI");
                        System.Windows.MessageBox.Show($"启动服务时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        StartMqttServiceButton.Content = "启动 LHMMQTT 服务";
                        StartMqttServiceButton.IsEnabled = true;
                        StopMqttServiceButton.IsEnabled = false;
                    }
                    finally
                    {
                        UpdateServiceStatusIndicator(); 
                    }
                }
                else
                {
                    System.Windows.MessageBox.Show("无法启动服务：硬件设备或配置未正确初始化。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateServiceStatusIndicator(); 
                }
            }
            else
            {
                // This case implies service is already running according to MqttUpdateService.IsServiceRunning()
                // So, ensure UI reflects this state correctly.
                StartMqttServiceButton.Content = "LHMMQTT 服务运行中";
                StartMqttServiceButton.IsEnabled = false;
                StopMqttServiceButton.IsEnabled = true;
                System.Windows.MessageBox.Show("服务已经在运行中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateServiceStatusIndicator();
            }
        }

        private async void StopMqttServiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (MqttUpdateService.IsServiceRunning())
            {
                // 标记服务正在停止中
                _isServiceStopping = true;
                
                // 禁用关闭按钮
                DisableCloseButton();

                // 添加UI反馈 - 仅禁用按钮，不改变文字
                StopMqttServiceButton.IsEnabled = false;
                StartMqttServiceButton.IsEnabled = false; // 确保启动按钮在停止过程中禁用
                
                // 更新服务状态指示器
                if (_serviceStatusIndicator != null)
                {
                    _serviceStatusIndicator.Text = "正在停止服务，请稍等...";
                    _serviceStatusIndicator.Foreground = System.Windows.Media.Brushes.Orange;
                }
                
                try
                {
                    // 在后台线程中异步停止服务
                    await Task.Run(async () => {
                        await MqttUpdateService.StopServiceAsync();
                    });
                    
                    // 记录服务停止的确切时间
                    _lastServiceStopTime = DateTime.Now;
                    Log.Information($"Service stop time recorded: {_lastServiceStopTime}");
                    
                    // 启动冷却计时器
                    _cooldownTimer?.Start();
                    
                    // 修改这里：在冷却期结束前不启用启动按钮
                    StartMqttServiceButton.Content = "启动 LHMMQTT 服务";
                    
                    // 停止按钮保持禁用状态
                    StopMqttServiceButton.IsEnabled = false;
                    Log.Information("LHMMQTT Service stopped by user.");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to stop MQTT Service from GUI");
                    System.Windows.MessageBox.Show($"停止服务时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    // 恢复UI状态
                    _isServiceStopping = false;
                    StartMqttServiceButton.Content = "启动 LHMMQTT 服务";
                    StartMqttServiceButton.IsEnabled = true;
                    StopMqttServiceButton.IsEnabled = false;
                    EnableCloseButton(); // 恢复关闭按钮
                    UpdateServiceStatusIndicator();
                }
            }
            else
            {
                System.Windows.MessageBox.Show("服务尚未运行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                // Ensure UI is consistent if this state is somehow reached incorrectly
                StartMqttServiceButton.Content = "启动 LHMMQTT 服务";
                StartMqttServiceButton.IsEnabled = true;
                StopMqttServiceButton.IsEnabled = false;
                UpdateServiceStatusIndicator();
            }
        }

        private void UpdateServiceStatusIndicator()
        {
            if (_serviceStatusIndicator != null)
            {
                // 如果服务正在停止中，保持"正在停止服务"的状态
                if (_isServiceStopping)
                {
                    _serviceStatusIndicator.Text = "正在停止服务，请稍等...";
                    _serviceStatusIndicator.Foreground = System.Windows.Media.Brushes.Orange;
                    return;
                }
                
                if (MqttUpdateService.IsServiceRunning())
                {
                    _serviceStatusIndicator.Text = "运行中";
                    _serviceStatusIndicator.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    _serviceStatusIndicator.Text = "已停止";
                    _serviceStatusIndicator.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
        }

        private void MainWindow_StateChanged(object? sender, System.EventArgs e) {
            if (WindowState == WindowState.Minimized && TrayIconCheckBox.IsChecked == true) {
                this.Hide();
                if (_notifyIcon != null) _notifyIcon.Visible = true;
            }
        }

        private void NotifyIcon_DoubleClick(object? sender, System.EventArgs e) {
            ShowWindow();
        }

        private void ShowWindow_Click(object? sender, System.EventArgs e) {
            ShowWindow();
        }

        private void ShowWindow() {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            if (_notifyIcon != null) _notifyIcon.Visible = false;
        }

        private async void Exit_Click(object? sender, System.EventArgs e) {
            bool serviceWasRunning = MqttUpdateService.IsServiceRunning();
            if (serviceWasRunning)
            {
                Log.Information("Exit_Click: Service is running, attempting to stop it.");
                DisableCloseButton(); // 禁用关闭按钮
                try
                {
                    // 设置一个更合理的超时，例如15秒，以匹配StopServiceAsync内部的等待时间
                    var stopTask = Task.Run(async () => await MqttUpdateService.StopServiceAsync());
                    var completedTask = await Task.WhenAny(stopTask, Task.Delay(15000)); 
                    
                    if (completedTask == stopTask)
                    {
                        Log.Information("Exit_Click: MQTT service stopped successfully within timeout.");
                    }
                    else
                    {
                        Log.Warning("Exit_Click: Timeout waiting for MQTT service to stop. Proceeding with exit anyway.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Exit_Click: Error stopping service during application exit.");
                }
            }
            
            UpdateServiceStatusIndicator(); 
            if(_notifyIcon != null) 
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            
            if (_systemMenuHandle != IntPtr.Zero)
            {
                DestroyMenu(_systemMenuHandle);
                _systemMenuHandle = IntPtr.Zero;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            Log.Information("Exit_Click: Flushing logs and exiting application via Environment.Exit(0).");
            Log.CloseAndFlush();
                Environment.Exit(0);
        }
        
        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // 如果服务正在停止中且在冷却期内，则阻止窗口关闭
            if (_isServiceStopping && _lastServiceStopTime.HasValue)
            {
                TimeSpan timeSinceStop = DateTime.Now - _lastServiceStopTime.Value;
                if (timeSinceStop.TotalMilliseconds < _serviceStopCooldownMs)
                {
                    e.Cancel = true; // 取消关闭操作
                    int remainingSeconds = (_serviceStopCooldownMs / 1000) - (int)timeSinceStop.TotalSeconds;
                    System.Windows.MessageBox.Show($"服务正在停止中，请等待{remainingSeconds}秒后再关闭窗口，以确保资源完全释放。", 
                        "请稍等", MessageBoxButton.OK, MessageBoxImage.Warning);
                    // 确保关闭按钮在提示期间保持禁用状态
                    DisableCloseButton(); 
                    return;
                }
            }

            bool serviceWasRunningAtStartOfClose = MqttUpdateService.IsServiceRunning();
            if (serviceWasRunningAtStartOfClose)
                {
                Log.Information("MainWindow_Closing: Service is running, attempting to stop it.");
                DisableCloseButton(); // 禁用关闭按钮
                try
                {
                    // 设置一个更合理的超时，例如15秒
                    var stopTask = Task.Run(async () => await MqttUpdateService.StopServiceAsync());
                    var completedTask = await Task.WhenAny(stopTask, Task.Delay(15000));
                    
                    if (completedTask == stopTask)
                    {
                        Log.Information("MainWindow_Closing: LHMMQTT Service stopped successfully within timeout.");
                    }
                    else
                    {
                        Log.Warning("MainWindow_Closing: Timeout waiting for MQTT service to stop. Proceeding with shutdown anyway.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "MainWindow_Closing: Error stopping MQTT Service on application exit.");
                }
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            
            if (_systemMenuHandle != IntPtr.Zero)
            {
                DestroyMenu(_systemMenuHandle);
                _systemMenuHandle = IntPtr.Zero;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            Log.Information("MainWindow_Closing: Flushing logs and exiting application via Environment.Exit(0).");
            Log.CloseAndFlush();
                Environment.Exit(0);
        }

        private void LoadSettings() {
            if (Settings.LoadFromConfig()) {
                // Load MQTT settings
                MqttHostnameTextBox.Text = Settings.Current?.MQTT?.Hostname ?? "";
                MqttPortTextBox.Text = Settings.Current?.MQTT?.Port.ToString() ?? "";
                MqttUsernameTextBox.Text = Settings.Current?.MQTT?.Username ?? "";
                MqttPasswordBox.Password = Settings.Current?.MQTT?.Password ?? "";

                // Load Sensor settings
                CpuCheckBox.IsChecked = Settings.Current?.Sensors?.CPU ?? false;
                GpuCheckBox.IsChecked = Settings.Current?.Sensors?.GPU ?? false;
                MemoryCheckBox.IsChecked = Settings.Current?.Sensors?.Memory ?? false;
                MotherboardCheckBox.IsChecked = Settings.Current?.Sensors?.Motherboard ?? false;
                ControllerCheckBox.IsChecked = Settings.Current?.Sensors?.Controller ?? false;
                NetworkingCheckBox.IsChecked = Settings.Current?.Sensors?.Networking ?? false;
                StorageCheckBox.IsChecked = Settings.Current?.Sensors?.Storage ?? false;

                // Load General settings
                if (Settings.Current?.Updates != null)
                {
                    UpdateIntervalTextBox.Text = Settings.Current.Updates.Delay.ToString();
                }
                StartupCheckBox.IsChecked = Settings.Current?.General?.Startup ?? false;
                TrayIconCheckBox.IsChecked = Settings.Current?.General?.TrayIcon ?? false;

            } else {
                System.Windows.MessageBox.Show("无法加载配置文件 config.yaml。请确保文件存在且格式正确。", "配置错误", MessageBoxButton.OK, MessageBoxImage.Error);
                // Initialize with default values or disable functionality
                UpdateIntervalTextBox.Text = "10"; // Default value
                StartupCheckBox.IsChecked = false;
                TrayIconCheckBox.IsChecked = false;
            }
        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (Settings.Current == null)
            {
                Settings.Current = new AppSettings
                {
                    MQTT = new MQTT { Hostname = "", Port = 1883, Username = "", Password = "" },
                    Updates = new Updates { Delay = 10 }, 
                    Sensors = new Sensors { CPU = false, GPU = false, Memory = false, Motherboard = false, Controller = false, Networking = false, Storage = false },
                    General = new GeneralSettings { Startup = false, TrayIcon = false }
                };
            }

            // Save General settings
            if (int.TryParse(UpdateIntervalTextBox.Text, out int delay))
            {
                Settings.Current.Updates.Delay = delay;
            }
            else
            {
                System.Windows.MessageBox.Show("更新间隔必须是一个有效的整数。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return; 
            }
            Settings.Current.General.Startup = StartupCheckBox.IsChecked ?? false;
            Settings.Current.General.TrayIcon = TrayIconCheckBox.IsChecked ?? false;

            SaveConfiguration();
            System.Windows.MessageBox.Show("常规设置已保存", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SaveMqttButton_Click(object sender, RoutedEventArgs e) {
            if (Settings.Current != null && Settings.Current.MQTT != null) {
                Settings.Current.MQTT.Hostname = MqttHostnameTextBox.Text;
                if (int.TryParse(MqttPortTextBox.Text, out int port)) {
                    Settings.Current.MQTT.Port = port;
                } else {
                    System.Windows.MessageBox.Show("MQTT 端口必须是一个有效的数字。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Settings.Current.MQTT.Username = MqttUsernameTextBox.Text;
                Settings.Current.MQTT.Password = MqttPasswordBox.Password;
                SaveConfiguration();
                System.Windows.MessageBox.Show("MQTT 设置已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            } else {
                 System.Windows.MessageBox.Show("无法保存 MQTT 设置，配置为空。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveSensorsButton_Click(object sender, RoutedEventArgs e) {
            if (Settings.Current != null && Settings.Current.Sensors != null) {
                Settings.Current.Sensors.CPU = CpuCheckBox.IsChecked ?? false;
                Settings.Current.Sensors.GPU = GpuCheckBox.IsChecked ?? false;
                Settings.Current.Sensors.Memory = MemoryCheckBox.IsChecked ?? false;
                Settings.Current.Sensors.Motherboard = MotherboardCheckBox.IsChecked ?? false;
                Settings.Current.Sensors.Controller = ControllerCheckBox.IsChecked ?? false;
                Settings.Current.Sensors.Networking = NetworkingCheckBox.IsChecked ?? false;
                Settings.Current.Sensors.Storage = StorageCheckBox.IsChecked ?? false;
                SaveConfiguration();
                System.Windows.MessageBox.Show("监控项设置已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            } else {
                System.Windows.MessageBox.Show("无法保存监控项设置，配置为空。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveConfiguration() {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(new { AppSettings = Settings.Current });
            File.WriteAllText("config.yaml", yaml);
        }

        private void LoadStartupSetting() {
            RegistryKey? rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            if (rk != null) {
                StartupCheckBox.IsChecked = (rk.GetValue(System.AppDomain.CurrentDomain.FriendlyName) != null);
                rk.Close();
            }
        }

        private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            RegistryKey? rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (rk != null)
            {
                if (StartupCheckBox.IsChecked == true)
                {
                    rk.SetValue(System.AppDomain.CurrentDomain.FriendlyName, System.Reflection.Assembly.GetExecutingAssembly().Location);
                }
                else
                {
                    rk.DeleteValue(System.AppDomain.CurrentDomain.FriendlyName, false);
                }
                rk.Close();
            }
        }

        private void TrayIconCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (TrayIconCheckBox.IsChecked == true)
            {
                // Logic is handled by Window_StateChanged and Closing events
            }
            else
            {
                // If unchecked, ensure window is visible and tray icon is hidden
                ShowWindow();
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void InitializeCooldownTimer()
        {
            _cooldownTimer = new DispatcherTimer();
            _cooldownTimer.Tick += CooldownTimer_Tick;
            _cooldownTimer.Interval = TimeSpan.FromSeconds(1); // 每秒触发一次
        }

        private void CooldownTimer_Tick(object? sender, EventArgs e)
        {
            if (_lastServiceStopTime.HasValue)
            {
                TimeSpan elapsed = DateTime.Now - _lastServiceStopTime.Value;
                int remainingSeconds = (_serviceStopCooldownMs / 1000) - (int)elapsed.TotalSeconds;
                
                if (remainingSeconds <= 0)
                {
                    // 冷却时间结束
                    _isServiceStopping = false;
                    _cooldownTimer?.Stop();
                    
                    // 启用"启动LHMMQTT服务"按钮
                    StartMqttServiceButton.IsEnabled = true;
                    EnableCloseButton(); // 启用关闭按钮
                    
                    UpdateServiceStatusIndicator(); // 恢复正常状态显示
                    
                    // 移除弹窗提示
                    // System.Windows.MessageBox.Show("服务已完全停止，现在可以安全关闭应用程序。", "服务停止完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    // 更新状态指示器显示剩余时间
                    if (_serviceStatusIndicator != null)
                    {
                        _serviceStatusIndicator.Text = $"正在停止服务，请稍等...({remainingSeconds}秒)";
                        _serviceStatusIndicator.Foreground = System.Windows.Media.Brushes.Orange;
                    }
                    
                    // 确保在倒计时期间"启动LHMMQTT服务"按钮保持禁用状态
                    StartMqttServiceButton.IsEnabled = false;
                    DisableCloseButton(); // 确保关闭按钮保持禁用状态
                }
            }
        }
    }
}