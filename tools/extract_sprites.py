"""
extract_sprites.py — Stardew Valley sprite extractor for GMDF
=============================================================
Reads game data JSONs + sprite sheets from your Content (unpacked) folder
and outputs individual PNG files named like (O)96_Chicken.png

Requirements: pip install Pillow

Usage:
    python extract_sprites.py

Edit CONTENT_DIR below if your game is installed elsewhere.
Output goes to a "sprites" folder next to this script.
"""

import json
import re
from pathlib import Path
from PIL import Image

# ── Config ────────────────────────────────────────────────────────────────────

CONTENT_DIR = Path(r"E:\SteamLibrary\steamapps\common\Stardew Valley\Content (unpacked)")
OUT_DIR      = Path(__file__).parent / "sprites"

# Scale factor: 1 = native (16px objects), 2 = 32px, 4 = 64px
SCALE = 4

# Which qualifiers to extract. Comment out any you don't want.
EXTRACT = ["O", "BC", "H", "W", "T", "F"]

# ── Sheet specs ───────────────────────────────────────────────────────────────
# qualifier -> (sheet path relative to CONTENT_DIR, sprite_w, sprite_h)
SHEETS = {
    "O":  ("Maps/springobjects.png",        16, 16),
    "BC": ("TileSheets/Craftables.png",      16, 32),
    "H":  ("Characters/Farmer/hats.png",     20, 20),
    "W":  ("TileSheets/weapons.png",         16, 16),
    "T":  ("TileSheets/tools.png",           16, 16),
    "F":  ("TileSheets/furniture.png",       16, 32),
}

# ── Data sources ──────────────────────────────────────────────────────────────
# qualifier -> (json path, name_field, sprite_index_field)
DATA = {
    "O":  ("Data/Objects.json",       "Name", "SpriteIndex"),
    "BC": ("Data/BigCraftables.json", "Name", "SpriteIndex"),
    "H":  ("Data/hats.json",          "Name", "SpriteIndex"),
    "W":  ("Data/Weapons.json",       "Name", "SpriteIndex"),
    "T":  ("Data/Tools.json",         "Name", "SpriteIndex"),
    "F":  ("Data/Furniture.json",     "Name", "SpriteIndex"),
}

# ── Helpers ───────────────────────────────────────────────────────────────────

def safe_name(s):
    return re.sub(r'[<>:"/\\|?*]', '_', s).strip()


def load_sheet(qualifier):
    path, sw, sh = SHEETS[qualifier]
    full = CONTENT_DIR / path
    if not full.exists():
        print(f"  ⚠  Sheet not found: {full}")
        return None, sw, sh
    return Image.open(full).convert("RGBA"), sw, sh


def crop_sprite(sheet, sprite_index, sw, sh, scale):
    cols = sheet.width // sw
    col  = sprite_index % cols
    row  = sprite_index // cols
    x, y = col * sw, row * sh
    sprite = sheet.crop((x, y, x + sw, y + sh))
    if scale != 1:
        sprite = sprite.resize((sw * scale, sh * scale), Image.NEAREST)
    return sprite


def is_blank(sprite):
    """Return True if the sprite is fully transparent (no visible pixels)."""
    return not any(px[3] > 0 for px in sprite.getdata())


# ── Legacy slash-delimited parsers ───────────────────────────────────────────
# Some data files (hats.json, Furniture.json) still use the old
# "Name/field1/field2/..." string format rather than a JSON object.
# In both cases the numeric key is the sprite index and fields[0] is the name.

def _parse_legacy(raw):
    """Parse a dict of {id: "Name/f1/f2/..."} entries into item dicts."""
    items = {}
    seen  = set()
    for item_id, entry in raw.items():
        if not isinstance(entry, str):
            continue
        fields = entry.split("/")
        name = fields[0].strip() if fields else item_id
        try:
            si = int(item_id)
        except ValueError:
            continue
        key = (name, si)
        if key in seen:
            continue
        seen.add(key)
        items[item_id] = {
            "name":              name,
            "sprite_index":      si,
            "menu_sprite_index": None,
        }
    return items


def load_items(qualifier):
    json_path, name_field, si_field = DATA[qualifier]
    full = CONTENT_DIR / json_path
    if not full.exists():
        print(f"  ⚠  Data not found: {full}")
        return {}

    with open(full, encoding="utf-8") as f:
        raw = json.load(f)

    # Detect legacy string-value format (hats, furniture)
    first_val = next(iter(raw.values()), None)
    if isinstance(first_val, str):
        return _parse_legacy(raw)

    items = {}
    seen  = set()

    for item_id, entry in raw.items():
        if not isinstance(entry, dict):
            continue
        name = entry.get(name_field) or f"{qualifier}{item_id}"
        si   = entry.get(si_field)
        if si is None:
            try:
                si = int(item_id)
            except ValueError:
                continue

        # Skip duplicate name+index combos (BigCraftables has many)
        key = (name, si)
        if key in seen:
            continue
        seen.add(key)

        items[item_id] = {
            "name":              name,
            "sprite_index":      si,
            "menu_sprite_index": entry.get("MenuSpriteIndex"),
        }

    return items


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    total = 0

    for qualifier in EXTRACT:
        print(f"\n── ({qualifier}) ──")
        sheet, sw, sh = load_sheet(qualifier)
        if sheet is None:
            continue

        sheet_max = (sheet.width // sw) * (sheet.height // sh)
        items = load_items(qualifier)
        print(f"  {len(items)} items")

        saved = 0
        for item_id, info in items.items():
            si  = info["sprite_index"]
            msi = info["menu_sprite_index"]

            if si >= sheet_max:
                print(f"  ⚠  ({qualifier}){item_id} index {si} out of range, skipping")
                continue
            try:
                sprite = crop_sprite(sheet, si, sw, sh, SCALE)
            except Exception as e:
                print(f"  ⚠  ({qualifier}){item_id}: {e}")
                continue

            # Fall back to MenuSpriteIndex when SpriteIndex tile is blank.
            # Some tools (all watering cans) store their held-animation frame
            # at SpriteIndex — which is transparent — and their actual
            # inventory icon at MenuSpriteIndex.
            if is_blank(sprite) and msi is not None and msi < sheet_max:
                try:
                    fallback = crop_sprite(sheet, msi, sw, sh, SCALE)
                    if not is_blank(fallback):
                        sprite = fallback
                except Exception:
                    pass

            filename = f"({qualifier}){item_id}_{safe_name(info['name'])}.png"
            sprite.save(OUT_DIR / filename)
            saved += 1

        print(f"  ✔  {saved} saved")
        total += saved

    print(f"\nDone — {total} sprites → {OUT_DIR}")


if __name__ == "__main__":
    main()
