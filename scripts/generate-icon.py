#!/usr/bin/env python3
"""Generate EchoGarage's application icon (multi-size ICO) from the user-supplied SVG artwork.

The artwork lives in a 64x64 viewBox with a transparent background: four columns of
8.5x8.5 rounded squares in blue shades (a stylised waveform / equalizer). All geometry
and colours below are copied verbatim from the SVG, in SVG user units:

  column 1: x = 9.75, y = 9.75 / 21.75 / 33.75 / 45.75, fill #1E40AF, rx = 2.5
  column 2: x = 21.75, y = 15.75 / 27.75 / 39.75,       fill #2563EB, rx = 2.5
  column 3: x = 33.75, y = 21.75 / 33.75,               fill #93C5FD, rx = 2.5
  column 4: x = 45.75, y = 27.75,                       fill #38BDF8, rx = 3

Pipeline (deterministic -> byte-identical output on every run):
  1. render the artwork on a supersampled RGBA canvas (default 1024x1024, transparent)
     with ImageDraw.rounded_rectangle, using the exact float coordinates expressed in
     device pixels (supersample 16 keeps every edge/corner on an integer pixel);
  2. downscale the master to every requested size with a LANCZOS filter applied in
     premultiplied-alpha space: the colour channels are multiplied by the alpha before
     resampling and the alpha is divided back out afterwards. Resampling straight RGBA
     would treat the RGB of fully transparent pixels (black) as data and let the filter's
     negative lobes pull it into the edges, which washes the fills out at small sizes;
  3. save the ICO from the largest frame with Pillow's `sizes=` parameter (Pillow stores
     the frames PNG-compressed); the remaining exact-size frames are supplied via
     `append_images` so no frame is resampled twice;
  4. write preview PNGs (256/64/32/16) and read the ICO back to verify frame sizes,
     exact fill colours, transparent background and anti-aliased rounded corners.

Usage (run from the repository root; use the bundled Python that ships Pillow):
  python scripts\\generate-icon.py
  python scripts\\generate-icon.py --ico src\\EchoGarage.App\\Resources\\echogarage.ico
  python scripts\\generate-icon.py --preview-dir .scratch\\icon-preview --sizes 16,20,24,32,40,48,64,128,256

Exit code 0 means the icon was written and all self-checks passed.
"""

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

# ---------------------------------------------------------------------------
# Artwork definition (verbatim from the user-supplied SVG, 64x64 viewBox).
# ---------------------------------------------------------------------------

VIEWBOX = 64.0
SQUARE = 8.5

# (x, [y values], rx, fill) per SVG column, in the SVG's document order.
COLUMNS: tuple[tuple[float, tuple[float, ...], float, str], ...] = (
    (9.75, (9.75, 21.75, 33.75, 45.75), 2.5, "#1E40AF"),
    (21.75, (15.75, 27.75, 39.75), 2.5, "#2563EB"),
    (33.75, (21.75, 33.75), 2.5, "#93C5FD"),
    (45.75, (27.75,), 3.0, "#38BDF8"),
)

# SVG bounding box of all artwork (x0, y0, x1, y1), in SVG user units.
CONTENT_BBOX = (9.75, 9.75, 54.25, 54.25)

DEFAULT_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
DEFAULT_SUPERSAMPLE = 16  # 64 * 16 = 1024 px master canvas
PREVIEW_SIZES = (256, 64, 32, 16)

LANCZOS = Image.Resampling.LANCZOS

