# -*- coding: utf-8 -*-
"""Prepare desktop-pet images from community fan-art candidates.

Inputs (assets-raw/):
  raw4.png  three-view chibi design sheet (white background)  -> main pet
  raw2.png  maid full-body illustration (cream background)     -> alternate pet
Outputs (assets-raw/out/):
  pet.png             main transparent pet image
  pet-alt-maid.png    alternate transparent pet image
  app.ico             application icon
  *_preview.png       visual check images (checkerboard background)
"""
import os
from PIL import Image, ImageDraw, ImageFilter
import numpy as np
from scipy import ndimage

BASE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(BASE, "..", "assets-raw")
OUT = os.path.join(RAW, "out")
os.makedirs(OUT, exist_ok=True)


def key_background(im, thresh=45, erode=1, feather=0.7):
    """Remove border-connected near-solid background color."""
    im = im.convert("RGBA")
    arr = np.asarray(im).astype(np.int16)
    rgb = arr[..., :3]
    border = np.concatenate([rgb[0, :], rgb[-1, :], rgb[:, 0], rgb[:, -1]])
    bg = np.median(border, axis=0)
    dist = np.abs(rgb - bg).sum(axis=2)
    near = dist < thresh

    lbl, n = ndimage.label(near)
    border_labels = set(lbl[0, :]) | set(lbl[-1, :]) | set(lbl[:, 0]) | set(lbl[:, -1])
    border_labels.discard(0)
    if not border_labels:
        bgmask = np.zeros(near.shape, dtype=bool)
    else:
        bgmask = np.isin(lbl, list(border_labels))
    char = (~bgmask).astype(np.uint8) * 255

    m = Image.fromarray(char, "L")
    if erode:
        m = m.filter(ImageFilter.MinFilter(3))
    if feather:
        m = m.filter(ImageFilter.GaussianBlur(feather))

    out = im.copy()
    out.putalpha(m)
    return out


def split_views(im, gap_threshold=8, min_width=60):
    """Split a keyed multi-view sheet into column groups."""
    alpha = im.getchannel("A")
    w, h = alpha.size
    prof = list(alpha.resize((w, 1), Image.BOX).getdata())
    groups, start = [], None
    for x, v in enumerate(prof):
        occupied = v > gap_threshold
        if occupied and start is None:
            start = x
        elif not occupied and start is not None:
            if x - start > min_width:
                groups.append((start, x))
            start = None
    if start is not None and len(prof) - start > min_width:
        groups.append((start, len(prof)))
    return groups


def tight_crop(im, pad=6):
    bbox = im.getchannel("A").getbbox()
    x0, y0, x1, y1 = bbox
    x0, y0 = max(0, x0 - pad), max(0, y0 - pad)
    x1, y1 = min(im.width, x1 + pad), min(im.height, y1 + pad)
    return im.crop((x0, y0, x1, y1))


def fit_size(im, max_side=820):
    scale = max_side / max(im.size)
    if scale < 1:
        im = im.resize((max(1, int(im.width * scale)), max(1, int(im.height * scale))), Image.LANCZOS)
    return im


def save_preview(im, path, scale_h=520):
    ratio = scale_h / im.height
    im2 = im.resize((max(1, int(im.width * ratio)), scale_h), Image.LANCZOS)
    pad = 44
    canvas = Image.new("RGB", (im2.width + pad * 2, im2.height + pad * 2), (245, 246, 250))
    d = ImageDraw.Draw(canvas)
    for yy in range(0, canvas.height, 20):
        for xx in range(0, canvas.width, 20):
            if (xx // 20 + yy // 20) % 2 == 0:
                d.rectangle([xx, yy, xx + 19, yy + 19], fill=(227, 230, 237))
    canvas.paste(im2, (pad, pad), im2)
    canvas.save(path)


def main():
    # ---------- main pet: front-view chibi from the three-view sheet ----------
    sheet = Image.open(os.path.join(RAW, "raw4.png"))
    keyed = key_background(sheet, thresh=45)
    groups = split_views(keyed)
    print("view groups:", groups)
    for i, (x0, x1) in enumerate(groups[:3]):
        view = keyed.crop((x0, 0, x1, keyed.height))
        view = tight_crop(view)
        view = fit_size(view)
        view.save(os.path.join(OUT, f"view{i + 1}.png"))
        print(f"view{i + 1}.png", view.size)
        if i == 0:
            view.save(os.path.join(OUT, "pet.png"))
            save_preview(view, os.path.join(OUT, "pet_preview.png"))
            # icon: head area
            head = view.crop((0, 0, view.width, int(view.height * 0.52)))
            side = max(head.size)
            sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
            sq.paste(head, ((side - head.width) // 2, int(side * 0.04)), head)
            sq = sq.resize((256, 256), Image.LANCZOS)
            sq.save(os.path.join(OUT, "app.ico"),
                    sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
            sq.save(os.path.join(OUT, "icon_preview.png"))
            print("app.ico saved")

    # ---------- alternate: maid full body ----------
    try:
        maid = Image.open(os.path.join(RAW, "raw2.png"))
        k2 = key_background(maid, thresh=70)
        k2 = tight_crop(k2)
        k2 = fit_size(k2)
        k2.save(os.path.join(OUT, "pet-alt-maid.png"))
        save_preview(k2, os.path.join(OUT, "pet-alt-preview.png"))
        print("alt saved", k2.size)
    except Exception as e:
        print("alt failed:", repr(e))


if __name__ == "__main__":
    main()
