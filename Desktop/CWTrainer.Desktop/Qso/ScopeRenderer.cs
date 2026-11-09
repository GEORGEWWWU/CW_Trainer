using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CWTrainer.Desktop.Qso;

/// <summary>
/// 频谱 + 瀑布的渲染器。
///
/// 做法参考了 SDR 的通用实践：dB 取对数后映射到一条"暗→亮"的色阶，
/// 噪声底压到接近背景色、信号越强越亮；瀑布用环形行缓冲，只写新的一行，
/// 避免每帧搬运整块像素。
///
/// 色阶没有用彩虹色：彩虹在绿黄交界处会出现肉眼可见的假边界，
/// 会把平滑的噪声底看成"有信号"。这里用单色系（黑 → 橙 → 白），
/// 既符合本项目的三色规范，亮度和 dB 也基本单调对应。
/// </summary>
public sealed class ScopeRenderer
{
    private readonly int _width;
    private readonly int _spectrumHeight;
    private readonly int _waterfallHeight;
    private readonly int[] _image;
    private readonly int[] _waterRows;
    private readonly int[] _palette = new int[256];

    private readonly double _dbMin;
    private readonly double _dbMax;

    private int _rowIndex;

    public ScopeRenderer(int width, int spectrumHeight, int waterfallHeight,
                         double dbMin = -118, double dbMax = -50)
    {
        _width = width;
        _spectrumHeight = spectrumHeight;
        _waterfallHeight = waterfallHeight;

        _dbMin = dbMin;
        _dbMax = dbMax;

        _image = new int[width * (spectrumHeight + waterfallHeight)];
        _waterRows = new int[width * waterfallHeight];

        BuildPalette();

        Bitmap = new WriteableBitmap(width, spectrumHeight + waterfallHeight, 96, 96,
                                     PixelFormats.Bgra32, null);

        // 初始填成背景色，否则第一帧之前是透明的
        Array.Fill(_image, _palette[0]);
        Bitmap.WritePixels(new Int32Rect(0, 0, width, spectrumHeight + waterfallHeight),
                           _image, width * 4, 0);
    }

    public WriteableBitmap Bitmap { get; }

    public int Width => _width;

    public int SpectrumHeight => _spectrumHeight;

    public int WaterfallHeight => _waterfallHeight;

    /// <summary>追加一行瀑布（保留给逐行调用的场合）。</summary>
    public void PushWaterfallRow(double[] spectrum) => PushWaterfallRows(spectrum, 1, 0, null);

    /// <summary>
    /// 一次追加多行瀑布。真实 SDR 的瀑布是按时间连续流动的，
    /// 每帧滚 3–5 行才能让 60 ms 的点、180 ms 的划在图上分出长短，
    /// 一行一帧（10 行/秒）的话点和划都会被压成同一个像素点。
    ///
    /// txAges 是我们的发射窗口（距现在多少毫秒），用来在中心画那根发射柱：
    /// 柱子有多高，就是手按了多久。
    /// </summary>
    public void PushWaterfallRows(
        double[] spectrum, int rows, double msPerRow,
        IReadOnlyList<(double StartAgeMs, double EndAgeMs)>? txAges)
    {
        if (rows <= 0)
            return;

        for (int r = 0; r < rows; r++)
        {
            int rowStart = _rowIndex * _width;

            for (int x = 0; x < _width; x++)
            {
                int bin = Math.Clamp(x * spectrum.Length / _width, 0, spectrum.Length - 1);
                double t = Normalize(spectrum[bin]);
                _waterRows[rowStart + x] = _palette[(int)(t * 255)];
            }

            // 这一行覆盖的时间窗（距离现在的毫秒数，越老越大）
            double ageNewer = (rows - r - 1) * msPerRow;
            double ageOlder = ageNewer + msPerRow;

            if (txAges != null && txAges.Count > 0)
                PaintTxColumn(rowStart, ageNewer, ageOlder, txAges);

            _rowIndex = (_rowIndex + 1) % _waterfallHeight;
        }
    }

