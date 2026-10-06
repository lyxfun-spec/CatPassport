#!/usr/bin/env python3
"""Generate the Cat Assistant pixel sprites and LVGL I4 image source."""

from pathlib import Path

from PIL import Image, ImageDraw


LOGICAL_SIZE = 36
SCALE = 2
POSES = ("idle", "blink", "listen", "think", "happy", "error")
PALETTE = (
    (0x00, 0x00, 0x00, 0x00),  # transparent
    (0x18, 0x1B, 0x1F, 0xFF),  # outline
    (0x4A, 0x4B, 0x50, 0xFF),  # fur
    (0x60, 0x61, 0x67, 0xFF),  # fur highlight
    (0xEA, 0xDF, 0xCB, 0xFF),  # cream
    (0x59, 0xC9, 0xB2, 0xFF),  # cat-eye teal
    (0x20, 0x70, 0x68, 0xFF),  # dark teal
    (0xD9, 0x89, 0x8C, 0xFF),  # nose and inner ear
    (0xF6, 0xF2, 0xE8, 0xFF),  # eye highlight
    (0x9B, 0xE6, 0xD7, 0xFF),  # state accent
    (0xE5, 0x7D, 0x8D, 0xFF),  # error accent
    (0x2B, 0x2D, 0x31, 0xFF),  # shadow
    (0x00, 0x00, 0x00, 0x00),
    (0x00, 0x00, 0x00, 0x00),
    (0x00, 0x00, 0x00, 0x00),
    (0x00, 0x00, 0x00, 0x00),
)


def polygon(draw: ImageDraw.ImageDraw, points, color: int) -> None:
    draw.polygon(points, fill=color)


def ellipse(draw: ImageDraw.ImageDraw, box, color: int) -> None:
    draw.ellipse(box, fill=color)


def draw_cat(pose: str) -> Image.Image:
    image = Image.new("P", (LOGICAL_SIZE, LOGICAL_SIZE), 0)
    flat_palette = []
    for red, green, blue, _alpha in PALETTE:
        flat_palette.extend((red, green, blue))
    flat_palette.extend((0, 0, 0) * (256 - len(PALETTE)))
    image.putpalette(flat_palette)
    image.info["transparency"] = 0
    draw = ImageDraw.Draw(image)

    # Curled tail, body and short paws establish the compact silhouette.
    draw.line(((27, 27), (31, 25), (32, 21), (31, 18)), fill=1, width=6)
    draw.line(((27, 27), (30, 25), (31, 21), (30, 18)), fill=2, width=3)
    ellipse(draw, (8, 19, 28, 35), 1)
    ellipse(draw, (9, 20, 27, 34), 2)
    ellipse(draw, (13, 23, 22, 34), 4)

    # Rounded ears and head follow the approved dark-gray and cream concept.
    polygon(draw, ((6, 12), (8, 2), (16, 8), (15, 14)), 1)
    polygon(draw, ((30, 12), (28, 2), (20, 8), (21, 14)), 1)
    polygon(draw, ((8, 10), (9, 5), (14, 9), (13, 12)), 7)
    polygon(draw, ((28, 10), (27, 5), (22, 9), (23, 12)), 7)
    ellipse(draw, (4, 7, 31, 28), 1)
    ellipse(draw, (5, 8, 30, 27), 2)
    ellipse(draw, (7, 9, 28, 16), 3)
    polygon(draw, ((16, 9), (18, 7), (20, 9), (19, 12), (17, 12)), 2)

    # Cream cheeks and muzzle keep the face soft at 72 px.
    ellipse(draw, (8, 17, 18, 26), 4)
    ellipse(draw, (18, 17, 28, 26), 4)
    ellipse(draw, (13, 20, 23, 27), 4)
    polygon(draw, ((17, 20), (19, 20), (18, 22)), 7)

    if pose in ("blink", "happy"):
        draw.line(((10, 17), (12, 18), (15, 17)), fill=1, width=2)
        draw.line(((21, 17), (24, 18), (26, 17)), fill=1, width=2)
    else:
        left_shift = 1 if pose == "think" else 0
        right_shift = 1 if pose == "think" else 0
        ellipse(draw, (9 + left_shift, 13, 16 + left_shift, 21), 1)
        ellipse(draw, (20 + right_shift, 13, 27 + right_shift, 21), 1)
        ellipse(draw, (10 + left_shift, 14, 15 + left_shift, 20), 5)
        ellipse(draw, (21 + right_shift, 14, 26 + right_shift, 20), 5)
        draw.rectangle((12 + left_shift, 17, 15 + left_shift, 20), fill=6)
        draw.rectangle((23 + right_shift, 17, 26 + right_shift, 20), fill=6)
        draw.point((11 + left_shift, 15), fill=8)
        draw.point((22 + right_shift, 15), fill=8)

    if pose == "happy":
        ellipse(draw, (16, 22, 20, 26), 1)
        draw.rectangle((17, 23, 19, 25), fill=7)
    elif pose == "error":
        draw.line(((16, 25), (18, 23), (20, 25)), fill=1, width=1)
        draw.line(((7, 13), (10, 12)), fill=10, width=1)
        draw.line(((26, 12), (29, 13)), fill=10, width=1)
    else:
        draw.line(((15, 24), (17, 25), (18, 23), (19, 25), (21, 24)), fill=1, width=1)

    # Small paws; pose-specific accents stay inside the 72x72 sprite.
    ellipse(draw, (8, 31, 15, 35), 1)
    ellipse(draw, (21, 31, 28, 35), 1)
    ellipse(draw, (9, 31, 14, 34), 4)
    ellipse(draw, (22, 31, 27, 34), 4)
    if pose == "listen":
        draw.line(((2, 15), (0, 17), (2, 19)), fill=9, width=1)
        draw.line(((33, 15), (35, 17), (33, 19)), fill=9, width=1)
    elif pose == "think":
        ellipse(draw, (29, 8, 30, 9), 9)
        ellipse(draw, (31, 5, 33, 7), 9)
        ellipse(draw, (33, 1, 35, 3), 9)
    elif pose == "happy":
        draw.line(((3, 18), (1, 18)), fill=9, width=1)
        draw.line(((33, 18), (35, 18)), fill=9, width=1)
    elif pose == "error":
        draw.line(((32, 5), (33, 3), (35, 5), (33, 7)), fill=10, width=1)
        draw.point((33, 9), fill=10)

    return image.resize((LOGICAL_SIZE * SCALE, LOGICAL_SIZE * SCALE), Image.Resampling.NEAREST)


