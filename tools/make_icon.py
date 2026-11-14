"""Generates sweepfella.ico (16-64 px) with no dependencies: a rounded green tile with a broom."""
import math, struct, sys

BG = (16, 124, 102)
HANDLE = (245, 235, 221)
BRISTLE = (246, 195, 67)
STRIPE = (214, 150, 40)
BAND = (190, 70, 50)

def norm(x, y):
    l = math.hypot(x, y)
    return x / l, y / l

D = norm(-0.33, 0.37)            # handle direction, towards the bristles
N = (D[1], -D[0])                # perpendicular
TOP = (0.475, 0.525)             # where the handle meets the bristles
LENGTH = 0.36

def in_rounded_rect(u, v, m=0.03, r=0.22):
    x = min(max(u, m + r), 1 - m - r)
    y = min(max(v, m + r), 1 - m - r)
    return (u - x) ** 2 + (v - y) ** 2 <= r * r and m <= u <= 1 - m and m <= v <= 1 - m

def color(u, v):
    if not in_rounded_rect(u, v):
        return None
    px, py = u - TOP[0], v - TOP[1]
    along = px * D[0] + py * D[1]
    across = px * N[0] + py * N[1]
    if 0 <= along <= LENGTH:
        half = 0.07 + (0.21 - 0.07) * (along / LENGTH)
        if abs(across) <= half:
            if along < 0.045:
                return BAND
            return STRIPE if (abs(across) / half * 3.5) % 1 < 0.22 else BRISTLE
    # handle: segment from TOP up and to the right
    if -0.42 <= along <= 0 and abs(across) <= 0.034:
        return HANDLE
    return BG

def render(size, ss=4):
    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    c = color((x + (sx + 0.5) / ss) / size, (y + (sy + 0.5) / ss) / size)
                    if c:
                        acc[0] += c[0]; acc[1] += c[1]; acc[2] += c[2]; acc[3] += 255
            n = ss * ss
            a = acc[3] / n
            if a == 0:
                row.append((0, 0, 0, 0))
            else:
                k = acc[3] / 255
                row.append((round(acc[0] / k), round(acc[1] / k), round(acc[2] / k), round(a)))
        rows.append(row)
    return rows

def bmp_entry(size):
    pixels = render(size)
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    data = bytearray()
    for row in reversed(pixels):
        for r, g, b, a in row:
            data += bytes((b, g, r, a))
    mask_row = ((size + 31) // 32) * 4
    data += bytes(mask_row * size)
    return header + bytes(data)

def main(out):
    sizes = [16, 24, 32, 48, 64]
    images = [bmp_entry(s) for s in sizes]
    offset = 6 + 16 * len(sizes)
    blob = struct.pack('<HHH', 0, 1, len(sizes))
    for s, img in zip(sizes, images):
        blob += struct.pack('<BBBBHHII', s, s, 0, 0, 1, 32, len(img), offset)
        offset += len(img)
    with open(out, 'wb') as f:
        f.write(blob + b''.join(images))

if __name__ == '__main__':
    main(sys.argv[1])
