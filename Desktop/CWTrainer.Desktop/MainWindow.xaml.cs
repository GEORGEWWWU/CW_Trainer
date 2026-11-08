using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CWTrainer.Desktop;

public partial class MainWindow : Window
{
    private const int BaudRate = 115200;

    /// <summary>心跳周期。明显小于固件 6 秒的失联判定。</summary>
    private const int HeartbeatMs = 1000;

    /// <summary>关闭软件时等待固件回复 BYE,BUZZER 的时间。</summary>
    private const int HandoffTimeoutMs = 900;

    /// <summary>解码状态机的推进周期。</summary>
    private const int DecodeTickMs = 20;

    private readonly ObservableCollection<string> _events = new();

    // 解码器、桨叶状态由串口接收线程和定时器线程共同访问
    private readonly object _stateLock = new();
    private readonly HashSet<string> _pressedPaddles = new();
    private readonly MorseDecoder _decoder = new();

    private readonly object _receiveLock = new();
    private readonly StringBuilder _receiveBuffer = new();

    private readonly object _writeLock = new();
    private readonly object _audioLock = new();

    // 心跳放在线程池上，避免 UI 线程一卡顿就被固件判定为“上位机失联”
    private Timer? _heartbeatTimer;
    private Timer? _decodeTimer;
    private bool _sendFailureLogged;

    private SerialPort? _port;
    private string? _portName;
    private TaskCompletionSource<bool>? _handshakeSource;
    private TaskCompletionSource<bool>? _byeSource;

    private volatile bool _isConnected;
    private bool _suppressUiEvents;

    private volatile string _outputMode = "BUZZER";
    private volatile string _deviceMode = "IAMBIC_B";

    // 音频参数在 UI 线程写入、在接收线程读取，缓存一份避免跨线程访问控件
    private volatile int _toneHz = 650;
    private volatile int _volume = 45;

    private long _straightMarkStartMs = -1;

    private int _decodeUiPending;

    private byte[]? _activeWave;
    private GCHandle _activeWavePin;
    private int _audioGeneration;

    private bool _allowClose;
    private bool _closing;

