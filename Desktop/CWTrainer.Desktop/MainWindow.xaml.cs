using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CWTrainer.Desktop;

public partial class MainWindow : Window
{
    private const int BaudRate = 115200;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;
    private const uint SndLoop = 0x0008;

    private readonly object _receiveLock = new();
    private readonly StringBuilder _receiveBuffer = new();
    private readonly ObservableCollection<string> _events = new();
    private readonly HashSet<string> _pressedPaddles = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _heartbeatTimer;

    private SerialPort? _port;
    private TaskCompletionSource<bool>? _handshakeSource;
    private bool _isConnected;
    private bool _suppressUiEvents;
    private string _outputMode = "BUZZER";
    private string _deviceMode = "IAMBIC_B";

    private byte[]? _activeWave;
    private GCHandle _activeWavePin;
    private int _audioGeneration;

    public MainWindow()
    {
        InitializeComponent();
        EventList.ItemsSource = _events;
        OutputCombo.SelectedIndex = 0;
        ModeCombo.SelectedIndex = 2;
        RefreshPorts();
        UpdateSliderLabels();
        SetConnectionUi(false, null);

        _heartbeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _heartbeatTimer.Tick += (_, _) => SendCommand("PING");
    }


    private void RefreshPorts()
    {
        string? previous = PortCombo.SelectedItem as string;
        PortCombo.Items.Clear();

        try
        {
            foreach (string name in SerialPort.GetPortNames()
                         .OrderBy(ParseComNumber))
            {
                PortCombo.Items.Add(name);
            }
        }
        catch (Exception ex)
        {
            AddLog($"读取 COM 端口失败：{ex.Message}");
        }

        // 保留之前选择的端口，但不再自动选择第一个。
        if (previous is not null &&
            PortCombo.Items.Contains(previous))
        {
            PortCombo.SelectedItem = previous;
        }
        else
        {
            PortCombo.SelectedIndex = -1;
        }

        AddLog($"发现 {PortCombo.Items.Count} 个串口，请选择 ESP32-S3 的 OTG 接口。");
    }


    private static int ParseComNumber(string name)
    {
        string digits = new(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int number) ? number : int.MaxValue;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_port?.IsOpen == true)
        {
            DisconnectDevice("用户断开连接");
            return;
        }

        if (PortCombo.SelectedItem is not string portName)
        {
            MessageBox.Show("没有可用的 COM 端口。请先连接 ESP32-S3 并刷新端口。", "CW Trainer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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
                Handshake = System.IO.Ports.Handshake.None
            };

            port.DataReceived += Port_DataReceived;
            port.Open();
            _port = port;
            await Task.Delay(300);
            port.DiscardInBuffer();
            var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _handshakeSource = handshake;
            AddLog($"已打开 {portName}，正在握手…");
            ConnectButton.IsEnabled = false;

            // The board may take a moment to enumerate after opening the native USB CDC port.
            // Retry HELLO instead of assuming that opening COM means the trainer is ready.

            for (int attempt = 1;
                 attempt <= 12 && !handshake.Task.IsCompleted;
                 attempt++)
            {
                AddLog($"发送设备握手 HELLO ({attempt}/12)");

                SendCommand("HELLO,1");

                await Task.Delay(500);
            }

            Task completed = await Task.WhenAny(
                handshake.Task,
                Task.Delay(1000)
            );

            if (completed != handshake.Task || !handshake.Task.Result)
            {
                DisconnectDevice("握手超时");
                MessageBox.Show(
                    "串口已经打开，但没有收到 CW Trainer 的 READY 握手。\n\n请确认：\n1. 已经烧录 CW Trainer V2 固件；\n2. Arduino 串口监视器已经关闭；\n3. 选择的是开发板运行时的 COM 端口；\n4. USB 数据线没有松动。",
                    "设备未响应", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isConnected = true;
            SetConnectionUi(true, portName);
            _heartbeatTimer.Start();
            SendCommand("GET");
            AddLog("设备握手成功。现在可以选择电脑发声。");
        }
        catch (Exception ex)
        {
            DisconnectDevice("连接失败");
            MessageBox.Show($"无法打开串口：{ex.Message}\n\n请关闭 Arduino 串口监视器等占用该 COM 口的程序。",
                "串口连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
                AddLog($"USB 串口接收异常：{ex.Message}");
            }));

            return;
        }

        if (string.IsNullOrEmpty(chunk)) return;

        List<string> completedLines = new();
        lock (_receiveLock)
        {
            _receiveBuffer.Append(chunk);
            while (true)
            {
                int newline = _receiveBuffer.ToString().IndexOf('\n');
                if (newline < 0) break;

                string line = _receiveBuffer.ToString(0, newline).Trim('\r', ' ', '\t');
                _receiveBuffer.Remove(0, newline + 1);
                if (line.Length > 0) completedLines.Add(line);
            }

            // Guard against a corrupt or unterminated stream growing forever.
            if (_receiveBuffer.Length > 2048) _receiveBuffer.Clear();
        }

