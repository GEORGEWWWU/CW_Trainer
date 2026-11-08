using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CWTrainer.Desktop;

public partial class MainWindow : Window
{
    private const int BaudRate = 115200;

    private readonly ObservableCollection<string> _events = new();
    private readonly HashSet<string> _pressedPaddles = new();
    private readonly StringBuilder _morseBuffer = new();
    private readonly object _receiveLock = new();
    private readonly StringBuilder _receiveBuffer = new();

    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _characterTimer;
    private readonly DispatcherTimer _wordTimer;

    private SerialPort? _port;
    private TaskCompletionSource<bool>? _handshakeSource;
    private TaskCompletionSource<bool>? _byeSource;

    private bool _isConnected;
    private bool _suppressUiEvents;
    private bool _isClosing;
    private string _outputMode = "BUZZER";
    private string _deviceMode = "IAMBIC_B";

    private byte[]? _activeWave;
    private GCHandle _activeWavePin;
    private int _audioGeneration;

    private static readonly Dictionary<string, char> MorseMap = new()
    {
        [".-"] = 'A',
        ["-..."] = 'B',
        ["-.-."] = 'C',
        ["-.."] = 'D',
        ["."] = 'E',
        ["..-."] = 'F',
        ["--."] = 'G',
        ["...."] = 'H',
        [".."] = 'I',
        [".---"] = 'J',
        ["-.-"] = 'K',
        [".-.."] = 'L',
        ["--"] = 'M',
        ["-."] = 'N',
        ["---"] = 'O',
        [".--."] = 'P',
        ["--.-"] = 'Q',
        [".-."] = 'R',
        ["..."] = 'S',
        ["-"] = 'T',
        ["..-"] = 'U',
        ["...-"] = 'V',
        [".--"] = 'W',
        ["-..-"] = 'X',
        ["-.--"] = 'Y',
        ["--.."] = 'Z',

        ["-----"] = '0',
        [".----"] = '1',
        ["..---"] = '2',
        ["...--"] = '3',
        ["....-"] = '4',
        ["....."] = '5',
        ["-...."] = '6',
        ["--..."] = '7',
        ["---.."] = '8',
        ["----."] = '9',

        [".-.-.-"] = '.',
        ["--..--"] = ',',
        ["..--.."] = '?',
        [".----."] = '\'',
        ["-.-.--"] = '!',
        ["-..-."] = '/',
        ["-.--."] = '(',
        ["-.--.-"] = ')',
        [".-..."] = '&',
        ["---..."] = ':',
        ["-.-.-."] = ';',
        ["-...-"] = '=',
        [".-.-."] = '+',
        ["-....-"] = '-',
        ["..--.-"] = '_',
        [".-..-."] = '"',
        ["...-..-"] = '$',
        [".--.-."] = '@'
    };

