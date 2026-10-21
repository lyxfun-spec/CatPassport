"""Regenerate the sized app icons and the renderer brand image from the master PNG.

    python assets/branding/generate-icons.py

Source of truth: cat-assistant-icon-transparent.png (1254x1254, transparent).
"""

from pathlib import Path

from PIL import Image

BRANDING = Path(__file__).resolve().parent
MASTER = BRANDING / "cat-assistant-icon-transparent.png"
DESKTOP = BRANDING.parents[1] / "windows" / "CatAssistant.Desktop"
TARGETS = {
    BRANDING / "cat-assistant-icon-256.png": 256,
    BRANDING / "cat-assistant-icon-32.png": 32,
    DESKTOP / "src" / "assets" / "brand.png": 128,
}
ICO_SIZES = [(size, size) for size in (16, 24, 32, 48, 64, 128, 256)]


def main() -> None:
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
