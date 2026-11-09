using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
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

    /// <summary>
    /// 多久没收到 PONG 就判定“失联”。
    /// 固件在 6 秒无心跳时接管蜂鸣器，这里必须早于它，界面才不会说谎。
    /// </summary>
    private const int LinkLostMs = 3000;

    /// <summary>拖动 WPM 滑块时的下发延迟，避免每格都发命令。</summary>
    private const int WpmApplyDelayMs = 280;

    private readonly ObservableCollection<string> _events = new();

    // 解码器、桨叶状态由串口接收线程和定时器线程共同访问
    private readonly object _stateLock = new();
    private readonly HashSet<string> _pressedPaddles = new();
    private readonly MorseDecoder _decoder = new();

    private readonly object _receiveLock = new();
    private readonly StringBuilder _receiveBuffer = new();

    private readonly object _writeLock = new();

    // 心跳与解码都放在线程池上，避免 UI 线程一卡顿就被固件判定为“上位机失联”
    private Timer? _heartbeatTimer;
    private Timer? _decodeTimer;
    private DispatcherTimer? _wpmApplyTimer;
    private bool _sendFailureLogged;

    private SerialPort? _port;
    private string? _portName;
    private TaskCompletionSource<bool>? _handshakeSource;
    private TaskCompletionSource<bool>? _byeSource;

    /// <summary>最近一次收到 PONG 的时刻（TickCount64）。</summary>
    private long _lastPongMs;

    /// <summary>心跳已断，但串口还开着。</summary>
    private volatile bool _linkLost;

    /// <summary>串口已连上的端口名，用于“失联”时仍然显示它。</summary>
    private string? _connectedPort;

    private volatile bool _isConnected;
    private bool _suppressUiEvents;

    private volatile string _outputMode = "BUZZER";
    private volatile string _deviceMode = "IAMBIC_B";

    // 音频参数在 UI 线程写入、在接收线程读取，缓存一份避免跨线程访问控件
    private volatile int _toneHz = 650;
    private volatile int _volume = 45;

    private int _pendingWpm = 15;
    private long _straightMarkStartMs = -1;

    private int _decodeUiPending;

    private readonly AudioOutput _audio;
    private bool _suppressAudioUi;

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

            stage = "初始化下拉框";
            PortCombo.DisplayMemberPath = null;

            stage = "读取偏好设置";
            ApplySavedPreferences();

            stage = "初始化音频输出";
            _audio = new AudioOutput(AddLog);
            RefreshAudioDevices();

            stage = "更新数值标签";
            UpdateSliderLabels();

            stage = "初始化连接状态";
            SetConnectionUi(false, null);

            stage = "初始化 WPM 下发定时器";
            _wpmApplyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(WpmApplyDelayMs)
            };

            _wpmApplyTimer.Tick += (_, _) =>
            {
                _wpmApplyTimer!.Stop();

                if (!_isConnected)
                    return;

                SendCommand($"SET,WPM,{_pendingWpm}");

                lock (_stateLock)
                    _decoder.SetWpm(_pendingWpm);
            };

            Activated += (_, _) => RefreshAudioDevices();

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

    // ---------- 窗口 ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);

        // 用了 WindowChrome 之后 DWM 不再自动给圆角，这里显式要求一次。
        // Win11 22000+ 有效；Win10 返回错误码，忽略即可。
        try
        {
            int round = DwmWindowCornerRound;
            DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref round, sizeof(int));
        }
        catch
        {
        }
    }

    private const int DwmWindowCornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DwmWindowCornerRound = 2;         // DWMWCP_ROUND

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Windows 切换深浅色时会广播 WM_SETTINGCHANGE，lParam 为 "ImmersiveColorSet"。
    /// SystemEvents 有时不触发，所以这里直接挂消息钩子。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmSettingChange = 0x001A;

        if (msg == WmSettingChange)
        {
            string? area = lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);

            if (string.IsNullOrEmpty(area) || area == "ImmersiveColorSet")
                App.RefreshTheme();
        }

        return IntPtr.Zero;
    }


    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed && WindowState == WindowState.Normal)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            return;
        }

        WindowState = WindowState.Maximized;
    }

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
            _linkLost = false;
            _lastPongMs = Environment.TickCount64;
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
    /// 避免界面卡顿把点划间隔算错。
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

        if (line == "PONG")
        {
            _lastPongMs = Environment.TickCount64;
            RecoverLinkIfNeeded();
            return;
        }

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
                ? "空闲"
                : string.Join("  +  ", _pressedPaddles
                    .OrderBy(p => p)
                    .Select(p => p == "DIT" ? "点 ·" : "划 —"));
        }

        bool anyPressed = pressed || _pressedPaddles.Count > 0;

        PostUi(() =>
        {
            KeyStateText.Text = text;
            KeyDot.SetResourceReference(Shape.FillProperty, anyPressed ? "Accent" : "Ink3");
        });
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
                CheckSegmentByTag(mode);

            if (output.Length > 0)
                CheckOutputByTag(output);
        }
        finally
        {
            _suppressUiEvents = false;
        }

        UpdateSliderLabels();
    }

    private void CheckOutputByTag(string tag)
    {
        switch (tag)
        {
            case "PC": OutputPcRadio.IsChecked = true; break;
            case "BOTH": OutputBothRadio.IsChecked = true; break;
            default: OutputBuzzerRadio.IsChecked = true; break;
        }
    }

    private void CheckSegmentByTag(string tag)
    {
        switch (tag)
        {
            case "STRAIGHT": ModeStraightRadio.IsChecked = true; break;
            case "IAMBIC_A": ModeIambicARadio.IsChecked = true; break;
            default: ModeIambicBRadio.IsChecked = true; break;
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
                if (!_isConnected) return;

                SendCommand("PING");

                // 发出 PING 后若连续几拍都没收到 PONG，就认为设备已不再响应。
                // 此时串口可能还“开着”，但设备要么挂了、要么被别的程序抢走。
                long silentMs = Environment.TickCount64 - Volatile.Read(ref _lastPongMs);

                if (silentMs > LinkLostMs)
                    MarkLinkLost(silentMs);
            },
            null,
            HeartbeatMs,
            HeartbeatMs);
    }

    /// <summary>设备不再回心跳：界面切到第三种状态，并停掉解码。</summary>
    private void MarkLinkLost(long silentMs)
    {
        if (_linkLost)
            return;

        _linkLost = true;

        StopDecodeTimer();
        StopComputerTone();

        PostUi(() =>
        {
            SetConnectionUi(true, _connectedPort);
            AddLog($"设备失联：已 {(silentMs / 1000.0):0.0} 秒没有心跳回应。");
        });
    }

    /// <summary>设备重新回 PONG，恢复到“已连接”。</summary>
    private void RecoverLinkIfNeeded()
    {
        if (!_linkLost)
            return;

        _linkLost = false;

        PostUi(() =>
        {
            SetConnectionUi(true, _connectedPort);
            StartDecodeTimer();
            AddLog("设备恢复响应，已重新连接。");
        });
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
        EmptyHint.Visibility = decoded.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (pending.Length == 0)
        {
            // 空白由中间的提示承担，这里不要重复文案
            PreviewText.Text = string.Empty;
            PreviewText.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
            return;
        }

        PreviewText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        PreviewText.Text = string.Join(
            " ",
            pending.Select(c => c == '.' ? "·" : "—"));
    }

    // ---------- 断开与安全交接 ----------

    /// <summary>
    /// 请固件交还实体蜂鸣器，并等待它明确回复 BYE,BUZZER。
    /// 只发送不确认的旧实现，在端口随即被关闭时可能丢掉这条命令，
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

        KeyStateText.Text = "空闲";
        KeyDot.SetResourceReference(Shape.FillProperty, "Ink3");

        StopComputerTone();

        _outputMode = "BUZZER";
        _suppressUiEvents = true;
        OutputBuzzerRadio.IsChecked = true;
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
        if (connected)
        {
            _connectedPort = portName ?? _connectedPort;
        }
        else
        {
            _connectedPort = null;
            _linkLost = false;
        }

        bool lost = connected && _linkLost;

        // 三种状态：未连接（灰点）/ 已连接（橙点）/ 失联（橙色空心环）
        if (lost)
        {
            ConnectionDot.SetResourceReference(Shape.FillProperty, "Paper");
            ConnectionDot.SetResourceReference(Shape.StrokeProperty, "Accent");
            ConnectionDot.StrokeThickness = 1.8;
        }
        else
        {
            ConnectionDot.SetResourceReference(Shape.FillProperty, connected ? "Accent" : "Ink3");
            ConnectionDot.StrokeThickness = 0;
        }

        ConnectionText.Text = lost
            ? $"失联 · {portName}"
            : connected
                ? $"已连接 · {portName}"
                : "未连接";

        ConnectButton.Content = connected ? "断开连接" : "连接设备";

        DeviceInfoText.Text = lost
            ? "设备没有回应心跳，请断开后重新连接。"
            : connected
                ? "设备已就绪。点划时序由设备生成，电脑只负责发声与抄收。"
                : "请选择 ESP32-S3 的 USB OTG 端口。";

        // 失联时设备不可控，控制项一律禁用；但端口仍占着，所以不能去改端口。
        bool controllable = connected && !lost;

        PortCombo.IsEnabled = !connected;
        RefreshButton.IsEnabled = !connected;

        OutputBuzzerRadio.IsEnabled = controllable;
        OutputPcRadio.IsEnabled = controllable;
        OutputBothRadio.IsEnabled = controllable;

        ModeStraightRadio.IsEnabled = controllable;
        ModeIambicARadio.IsEnabled = controllable;
        ModeIambicBRadio.IsEnabled = controllable;

        WpmSlider.IsEnabled = controllable;
    }

    // ---------- 声音输出 ----------

    private void OutputSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || sender is not RadioButton radio)
            return;

        string mode = radio.Tag?.ToString() ?? "BUZZER";
        _outputMode = mode;

        if (!_isConnected)
            return;

        SendCommand($"SET,OUTPUT,{mode}");

        if (mode == "BUZZER")
            StopComputerTone();

        AddLog($"声音输出：{radio.Content}");
    }

    private void ModeSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || sender is not RadioButton radio)
            return;

        string mode = radio.Tag?.ToString() ?? "IAMBIC_B";
        _deviceMode = mode;

        if (!_isConnected)
            return;

        SendCommand($"SET,MODE,{mode}");
        AddLog($"键控模式：{radio.Content}");
    }

    private void WpmSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WpmValueText == null)
            return;

        int wpm = (int)Math.Round(e.NewValue);
        _pendingWpm = wpm;
        WpmValueText.Text = $"{wpm} WPM";
        UpdateTimingText(wpm);

        if (_suppressUiEvents || !_isConnected || _wpmApplyTimer == null)
            return;

        // 拖动过程中不逐格下发，停手后再发一次。
        _wpmApplyTimer.Stop();
        _wpmApplyTimer.Start();
    }

    private void ToneSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ToneValueText == null)
            return;

        _toneHz = (int)e.NewValue;
        ToneValueText.Text = $"{_toneHz} Hz";
        ScheduleSettingsSave();
    }

    private void VolumeSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValueText == null)
            return;

        _volume = (int)e.NewValue;
        VolumeValueText.Text = $"{_volume}%";
        ScheduleSettingsSave();
    }

    private void UpdateSliderLabels()
    {
        WpmValueText.Text = $"{(int)WpmSlider.Value} WPM";
        ToneValueText.Text = $"{(int)ToneSlider.Value} Hz";
        VolumeValueText.Text = $"{(int)VolumeSlider.Value}%";
        UpdateTimingText((int)WpmSlider.Value);
    }

    /// <summary>把当前速度换算成本地能看懂的点划时长。</summary>
    private void UpdateTimingText(int wpm)
    {
        if (KeyTimingText == null)
            return;

        int dot = (int)Math.Round(1200.0 / Math.Clamp(wpm, 5, 60));
        KeyTimingText.Text = $"点 {dot} · 划 {dot * 3} ms";
    }

    // ---------- 设置 ----------

    /// <summary>把上次保存的音调、音量套到界面上，并准备防抖保存。</summary>
    private void ApplySavedPreferences()
    {
        _settingsSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };

        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer!.Stop();

            App.Settings.ToneHz = _toneHz;
            App.Settings.Volume = _volume;
            App.Settings.Save();
        };

        int tone = Math.Clamp(App.Settings.ToneHz, 300, 1200);
        int volume = Math.Clamp(App.Settings.Volume, 0, 100);

        ToneSlider.Value = tone;
        VolumeSlider.Value = volume;

        _toneHz = tone;
        _volume = volume;

        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "2.1.0";
        AboutVersionText.Text = $"V{version}";
    }

    private void ScheduleSettingsSave()
    {
        if (_settingsSaveTimer is null)
            return;

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HideDictionary();

        _suppressThemeUi = true;

        try
        {
            (App.Settings.Theme switch
            {
                AppTheme.Light => ThemeLightRadio,
                AppTheme.Dark => ThemeDarkRadio,
                _ => ThemeSystemRadio
            }).IsChecked = true;
        }
        finally
        {
            _suppressThemeUi = false;
        }

        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void SettingsClose_Click(object sender, RoutedEventArgs e)
        => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void SettingsOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
            SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void ThemeSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeUi || sender is not RadioButton radio)
            return;

        AppTheme mode = radio.Tag?.ToString() switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.System
        };

        App.SetThemeMode(mode);
        AddLog($"外观：{radio.Content}");
    }

    // ---------- 电码表 ----------

    private bool _dictionaryBuilt;
    private bool _suppressThemeUi;
    private DispatcherTimer? _settingsSaveTimer;

    private void DictionaryButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;

        if (!_dictionaryBuilt)
        {
            DictionaryList.ItemsSource = MorseTable.All;
            _dictionaryBuilt = true;
        }

        DictionaryOverlay.Visibility = Visibility.Visible;
    }

    private void DictionaryClose_Click(object sender, RoutedEventArgs e)
        => HideDictionary();

    private void DictionaryOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点到遮罩本身才关，点卡片里面的内容不关
        if (ReferenceEquals(e.OriginalSource, sender))
            HideDictionary();
    }

    private void HideDictionary()
        => DictionaryOverlay.Visibility = Visibility.Collapsed;

    private void DictionaryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string code })
            PlayCode(code);
    }

    /// <summary>按当前 WPM 把一段点划播放出来。</summary>
    private async void PlayCode(string code)
    {
        int unit = (int)Math.Round(1200.0 / Math.Clamp(_pendingWpm, 5, 60));

        foreach (char c in code)
        {
            int mark = c == '.' ? unit : unit * 3;
            PlayComputerTone(mark);
            await Task.Delay(mark + unit);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (DictionaryOverlay.Visibility == Visibility.Visible)
        {
            HideDictionary();
            e.Handled = true;
        }
    }

    // ---------- 抄收区 ----------

    private void ClearPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        lock (_stateLock)
        {
            _decoder.Reset();
            _pressedPaddles.Clear();
        }

        PreviewText.Text = string.Empty;
        PreviewText.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
        DecodedText.Text = "";
        EmptyHint.Visibility = Visibility.Visible;
        KeyStateText.Text = "空闲";
        KeyDot.SetResourceReference(Shape.FillProperty, "Ink3");
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

    private void TestToneButton_Click(object sender, RoutedEventArgs e)
        => PlayComputerTone(350);

    private void PlayComputerTone(int durationMs)
        => _audio.PlayTone(durationMs, _toneHz, _volume);

    private void StartComputerToneLoop()
        => _audio.StartTone(_toneHz, _volume);

    private void StopComputerTone()
        => _audio.Stop();

    private void RefreshAudioDevices()
    {
        int keep = _audio.DeviceIndex;
        List<AudioDevice> devices = _audio.GetDevices();

        _suppressAudioUi = true;

        try
        {
            AudioDeviceCombo.ItemsSource = devices;

            AudioDevice selected = devices.FirstOrDefault(d => d.Index == keep) ?? devices[0];
            AudioDeviceCombo.SelectedItem = selected;
        }
        finally
        {
            _suppressAudioUi = false;
        }
    }

    private void AudioDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAudioUi || AudioDeviceCombo.SelectedItem is not AudioDevice device)
            return;

        _audio.SelectDevice(device.Index);
        AddLog($"电脑音频输出：{device.Name}");
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
                _audio.Dispose();
                Close();
            }
            catch
            {
                Application.Current?.Shutdown();
            }
        }
    }
}
