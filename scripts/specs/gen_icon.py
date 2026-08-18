"""
Generate TradingStudio application icon.
Design: dark navy square with green candlestick + chart grid + gold accent.
"""
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]

def draw_icon(size):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    m = size - 1

    # 1. Background: dark navy
    draw.rectangle([0, 0, m, m], fill=(22, 27, 46, 255))

    # 2. Grid lines
    gc = (38, 45, 68, 200)
    lw = max(1, size // 64)
    for i in range(1, 4):
        y = int(size * i / 4)
        draw.line([(int(size * 0.12), y), (int(size * 0.88), y)], fill=gc, width=lw)

    # 3. Candlestick wick
    wx = int(size * 0.50)
    draw.line([(wx, int(size * 0.13)), (wx, int(size * 0.75))],
              fill=(160, 185, 210, 255), width=max(1, size // 16))

    # 4. Candlestick body (green)
    draw.rectangle([int(size * 0.30), int(size * 0.26),
                    int(size * 0.70), int(size * 0.60)],
                   fill=(0, 205, 135, 255))

    # 5. Gold accent dot
    dr = max(1, size // 20)
    dx, dy = int(size * 0.80), int(size * 0.18)
    draw.ellipse([dx - dr, dy - dr, dx + dr, dy + dr], fill=(255, 195, 50, 255))

    return img

def main():
    images = [draw_icon(s) for s in SIZES]
    path = "src/TradingStudio/icon.ico"
    images[0].save(path, format="ICO", append_images=images[1:])
    print(f"Icon saved: {path} ({SIZES} sizes)")

if __name__ == "__main__":
    main()