# Verification tolerances, calibrated against the LANCZOS kernel (see verify_ico):
HALO_GAP_PX = 3.0  # half-width of the LANCZOS kernel's reach, in destination pixels
HALO_ALPHA_MAX = 96  # ringing inside that reach must stay faint
HALO_MASS_FRACTION = 0.02  # total alpha outside the artwork vs. inside
SIGNIFICANT_ALPHA = 16  # alpha threshold used to measure the content bounding box
BBOX_TOLERANCE_PX = 1.5  # allowed deviation of that bbox from the SVG-derived bbox
CENTER_MIN_ALPHA = 200  # square centres must be (nearly) opaque
CENTER_EXACT_MIN_SIZE = 32  # at and above this size centre colours must be exact
CENTER_DELTA_SMALL = 8  # channel tolerance for the tiny sizes
LARGEST_UNIQUE_COLORS_MIN = 100  # the 256px frame must carry a rich AA ramp

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_ICO = REPO_ROOT / "src" / "EchoGarage.App" / "Resources" / "echogarage.ico"
DEFAULT_PREVIEW_DIR = REPO_ROOT / ".scratch" / "icon-preview"


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Render the EchoGarage application icon into a multi-size ICO.",
    )
    parser.add_argument(
        "--ico",
        type=Path,
        default=DEFAULT_ICO,
        help=f"output .ico path (default: {DEFAULT_ICO})",
    )
    parser.add_argument(
        "--preview-dir",
        type=Path,
        default=DEFAULT_PREVIEW_DIR,
        help=f"directory for preview PNGs (default: {DEFAULT_PREVIEW_DIR})",
    )
    parser.add_argument(
        "--sizes",
        default=",".join(str(size) for size in DEFAULT_SIZES),
        help="comma-separated square icon sizes to embed, e.g. 16,32,48,256",
    )
    parser.add_argument(
        "--supersample",
        type=int,
        default=DEFAULT_SUPERSAMPLE,
        help=f"supersample factor for the master canvas (default: {DEFAULT_SUPERSAMPLE})",
    )
    parser.add_argument(
        "--no-previews",
        action="store_true",
        help="skip writing preview PNGs",
    )
    return parser.parse_args(argv)


def parse_sizes(raw: str) -> tuple[int, ...]:
    try:
        sizes = tuple(sorted({int(part.strip()) for part in raw.split(",") if part.strip()}))
    except ValueError as exc:  # pragma: no cover - defensive CLI validation
        raise SystemExit(f"invalid --sizes value {raw!r}: {exc}") from exc
    if not sizes:
        raise SystemExit("--sizes must not be empty")
    if sizes[0] < 1 or sizes[-1] > 256:
        raise SystemExit(f"icon sizes must be in 1..256, got {sizes}")
    if 256 not in sizes:
        raise SystemExit(f"the ICO must embed a 256x256 frame as the largest source, got {sizes}")
    return sizes


def validate_supersample(supersample: int) -> None:
    if supersample < 1:
        raise SystemExit(f"--supersample must be >= 1, got {supersample}")
    values: list[float] = [VIEWBOX, SQUARE, *CONTENT_BBOX]
    for x, ys, rx, _fill in COLUMNS:
        values.extend((x, rx))
        values.extend(ys)
    for value in values:
        scaled = value * supersample
        if abs(scaled - round(scaled)) > 1e-9:
            raise SystemExit(
                f"--supersample {supersample} maps SVG coordinate {value} to non-integer "
                f"pixel {scaled}; use a multiple of 4 (e.g. 8, 16, 32) instead"
            )


def _pixels(image: Image.Image):
    """Iterate over an image's pixel values, preferring the non-deprecated Pillow API."""
    getter = getattr(image, "get_flattened_data", None) or image.getdata
    return getter()


def _byte(value: float) -> int:
    """Round a float channel value to an 8-bit channel, clamping out-of-range values."""
    if value <= 0.0:
        return 0
    if value >= 255.0:
        return 255
    return int(round(value))


# ---------------------------------------------------------------------------
# Rendering.
# ---------------------------------------------------------------------------


def render_master(supersample: int) -> Image.Image:
    """Draw the SVG artwork on a transparent RGBA canvas of 64 * supersample pixels."""
    canvas_size = int(round(VIEWBOX * supersample))
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def px(value: float) -> int:
        return int(round(value * supersample))

    for x, ys, rx, fill in COLUMNS:
        left = px(x)
        right = px(x + SQUARE)
        radius = px(rx)
        for y in ys:
            top = px(y)
            bottom = px(y + SQUARE)
            draw.rounded_rectangle((left, top, right, bottom), radius=radius, fill=fill)

    return image