def rgba_image(indexed: Image.Image) -> Image.Image:
    rgba = indexed.convert("RGBA")
    pixels = rgba.load()
    source = indexed.load()
    for y in range(indexed.height):
        for x in range(indexed.width):
            if source[x, y] == 0:
                pixels[x, y] = (0, 0, 0, 0)
    return rgba


def packed_i4(indexed: Image.Image) -> bytes:
    palette_bytes = bytearray()
    for red, green, blue, alpha in PALETTE:
        palette_bytes.extend((blue, green, red, alpha))
    pixels = bytearray()
    values = list(indexed.tobytes())
    for offset in range(0, len(values), 2):
        pixels.append((values[offset] << 4) | values[offset + 1])
    return bytes(palette_bytes + pixels)


def c_bytes(data: bytes) -> str:
    rows = []
    for offset in range(0, len(data), 16):
        rows.append("    " + ", ".join(f"0x{value:02X}" for value in data[offset : offset + 16]) + ",")
    return "\n".join(rows)


def write_c_source(path: Path, sprites: dict[str, Image.Image]) -> None:
    sections = [
        "/* Generated by tools/generate_cat_sprites.py. Do not edit manually. */",
        '#include "cat_sprite_assets.h"',
        "",
    ]
    for pose, image in sprites.items():
        data = packed_i4(image)
        sections.extend(
            (
                "static const LV_ATTRIBUTE_MEM_ALIGN LV_ATTRIBUTE_LARGE_CONST",
                f"uint8_t s_cat_{pose}_map[] = {{",
                c_bytes(data),
                "};",
                "",
                f"static const lv_image_dsc_t s_cat_{pose} = {{",
                "    .header = {",
                "        .magic = LV_IMAGE_HEADER_MAGIC,",
                "        .cf = LV_COLOR_FORMAT_I4,",
                "        .flags = 0,",
                "        .w = 72,",
                "        .h = 72,",
                "        .stride = 36,",
                "        .reserved_2 = 0,",
                "    },",
                f"    .data_size = sizeof(s_cat_{pose}_map),",
                f"    .data = s_cat_{pose}_map,",
                "    .reserved = NULL,",
                "};",
                "",
            )
        )
    sections.extend(
        (
            "static const lv_image_dsc_t *const s_sprites[CAT_SPRITE_COUNT] = {",
            *[f"    [CAT_SPRITE_{pose.upper()}] = &s_cat_{pose}," for pose in POSES],
            "};",
            "",
            "const lv_image_dsc_t *cat_sprite_get(cat_sprite_pose_t pose)",
            "{",
            "    if ((unsigned)pose >= CAT_SPRITE_COUNT) pose = CAT_SPRITE_IDLE;",
            "    return s_sprites[pose];",
            "}",
            "",
        )
    )
    path.write_text("\n".join(sections), encoding="utf-8")


def main() -> None:
    firmware_root = Path(__file__).resolve().parents[1]
    asset_dir = firmware_root / "assets" / "images" / "cat-assistant"
    asset_dir.mkdir(parents=True, exist_ok=True)
    sprites = {pose: draw_cat(pose) for pose in POSES}
    for pose, sprite in sprites.items():
        rgba_image(sprite).save(asset_dir / f"cat-{pose}-72.png")

    preview = Image.new("RGBA", (3 * 96, 2 * 96), (18, 20, 23, 255))
    for index, pose in enumerate(POSES):
        x = (index % 3) * 96 + 12
        y = (index // 3) * 96 + 12
        preview.alpha_composite(rgba_image(sprites[pose]), (x, y))
    preview.save(asset_dir / "cat-sprite-preview.png")
    write_c_source(firmware_root / "main" / "cat_sprite_assets.c", sprites)


if __name__ == "__main__":
    main()