    public MainWindow()
    {
        InitializeComponent();

        EventList.ItemsSource = _events;

        OutputCombo.SelectedIndex = 0;
        ModeCombo.SelectedIndex = 2;

        _heartbeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _heartbeatTimer.Tick += (_, _) => SendCommand("PING");

        _characterTimer = new DispatcherTimer();
        _characterTimer.Tick += (_, _) => FinishMorseCharacter();

        _wordTimer = new DispatcherTimer();
        _wordTimer.Tick += (_, _) => FinishMorseWord();

        RefreshPorts();
        UpdateSliderLabels();
        SetConnectionUi(false, null);
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

            lock (_receiveLock)
                _receiveBuffer.Clear();

            _handshakeSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            AddLog($"已打开 {portName}，正在连接设备…");

            for (int i = 0; i < 12 && !_handshakeSource.Task.IsCompleted; i++)
            {
                SendCommand("HELLO,1");
                await Task.Delay(500);
            }

            Task completed = await Task.WhenAny(
                _handshakeSource.Task,
                Task.Delay(1000));

            if (completed != _handshakeSource.Task ||
                !await _handshakeSource.Task)
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
            _heartbeatTimer.Start();

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
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                AddLog($"USB 连接异常：{ex.Message}");
                _ = DisconnectDeviceAsync("USB 连接中断，等待硬件蜂鸣器接管");
            }));
            return;
        }

        if (string.IsNullOrEmpty(chunk)) return;

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
            _ = Dispatcher.BeginInvoke(new Action(() => ProcessDeviceLine(line)));
    }

    private void ProcessDeviceLine(string line)
    {
        if (line.StartsWith("READY,1,CWTRAINER,", StringComparison.Ordinal))
        {
            _handshakeSource?.TrySetResult(true);
            _isConnected = true;
            SetConnectionUi(true, _port?.PortName);
            AddLog("设备已就绪。");
            return;
        }

        if (line == "BYE,BUZZER")
        {
            _byeSource?.TrySetResult(true);
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
            string paddle = parts[1];
            bool pressed = parts[2] == "1";

            if (pressed) _pressedPaddles.Add(paddle);
            else _pressedPaddles.Remove(paddle);

            KeyStateText.Text = _pressedPaddles.Count == 0
                ? "等待输入"
                : string.Join("  +  ", _pressedPaddles.Select(
                    p => p == "DIT" ? "点 ·" : "划 —"));

            return;
        }

        if (parts.Length >= 3 && parts[0] == "ELEMENT")
        {
            bool isDit = parts[1] == "DIT";
            if (!uint.TryParse(parts[2], out uint durationUs)) return;

            AppendMorseElement(isDit ? "." : "-");

            if (_outputMode is "PC" or "BOTH")
            {
                int durationMs = Math.Clamp(
                    (int)Math.Round(durationUs / 1000.0), 1, 2000);
                PlayComputerTone(durationMs);
            }
            return;
        }

        if (parts.Length >= 2 && parts[0] == "TONE")
        {
            if (_outputMode is "PC" or "BOTH")
            {
                if (parts[1] == "ON") StartComputerToneLoop();
                else if (parts[1] == "OFF") StopComputerTone();
            }
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

    private void ParseStatus(string line)
    {
        string[] p = line.Split(',');

        for (int i = 1; i + 1 < p.Length; i += 2)
        {
            string key = p[i];
            string value = p[i + 1];

            if (key == "WPM" && int.TryParse(value, out int wpm))
            {
                _suppressUiEvents = true;
                WpmSlider.Value = Math.Clamp(wpm, 5, 60);
                _suppressUiEvents = false;
            }
            else if (key == "MODE")
            {
                _deviceMode = value;
                _suppressUiEvents = true;
                SelectComboByTag(ModeCombo, value);
                _suppressUiEvents = false;
            }
            else if (key == "OUTPUT")
            {
                _outputMode = value;
                _suppressUiEvents = true;
                SelectComboByTag(OutputCombo, value);
                _suppressUiEvents = false;

                if (value == "BUZZER")
                    StopComputerTone();
            }
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
            if (_port?.IsOpen == true)
                _port.Write(command + "\n");
        }
        catch (Exception ex)
        {
            AddLog($"发送命令失败：{ex.Message}");
        }
    }

    // ---------- 断开与安全接管 ----------

    private async Task DisconnectDeviceAsync(string reason)
    {
        _heartbeatTimer.Stop();

        if (_port?.IsOpen == true && _isConnected)
        {
            try
            {
                _byeSource = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                SendCommand("BYE");

                // 等待固件明确回复已经切回实体蜂鸣器。
                Task completed = await Task.WhenAny(
                    _byeSource.Task,
                    Task.Delay(700));

                if (completed == _byeSource.Task && _byeSource.Task.Result)
                    AddLog("实体蜂鸣器已接管。");
                else
                    AddLog("未收到交接确认，将由固件心跳超时保护接管。");
            }
            catch
            {
                AddLog("串口已中断，依靠固件心跳保护接管。");
            }
        }

        ClosePortAndReset(reason);
    }

    private void ClosePortAndReset(string reason)
    {
        _heartbeatTimer.Stop();
        _characterTimer.Stop();
        _wordTimer.Stop();

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
        _isConnected = false;

        _handshakeSource?.TrySetResult(false);
        _handshakeSource = null;
        _byeSource = null;

        _pressedPaddles.Clear();
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
        AddLog($"设置已发送：{wpm} WPM / {mode}");
    }

    private void WpmSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
        => WpmValueText.Text = $"{(int)WpmSlider.Value} WPM";

    private void ToneSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
        => ToneValueText.Text = $"{(int)ToneSlider.Value} Hz";

    private void VolumeSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
        => VolumeValueText.Text = $"{(int)VolumeSlider.Value}%";

    private void UpdateSliderLabels()
    {
        WpmValueText.Text = $"{(int)WpmSlider.Value} WPM";
        ToneValueText.Text = $"{(int)ToneSlider.Value} Hz";
        VolumeValueText.Text = $"{(int)VolumeSlider.Value}%";
    }

    // ---------- 摩斯码解码 ----------

    private void AppendMorseElement(string element)
    {
        _morseBuffer.Append(element);

        string visual = element == "." ? "·" : "—";
        PreviewText.Text = PreviewText.Text == "等待输入"
            ? visual
            : PreviewText.Text + " " + visual;

        int ditMs = (int)(1200.0 / Math.Max(5, WpmSlider.Value));

        _characterTimer.Stop();
        _wordTimer.Stop();

        _characterTimer.Interval = TimeSpan.FromMilliseconds(ditMs * 3);
        _wordTimer.Interval = TimeSpan.FromMilliseconds(ditMs * 7);

        _characterTimer.Start();
        _wordTimer.Start();
    }

    private void FinishMorseCharacter()
    {
        _characterTimer.Stop();

        if (_morseBuffer.Length == 0) return;

        string code = _morseBuffer.ToString();
        _morseBuffer.Clear();

        if (MorseMap.TryGetValue(code, out char decoded))
        {
            DecodedText.Text += decoded;

            if (DecodedText.Text.Length > 2000)
                DecodedText.Text = DecodedText.Text[^2000..];
        }
        else
        {
            DecodedText.Text += "�";
        }
    }

    private void FinishMorseWord()
    {
        _wordTimer.Stop();

        if (_morseBuffer.Length > 0)
            FinishMorseCharacter();

        if (DecodedText.Text.Length > 0 &&
            !DecodedText.Text.EndsWith(' '))
        {
            DecodedText.Text += " ";
        }
    }

    private void ClearPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        _characterTimer.Stop();
        _wordTimer.Stop();
        _morseBuffer.Clear();

        PreviewText.Text = "等待输入";
        DecodedText.Text = "";
        _pressedPaddles.Clear();
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
            _ = Dispatcher.BeginInvoke(new Action(() => AddLog(message)));
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
        int volume = (int)VolumeSlider.Value;
        if (volume <= 0) return;

        StopComputerTone();

        byte[] wav = CreateToneWave(
            durationMs, (int)ToneSlider.Value, volume);

        _activeWave = wav;
        _activeWavePin = GCHandle.Alloc(wav, GCHandleType.Pinned);

        if (!NativePlaySound(
            _activeWavePin.AddrOfPinnedObject(),
            IntPtr.Zero,
            SndAsync | SndMemory | SndNoDefault))
        {
            ReleaseAudioBuffer(true);
            AddLog("电脑音频播放失败。");
            return;
        }

        int generation = ++_audioGeneration;

        _ = Task.Delay(durationMs + 200).ContinueWith(_ =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation == _audioGeneration)
                    ReleaseAudioBuffer(false);
            })));
    }

    private void StartComputerToneLoop()
    {
        int volume = (int)VolumeSlider.Value;
        if (volume <= 0) return;

        StopComputerTone();

        byte[] wav = CreateToneWave(
            1000, (int)ToneSlider.Value, volume);

        _activeWave = wav;
        _activeWavePin = GCHandle.Alloc(wav, GCHandleType.Pinned);

        NativePlaySound(
            _activeWavePin.AddrOfPinnedObject(),
            IntPtr.Zero,
            SndAsync | SndMemory | SndLoop | SndNoDefault);
    }

    private void StopComputerTone()
    {
        _audioGeneration++;
        ReleaseAudioBuffer(true);
    }

    private void ReleaseAudioBuffer(bool stopPlayback)
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
        _isClosing = true;

        // 关闭窗口时不阻塞等待 UI 回调。
        // BYE 尝试发送；若 USB 已断开，固件心跳超时会兜底。
        try
        {
            if (_port?.IsOpen == true && _isConnected)
                _port.Write("BYE\n");
        }
        catch { }

        ClosePortAndReset("应用程序关闭");
    }
}