def downscale_premultiplied(master: Image.Image, size: int) -> Image.Image:
    """LANCZOS-resize the master to `size` x `size` px in premultiplied-alpha space.

    Resampling straight-alpha RGBA treats the RGB of fully transparent pixels (black in
    the master) as data and the LANCZOS negative lobes pull that black into the artwork,
    overshooting the fills at small sizes (the 16px icon looked washed out and too bright).
    Premultiplying the colours, resampling and dividing the alpha back out is the correct
    way to downscale translucent artwork: every covered pixel ends up as the alpha-weighted
    average of the SVG fills, so the exact #1E40AF/#2563EB/#93C5FD/#38BDF8 ramp survives.
    """
    alpha = master.getchannel("A")
    alpha_small = alpha.convert("F").resize((size, size), LANCZOS)
    alphas = list(_pixels(alpha_small))

    premultiplied_small: list[list[float]] = []
    for band in master.convert("RGB").split():
        premultiplied = ImageChops.multiply(band, alpha).convert("F").resize((size, size), LANCZOS)
        premultiplied_small.append(list(_pixels(premultiplied)))

    pixels: list[tuple[int, int, int, int]] = []
    for index, alpha_value in enumerate(alphas):
        if alpha_value < 0.5:  # fully transparent (also drops the negative ringing lobes)
            pixels.append((0, 0, 0, 0))
            continue
        scale = 255.0 / alpha_value
        red, green, blue = (channel[index] for channel in premultiplied_small)
        pixels.append(
            (
                _byte(red * scale),
                _byte(green * scale),
                _byte(blue * scale),
                _byte(alpha_value),
            )
        )

    frame = Image.new("RGBA", (size, size))
    frame.putdata(pixels)
    frame.load()
    return frame


def build_frames(master: Image.Image, sizes: tuple[int, ...]) -> dict[int, Image.Image]:
    """Downscale the supersampled master to every requested square size."""
    return {size: downscale_premultiplied(master, size) for size in sizes}


def save_ico(path: Path, frames: dict[int, Image.Image], sizes: tuple[int, ...]) -> None:
    """Save the multi-size ICO from the largest frame, embedding every exact-size frame."""
    largest = max(sizes)
    path.parent.mkdir(parents=True, exist_ok=True)
    frames[largest].save(
        path,
        format="ICO",
        sizes=[(size, size) for size in sizes],
        append_images=[frames[size] for size in sizes if size != largest],
    )


def save_previews(preview_dir: Path, frames: dict[int, Image.Image]) -> list[Path]:
    preview_dir.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []
    for size in PREVIEW_SIZES:
        if size not in frames:
            continue
        path = preview_dir / f"icon-{size}.png"
        frames[size].save(path, format="PNG")
        written.append(path)
    return written


# ---------------------------------------------------------------------------
# Verification (read the produced ICO back and check it against the artwork).
# ---------------------------------------------------------------------------


def rgb(fill: str) -> tuple[int, int, int]:
    value = fill.lstrip("#")
    return (int(value[0:2], 16), int(value[2:4], 16), int(value[4:6], 16))


def square_centers() -> list[tuple[float, float, tuple[int, int, int]]]:
    """Centres (SVG units) and fill colours of every square, for pixel probes."""
    centers: list[tuple[float, float, tuple[int, int, int]]] = []
    for x, ys, _rx, fill in COLUMNS:
        for y in ys:
            centers.append((x + SQUARE / 2.0, y + SQUARE / 2.0, rgb(fill)))
    return centers


def square_rects() -> list[tuple[float, float, float, float]]:
    """Bounding boxes (SVG units, x0/y0/x1/y1) of every square, ignoring corner rounding."""
    rects: list[tuple[float, float, float, float]] = []
    for x, ys, _rx, _fill in COLUMNS:
        for y in ys:
            rects.append((x, y, x + SQUARE, y + SQUARE))
    return rects


def intersects_any(
    left: float, top: float, right: float, bottom: float, rects: list[tuple[float, float, float, float]]
) -> bool:
    return any(
        left < rect_right and right > rect_left and top < rect_bottom and bottom > rect_top
        for rect_left, rect_top, rect_right, rect_bottom in rects
    )


def bbox_gap_px(
    left: float, top: float, right: float, bottom: float,
    rects: list[tuple[float, float, float, float]], unit: float,
) -> float:
    """Distance in destination pixels from a pixel's SVG footprint to the nearest square."""
    nearest = float("inf")
    for rect_left, rect_top, rect_right, rect_bottom in rects:
        dx = max(rect_left - right, left - rect_right)
        dy = max(rect_top - bottom, top - rect_bottom)
        if dx <= 0.0 and dy <= 0.0:
            return 0.0  # the footprint overlaps this square's bounding box
        nearest = min(nearest, max(dx, dy) / unit)
    return nearest


