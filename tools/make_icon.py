"""Regenerates src/HdrScreenshot/icon.ico from the 8x8 pixel logo (keep in sync with UI/Logo.cs).

    python tools/make_icon.py
"""

from pathlib import Path

from PIL import Image

PIXELS = ["..aaaa..",
          ".abbbba.",
          "abcccbba",
          "abcwwcba",
          "abcwwcbd",
          "abbccbda",
          ".abbbda.",
          "..adda.."]
COLORS = {"a": "#3b2f78", "b": "#7c5cff", "c": "#c9bcff", "d": "#5a43c9", "w": "#ffffff"}


def main():
    img = Image.new("RGBA", (8, 8), (0, 0, 0, 0))
    for y, row in enumerate(PIXELS):
        for x, ch in enumerate(row):
            if ch in COLORS:
                img.putpixel((x, y), Image.new("RGB", (1, 1), COLORS[ch]).getpixel((0, 0)) + (255,))
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [img.resize((s, s), Image.NEAREST) for s in sizes]   # integer multiples of 8: stays crisp
    out = Path(__file__).resolve().parent.parent / "src" / "HdrScreenshot" / "icon.ico"
    frames[-1].save(out, sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
