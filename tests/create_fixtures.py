"""Create independent decoder fixtures with Pillow (test-only dependency)."""
from pathlib import Path
import struct
import zlib
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1] / "artifacts" / "fixtures"
gallery = root / "갤러리 공백"
orientation_dir = root / "orientation"
gallery.mkdir(parents=True, exist_ok=True)
orientation_dir.mkdir(parents=True, exist_ok=True)

# Four distinct corners, with large solid regions resilient to JPEG compression.
base = Image.new("RGB", (120, 80))
d = ImageDraw.Draw(base)
d.rectangle((0, 0, 59, 39), fill=(240, 30, 30))
d.rectangle((60, 0, 119, 39), fill=(30, 220, 30))
d.rectangle((0, 40, 59, 79), fill=(30, 30, 240))
d.rectangle((60, 40, 119, 79), fill=(240, 220, 30))
for orientation in range(1, 9):
    exif = Image.Exif()
    exif[274] = orientation
    base.save(orientation_dir / f"{orientation}.jpg", quality=98, subsampling=0, exif=exif)

transparent = Image.new("RGBA", (960, 600), (0, 0, 0, 0))
d = ImageDraw.Draw(transparent)
d.rounded_rectangle((110, 70, 850, 530), radius=48, fill=(45, 70, 113, 255))
d.ellipse((590, 120, 730, 260), fill=(255, 203, 111, 255))
d.polygon([(140, 470), (360, 190), (570, 470)], fill=(121, 176, 202, 255))
d.polygon([(370, 470), (570, 290), (790, 470)], fill=(168, 211, 212, 255))
d.rounded_rectangle((420, 400, 800, 490), radius=18, fill=(255, 255, 255, 120))
transparent.save(gallery / "1.png")
base.resize((1200, 800)).save(gallery / "2.jpg", quality=98)
exif = Image.Exif()
exif[274] = 6
base.save(gallery / "3.jpeg", quality=98, exif=exif)
transparent.save(gallery / "4.webp", lossless=True)
base.save(gallery / "10.BMP")
Image.new("RGB", (64, 48), "red").save(
    gallery / "11.gif", save_all=True,
    append_images=[Image.new("RGB", (64, 48), "blue")], duration=100, loop=0
)
base.save(gallery / "12.tiff")
Image.new("RGBA", (64, 48), "red").save(
    gallery / "13.webp", save_all=True,
    append_images=[Image.new("RGBA", (64, 48), "blue")], duration=100, loop=0, lossless=True
)
(gallery / "20-corrupt.png").write_bytes(b"this is deliberately not an image")
(gallery / "메모.txt").write_text("Must not appear in gallery", encoding="utf-8")
(gallery / "nested").mkdir(exist_ok=True)
base.save(gallery / "nested" / "hidden.png")

# An IHDR with oversized dimensions checks allocation rejection before decode.
png = bytearray((gallery / "1.png").read_bytes())
png[16:24] = struct.pack(">II", 40000, 40000)
png[29:33] = struct.pack(">I", zlib.crc32(png[12:29]) & 0xFFFFFFFF)
(root / "oversized.png").write_bytes(png)
print(root)

# Version 0.2 fixtures are isolated so the original gallery order stays stable.
features = root / "features"
features.mkdir(exist_ok=True)
base.save(features / "photo.jfif", format="JPEG", quality=98)
base.resize((64, 64)).save(features / "icon.ico", sizes=[(64, 64), (32, 32)])
base.save(features / "pages.tiff", save_all=True,
          append_images=[Image.new("RGB", (200, 140), "blue"), Image.new("RGB", (72, 96), "green")])
base.resize((6000, 4000)).save(features / "large.jpg", quality=90)

frames = []
for index in range(4):
    frame = Image.new("RGBA", (96, 64), (255, 255, 0, 255))
    draw = ImageDraw.Draw(frame)
    draw.rectangle((8 + index * 16, 12, 28 + index * 16, 48), fill=(255, 0, 0, 255))
    draw.rectangle((0, 0, 6, 6), fill=(0, 0, 255, 255))
    frames.append(frame)
frames[0].save(features / "motion.gif", save_all=True, append_images=frames[1:],
               duration=[60, 90, 140, 110], loop=1, disposal=[1, 2, 3, 1], optimize=True, transparency=0)
frames[0].save(features / "motion.webp", save_all=True, append_images=frames[1:],
               duration=[60, 90, 140, 110], loop=1, lossless=True)
for extension in ("gif", "webp"):
    animation = Image.open(features / f"motion.{extension}")
    for index in range(animation.n_frames):
        animation.seek(index)
        animation.convert("RGBA").save(features / f"expected-{extension}-{index}.png")

# Independent Pillow encoders for additional formats and RIFF ANI with explicit sequence/rates.
import io
extra = root / "extended"
extra.mkdir(exist_ok=True)
red = Image.new("RGB", (64, 48), (230, 20, 30))
for ext, fmt, mode in [("avif", "AVIF", "RGB"), ("pcx", "PCX", "RGB"), ("tga", "TGA", "RGB"), ("pbm", "PPM", "1"), ("pgm", "PPM", "L"), ("ppm", "PPM", "RGB")]:
    red.convert(mode).save(extra / ("sample." + ext), format=fmt)
Image.new("RGBA", (1024, 1024), (230, 20, 30, 128)).save(extra / "sample.icns")
icons = []
for color in [(230, 20, 30, 255), (10, 30, 240, 255)]:
    output = io.BytesIO()
    Image.new("RGBA", (32, 32), color).save(output, format="ICO", sizes=[(32, 32)])
    icons.append(output.getvalue())
cur = bytearray(icons[0]); cur[2] = 2; cur[10:14] = struct.pack("<HH", 8, 10)
(extra / "sample.cur").write_bytes(cur)
def chunk(tag, data):
    return tag + struct.pack("<I", len(data)) + data + (b"\0" if len(data) % 2 else b"")
header = struct.pack("<9I", 36, 2, 3, 32, 32, 32, 1, 6, 3)
body = b"ACON" + chunk(b"anih", header) + chunk(b"rate", struct.pack("<3I", 6, 12, 3)) + chunk(b"seq ", struct.pack("<3I", 1, 0, 1)) + chunk(b"LIST", b"fram" + chunk(b"icon", bytes(cur)) + chunk(b"icon", icons[1]))
(extra / "sample.ani").write_bytes(chunk(b"RIFF", body))
bad = body.replace(struct.pack("<3I", 1, 0, 1), struct.pack("<3I", 9, 0, 1))
(extra / "bad-sequence.ani").write_bytes(chunk(b"RIFF", bad))
(extra / "truncated.ani").write_bytes(chunk(b"RIFF", body)[:-7])
# Classic 32-bit ICNS planar RLE plus a half-transparent mask.
def bechunk(tag, payload): return tag + struct.pack(">I", len(payload)+8) + payload
rgb = b"".join(bytes([255, c, 251, c]) for c in (230, 20, 30))  # 130+126 pixels/channel
legacy = bechunk(b"is32", rgb) + bechunk(b"s8mk", bytes([128])*256)
(extra / "classic.icns").write_bytes(bechunk(b"icns", legacy))
(extra / "classic-raw.icns").write_bytes(bechunk(b"icns", bechunk(b"is32", bytes([230,20,30])*256)))