    private void PaintTxColumn(
        int rowStart, double ageNewer, double ageOlder,
        IReadOnlyList<(double StartAgeMs, double EndAgeMs)> txAges)
    {
        bool keyed = false;

        foreach ((double startAge, double endAge) in txAges)
        {
            if (Math.Max(ageNewer, endAge) < Math.Min(ageOlder, startAge))
            {
                keyed = true;
                break;
            }
        }

        if (!keyed)
            return;

        int center = _width / 2;
        int half = Math.Max(1, _width / 260);   // 扫宽越窄，柱子越细

        for (int x = Math.Max(0, center - half); x <= Math.Min(_width - 1, center + half); x++)
            _waterRows[rowStart + x] = _palette[255];
    }

    /// <summary>把频谱曲线与瀑布合成到一张位图上并提交。</summary>
    public void Render(double[] spectrum, int peakHoldBins = 0)
    {
        int specHeight = _spectrumHeight;

        // 1) 频谱区域：底色 + 曲线下方填充 + 曲线本身
        for (int x = 0; x < _width; x++)
        {
            int bin = Math.Clamp(x * spectrum.Length / _width, 0, spectrum.Length - 1);
            double t = Normalize(spectrum[bin]);

            int y = (int)Math.Round((1 - t) * (specHeight - 1));
            int fill = _palette[(int)(t * 255 * 0.45)] | unchecked((int)0xFF000000);
            int line = _palette[(int)(Math.Min(1, t * 1.15) * 255)];

            for (int row = 0; row < specHeight; row++)
                _image[row * _width + x] = row < y ? _palette[0] : fill;

            _image[y * _width + x] = line;

            if (y + 1 < specHeight)
                _image[(y + 1) * _width + x] = line;
        }

        _ = peakHoldBins;

        // 2) 瀑布：把环形缓冲按"最新在上"的顺序整行拷进图像
        for (int r = 0; r < _waterfallHeight; r++)
        {
            int srcRow = ((_rowIndex - 1 - r) % _waterfallHeight + _waterfallHeight) % _waterfallHeight;
            int dst = (specHeight + r) * _width;
            int src = srcRow * _width;

            Array.Copy(_waterRows, src, _image, dst, _width);
        }

        Bitmap.WritePixels(
            new Int32Rect(0, 0, _width, specHeight + _waterfallHeight),
            _image, _width * 4, 0);
    }

    public void Clear()
    {
        Array.Fill(_waterRows, _palette[0]);
        Array.Fill(_image, _palette[0]);
    }

    private double Normalize(double db)
    {
        double t = (db - _dbMin) / (_dbMax - _dbMin);
        return Math.Clamp(t, 0, 1);
    }

    private void BuildPalette()
    {
        // 五个锚点：背景 → 暗红 → 橙红 → 亮橙 → 米白
        (double Pos, int R, int G, int B)[] stops =
        {
            (0.00, 0x0B, 0x0B, 0x0C),
            (0.18, 0x2A, 0x10, 0x0A),
            (0.40, 0x6B, 0x24, 0x0B),
            (0.62, 0xC2, 0x4B, 0x12),
            (0.82, 0xFF, 0x7A, 0x33),
            (1.00, 0xFF, 0xEC, 0xDB)
        };

        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            int index = 0;

            while (index < stops.Length - 2 && t > stops[index + 1].Pos)
                index++;

            var a = stops[index];
            var b = stops[index + 1];

            double span = b.Pos - a.Pos;
            double k = span <= 0 ? 0 : (t - a.Pos) / span;

            int r = (int)(a.R + (b.R - a.R) * k);
            int g = (int)(a.G + (b.G - a.G) * k);
            int bb = (int)(a.B + (b.B - a.B) * k);

            _palette[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bb;
        }
    }
}
