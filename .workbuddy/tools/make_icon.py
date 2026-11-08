"""
CW Trainer 应用图标生成器。

设计：近黑圆角方块 + 一个点 + 一个划（摩斯码 A = ".-"），
只用两色（#0F0F10 / #FF5A1F），与软件界面令牌一致。
输出多尺寸 .ico（Windows 任务栏、标题栏、资源管理器都需要）。

运行：python make_icon.py
"""

from pathlib import Path

from PIL import Image, ImageDraw

INK = (15, 15, 16, 255)
ACCENT = (255, 90, 31, 255)

SIZE = 1024          # 超采样画布
SS = 4               # 超采样倍率（实际按 SIZE 画，最后缩小时已有抗锯齿）
BASE = 256


def render() -> Image.Image:
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 圆角方块：半径约为边长的 22%，接近 Apple 的 squircle 观感
    radius = int(SIZE * 0.225)
    d.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=radius, fill=INK)

    # 点划几何（以 256 为基准设计，再按 SIZE 缩放）
    k = SIZE / BASE
    dot_r = 17 * k
    bar_w, bar_h = 76 * k, 34 * k
    gap = 18 * k

    total_w = dot_r * 2 + gap + bar_w
    left = (SIZE - total_w) / 2
    cy = SIZE / 2

    # 点
    d.ellipse(
        [left, cy - dot_r, left + dot_r * 2, cy + dot_r],
        fill=ACCENT,
    )

    # 划（圆角胶囊）
    x0 = left + dot_r * 2 + gap
    d.rounded_rectangle(
        [x0, cy - bar_h / 2, x0 + bar_w, cy + bar_h / 2],
        radius=bar_h / 2,
        fill=ACCENT,
    )

    return img


def main() -> None:
    master = render()
    sizes = [256, 128, 64, 48, 32, 16]

    # 直接写到 WPF 工程的 Assets 目录
    out_dir = Path(__file__).resolve().parents[2] / "Desktop" / "CWTrainer.Desktop" / "Assets"
    out_dir.mkdir(parents=True, exist_ok=True)

    frames = [master.resize((s, s), Image.LANCZOS) for s in sizes]
    out = str(out_dir / "app.ico")
    frames[0].save(
        out,
        format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=frames[1:],
    )

    master.resize((512, 512), Image.LANCZOS).save(out_dir / "app-512.png")
    print(f"wrote {out} and app-512.png")


if __name__ == "__main__":
    main()
