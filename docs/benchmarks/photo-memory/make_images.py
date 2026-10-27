"""Builds the measurement and smoke-test images: python3 -I make_images.py <output directory>

Standard library plus macOS `sips` for JPEG encoding (so it runs on macOS as written). Produces images at the
configured limits: 40 MP PNGs (the most expensive decode), a 40 MP JPEG just under 5 MiB (the largest body),
a 12 MP phone-sized JPEG, and small EXIF/GPS, transparent, and deceptive files for the smoke test.
"""
import struct, zlib, random, subprocess, sys, os
out = sys.argv[1]

def png(path, w, h, row_fn, alpha=False):
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    comp = zlib.compressobj(9)
    parts = []
    for y in range(h):
        parts.append(comp.compress(b'\x02' + row_fn(y)))  # filter "Up": smooth vertical gradients compress well
    parts.append(comp.flush())
    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6 if alpha else 2, 0, 0, 0)
    data = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) + chunk(b'IDAT', b''.join(parts)) + chunk(b'IEND', b'')
    open(path, 'wb').write(data)
    return len(data)

def gradient_rows(w, h):
    # Raw first row is a horizontal gradient; later rows are "Up"-filtered deltas of a slow vertical change.
    first = bytes(v for x in range(w) for v in (x * 255 // w, 128, 255 - x * 255 // w))
    delta = bytes([1, 0, 0] * w)
    zero = bytes(3 * w)
    def row(y):
        if y == 0: return first  # with filter Up against an implicit zero row
        return delta if y % (h // 64 or 1) == 0 else zero
    return row

def jpeg_from_png(src, dst, quality):
    subprocess.run(['sips', '-s', 'format', 'jpeg', '-s', 'formatOptions', str(quality), src, '--out', dst],
                   check=True, capture_output=True)
    return os.path.getsize(dst)

sizes = {}
sizes['max-40mp.png'] = png(f'{out}/max-40mp.png', 8000, 5000, gradient_rows(8000, 5000))
sizes['max-edge-10000x4000.png'] = png(f'{out}/max-edge-10000x4000.png', 10000, 4000, gradient_rows(10000, 4000))
sizes['max-40mp.jpg'] = jpeg_from_png(f'{out}/max-40mp.png', f'{out}/max-40mp.jpg', 80)

# A "phone photo": 4032x3024 with mild texture so JPEG size is realistic (a few MB).
random.seed(7)
w, h = 4032, 3024
noise = bytes(random.randrange(0, 40) for _ in range(3 * w * 64))
def textured(y):
    base = noise[(y % 64) * 3 * w:(y % 64 + 1) * 3 * w]
    return base if y == 0 else bytes(3 * w)  # Up filter: repeat the first noisy row's texture with drift
tmp = f'{out}/phone-src.png'
png(tmp, w, h, lambda y: bytes((b + (y * 255 // h)) % 256 for b in noise[(y % 64) * 3 * w:(y % 64 + 1) * 3 * w]) if False else textured(y))
sizes['phone-12mp.jpg'] = jpeg_from_png(tmp, f'{out}/phone-12mp.jpg', 92)
os.remove(tmp)

# Smoke-test image: 300x100, left half red, right half blue; JPEG with EXIF orientation 6 + GPS IFD + a comment.
w, h = 300, 100
row = bytes([255, 0, 0] * (w // 2) + [0, 0, 255] * (w // 2))
png(f'{out}/lr.png', w, h, lambda y: row if y == 0 else bytes(3 * w))
jpeg_from_png(f'{out}/lr.png', f'{out}/lr.jpg', 95)
jpeg = open(f'{out}/lr.jpg', 'rb').read()
tiff = bytearray(56)
tiff[0:4] = b'II*\x00'; struct.pack_into('<I', tiff, 4, 8); struct.pack_into('<H', tiff, 8, 2)
def entry(off, tag, typ, count, value): struct.pack_into('<HHII', tiff, off, tag, typ, count, value)
entry(10, 0x0112, 3, 1, 6); entry(22, 0x8825, 4, 1, 38); struct.pack_into('<H', tiff, 38, 1); entry(40, 0x0001, 2, 2, ord('N'))
app1 = b'\xff\xe1' + struct.pack('>H', 2 + 6 + len(tiff)) + b'Exif\x00\x00' + bytes(tiff)
comment = b'harbour-secret-location-note'
com = b'\xff\xfe' + struct.pack('>H', 2 + len(comment)) + comment
open(f'{out}/exif-gps-orient6.jpg', 'wb').write(jpeg[:2] + app1 + com + jpeg[2:])

# Transparent PNG (alpha 0 everywhere) and a deceptive "photo.jpg" that is HTML.
png(f'{out}/transparent.png', 200, 100, lambda y: bytes(4 * 200), alpha=True)
open(f'{out}/deceptive.jpg', 'wb').write(b'<html><script>alert(1)</script></html>')
# A 40 MP JPEG just under 5 MiB: smooth noise (upscaled from 1000x625), encoded at quality 30.
random.seed(11)
w, h = 1000, 625
raw = b''.join(b'\x00' + random.randbytes(3 * w) for _ in range(h))
def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
open(f'{out}/noise-small.png', 'wb').write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 6)) + chunk(b'IEND', b''))
subprocess.run(['sips', '-z', '5000', '8000', f'{out}/noise-small.png', '--out', f'{out}/noise-40mp.png'], check=True, capture_output=True)
sizes['max-40mp-5mib.jpg'] = jpeg_from_png(f'{out}/noise-40mp.png', f'{out}/max-40mp-5mib.jpg', 30)
for name in ('noise-small.png', 'noise-40mp.png'):
    os.remove(f'{out}/{name}')
for k, v in sizes.items(): print(f'{k}: {v} bytes ({v/1048576:.2f} MiB)')
