"""Regenerate the sized app icons and the renderer brand image from the source artwork.

    python assets/branding/generate-icons.py

Source of truth: cat-assistant-icon-source.png (the artwork as delivered, on a
flat light background). The script drops that background outside the rounded
square, crops to the icon bounds, and writes the transparent master plus every
size derived from it.
"""

from pathlib import Path

from PIL import Image, ImageDraw

BRANDING = Path(__file__).resolve().parent
SOURCE = BRANDING / "cat-assistant-icon-source.png"
MASTER = BRANDING / "cat-assistant-icon-transparent.png"
DESKTOP = BRANDING.parents[1] / "windows" / "CatAssistant.Desktop"
TARGETS = {
    BRANDING / "cat-assistant-icon-256.png": 256,
    BRANDING / "cat-assistant-icon-32.png": 32,
    DESKTOP / "src" / "assets" / "brand.png": 128,
}
ICO_SIZES = [(size, size) for size in (16, 24, 32, 48, 64, 128, 256)]
# Distance from the flat background colour still treated as background. It also
# consumes the pale anti-aliased rim the artwork carries, while staying below the
# light-blue rounded square so the icon itself is never eaten into.
BACKGROUND_THRESHOLD = 28


def prepare_master() -> None:
    art = Image.open(SOURCE).convert("RGBA")
    # Flood from the corners so enclosed white details (the cat's muzzle) survive.
    ImageDraw.floodfill(art, (0, 0), (255, 0, 255, 255), thresh=BACKGROUND_THRESHOLD)
    pixels = art.load()
    for y in range(art.height):
        for x in range(art.width):
            if pixels[x, y][:3] == (255, 0, 255):
                pixels[x, y] = (0, 0, 0, 0)
    art = art.crop(art.getbbox())
    side = max(art.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(art, ((side - art.width) // 2, (side - art.height) // 2))
    art = square
    art.save(MASTER, optimize=True)
    print(f"wrote {MASTER.relative_to(BRANDING.parents[1])} ({art.width}x{art.height})")


def main() -> None:
    prepare_master()
    master = Image.open(MASTER).convert("RGBA")
    for path, size in TARGETS.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        master.resize((size, size), Image.LANCZOS).save(path, optimize=True)
        print(f"wrote {path.relative_to(BRANDING.parents[1])} ({size}x{size})")
    ico = BRANDING / "cat-assistant-icon.ico"
    master.resize((256, 256), Image.LANCZOS).save(ico, sizes=ICO_SIZES)
    print(f"wrote {ico.relative_to(BRANDING.parents[1])} ({', '.join(str(size[0]) for size in ICO_SIZES)})")


if __name__ == "__main__":
    main()