    public MainWindow()
    {
        string stage = "InitializeComponent";

        try
        {
            InitializeComponent();

            stage = "绑定事件列表";
            EventList.ItemsSource = _events;

            stage = "初始化默认选项";
            OutputCombo.SelectedIndex = 0;
            ModeCombo.SelectedIndex = 2;

            stage = "更新滑块标签";
            UpdateSliderLabels();

            stage = "初始化连接状态";
            SetConnectionUi(false, null);

            // 窗口加载后再扫描串口，避免阻塞首次显示。
            stage = "安排串口扫描";
            Loaded += (_, _) =>
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(RefreshPorts));
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"启动失败位置：{stage}\n\n{ex}",
                "CW Trainer 启动异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            throw;
        }
    }

    // ---------- 标题栏 ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    // ---------- 串口 ----------

    private void RefreshPorts()
    {
        string? previous = PortCombo.SelectedItem as string;
        PortCombo.Items.Clear();

        try
        {
            foreach (string name in SerialPort.GetPortNames().OrderBy(ParseComNumber))
                PortCombo.Items.Add(name);
        }
        catch (Exception ex)
        {
            AddLog($"读取端口失败：{ex.Message}");
        }

        if (previous is not null && PortCombo.Items.Contains(previous))
            PortCombo.SelectedItem = previous;
        else if (PortCombo.Items.Count == 1)
            PortCombo.SelectedIndex = 0;
        else
            PortCombo.SelectedIndex = -1;

        AddLog($"发现 {PortCombo.Items.Count} 个串口。请选择 ESP32-S3 OTG 接口。");
    }

    private static int ParseComNumber(string name)
    {
        string digits = new(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : int.MaxValue;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
        => RefreshPorts();

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_port?.IsOpen == true)
        {
            await DisconnectDeviceAsync("用户主动断开");
            return;
        }

        if (PortCombo.SelectedItem is not string portName)
        {
            MessageBox.Show("请先选择 ESP32-S3 的 OTG COM 端口。");
            return;
        }

        ConnectButton.IsEnabled = false;

        try
        {
            var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
            {
                Encoding = Encoding.ASCII,
                NewLine = "\n",
                ReadTimeout = 500,
                WriteTimeout = 500,
                ReadBufferSize = 4096,
                WriteBufferSize = 2048,
                DtrEnable = true,
                RtsEnable = false,
                Handshake = Handshake.None
            };

            port.DataReceived += Port_DataReceived;
            port.Open();

            _port = port;
            _portName = portName;

            lock (_receiveLock)
                _receiveBuffer.Clear();

            lock (_stateLock)
                _decoder.Reset();

            _handshakeSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // 用局部变量持有，避免握手期间串口异常断开把字段置空后出现空引用
            TaskCompletionSource<bool> handshake = _handshakeSource;

            AddLog($"已打开 {portName}，正在连接设备…");

            for (int i = 0; i < 12 && !handshake.Task.IsCompleted; i++)
            {
                SendCommand("HELLO,1");
                await Task.Delay(500);
            }

            Task completed = await Task.WhenAny(
                handshake.Task,
                Task.Delay(1000));

            if (completed != handshake.Task || !await handshake.Task)
            {
                await DisconnectDeviceAsync("设备握手超时");
                MessageBox.Show(
                    "没有收到设备 READY 握手。\n请确认 OTG 接口、固件和 COM 端口。",
                    "连接失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _isConnected = true;
            SetConnectionUi(true, portName);
            StartHeartbeat();
            StartDecodeTimer();

            SendCommand("GET");
            AddLog("设备连接成功。");
        }
        catch (Exception ex)
        {
            await DisconnectDeviceAsync("连接失败");
            MessageBox.Show($"串口连接失败：{ex.Message}");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 串口接收线程。所有和时间相关的事件都在这个线程上处理，不经过 UI 线程，
    /// 避免界面卡顿把点划间隔算错——解码不准有相当一部分原因在这里。
    /// </summary>
    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port) return;

        string chunk;

        try
        {
            chunk = port.ReadExisting();
        }
        catch (Exception ex)
        {
            AddLog($"USB 连接异常：{ex.Message}");
            PostUi(() => _ = DisconnectDeviceAsync("USB 连接中断，等待硬件蜂鸣器接管"));
            return;
        }

        if (string.IsNullOrEmpty(chunk)) return;

        // 数据到达的时刻最接近真实事件时刻，这里取值，不在 UI 线程取值。
        long receivedAtMs = Environment.TickCount64;
        List<string> lines = new();

        lock (_receiveLock)
        {
            _receiveBuffer.Append(chunk);

            while (true)
            {
                string all = _receiveBuffer.ToString();
                int newline = all.IndexOf('\n');
                if (newline < 0) break;

                string line = all[..newline].Trim('\r', ' ', '\t');
                _receiveBuffer.Remove(0, newline + 1);

                if (line.Length > 0)
                    lines.Add(line);
            }

            if (_receiveBuffer.Length > 2048)
                _receiveBuffer.Clear();
        }

        foreach (string line in lines)
        {
            try
            {
                ProcessDeviceLine(line, receivedAtMs);
            }
            catch (Exception ex)
            {
                AddLog($"处理设备数据出错：{ex.Message}");
            }
        }
    }

    private void ProcessDeviceLine(string line, long receivedAtMs)
    {
        if (line.StartsWith("READY,1,CWTRAINER,", StringComparison.Ordinal))
        {
            _handshakeSource?.TrySetResult(true);
            _isConnected = true;
            string? name = _portName;
            PostUi(() => SetConnectionUi(true, name));
            AddLog("设备已就绪。");
            return;
        }

        if (line == "BYE,BUZZER")
        {
            _byeSource?.TrySetResult(true);
            AddLog("设备已切回实体蜂鸣器。");
            return;
        }

        if (line.StartsWith("STATUS,", StringComparison.Ordinal))
        {
            ParseStatus(line);
            return;
        }

        string[] parts = line.Split(',');

        if (parts.Length >= 3 && parts[0] == "KEY")
        {
            HandleKeyEvent(parts[1], parts[2] == "1");
            return;
        }

        if (parts.Length >= 3 && parts[0] == "ELEMENT")
        {
            if (!uint.TryParse(parts[2], out uint durationUs) || durationUs == 0)
                return;

            bool isDit = parts[1] == "DIT";

            // 固件在元素“开始”时上报，所以元素结束时刻 = 收到时刻 + 时长。
            long durationMs = (long)Math.Round(durationUs / 1000.0);
            long startMs = receivedAtMs;
            FeedElement(isDit, durationUs, startMs, startMs + durationMs);

            if (_outputMode is "PC" or "BOTH")
                PlayComputerTone(Math.Clamp((int)durationMs, 1, 2000));

            return;
        }

        if (parts.Length >= 2 && parts[0] == "TONE")
        {
            HandleToneEvent(parts[1] == "ON", receivedAtMs);
            return;
        }

        if (line == "PONG") return;

        if (line.StartsWith("ACK,", StringComparison.Ordinal))
        {
            AddLog(line);
            return;
        }

        if (line.StartsWith("ERROR,", StringComparison.Ordinal))
        {
            AddLog("设备错误：" + line);
            return;
        }

        AddLog(line);
    }

    private void HandleKeyEvent(string paddle, bool pressed)
    {
        string text;

        lock (_stateLock)
        {
            if (pressed) _pressedPaddles.Add(paddle);
            else _pressedPaddles.Remove(paddle);

            text = _pressedPaddles.Count == 0
                ? "等待输入"
                : string.Join("  +  ", _pressedPaddles
                    .OrderBy(p => p)
                    .Select(p => p == "DIT" ? "点 ·" : "划 —"));
        }

        PostUi(() => KeyStateText.Text = text);
    }

    /// <summary>
    /// 直键模式：固件只在音调开/关时上报，没有元素时长，
    /// 这里用事件到达时刻算这一次按住持续了多久。
    /// </summary>
    private void HandleToneEvent(bool on, long receivedAtMs)
    {
        if (on)
        {
            _straightMarkStartMs = receivedAtMs;

            if (_outputMode is "PC" or "BOTH")
                StartComputerToneLoop();
            return;
        }

        long startMs = _straightMarkStartMs;
        _straightMarkStartMs = -1;

        if (_outputMode is "PC" or "BOTH")
            StopComputerTone();

        if (startMs < 0)
            return;

        long durationMs = Math.Max(1, receivedAtMs - startMs);

        uint unitMs;
        lock (_stateLock)
            unitMs = Math.Max(10u, _decoder.UnitUs / 1000);

        bool isDit = durationMs <= unitMs * 2;
        FeedElement(isDit, (uint)(durationMs * 1000), startMs, receivedAtMs);
    }

    private void FeedElement(bool isDit, uint durationUs, long markStartMs, long markEndMs)
    {
        List<MorseCommit>? commits = null;

        lock (_stateLock)
        {
            // 自动键时序由固件生成，可以用元素时长校准时间单位；
            // 直键由人控制，时间单位只取 WPM 设定值。
            bool deriveUnit = _deviceMode != "STRAIGHT";
            _decoder.PushElement(isDit, durationUs, deriveUnit, markStartMs, markEndMs);

            if (_decoder.Commits.Count > 0)
            {
                commits = new List<MorseCommit>(_decoder.Commits);
                _decoder.ClearCommits();
            }
        }

        if (commits is not null)
            LogCommits(commits);

        PostDecodeUiUpdate();
    }

    private void LogCommits(List<MorseCommit> commits)
    {
        foreach (MorseCommit c in commits)
        {
            if (c.IsWordSpace)
            {
                AddLog("单词间隔");
                continue;
            }

            AddLog(c.Symbol == MorseTable.Unknown
                ? $"{c.Code}  →  未识别"
                : $"{c.Code}  →  {c.Symbol}");
        }
    }

    private void ParseStatus(string line)
    {
        string[] p = line.Split(',');

        int wpm = 0;
        string mode = string.Empty;
        string output = string.Empty;

        for (int i = 1; i + 1 < p.Length; i += 2)
        {
            switch (p[i])
            {
                case "WPM":
                    int.TryParse(p[i + 1], out wpm);
                    break;
                case "MODE":
                    mode = p[i + 1];
                    break;
                case "OUTPUT":
                    output = p[i + 1];
                    break;
            }
        }

        lock (_stateLock)
        {
            if (wpm > 0)
                _decoder.SetWpm(wpm);
        }

        if (mode.Length > 0)
            _deviceMode = mode;

        if (output.Length > 0)
        {
            _outputMode = output;

            if (output == "BUZZER")
                StopComputerTone();
        }

        PostUi(() => ApplyStatusToUi(wpm, mode, output));
    }

    private void ApplyStatusToUi(int wpm, string mode, string output)
    {
        _suppressUiEvents = true;

        try
        {
            if (wpm > 0)
                WpmSlider.Value = Math.Clamp(wpm, 5, 60);

            if (mode.Length > 0)
                SelectComboByTag(ModeCombo, mode);

            if (output.Length > 0)
                SelectComboByTag(OutputCombo, output);
        }
        finally
        {
            _suppressUiEvents = false;
        }

        UpdateSliderLabels();
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag,
                StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void SendCommand(string command)
    {
        try
        {
            lock (_writeLock)
            {
                if (_port?.IsOpen == true)
                    _port.Write(command + "\n");
            }

            _sendFailureLogged = false;
        }
        catch (Exception ex)
        {
            // 心跳每秒一次，失败时只提示一次，避免刷屏
            if (!_sendFailureLogged)
            {
                _sendFailureLogged = true;
                AddLog($"发送命令失败：{ex.Message}");
            }
        }
    }

    private void PostUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    // ---------- 心跳 ----------

    private void StartHeartbeat()
    {
        StopHeartbeat();

        _heartbeatTimer = new Timer(
            _ =>
            {
                if (_isConnected) SendCommand("PING");
            },
            null,
            HeartbeatMs,
            HeartbeatMs);
    }

    private void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    // ---------- 解码定时器 ----------

    private void StartDecodeTimer()
    {
        StopDecodeTimer();

        _decodeTimer = new Timer(
            _ => PumpDecoder(),
            null,
            DecodeTickMs,
            DecodeTickMs);
    }

    private void StopDecodeTimer()
    {
        _decodeTimer?.Dispose();
        _decodeTimer = null;
    }

    /// <summary>
    /// 周期性推进解码状态机，负责提交最后一个字符和单词间隔。
    /// 跑在线程池上，不依赖 UI 线程是否空闲。
    /// </summary>
    private void PumpDecoder()
    {
        List<MorseCommit>? commits = null;

        lock (_stateLock)
        {
            _decoder.Tick(Environment.TickCount64);

            if (_decoder.Commits.Count > 0)
            {
                commits = new List<MorseCommit>(_decoder.Commits);
                _decoder.ClearCommits();
            }
        }

        if (commits is null)
            return;

        LogCommits(commits);
        PostDecodeUiUpdate();
    }

    private void PostDecodeUiUpdate()
    {
        if (Interlocked.Exchange(ref _decodeUiPending, 1) == 1)
            return;

        PostUi(() =>
        {
            Interlocked.Exchange(ref _decodeUiPending, 0);
            RefreshDecodeUi();
        });
    }

    private void RefreshDecodeUi()
    {
        string decoded;
        string pending;

        lock (_stateLock)
        {
            decoded = _decoder.Text;
            pending = _decoder.PendingSymbols;
        }

        DecodedText.Text = decoded;

        if (pending.Length == 0)
        {
            PreviewText.Text = "等待输入";
            return;
        }

        PreviewText.Text = string.Join(
            " ",
            pending.Select(c => c == '.' ? "·" : "—"));
    }

    // ---------- 断开与安全交接 ----------

    /// <summary>
    /// 请固件交还实体蜂鸣器，并等待它明确回复 BYE,BUZZER。
    /// 旧实现只写不确认，端口随后立刻关闭，这条命令可能根本没送出去，
    /// 硬件于是停在静音状态，只能 RST。
    /// </summary>
    private async Task<bool> RequestDeviceHandoffAsync(int timeoutMs)
    {
        SerialPort? port = _port;

        if (port is null || !port.IsOpen)
            return false;

        var source = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _byeSource = source;

        try
        {
            lock (_writeLock)
            {
                port.Write("BYE\n");

                try { port.BaseStream.Flush(); } catch { }
            }
        }
        catch (Exception ex)
        {
            AddLog($"发送 BYE 失败：{ex.Message}");
            return false;
        }

        Task completed = await Task.WhenAny(source.Task, Task.Delay(timeoutMs));

        return completed == source.Task &&
               source.Task.Status == TaskStatus.RanToCompletion &&
               source.Task.Result;
    }

    private async Task DisconnectDeviceAsync(string reason)
    {
        StopHeartbeat();
        StopDecodeTimer();

        if (_port?.IsOpen == true)
        {
            bool confirmed = await RequestDeviceHandoffAsync(HandoffTimeoutMs);

            AddLog(confirmed
                ? "实体蜂鸣器已接管。"
                : "未收到交接确认，将由固件心跳超时接管。");

            // 留一点时间让驱动把最后几字节真正发出去，再关闭端口。
            await Task.Delay(80);
        }

        ClosePortAndReset(reason);
    }

    private void ClosePortAndReset(string reason)
    {
        StopHeartbeat();
        StopDecodeTimer();

        try
        {
            if (_port is not null)
            {
                _port.DataReceived -= Port_DataReceived;
                if (_port.IsOpen) _port.Close();
                _port.Dispose();
            }
        }
        catch { }

        _port = null;
        _portName = null;
        _isConnected = false;

        _handshakeSource?.TrySetResult(false);
        _handshakeSource = null;
        _byeSource = null;

        lock (_stateLock)
            _pressedPaddles.Clear();

        _straightMarkStartMs = -1;

        KeyStateText.Text = "等待输入";

        StopComputerTone();

        _outputMode = "BUZZER";
        _suppressUiEvents = true;
        OutputCombo.SelectedIndex = 0;
        _suppressUiEvents = false;

        SetConnectionUi(false, null);

        if (!string.IsNullOrWhiteSpace(reason))
            AddLog(reason);
    }

    /// <summary>
    /// 系统关机 / 注销 / 进程异常退出时的最后手段：
    /// 同步发送 BYE，尽量不让硬件停在静音状态。
    /// </summary>
    public void EmergencyHandoff()
    {
        SerialPort? port = _port;

        if (port is null || !port.IsOpen)
            return;

        try
        {
            lock (_writeLock)
            {
                port.Write("BYE\n");

                try { port.BaseStream.Flush(); } catch { }
            }

            // 给固件一点时间处理，端口一关数据就丢了。
            Thread.Sleep(150);
        }
        catch { }
    }

    private void SetConnectionUi(bool connected, string? portName)
    {
        ConnectionDot.Fill = connected
            ? new SolidColorBrush(Color.FromRgb(78, 142, 105))
            : new SolidColorBrush(Color.FromRgb(184, 153, 105));

        ConnectionText.Text = connected
            ? $"已连接 · {portName}"
            : "尚未连接";

        ConnectButton.Content = connected ? "断开设备" : "连接设备";

        DeviceInfoText.Text = connected
            ? "设备已就绪，电脑音频与实体蜂鸣器可切换。"
            : "请连接 ESP32-S3 的 USB OTG 接口。";

        OutputCombo.IsEnabled = connected;
        ModeCombo.IsEnabled = connected;
        WpmSlider.IsEnabled = connected;
        ApplySettingsButton.IsEnabled = connected;
    }

    // ---------- 输出与设置 ----------

    private void OutputCombo_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents ||
            OutputCombo.SelectedItem is not ComboBoxItem item)
            return;

        string mode = item.Tag?.ToString() ?? "BUZZER";
        _outputMode = mode;

        if (_isConnected)
        {
            SendCommand($"SET,OUTPUT,{mode}");

            if (mode == "BUZZER")
                StopComputerTone();

            AddLog($"声音输出：{item.Content}");
        }
    }

    private void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConnected)
        {
            MessageBox.Show("请先连接设备。");
            return;
        }

        int wpm = (int)WpmSlider.Value;
        string mode = (ModeCombo.SelectedItem as ComboBoxItem)?
            .Tag?.ToString() ?? "IAMBIC_B";

        SendCommand($"SET,WPM,{wpm}");
        SendCommand($"SET,MODE,{mode}");

        _deviceMode = mode;

        lock (_stateLock)
            _decoder.SetWpm(wpm);

        AddLog($"设置已发送：{wpm} WPM / {mode}");
    }


    private void WpmSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WpmValueText == null)
            return;

        WpmValueText.Text = $"{(int)e.NewValue} WPM";
    }

    private void ToneSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ToneValueText == null)
            return;

        _toneHz = (int)e.NewValue;
        ToneValueText.Text = $"{_toneHz} Hz";
    }

    private void VolumeSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValueText == null)
            return;

        _volume = (int)e.NewValue;
        VolumeValueText.Text = $"{_volume}%";
    }


    private void UpdateSliderLabels()
    {
        WpmValueText.Text = $"{(int)WpmSlider.Value} WPM";
        ToneValueText.Text = $"{(int)ToneSlider.Value} Hz";
        VolumeValueText.Text = $"{(int)VolumeSlider.Value}%";
    }

    // ---------- 摩斯码解码（界面部分） ----------

    private void ClearPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        lock (_stateLock)
        {
            _decoder.Reset();
            _pressedPaddles.Clear();
        }

        PreviewText.Text = "等待输入";
        DecodedText.Text = "";
        KeyStateText.Text = "等待输入";
    }

    private void CopyTextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(DecodedText.Text))
        {
            Clipboard.SetText(DecodedText.Text);
            AddLog("已复制抄收文本。");
        }
    }

    // ---------- 事件日志 ----------

    private void AddLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            PostUi(() => AddLog(message));
            return;
        }

        _events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");

        while (_events.Count > 100)
            _events.RemoveAt(_events.Count - 1);
    }

    // ---------- 电脑音频 ----------

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;
    private const uint SndLoop = 0x0008;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativePlaySound(
        IntPtr sound, IntPtr module, uint flags);

    private void TestToneButton_Click(object sender, RoutedEventArgs e)
        => PlayComputerTone(350);

    private void PlayComputerTone(int durationMs)
    {
        int volume = _volume;
        if (volume <= 0) return;

        byte[] wav = CreateToneWave(durationMs, _toneHz, volume);
        int generation;

        lock (_audioLock)
        {
            StopAudioLocked();

            _activeWave = wav;
            _activeWavePin = GCHandle.Alloc(wav, GCHandleType.Pinned);

            if (!NativePlaySound(
                _activeWavePin.AddrOfPinnedObject(),
                IntPtr.Zero,
                SndAsync | SndMemory | SndNoDefault))
            {
                ReleaseAudioLocked(true);
                AddLog("电脑音频播放失败。");
                return;
            }

            generation = ++_audioGeneration;
        }

        _ = Task.Delay(durationMs + 200).ContinueWith(_ =>
        {
            lock (_audioLock)
            {
                if (generation == _audioGeneration)
                    ReleaseAudioLocked(false);
            }
        });
    }

    private void StartComputerToneLoop()
    {
        int volume = _volume;
        if (volume <= 0) return;

        byte[] wav = CreateToneWave(1000, _toneHz, volume);

        lock (_audioLock)
        {
            StopAudioLocked();

            _activeWave = wav;
            _activeWavePin = GCHandle.Alloc(wav, GCHandleType.Pinned);

            NativePlaySound(
                _activeWavePin.AddrOfPinnedObject(),
                IntPtr.Zero,
                SndAsync | SndMemory | SndLoop | SndNoDefault);

            _audioGeneration++;
        }
    }

    private void StopComputerTone()
    {
        lock (_audioLock)
            StopAudioLocked();
    }

    private void StopAudioLocked()
    {
        _audioGeneration++;
        NativePlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        ReleaseAudioLocked(false);
    }

    private void ReleaseAudioLocked(bool stopPlayback)
    {
        if (stopPlayback)
            NativePlaySound(IntPtr.Zero, IntPtr.Zero, 0);

        if (_activeWavePin.IsAllocated)
            _activeWavePin.Free();

        _activeWave = null;
    }

    private static byte[] CreateToneWave(
        int durationMs, int frequencyHz, int volumePercent)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bits = 16;

        int sampleCount = Math.Max(1, sampleRate * durationMs / 1000);
        int dataBytes = sampleCount * 2;

        byte[] wav = new byte[44 + dataBytes];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(
            wav.AsSpan(4, 4), 36 + dataBytes);

        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);

        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), bits);

        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), dataBytes);

        double amplitude = Math.Clamp(volumePercent, 0, 100) / 100.0 * 0.45;
        int frequency = Math.Clamp(frequencyHz, 100, 3000);
        int fade = Math.Min(sampleRate / 250, sampleCount / 2);

        for (int i = 0; i < sampleCount; i++)
        {
            double envelope = 1;

            if (fade > 0 && i < fade)
                envelope = i / (double)fade;

            if (fade > 0 && i >= sampleCount - fade)
                envelope = Math.Min(envelope,
                    (sampleCount - 1 - i) / (double)fade);

            double sample = Math.Sin(
                2 * Math.PI * frequency * i / sampleRate)
                * amplitude * envelope;

            short pcm = (short)Math.Clamp(
                (int)(sample * short.MaxValue),
                short.MinValue, short.MaxValue);

            BinaryPrimitives.WriteInt16LittleEndian(
                wav.AsSpan(44 + i * 2, 2), pcm);
        }

        return wav;
    }

    // ---------- 窗口关闭 ----------

    private void Window_Closing(
        object? sender,
        System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
            return;

        // 第一次关闭请求：先取消，完成带确认的交接后再真正关闭。
        e.Cancel = true;

        if (_closing)
            return;

        _closing = true;
        _ = CloseSafelyAsync();
    }

    private async Task CloseSafelyAsync()
    {
        try
        {
            await DisconnectDeviceAsync("应用程序关闭");
        }
        catch (Exception ex)
        {
            AddLog($"关闭时发生异常：{ex.Message}");
        }
        finally
        {
            _allowClose = true;

            try
            {
                Close();
            }
            catch
            {
                Application.Current?.Shutdown();
            }
        }
    }
}