def verify_transparency(frame: Image.Image) -> dict[str, float]:
    """Check that everything outside the artwork is transparent up to the filter halo.

    LANCZOS has a finite kernel, so a few pixels immediately around the artwork can carry
    faint ringing; further away nothing is allowed to leak. The check therefore:
      * requires exact alpha 0 for every pixel whose footprint is at least HALO_GAP_PX
        away from the nearest square (that is beyond the kernel's reach);
      * allows only faint (<= HALO_ALPHA_MAX) halo pixels inside that reach;
      * keeps the total halo mass negligible compared with the artwork's alpha mass;
      * requires the four corner pixels (always far from the artwork) to be fully clear.
    """
    size = frame.width
    rects = square_rects()
    unit = VIEWBOX / size
    alpha = frame.getchannel("A")

    outside_mass = 0
    inside_mass = 0
    strict_checked = 0
    for py in range(size):
        top = py * unit
        bottom = top + unit
        for px in range(size):
            left = px * unit
            right = left + unit
            value = alpha.getpixel((px, py))
            if intersects_any(left, top, right, bottom, rects):
                inside_mass += value
                continue
            outside_mass += value
            gap = bbox_gap_px(left, top, right, bottom, rects, unit)
            if gap >= HALO_GAP_PX:
                strict_checked += 1
                assert value == 0, (
                    f"frame {size}: pixel ({px}, {py}) is {gap:.2f}px away from the artwork "
                    f"but has alpha {value} (expected a fully transparent pixel)"
                )
            else:
                assert value <= HALO_ALPHA_MAX, (
                    f"frame {size}: pixel ({px}, {py}) just outside the artwork has alpha "
                    f"{value} (> {HALO_ALPHA_MAX}) instead of a faint filter halo"
                )

    for corner in ((0, 0), (size - 1, 0), (0, size - 1), (size - 1, size - 1)):
        assert alpha.getpixel(corner) == 0, f"frame {size}: corner {corner} is not transparent"
    assert strict_checked >= 8, (
        f"frame {size}: only {strict_checked} pixels beyond the filter halo were checked"
    )
    assert outside_mass <= HALO_MASS_FRACTION * max(inside_mass, 1), (
        f"frame {size}: halo alpha mass {outside_mass} outside the artwork is not negligible "
        f"compared with {inside_mass} inside it"
    )
    return {
        "strict_checked": strict_checked,
        "outside_mass": outside_mass,
        "inside_mass": inside_mass,
    }


def significant_bbox(frame: Image.Image, threshold: int) -> tuple[int, int, int, int]:
    """Bounding box of the pixels whose alpha reaches `threshold` (ignores faint halo)."""
    mask = frame.getchannel("A").point(lambda value: 255 if value >= threshold else 0)
    bbox = mask.getbbox()
    assert bbox is not None, f"frame {frame.width}: no pixel reaches alpha {threshold}"
    return bbox


def open_ico_frame(ico: Image.Image, size: int) -> Image.Image:
    """Pick a frame of the given square size via the documented ICO size attribute."""
    ico.size = (size, size)
    ico.load()
    return ico.copy()