        foreach (string line in completedLines)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => ProcessDeviceLine(line)));
        }
    }

    private void ProcessDeviceLine(string line)
    {
        if (line.StartsWith("READY,1,CWTRAINER,", StringComparison.Ordinal))
        {
            _isConnected = true;
            _handshakeSource?.TrySetResult(true);
            SetConnectionUi(true, _port?.PortName ?? "USB CDC");
            AddLog($"设备已就绪：{line}");
            return;
        }

        if (line.StartsWith("STATUS,", StringComparison.Ordinal))
        {
            ParseStatus(line);
            AddLog(line);
            return;
        }

        string[] parts = line.Split(',');
        if (parts.Length >= 3 && parts[0] == "KEY")
        {
            string paddle = parts[1];
            bool pressed = parts[2] == "1";
            string label = paddle == "DIT" ? "LEFT · DIT（点）" : "RIGHT · DAH（划）";
            if (pressed) _pressedPaddles.Add(paddle);
            else _pressedPaddles.Remove(paddle);

            KeyStateText.Text = _pressedPaddles.Count == 0
                ? "等待电键输入…"
                : string.Join("  +  ", _pressedPaddles.Select(p => p == "DIT" ? "LEFT · DIT" : "RIGHT · DAH"));
            AddLog($"{label} {(pressed ? "按下" : "松开")}");
            return;
        }

        if (parts.Length >= 3 && parts[0] == "ELEMENT")
        {
            bool isDit = parts[1] == "DIT";
            if (!uint.TryParse(parts[2], out uint durationUs)) return;
            string symbol = isDit ? "·" : "—";
            AppendPreview(symbol);

            if (_outputMode is "PC" or "BOTH")
            {
                int durationMs = Math.Clamp((int)Math.Round(durationUs / 1000.0), 1, 2000);
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
                if (value == "BUZZER") StopComputerTone();
            }
        }
        UpdateSliderLabels();
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void OutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || OutputCombo.SelectedItem is not ComboBoxItem item) return;
        string mode = item.Tag?.ToString() ?? "BUZZER";
        _outputMode = mode;
        if (_isConnected)
        {
            SendCommand($"SET,OUTPUT,{mode}");
            if (mode == "BUZZER") StopComputerTone();
            AddLog($"切换声音输出：{item.Content}");
        }
    }

    private void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConnected)
        {
            MessageBox.Show("请先连接练习器，再应用键控参数。", "尚未连接", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int wpm = (int)WpmSlider.Value;
        string mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "IAMBIC_B";
        SendCommand($"SET,WPM,{wpm}");
        SendCommand($"SET,MODE,{mode}");
        _deviceMode = mode;
        AddLog($"应用设置：{wpm} WPM / {mode}");
    }

    private void WpmSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WpmValueText is not null) WpmValueText.Text = $"{(int)WpmSlider.Value} WPM";
    }

    private void ToneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ToneValueText is not null) ToneValueText.Text = $"{(int)ToneSlider.Value} Hz";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValueText is not null) VolumeValueText.Text = $"{(int)VolumeSlider.Value}%";
    }

    private void UpdateSliderLabels()
    {
        WpmValueText.Text = $"{(int)WpmSlider.Value} WPM";
        ToneValueText.Text = $"{(int)ToneSlider.Value} Hz";
        VolumeValueText.Text = $"{(int)VolumeSlider.Value}%";
    }

    private void TestToneButton_Click(object sender, RoutedEventArgs e) => PlayComputerTone(350);

    private void AppendPreview(string symbol)
    {
        string current = PreviewText.Text == "等待发送…" ? string.Empty : PreviewText.Text;
        current += symbol + " ";
        if (current.Length > 240) current = current[^240..];
        PreviewText.Text = current;
    }

    private void ClearPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        PreviewText.Text = "等待发送…";
        _pressedPaddles.Clear();
        KeyStateText.Text = "等待电键输入…";
    }

    private void AddLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _events.Insert(0, $"{timestamp}  {message}");
        while (_events.Count > 100) _events.RemoveAt(_events.Count - 1);
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

    private void SetConnectionUi(bool connected, string? portName)
    {
        ConnectionDot.Fill = connected ? new SolidColorBrush(Color.FromRgb(69, 194, 133)) : new SolidColorBrush(Color.FromRgb(212, 155, 69));
        ConnectionText.Text = connected ? $"已连接 · {portName}" : "未连接";
        ConnectButton.Content = connected ? "断开连接" : "连接练习器";
        DeviceInfoText.Text = connected
            ? "设备握手成功。现在可以安全切换到电脑软件发声。"
            : "连接设备后可切换电脑发声、设置速度和键控模式。";
        OutputCombo.IsEnabled = connected;
        ModeCombo.IsEnabled = connected;
        WpmSlider.IsEnabled = connected;
        ApplySettingsButton.IsEnabled = connected;
    }

    private void DisconnectDevice(string reason)
    {
        _heartbeatTimer.Stop();
        try
        {
            if (_port?.IsOpen == true)
            {
                // Explicitly restore the physical buzzer before closing. The
                // firmware also falls back to BUZZER after heartbeat timeout.
                _port.Write("SET,OUTPUT,BUZZER\n");
                _port.BaseStream.Flush();
                Thread.Sleep(40);
            }
        }
        catch { /* closing a vanished USB device is expected to fail sometimes */ }

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
        _pressedPaddles.Clear();
        KeyStateText.Text = "等待电键输入…";
        StopComputerTone();
        _outputMode = "BUZZER";
        _suppressUiEvents = true;
        OutputCombo.SelectedIndex = 0;
        _suppressUiEvents = false;
        SetConnectionUi(false, null);
        if (!string.IsNullOrWhiteSpace(reason)) AddLog(reason);
    }

    // ---------------- Windows built-in audio (winmm PlaySound) ----------------
    // No NAudio or other audio NuGet package is needed. The software creates
    // small PCM WAV buffers and asks Windows to play them asynchronously.

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativePlaySound(IntPtr sound, IntPtr module, uint flags);

    private void PlayComputerTone(int durationMs)
    {
        int volume = (int)VolumeSlider.Value;
        if (volume <= 0) return;
        StopComputerTone();

        byte[] wav = CreateToneWave(durationMs, (int)ToneSlider.Value, volume, loopable: false);
        _activeWave = wav;
        _activeWavePin = GCHandle.Alloc(wav, GCHandleType.Pinned);
        bool played = NativePlaySound(_activeWavePin.AddrOfPinnedObject(), IntPtr.Zero,
            SndAsync | SndMemory | SndNoDefault);

        if (!played)
        {
            ReleaseAudioBuffer(stopPlayback: true);
            AddLog("Windows 音频播放失败，请检查默认音频输出设备。");
            return;
        }

        int generation = ++_audioGeneration;
        _ = Task.Delay(durationMs + 300).ContinueWith(_ =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation == _audioGeneration)
                    ReleaseAudioBuffer(stopPlayback: false);
            })));
    }

    private void StartComputerToneLoop()
    {
        int volume = (int)VolumeSlider.Value;
        if (volume <= 0) return;
        StopComputerTone();
        byte[] wav = CreateToneWave(1000, (int)ToneSlider.Value, volume, loopable: true);
        _activeWave = wav;
        _activeWavePin = GCHandle.Alloc(wav, GCHandleType.Pinned);
        bool played = NativePlaySound(_activeWavePin.AddrOfPinnedObject(), IntPtr.Zero,
            SndAsync | SndMemory | SndLoop | SndNoDefault);
        if (!played)
        {
            ReleaseAudioBuffer(stopPlayback: true);
            AddLog("Windows 连续音频播放失败。");
        }
    }

    private void StopComputerTone()
    {
        _audioGeneration++;
        ReleaseAudioBuffer(stopPlayback: true);
    }

    private void ReleaseAudioBuffer(bool stopPlayback)
    {
        if (stopPlayback)
            NativePlaySound(IntPtr.Zero, IntPtr.Zero, 0);

        if (_activeWavePin.IsAllocated) _activeWavePin.Free();
        _activeWave = null;
    }

    private static byte[] CreateToneWave(int durationMs, int frequencyHz, int volumePercent, bool loopable)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        int sampleCount = Math.Max(1, sampleRate * durationMs / 1000);
        int dataBytes = sampleCount * (bitsPerSample / 8);
        byte[] wav = new byte[44 + dataBytes];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), sampleRate * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), (short)(channels * bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), dataBytes);

        double amplitude = Math.Clamp(volumePercent, 0, 100) / 100.0 * 0.48;
        int fadeSamples = Math.Min(sampleRate / 250, sampleCount / 2); // 4 ms fade
        int safeFrequency = Math.Clamp(frequencyHz, 100, 3000);

        for (int i = 0; i < sampleCount; i++)
        {
            double envelope = 1.0;
            if (fadeSamples > 0 && i < fadeSamples)
                envelope = i / (double)fadeSamples;
            if (fadeSamples > 0 && i >= sampleCount - fadeSamples)
                envelope = Math.Min(envelope, (sampleCount - 1 - i) / (double)fadeSamples);

            double sample = Math.Sin(2.0 * Math.PI * safeFrequency * i / sampleRate) * amplitude * envelope;
            short pcm = (short)Math.Clamp((int)Math.Round(sample * short.MaxValue), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2, 2), pcm);
        }

        return wav;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        DisconnectDevice("应用程序关闭");
    }
}
