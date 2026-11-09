using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CWTrainer.Desktop.Qso;

namespace CWTrainer.Desktop;

/// <summary>
/// 通联模式（模拟）。与练习模式完全独立：
/// 不复用抄收、键控、发送任何一块，只共用顶栏的连接状态与设置。
/// </summary>
public partial class MainWindow
{
    private QsoSimulator? _qso;
    private ScopeRenderer? _scope;
    private DispatcherTimer? _qsoTimer;

    private double _qsoSpanHz = 25_000;
    private int _qsoStepHz = 100;
    private double[]? _qsoSpectrum;
    private int _qsoRowCounter;
    private int _qsoLabelCounter;
    private int _qsoActivityCounter;
    private bool _scopePrefilled;

    // 每个波段记住上次停留的频率，切回去时手不陌生
    private readonly Dictionary<string, long> _bandMemory = new();

    // 拖动调谐
    private bool _scopeDragging;
    private Point _scopeDragStart;
    private long _scopeDragStartFreq;

    private void InitQso()
    {
        _qso = new QsoSimulator();
        BuildBandButtons();

        _qsoTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50)   // 20 fps，与真机频谱刷新率接近
        };

        _qsoTimer.Tick += QsoTick;

        UpdateQsoLabels(force: true);
    }

    // ---------- 模式切换 ----------

    private void AppModeSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || PracticeView == null || QsoView == null)
            return;

        bool qso = radio.Tag?.ToString() == "Qso";

        PracticeView.Visibility = qso ? Visibility.Collapsed : Visibility.Visible;
        QsoView.Visibility = qso ? Visibility.Visible : Visibility.Collapsed;

        if (!qso)
        {
            StopQso();
            return;
        }

        StartQso();
    }

    private void StartQso()
    {
        if (_qso == null)
            InitQso();

        _qsoTimer?.Start();
        UpdateQsoLabels(force: true);
        RebuildActivityList();
    }

    private void StopQso()
    {
        _qsoTimer?.Stop();
        _audio.Stop();
    }

    // ---------- 渲染 ----------

    private void QsoTick(object? sender, EventArgs e)
    {
        if (_qso == null || _scope == null)
            return;

        const double dt = 0.05;

        _qso.Tick(dt);

        // 刚进通联模式时瀑布是空的，先补满一屏历史，否则看起来像坏了
        if (!_scopePrefilled)
        {
            PrefillWaterfall();
            _scopePrefilled = true;
        }

        _qsoSpectrum = _qso.BuildSpectrum(_scope.Width, _qsoSpanHz);

        // 瀑布 10 行/秒：再快只是更亮更糊，人眼看不出信息量
        if (++_qsoRowCounter >= 2)
        {
            _scope.PushWaterfallRow(_qsoSpectrum);
            _qsoRowCounter = 0;
        }

        _scope.Render(_qsoSpectrum);

        // 文字指标 4 Hz 刷新就够，避免每帧都排版
        if (++_qsoLabelCounter >= 5)
        {
            _qsoLabelCounter = 0;
            UpdateQsoLabels(force: false);
        }

        // 电台列表 2 秒重建一次：信号强度会变，排序也会跟着变
        if (++_qsoActivityCounter >= 40)
        {
            _qsoActivityCounter = 0;
            RebuildActivityList();
        }
    }

    /// <summary>用当前频谱把瀑布刷满，模拟"开机前它一直在扫描"。</summary>
    private void PrefillWaterfall()
    {
        if (_qso == null || _scope == null)
            return;

        for (int i = 0; i < _scope.WaterfallHeight; i++)
            _scope.PushWaterfallRow(_qso.BuildSpectrum(_scope.Width, _qsoSpanHz));
    }

    private void RebuildActivityList()
    {
        if (QsoActivityList == null || _qso == null)
            return;

        QsoActivityList.Children.Clear();

        foreach (Station station in _qso.StationsInBand.Take(5))
        {
            var row = new Button
            {
                Style = (Style)FindResource("DictItem"),
                Height = 34,
                Tag = station,
                Margin = new Thickness(-6, 0, -4, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            var freq = new TextBlock
            {
                Text = (station.FreqHz / 1e6).ToString("0.000"),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("Ink3"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var call = new TextBlock
            {
                Text = station.Call,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Ink"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(call, 1);

            var mode = new TextBlock
            {
                Text = ModeText(station.Mode),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(mode, 2);

            grid.Children.Add(freq);
            grid.Children.Add(call);
            grid.Children.Add(mode);

            row.Content = grid;
            row.Click += ActivityRow_Click;

            // 复合内容不会自动生成无障碍名称，这里显式给一个
            AutomationProperties.SetName(row, station.Call);

            QsoActivityList.Children.Add(row);
        }
    }

    private void ActivityRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Station station } || _qso == null)
            return;

        // SSB 不能停在载波上：USB 落在载波上方、LSB 落在下方，1.2 kHz 刚好在通带中间
        long target = station.Mode switch
        {
            EmissionMode.Usb => station.FreqHz + 1200,
            EmissionMode.Lsb => station.FreqHz - 1200,
            _ => station.FreqHz
        };

        _qso.FrequencyHz = RoundTo(target, 10);

        // 接收模式跟着对方走：点了 LSB 电台还停在 USB，等于什么都没收到
        RadioButton modeButton = station.Mode switch
        {
            EmissionMode.Usb => QsoUsbRadio,
            EmissionMode.Lsb => QsoLsbRadio,
            _ => QsoCwRadio
        };

        _qso.Mode = station.Mode;

        if (modeButton.IsChecked != true)
            modeButton.IsChecked = true;
        else
        {
            QsoBandwidthText.Text = BandwidthText(_qso.Mode);
            UpdatePassband();
        }

        UpdateQsoLabels(force: true);
    }

    private void UpdateQsoLabels(bool force)
    {
        if (_qso == null || QsoFreqText == null)
            return;

        long hz = _qso.FrequencyHz;

        QsoFreqText.Text = FormatHz(hz);
        QsoBandText.Text = $"Band {_qso.Band.Name}";

        double dbm = _qso.SMeterDbm;

        QsoDbmText.Text = $"{dbm:0} dBm";
        QsoSUnitText.Text = QsoSimulator.FormatSUnits(dbm);
        QsoMicrovoltText.Text = FormatMicrovolts(QsoSimulator.DbmToMicrovolts(dbm));

        UpdateSMeterBar(dbm);

        Station? station = _qso.TunedStation;

        QsoTunedCallsign.Text = station == null ? string.Empty : $"◉ {station.Call}";
        QsoSignalCall.Text = station?.Call ?? "无信号";
        QsoSignalCall.Foreground = (Brush)FindResource(station == null ? "Ink3" : "Ink");

        QsoSignalDetail.Text = station == null
            ? "调谐频率或点击瀑布，寻找波段的活跃信号"
            : $"{ModeText(station.Mode)} · {FormatHz(station.FreqHz)}\n" +
              $"{station.LevelDbm:0} dBm 中值，强度随传播起伏";

        QsoNoiseText.Text = $"{_qso.NoiseDbm:0} dBm";

        if (force)
        {
            QsoBandwidthText.Text = BandwidthText(_qso.Mode);
            UpdatePassband();
            RebuildScopeAxis();
        }
    }

    private static string BandwidthText(EmissionMode mode) => mode switch
    {
        EmissionMode.Cw => "CW · 500 Hz",
        EmissionMode.Usb => "USB · 2.4 kHz",
        EmissionMode.Lsb => "LSB · 2.4 kHz",
        _ => "—"
    };

    private static string ModeText(EmissionMode mode) => mode switch
    {
        EmissionMode.Cw => "CW",
        EmissionMode.Usb => "USB",
        EmissionMode.Lsb => "LSB",
        _ => "?"
    };

    /// <summary>把 14025000 写成 14.025.000，和真机的分隔方式一致。</summary>
    private static string FormatHz(long hz)
    {
        string digits = hz.ToString();
        var sb = new System.Text.StringBuilder(digits.Length + 3);
        int count = 0;

        for (int i = digits.Length - 1; i >= 0; i--)
        {
            sb.Insert(0, digits[i]);

            if (++count % 3 == 0 && i > 0)
                sb.Insert(0, '.');
        }

        return sb.ToString();
    }

    private static string FormatMicrovolts(double microvolts)
    {
        if (microvolts < 1)
            return $"{microvolts:0.0} µV";

        if (microvolts < 100)
            return $"{microvolts:0} µV";

        return $"{microvolts:0} µV";
    }

    // ---------- S 表 ----------

    private void UpdateSMeterBar(double dbm)
    {
        if (SMeterFill == null || SMeterHost == null)
            return;

        // 表盘范围：S1(-121) 到 S9+60(-13)，共 108 dB。
        // S9 = -73 dBm，每格 6 dB，所以 S1 是 -73 - 48 = -121。
        const double floorDbm = -121;
        const double rangeDb = 108;

        double fraction = Math.Clamp((dbm - floorDbm) / rangeDb, 0, 1);
        SMeterFill.Width = Math.Max(0, SMeterHost.ActualWidth * fraction);
    }

    private void SMeterHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => BuildSMeterTicks();

    private void BuildSMeterTicks()
    {
        if (SMeterTicks == null || SMeterTicks.ActualWidth <= 0)
            return;

        SMeterTicks.Children.Clear();

        double width = SMeterTicks.ActualWidth;

        // 刻度按真实 S 表的惯例：S1–S9 每格 6 dB，之后 +20/+40/+60 压缩显示
        (string Label, double Dbm)[] marks =
        {
            ("S1", -121), ("S3", -109), ("S5", -97), ("S7", -85), ("S9", -73),
            ("+20", -53), ("+40", -33), ("+60", -13)
        };

        const double floorDbm = -121;
        const double rangeDb = 108;

        foreach ((string label, double dbm) in marks)
        {
            double x = Math.Clamp((dbm - floorDbm) / rangeDb, 0, 1) * width;

            var tick = new Rectangle
            {
                Width = 1,
                Height = 4,
                Fill = (Brush)FindResource("Hairline"),
                SnapsToDevicePixels = true
            };

            Canvas.SetLeft(tick, Math.Min(x, width - 1));
            Canvas.SetTop(tick, 0);
            SMeterTicks.Children.Add(tick);

            var text = new TextBlock
            {
                Text = label,
                FontSize = 9.5,
                Foreground = (Brush)FindResource("Ink3")
            };

            Canvas.SetLeft(text, Math.Clamp(x - 6, 0, Math.Max(0, width - 18)));
            Canvas.SetTop(text, 4);
            SMeterTicks.Children.Add(text);
        }
    }

    // ---------- 频谱装饰 ----------

    private void ScopeHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ScopeHost == null)
            return;

        int width = (int)Math.Round(ScopeHost.ActualWidth);
        int height = (int)Math.Round(ScopeHost.ActualHeight);

        if (width < 120 || height < 120)
            return;

        int spectrumHeight = Math.Max(70, (int)(height * 0.33));
        int waterfallHeight = height - spectrumHeight;

        if (_scope == null ||
            Math.Abs(_scope.Width - width) > 1 ||
            _scope.SpectrumHeight != spectrumHeight)
        {
            _scope = new ScopeRenderer(width, spectrumHeight, waterfallHeight);
            ScopeImage.Source = _scope.Bitmap;
            _scopePrefilled = false;
        }

        RebuildScopeGrid(width, height, spectrumHeight);
        RebuildScopeAxis();
        UpdatePassband();
    }

    private void RebuildScopeGrid(int width, int height, int spectrumHeight)
    {
        if (ScopeGridCanvas == null)
            return;

        ScopeGridCanvas.Children.Clear();

        Brush hair = (Brush)FindResource("Hairline");

        // 频谱区的水平刻度
        for (int i = 1; i < 4; i++)
        {
            double y = spectrumHeight * i / 4.0;
            ScopeGridCanvas.Children.Add(new Line
            {
                X1 = 0, X2 = width, Y1 = y, Y2 = y,
                Stroke = hair, StrokeThickness = 1, Opacity = 0.45
            });
        }

        // 频率方向每 1/8 一根
        for (int i = 1; i < 8; i++)
        {
            double x = width * i / 8.0;
            ScopeGridCanvas.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = height,
                Stroke = hair, StrokeThickness = 1, Opacity = 0.3
            });
        }

        // 中心游标：只画在频谱区，瀑布里由通带阴影表达，
        // 否则一条贯穿到底的亮线会被误认成一个信号。
        ScopeGridCanvas.Children.Add(new Line
        {
            X1 = width / 2.0, X2 = width / 2.0,
            Y1 = 0, Y2 = spectrumHeight,
            Stroke = (Brush)FindResource("Accent"),
            StrokeThickness = 1,
            Opacity = 0.55
        });
    }

    private void RebuildScopeAxis()
    {
        if (ScopeAxis == null || _scope == null || _qso == null)
            return;

        double width = ScopeAxis.ActualWidth > 0 ? ScopeAxis.ActualWidth : _scope.Width;

        ScopeAxis.Children.Clear();

        for (int i = 0; i < 5; i++)
        {
            double fraction = i / 4.0;
            long hz = (long)Math.Round(_qso.FrequencyHz - _qsoSpanHz / 2 + _qsoSpanHz * fraction);

            var text = new TextBlock
            {
                Text = $"{hz / 1e6:0.000}",
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 9.5,
                Foreground = (Brush)FindResource("Ink3")
            };

            double x = width * fraction;
            double offset = text.Text.Length * 3.1;

            if (i == 0) offset = 0;
            if (i == 4) offset = text.Text.Length * 6.2;

            Canvas.SetLeft(text, Math.Clamp(x - offset, 0, Math.Max(0, width - 30)));
            Canvas.SetTop(text, 1);
            ScopeAxis.Children.Add(text);
        }
    }

    private void UpdatePassband()
    {
        if (PassbandRect == null || ScopeHost == null || _qso == null)
            return;

        double width = ScopeHost.ActualWidth;
        if (width <= 0)
            return;

        double bandwidth = _qso.Mode == EmissionMode.Cw ? QsoSimulator.CwBandwidthHz
                                                       : QsoSimulator.SsbBandwidthHz;

        double pixels = Math.Max(8, bandwidth / _qsoSpanHz * width);

        // SSB 的通带在载波一侧，视觉上偏一点更像真机
        double offsetPx = _qso.Mode switch
        {
            EmissionMode.Usb => 1500.0 / _qsoSpanHz * width,
            EmissionMode.Lsb => -1500.0 / _qsoSpanHz * width,
            _ => 0
        };

        PassbandRect.Width = pixels;
        PassbandRect.Height = _scope?.SpectrumHeight ?? 120;
        PassbandRect.VerticalAlignment = VerticalAlignment.Top;
        PassbandRect.HorizontalAlignment = HorizontalAlignment.Left;
        PassbandRect.Margin = new Thickness(width / 2 - pixels / 2 + offsetPx, 0, 0, 0);
    }

    // ---------- 交互 ----------

    private void QsoMode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || _qso == null)
            return;

        _qso.Mode = radio.Tag?.ToString() switch
        {
            "USB" => EmissionMode.Usb,
            "LSB" => EmissionMode.Lsb,
            _ => EmissionMode.Cw
        };

        QsoBandwidthText.Text = BandwidthText(_qso.Mode);
        UpdatePassband();
        UpdateQsoLabels(force: false);
    }

    private void QsoStep_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio)
            return;

        if (int.TryParse(radio.Tag?.ToString(), out int step))
            _qsoStepHz = step;
    }

    private void QsoSpan_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || _qso == null)
            return;

        if (int.TryParse(radio.Tag?.ToString(), out int khz))
            _qsoSpanHz = khz * 1000.0;

        RebuildScopeAxis();
        UpdatePassband();
    }

    private void QsoTuneUp_Click(object sender, RoutedEventArgs e) => Tune(_qsoStepHz);

    private void QsoTuneDown_Click(object sender, RoutedEventArgs e) => Tune(-_qsoStepHz);

    private void Tune(int deltaHz)
    {
        if (_qso == null)
            return;

        _qso.FrequencyHz += deltaHz;
        UpdateQsoLabels(force: false);
    }

    private void Scope_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_qso == null)
            return;

        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? _qsoStepHz * 10 : _qsoStepHz;
        Tune(e.Delta > 0 ? step : -step);
        e.Handled = true;
    }

    private void Scope_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_qso == null || ScopeHost == null)
            return;

        _scopeDragging = true;
        _scopeDragStart = e.GetPosition(ScopeHost);
        _scopeDragStartFreq = _qso.FrequencyHz;

        ScopeHost.CaptureMouse();
    }

    private void Scope_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_scopeDragging || _qso == null || ScopeHost == null)
            return;

        Point now = e.GetPosition(ScopeHost);
        double dx = now.X - _scopeDragStart.X;

        if (Math.Abs(dx) < 3)
            return;

        double hzPerPixel = _qsoSpanHz / Math.Max(1, ScopeHost.ActualWidth);
        long target = _scopeDragStartFreq - (long)Math.Round(dx * hzPerPixel);

        _qso.FrequencyHz = RoundTo(target, 10);
        UpdateQsoLabels(force: false);
    }

    private void Scope_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scopeDragging)
            return;

        _scopeDragging = false;
        ScopeHost.ReleaseMouseCapture();

        if (_qso == null || ScopeHost == null)
            return;

        Point now = e.GetPosition(ScopeHost);
        double dx = Math.Abs(now.X - _scopeDragStart.X);

        // 没怎么动就是"点一下跳过去"
        if (dx < 3)
        {
            double hzPerPixel = _qsoSpanHz / Math.Max(1, ScopeHost.ActualWidth);
            double offset = (now.X - ScopeHost.ActualWidth / 2) * hzPerPixel;

            _qso.FrequencyHz = RoundTo(_qso.FrequencyHz + (long)Math.Round(offset), 10);
            UpdateQsoLabels(force: false);
        }
    }

    private void Scope_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_scopeDragging)
            return;

        _scopeDragging = false;
        ScopeHost?.ReleaseMouseCapture();
    }

    private static long RoundTo(long value, int step) => (long)Math.Round(value / (double)step) * step;

    // ---------- 波段 ----------

    private void BuildBandButtons()
    {
        if (BandGrid == null)
            return;

        BandGrid.Children.Clear();
        _bandMemory.Clear();

        foreach (BandInfo band in QsoSimulator.Bands)
        {
            var button = new RadioButton
            {
                Content = band.Name,
                Tag = band,
                GroupName = "QsoBand",
                Style = (Style)FindResource("BandButton"),
                IsChecked = band.Name == "20m"
            };

            button.Click += BandButton_Click;
            BandGrid.Children.Add(button);
        }
    }

    private void BandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: BandInfo band } || _qso == null)
            return;

        // 记住上个波段的频率
        _bandMemory[_qso.Band.Name] = _qso.FrequencyHz;

        long target = _bandMemory.TryGetValue(band.Name, out long saved)
            ? saved
            : DefaultFrequencyFor(band);

        _qso.FrequencyHz = target;
        RebuildScopeAxis();
        UpdateQsoLabels(force: true);
        RebuildActivityList();
    }

    /// <summary>默认停在波段里的热门段：CW 在低端，SSB 在中段。</summary>
    private long DefaultFrequencyFor(BandInfo band)
        => _qso?.Mode switch
        {
            EmissionMode.Cw => band.StartHz + 25_000,
            EmissionMode.Usb => band.StartHz + (long)((band.EndHz - band.StartHz) * 0.62),
            _ => band.StartHz + (long)((band.EndHz - band.StartHz) * 0.55)
        };

    // ---------- 音频 ----------

    private void NoiseSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || NoiseVolumeSlider == null)
            return;

        bool on = radio.Tag?.ToString() == "1";

        if (on)
            _audio.StartNoise((int)NoiseVolumeSlider.Value);
        else
            _audio.Stop();
    }

    private void NoiseVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NoiseVolumeText == null)
            return;

        NoiseVolumeText.Text = $"{(int)e.NewValue}%";

        if (NoiseOnRadio?.IsChecked == true)
            _audio.StartNoise((int)e.NewValue);
    }
}
