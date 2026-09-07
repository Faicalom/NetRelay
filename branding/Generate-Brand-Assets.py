from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
MASTER = ROOT / "branding" / "NetRelay-Master-Icon.png"
ANDROID_RES = ROOT / "android" / "app" / "src" / "main" / "res"
WINDOWS_ASSETS = ROOT / "windows" / "assets"


def load_master() -> Image.Image:
    image = Image.open(MASTER).convert("RGBA")
    if image.width != image.height:
        raise RuntimeError(f"Master icon must be square, got {image.size}")
    return image


def write_android_icons(master: Image.Image) -> None:
    sizes = {
        "mipmap-mdpi": 48,
        "mipmap-hdpi": 72,
        "mipmap-xhdpi": 96,
        "mipmap-xxhdpi": 144,
        "mipmap-xxxhdpi": 192,
    }
    for folder, size in sizes.items():
        target = ANDROID_RES / folder
        target.mkdir(parents=True, exist_ok=True)
        resized = master.resize((size, size), Image.Resampling.LANCZOS)
        resized.save(target / "ic_launcher.png", optimize=True)
        resized.save(target / "ic_launcher_round.png", optimize=True)

    # Preserve the selected artwork while giving adaptive masks a generous safe zone.
    foreground = Image.new("RGBA", (432, 432), (0, 0, 0, 0))
    inset = master.resize((320, 320), Image.Resampling.LANCZOS)
    foreground.alpha_composite(inset, ((432 - 320) // 2, (432 - 320) // 2))
    drawable = ANDROID_RES / "drawable-nodpi"
    drawable.mkdir(parents=True, exist_ok=True)
    foreground.save(drawable / "ic_netrelay_foreground.png", optimize=True)

    # Android small-notification icon: monochrome alpha mask derived from cyan glyphs.
    small_source = master.resize((96, 96), Image.Resampling.LANCZOS)
    small = Image.new("RGBA", small_source.size, (255, 255, 255, 0))
    source_pixels = small_source.load()
    target_pixels = small.load()
    for y in range(small_source.height):
        for x in range(small_source.width):
            red, green, blue, _ = source_pixels[x, y]
            cyan_strength = max(0, min(255, int((green + blue) / 2) - red))
            brightness = max(green, blue)
            alpha = max(0, min(255, (cyan_strength - 18) * 5, brightness))
            if alpha > 24:
                target_pixels[x, y] = (255, 255, 255, alpha)
    drawable_any = ANDROID_RES / "drawable"
    drawable_any.mkdir(parents=True, exist_ok=True)
    small.save(drawable_any / "ic_netrelay_notification.png", optimize=True)


def write_windows_icon(master: Image.Image) -> None:
    WINDOWS_ASSETS.mkdir(parents=True, exist_ok=True)
    icon_sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40),
                  (48, 48), (64, 64), (128, 128), (256, 256)]
    master.save(WINDOWS_ASSETS / "NetRelay.ico", format="ICO", sizes=icon_sizes)
    master.resize((256, 256), Image.Resampling.LANCZOS).save(
        WINDOWS_ASSETS / "NetRelay.png", optimize=True
    )


def main() -> None:
    master = load_master()
    write_android_icons(master)
    write_windows_icon(master)
    print(f"Generated NetRelay assets from {MASTER} ({master.width}x{master.height})")


if __name__ == "__main__":
    main()