def verify_ico(
    ico_path: Path,
    sizes: tuple[int, ...],
    previews: list[Path],
    frames: dict[int, Image.Image],
) -> None:
    if not ico_path.is_file() or ico_path.stat().st_size == 0:
        raise AssertionError(f"ICO was not written: {ico_path}")

    expected_sizes = {(size, size) for size in sizes}
    with Image.open(ico_path) as ico:
        assert ico.format == "ICO", f"unexpected format {ico.format!r}"
        assert set(ico.ico.sizes()) == expected_sizes, (
            "ICO frame sizes differ from the requested set: "
            f"got {sorted(ico.ico.sizes())}, want {sorted(expected_sizes)}"
        )

        for size in sizes:
            frame = open_ico_frame(ico, size)
            assert frame.size == (size, size), f"frame {size}: unexpected size {frame.size}"
            assert frame.mode == "RGBA", f"frame {size}: unexpected mode {frame.mode!r}"

            # 1) Transparent background: nothing leaks outside the artwork but the halo.
            verify_transparency(frame)

            # 2) Content occupies exactly the SVG-derived area (9.75..54.25 user units).
            bbox = significant_bbox(frame, SIGNIFICANT_ALPHA)
            expected = tuple(bound * size / VIEWBOX for bound in CONTENT_BBOX)
            for got, want in zip(bbox, expected):
                assert abs(got - want) <= BBOX_TOLERANCE_PX, (
                    f"frame {size}: content bbox {bbox} deviates from expected "
                    f"{tuple(round(value, 2) for value in expected)}"
                )

            # 3) Anti-aliasing: rounded corners must yield partially transparent pixels.
            partial = sum(1 for value in _pixels(frame.getchannel("A")) if 0 < value < 255)
            assert partial > 0, f"frame {size}: no anti-aliased edge pixels found"

            # 4) Colours: every square centre carries its SVG fill. Frames at and above
            #    CENTER_EXACT_MIN_SIZE are large enough for an untouched centre pixel;
            #    the tiny frames get a small tolerance (measured deviation is <= 1).
            for sx, sy, fill in square_centers():
                point = (int(sx * size / VIEWBOX), int(sy * size / VIEWBOX))
                pixel = frame.getpixel(point)
                assert pixel[3] >= CENTER_MIN_ALPHA, (
                    f"frame {size}: square centre {point} is too transparent ({pixel[3]})"
                )
                if size >= CENTER_EXACT_MIN_SIZE:
                    assert pixel[:3] == fill, (
                        f"frame {size}: square centre {point} colour {pixel[:3]} != SVG fill {fill}"
                    )
                else:
                    delta = max(abs(pixel[i] - fill[i]) for i in range(3))
                    assert delta <= CENTER_DELTA_SMALL, (
                        f"frame {size}: square centre {point} colour {pixel[:3]} is far from "
                        f"SVG fill {fill} (max channel delta {delta})"
                    )

        # The largest frame must carry all four exact fills and a rich AA colour ramp.
        largest = open_ico_frame(ico, max(sizes))
        largest_colors = largest.getcolors(maxcolors=max(sizes) ** 2 + 1)
        assert largest_colors is not None, "largest frame: colour count exceeded maxcolors"
        exact_fills = {color for _count, color in largest_colors if color[3] == 255}
        for _sx, _sy, fill in square_centers():
            assert fill + (255,) in exact_fills, f"largest frame lacks exact fill {fill}"
        assert len(largest_colors) > LARGEST_UNIQUE_COLORS_MIN, (
            f"largest frame has only {len(largest_colors)} unique colours; expected a rich "
            "anti-aliasing ramp"
        )

    # Preview PNGs must exist and be pixel-identical to the matching ICO frames.
    for path in previews:
        preview_size = int(path.stem.rsplit("-", 1)[-1])
        with Image.open(path) as preview:
            preview.load()
            assert preview.size == (preview_size, preview_size), (
                f"{path.name}: unexpected size {preview.size}"
            )
            diff = ImageChops.difference(preview.convert("RGBA"), frames[preview_size])
            assert diff.getbbox() is None, f"{path.name}: differs from in-memory frame"


# ---------------------------------------------------------------------------
# Entry point.
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    sizes = parse_sizes(args.sizes)
    validate_supersample(args.supersample)

    ico_path = args.ico
    if not ico_path.is_absolute():
        ico_path = (Path.cwd() / ico_path).resolve()

    master = render_master(args.supersample)
    frames = build_frames(master, sizes)
    save_ico(ico_path, frames, sizes)

    previews: list[Path] = []
    if not args.no_previews:
        previews = save_previews(args.preview_dir, frames)

    verify_ico(ico_path, sizes, previews, frames)

    data = ico_path.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    print(f"master canvas : {master.width}x{master.height} (supersample={args.supersample})")
    print(f"ICO           : {ico_path}")
    print(f"ICO sizes     : {', '.join(f'{size}x{size}' for size in sizes)} ({len(sizes)} frames)")
    print(f"ICO bytes     : {len(data):,}")
    print(f"ICO sha256    : {digest}")
    if previews:
        print("preview PNGs  : " + ", ".join(str(path) for path in previews))
    print("verification  : OK (frame sizes, transparent background, exact fills, AA corners)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
