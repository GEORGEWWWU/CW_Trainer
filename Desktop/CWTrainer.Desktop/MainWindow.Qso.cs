using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    /// <summary>侧音频率。真机 600–800 Hz，取 700 是 Elecraft/八重洲的常见默认值。</summary>
    private const int QsoPitchHz = 700;

    /// <summary>瀑布每行代表多少毫秒。12.5 ms/行 × 每帧 4 行 = 80 px/秒。</summary>
    private const double WaterfallMsPerRow = 12.5;

    private const int WaterfallRowsPerTick = 4;

    private QsoSimulator? _qso;
    private ScopeRenderer? _scope;
    private QsoAudioEngine? _qsoAudio;
    private DispatcherTimer? _qsoTimer;

    private double _qsoSpanHz = 25_000;
    private int _qsoStepHz = 100;
    private double[]? _qsoSpectrum;
    private int _qsoLabelCounter;
    private int _qsoActivityCounter;
    private int _qsoAudioCounter;
    private bool _scopePrefilled;

    private bool _qsoNoiseOn;
    private int _qsoVolume = 25;

    // 每个波段记住上次停留的频率，切回去时手不陌生
    private readonly Dictionary<string, long> _bandMemory = new();

    // S 表峰值保持
    private double _peakFraction;
    private double _peakHoldSeconds;

    // 拖动调谐
    private bool _scopeDragging;
    private Point _scopeDragStart;
    private long _scopeDragStartFreq;

    private bool _qsoVisible => QsoView != null && QsoView.Visibility == Visibility.Visible;

    private void InitQso()
    {
        _qso = new QsoSimulator();
        BuildBandButtons();

        _qsoAudio = new QsoAudioEngine(AddLog);

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
        _qsoAudio?.Start();

        // 之前开着底噪，切回来时要把开关的状态真正恢复上，
        // 不能只是"开关看着是开的"。
        _qsoNoiseOn = NoiseOnRadio?.IsChecked == true;
        PushAudioScene();

        UpdateQsoLabels(force: true);
        RebuildActivityList();
    }

    private void StopQso()
    {
        _qsoTimer?.Stop();
        _qsoAudio?.Stop();
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

        // 瀑布按 80 px/s 走：点和划在这个速度下才分得出长短
        _scope.PushWaterfallRows(
            _qsoSpectrum, WaterfallRowsPerTick, WaterfallMsPerRow, _qso.TxAges());

        _scope.Render(_qsoSpectrum);

        UpdateTxBadge();

        // 文字指标 4 Hz 刷新就够，避免每帧都排版
        if (++_qsoLabelCounter >= 5)
        {
            _qsoLabelCounter = 0;
            UpdateQsoLabels(force: false);
        }

        // 音频场景 4 Hz 重建；信号强度、底噪都在缓慢变化，不用每帧算
        if (++_qsoAudioCounter >= 5)
        {
            _qsoAudioCounter = 0;
            PushAudioScene();
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
            _scope.PushWaterfallRows(_qso.BuildSpectrum(_scope.Width, _qsoSpanHz), 1, 0, null);
    }

    /// <summary>把当前场景送给音频线程。发射时 RX 静音，和半双工真机一致。</summary>
    private void PushAudioScene()
    {
        if (_qso == null || _qsoAudio == null)
            return;

        _qsoAudio.Update(
            _qso.BuildAudioScene(_qsoVolume / 100.0, _qsoNoiseOn, QsoPitchHz, _qso.IsTransmitting));
    }

    /// <summary>
    /// 设备每发一个点或划，固件都会报 ELEMENT,DIT|DAH,&lt;微秒&gt;。
    /// 通联模式下把它画到瀑布中心，柱子高度 = 这个元素的实际时长。
    /// </summary>
    internal void OnQsoElement(long durationMs)
    {
        if (_qso == null || !_qsoVisible)
            return;

        _qso.NotifyTx(durationMs);
        PushAudioScene();
    }

    /// <summary>直键：按下/松开，长度等松开才知道。</summary>
    internal void OnQsoKey(bool down)
    {
        if (_qso == null || !_qsoVisible)
            return;

        if (down)
            _qso.NotifyTxDown();
        else
            _qso.NotifyTxUp();

        PushAudioScene();
    }

    private void RebuildActivityList()
    {
        if (QsoActivityList == null || _qso == null)
            return;

        QsoActivityList.Children.Clear();

        int rowIndex = 0;

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
                Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
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
            row.SetValue(AutomationProperties.AutomationIdProperty, $"ActivityRow{rowIndex}");
            rowIndex++;

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
            QsoBandwidthText.Text = BandwidthText();
            UpdatePassband();
        }

        UpdateQsoLabels(force: true);
        PushAudioScene();
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

        if (station == null)
        {
            QsoSignalDetail.Text = "调谐频率或点击瀑布，寻找波段的活跃信号";
            QsoStationTraffic.Text = "—";
        }
        else
        {
            QsoSignalDetail.Text =
                $"{ModeText(station.Mode)} · {FormatHz(station.FreqHz)}\n" +
                $"{station.LevelDbm:0} dBm 中值，强度随传播起伏";

            QsoStationTraffic.Text = station.Mode == EmissionMode.Cw
                ? $"{station.Wpm} WPM · {station.Message}"
                : "语音（SSB），无法抄成文字";
        }

        QsoNoiseText.Text = $"{_qso.NoiseDbm:0} dBm";

        if (force)
        {
            QsoBandwidthText.Text = BandwidthText();
            UpdatePassband();
            RebuildScopeAxis();
        }
    }

    private string BandwidthText() => _qso == null ? "—" : _qso.Mode switch
    {
        EmissionMode.Cw => $"CW · {_qso.BandwidthHz:0} Hz",
        EmissionMode.Usb => $"USB · {_qso.BandwidthHz / 1000.0:0.0} kHz",
        EmissionMode.Lsb => $"LSB · {_qso.BandwidthHz / 1000.0:0.0} kHz",
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

        return $"{microvolts:0} µV";
    }

    private void UpdateTxBadge()
    {
        if (QsoTxBadge == null || _qso == null)
            return;

        QsoTxBadge.Visibility = _qso.IsTransmitting ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- S 表 ----------

    private const double SMeterFloorDbm = -121;   // S1
    private const double SMeterRangeDb = 108;     // S1 → S9+60

    private void UpdateSMeterBar(double dbm)
    {
        if (SMeterFill == null || SMeterHost == null)
            return;

        double width = SMeterHost.ActualWidth;

        if (width <= 0)
            return;

        double fraction = Math.Clamp((dbm - SMeterFloorDbm) / SMeterRangeDb, 0, 1);

        // 真表针有惯性：WPF 用一段时间补齐到目标值，20 fps 的数据也能看着丝滑
        var animation = new DoubleAnimation(Math.Max(0, width * fraction),
                                            TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        SMeterFill.BeginAnimation(FrameworkElement.WidthProperty, animation);

        // 峰值保持：冲上去立刻标记，然后慢慢回落
        if (fraction >= _peakFraction)
        {
            _peakFraction = fraction;
            _peakHoldSeconds = 1.2;
        }
        else if (_peakHoldSeconds > 0)
        {
            _peakHoldSeconds -= 0.05;
        }
        else
        {
            _peakFraction = Math.Max(fraction, _peakFraction - 0.004);
        }

        if (SMeterPeak != null)
        {
            SMeterPeak.Visibility = _peakFraction > 0.01 ? Visibility.Visible : Visibility.Collapsed;
            SMeterPeak.Margin = new Thickness(
                Math.Clamp(width * _peakFraction - 1, 0, Math.Max(0, width - 2)), 0, 0, 0);
        }
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

        foreach ((string label, double dbm) in marks)
        {
            double x = Math.Clamp((dbm - SMeterFloorDbm) / SMeterRangeDb, 0, 1) * width;

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

        double bandwidth = _qso.BandwidthHz;
        double pixels = Math.Max(4, bandwidth / _qsoSpanHz * width);

        // 通带相对载波的位置：CW 的滤波器在侧音上，USB 在上边带，LSB 在下边带
        double offsetPx = _qso.Mode switch
        {
            EmissionMode.Cw => QsoPitchHz / _qsoSpanHz * width,
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

        QsoBandwidthText.Text = BandwidthText();
        UpdatePassband();
        UpdateQsoLabels(force: false);
        PushAudioScene();
    }

    private void QsoFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || _qso == null)
            return;

        _qso.Filter = radio.Tag?.ToString() switch
        {
            "Narrow" => FilterSlot.Narrow,
            "Wide" => FilterSlot.Wide,
            _ => FilterSlot.Medium
        };

        QsoBandwidthText.Text = BandwidthText();
        UpdatePassband();
        UpdateQsoLabels(force: false);
        PushAudioScene();
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
        PushAudioScene();
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

        PushAudioScene();
    }

    private void Scope_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_scopeDragging)
            return;

        _scopeDragging = false;
        ScopeHost?.ReleaseMouseCapture();
    }

    private static long RoundTo(long value, int step) => (long)Math.Round(value / (double)step) * step;

    // ---------- 频率直接输入 ----------

    private void QsoFreq_MouseDown(object sender, MouseButtonEventArgs e) => BeginFreqEdit();

    private void BeginFreqEdit()
    {
        if (_qso == null || QsoFreqEditor == null)
            return;

        QsoFreqEditor.Text = (_qso.FrequencyHz / 1e6).ToString("0.000000", CultureInfo.InvariantCulture);
        QsoFreqText.Visibility = Visibility.Collapsed;
        QsoFreqEditor.Visibility = Visibility.Visible;
        QsoFreqEditor.Focus();
        QsoFreqEditor.SelectAll();
    }

    private void QsoFreqEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitFreqEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndFreqEdit();
            e.Handled = true;
        }
    }

    private void QsoFreqEditor_LostFocus(object sender, RoutedEventArgs e) => CommitFreqEdit();

    private void CommitFreqEdit()
    {
        if (_qso == null || QsoFreqEditor == null)
            return;

        if (TryParseFrequency(QsoFreqEditor.Text, out long hz))
        {
            _qso.FrequencyHz = hz;
            UpdateQsoLabels(force: true);
            RebuildActivityList();
            PushAudioScene();
        }

        EndFreqEdit();
    }

    private void EndFreqEdit()
    {
        if (QsoFreqEditor == null || QsoFreqText == null)
            return;

        QsoFreqEditor.Visibility = Visibility.Collapsed;
        QsoFreqText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 接受 14.030500（MHz）、7030（kHz）、7030000（Hz）三种写法。
    /// 和真机的"直接置频"一样，输完回车就跳过去。
    /// </summary>
    internal static bool TryParseFrequency(string? text, out long hz)
    {
        hz = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string cleaned = text.Trim().Replace(" ", "").Replace("_", "").Replace(",", "");

        if (cleaned.Contains('.'))
        {
            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz))
                return false;

            hz = (long)Math.Round(mhz * 1e6);
        }
        else
        {
            if (!long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                return false;

            // 位数少的是 kHz，多了才是 Hz —— 7030 说成 7.030 MHz 才是人之常情
            hz = cleaned.Length <= 5 ? value * 1000 : value;
        }

        return hz is >= 30_000 and <= 30_000_000;
    }

    // ---------- 波段 ----------

    private void BuildBandButtons()
    {
        if (BandGrid == null)
            return;

        BandGrid.Children.Clear();

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

        // 记住上个波段的频率，切回去时手不陌生
        _bandMemory[_qso.Band.Name] = _qso.FrequencyHz;

        long target = _bandMemory.TryGetValue(band.Name, out long saved)
            ? saved
            : DefaultFrequencyFor(band);

        _qso.FrequencyHz = target;
        RebuildScopeAxis();
        UpdateQsoLabels(force: true);
        RebuildActivityList();
        PushAudioScene();

        // 换波段后瀑布上全是上一个波段的痕迹，清一下更像真机
        _scope?.Clear();
        _scopePrefilled = false;
    }

    /// <summary>默认停在波段里的热门段：CW 在低端，SSB 在中段。</summary>
    private long DefaultFrequencyFor(BandInfo band)
    {
        long hot = _qso?.Mode switch
        {
            EmissionMode.Usb => band.StartHz + (long)((band.EndHz - band.StartHz) * 0.62),
            EmissionMode.Lsb => band.StartHz + (long)((band.EndHz - band.StartHz) * 0.55),
            _ => band.StartHz + 25_000
        };

        // 热门频点：40m 7.030、20m 14.025 这些是 CW 的"公共频道"
        long classic = band.Name switch
        {
            "160m" => 1_825_000,
            "80m" => 3_560_000,
            "40m" => 7_030_000,
            "30m" => 10_115_000,
            "20m" => 14_025_000,
            "17m" => 18_080_000,
            "15m" => 21_030_000,
            "12m" => 24_900_000,
            "10m" => 28_030_000,
            _ => hot
        };

        return _qso?.Mode == EmissionMode.Cw ? classic : hot;
    }

    // ---------- 音频 ----------

    private void NoiseSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || NoiseVolumeSlider == null)
            return;

        _qsoNoiseOn = radio.Tag?.ToString() == "1";

        if (_qsoAudio == null)
            return;

        // 引擎只在通联模式里跑；切回来时 StartQso 会按开关状态重新拉起
        if (_qsoNoiseOn && _qsoVisible)
            _qsoAudio.Start();

        PushAudioScene();
    }

    private void NoiseVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NoiseVolumeText == null)
            return;

        NoiseVolumeText.Text = $"{(int)e.NewValue}%";
        _qsoVolume = (int)e.NewValue;

        if (_qsoAudio != null && _qsoVisible)
            PushAudioScene();
    }
}
