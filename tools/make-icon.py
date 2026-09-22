# /// script
# requires-python = ">=3.12"
# dependencies = ["pillow>=11.0"]
# ///
"""从源 PNG 生成 DelayStart 的全部图标资源（管理端 / 调度端 / 守卫 / 两个中转器）。

用法（单文件脚本，依赖内联声明，必须用 uv run 执行）：

    uv run tools/make-icon.py                # 生成全部（AppIcon + 调度端两枚 + 守卫 + 两个中转器）
    uv run tools/make-icon.py --roles app    # 只生成管理端 AppIcon.ico
    uv run tools/make-icon.py --roles tray   # 只生成调度端 Scheduler.ico / SchedulerWarning.ico
    uv run tools/make-icon.py --roles guard  # 只生成守卫 DelayStart.Guard/Assets/Guard.ico
    uv run tools/make-icon.py --roles broker # 只生成两个中转器的 ico（LaunchBroker / NotifyBroker）
    uv run tools/make-icon.py --info         # 只看源图信息，不写文件

产物（五份都源自同一张 assets/icon/icon.png，改图后重跑本脚本即可）：

    src/DelayStart.App/Assets/AppIcon.ico             10 档 —— exe 内嵌图标 + 窗口图标 + 安装程序自身
    src/DelayStart.Scheduler/Assets/Scheduler.ico      10 档 —— 调度端 exe 图标 + 托盘（正常态）
    src/DelayStart.Scheduler/Assets/SchedulerWarning.ico
                                                       10 档 —— 托盘「完成但有失败」角标态（D31）
    src/DelayStart.Guard/Assets/Guard.ico              10 档 —— 守卫 exe 图标（计划任务 / 任务管理器可见）
    src/DelayStart.LaunchBroker/Assets/LaunchBroker.ico
                                                       10 档 —— 降权中转器 exe 图标（纯一致性）
    src/DelayStart.NotifyBroker/Assets/NotifyBroker.ico
                                                       10 档 —— 通知中转器 exe 图标（纯一致性）

为什么要自己写 ICO 容器而不用 Pillow 的 `save(format="ICO", sizes=[...])`：
  Pillow 的 ICO 插件对**所有**尺寸都用 PNG 压缩条目。PNG-in-ICO 在 Windows
  Vista+ 都支持，但小尺寸（16/20/24/32）走 PNG 时，某些老外壳路径（缩略图、
  部分第三方程序的文件对话框）会取不到图标而显示空白。
  这里按 Windows 官方建议做混合容器：小尺寸用 BMP/DIB(32bpp BGRA + AND 掩码)，
  256 用 PNG —— 兼容性最好，体积也可控。

源图要求（本项目）：正方形、带透明背景的 256x256 PNG。
  若源图不透明（无 alpha），生成的图标会是实心方块 —— 脚本会警告但不擅自改图。
"""

from __future__ import annotations

import argparse
import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

# 覆盖 Windows 外壳与任务栏用到的所有缩放档位：
# 16/20/24/32/40/48/64 = 100%~400% DPI 下的"小图标/中图标"，
# 96/128 = 大图标，"256 = 超大图标"（Explorer 视图 + 应用和功能页）。
DEFAULT_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

# >= 该尺寸的条目用 PNG 压缩，其余用 DIB（BMP）。
# 阈值取 96 而不是 256：96/128 两张 DIB 就要 105 KB，占满整个文件的一大半，
# 而它们只用在「大图标 / 超大图标」视图 —— 那些路径都是 Vista+ 的现代外壳，
# PNG 条目完全支持。小尺寸（16~48，会走缩略图、老式列表、第三方文件对话框）
# 仍然保持 DIB，那才是 PNG-in-ICO 真正有兼容风险的地方。
PNG_ENTRY_MIN = 96

# 托盘告警角标：红底 + 白圈 + 白叹号（与系统通知角标同一套语汇）。
BADGE_RED = (229, 20, 20, 255)
BADGE_WHITE = (255, 255, 255, 255)
BADGE_RATIO = 0.5      # 角标直径 / 图标边长（32px 图上 = 16px，够看清）
BADGE_SUPERSAMPLE = 4  # 角标按 4 倍画再缩小 —— 圆与叹号直接画在 16px 上会有锯齿


def dib_entry(img: Image.Image) -> bytes:
    """把一个 RGBA 图像编码成 ICO 用的 BITMAPINFOHEADER + XOR + AND 数据块。"""
    w, h = img.size
    # BMP 的行序是自下而上；32bpp 的字节序是 BGRA
    xor = img.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes("raw", "BGRA")
    # AND 掩码：1bpp，每行按 4 字节对齐。32bpp 图标靠 alpha 通道定透明，
    # 掩码全 0（= 全不透明）是通行做法。
    mask_row = ((w + 31) // 32) * 4
    mask = bytes(mask_row * h)
    header = struct.pack(
        "<IiiHHIIiiII",
        40,          # biSize
        w,           # biWidth
        h * 2,       # biHeight（XOR + AND 两张图，必须写成两倍）
        1,           # biPlanes
        32,          # biBitCount
        0,           # biCompression = BI_RGB
        len(xor) + len(mask),
        0, 0,        # 分辨率（0 = 不指定）
        0, 0,        # 调色板相关
    )
    return header + xor + mask


def pack_ico(frames: list[tuple[int, Image.Image]]) -> bytes:
    """把逐尺寸渲染好的帧按尺寸升序打包成 ICO 容器。

    ⚠️ 角标态必须**逐尺寸合成**再打包，不能"先合成 256 再统一缩" ——
    那样每个尺寸上的角标占比会不一致。
    """
    ordered = sorted(frames, key=lambda frame: frame[0])
    entries: list[bytes] = []
    for size, frame in ordered:
        if size >= PNG_ENTRY_MIN:
            buf = io.BytesIO()
            frame.save(buf, format="PNG", optimize=True)
            entries.append(buf.getvalue())
        else:
            entries.append(dib_entry(frame))

    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))  # ICONDIR
    offset = 6 + 16 * len(entries)
    body = bytearray()
    for (size, _), data in zip(ordered, entries):
        out += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,  # 0 表示 256
            0 if size >= 256 else size,
            0,   # 调色板颜色数（32bpp 固定 0）
            0,   # reserved
            1,   # planes
            32,  # bit count
            len(data),
            offset,
        )
        body += data
        offset += len(data)
    return bytes(out + body)


def scale(src: Image.Image, size: int) -> Image.Image:
    """等比缩放到 size×size（源图已经是目标尺寸时直接返回，不做插值）。"""
    return src if src.size == (size, size) else src.resize((size, size), Image.Resampling.LANCZOS)


def with_warning_badge(src: Image.Image, size: int) -> Image.Image:
    """右下角合成一枚红底白叹号角标（调度端「完成但有失败」的托盘态，D31）。

    角标先按 BADGE_SUPERSAMPLE 倍画再缩小，避免在 16/32px 上直接画圆与斜线出锯齿；
    叹号用手画的圆角矩形 + 圆点而不是字体 —— 不依赖系统字体，也不受 Pillow
    内置位图字体的尺寸限制。
    """
    base = scale(src, size).copy()

    diameter = max(8, round(size * BADGE_RATIO))
    ss = diameter * BADGE_SUPERSAMPLE
    badge = Image.new("RGBA", (ss, ss), (0, 0, 0, 0))
    draw = ImageDraw.Draw(badge)

    pad = max(1, round(ss * 0.04))
    ring = max(1, round(ss * 0.07))
    draw.ellipse((pad, pad, ss - pad - 1, ss - pad - 1), fill=BADGE_RED, outline=BADGE_WHITE, width=ring)

    # 叹号占圆内中间区域（外圈留给白边与呼吸空间）
    cx = ss / 2
    bar_w = max(2, round(ss * 0.13))
    draw.rounded_rectangle(
        (cx - bar_w / 2, ss * 0.28, cx + bar_w / 2, ss * 0.56),
        radius=bar_w / 2,
        fill=BADGE_WHITE,
    )
    dot_r = max(1.5, ss * 0.055)
    draw.ellipse((cx - dot_r, ss * 0.68 - dot_r, cx + dot_r, ss * 0.68 + dot_r), fill=BADGE_WHITE)

    badge = badge.resize((diameter, diameter), Image.Resampling.LANCZOS)
    inset = round(size * 0.02)
    base.alpha_composite(badge, (size - diameter - inset, size - diameter - inset))
    return base


def describe(src_path: Path, img: Image.Image) -> None:
    has_alpha = img.mode in ("RGBA", "LA") or "transparency" in img.info
    print(f"源图：{src_path}")
    print(f"  尺寸 {img.size[0]}x{img.size[1]} · 模式 {img.mode} · 有 alpha = {has_alpha} · {src_path.stat().st_size} 字节")

    if has_alpha:
        lo, hi = img.convert("RGBA").getchannel("A").getextrema()
        print(f"  alpha 范围 {lo}~{hi}")
        if lo == 255:
            print("  ⚠️ alpha 通道全不透明 —— 图标会是实心方块（在深色任务栏上不好看）。")
    else:
        print("  ⚠️ 源图没有透明通道 —— 生成的图标会是实心方块。")
        print("     如需透明背景，请用带 alpha 的 PNG 重新导出（VS 里建议导出 PNG 而非 JPG）。")

    if img.size[0] != img.size[1]:
        print(f"  ⚠️ 非正方形 {img.size} —— 会被拉伸成正方形，建议先裁成正方形。")
    if max(img.size) < 256:
        print(f"  ⚠️ 源图小于 256x256 —— 256 档会被放大，超大图标视图下会发虚。")


def write_ico(path: Path, data: bytes, sizes: tuple[int, ...], note: str = "") -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    suffix = f" · {note}" if note else ""
    print(f"✓ {path} · {len(data):,} 字节 · {len(sizes)} 档（{', '.join(map(str, sizes))}）{suffix}")


def main() -> int:
    repo = Path(__file__).resolve().parent.parent
    ap = argparse.ArgumentParser(description="生成 DelayStart 的图标资源")
    ap.add_argument("--src", default=str(repo / "assets" / "icon" / "icon.png"),
                    help="源 PNG（默认 assets/icon/icon.png）")
    ap.add_argument("--dst", default=str(repo / "src" / "DelayStart.App" / "Assets" / "AppIcon.ico"),
                    help="管理端图标输出路径")
    ap.add_argument("--sizes", default=",".join(str(s) for s in DEFAULT_SIZES))
    ap.add_argument("--roles", default="app,tray,guard,broker",
                    help="app / tray / guard / broker（默认全部）")
    ap.add_argument("--info", action="store_true", help="只打印源图信息，不生成")
    args = ap.parse_args()

    src_path = Path(args.src)
    if not src_path.is_file():
        print(f"✗ 源图不存在：{src_path}", file=sys.stderr)
        return 2

    img = Image.open(src_path)
    describe(src_path, img)

    if args.info:
        return 0

    roles = {role.strip() for role in args.roles.split(",") if role.strip()}
    unknown = roles - {"app", "tray", "guard", "broker"}
    if unknown:
        print(f"✗ 未知的 --roles：{', '.join(sorted(unknown))}", file=sys.stderr)
        return 2

    rgba = img.convert("RGBA")
    sizes = tuple(sorted(int(s) for s in args.sizes.split(",") if s.strip()))

    if "app" in roles:
        write_ico(Path(args.dst), pack_ico([(s, scale(rgba, s)) for s in sizes]), sizes)

    if "guard" in roles:
        guard_dir = repo / "src" / "DelayStart.Guard" / "Assets"
        write_ico(guard_dir / "Guard.ico", pack_ico([(s, scale(rgba, s)) for s in sizes]), sizes)

    if "broker" in roles:
        # 两个降权中转器形态相同（跑完即退、无窗口/托盘/计划任务），exe 图标纯属
        # 资源管理器里的一致性 —— 共用同一张源图，各落一份 ico。
        broker_note = "与守卫同图（broker 无图标露出面，纯一致性）"
        write_ico(repo / "src" / "DelayStart.LaunchBroker" / "Assets" / "LaunchBroker.ico",
                  pack_ico([(s, scale(rgba, s)) for s in sizes]), sizes, note=broker_note)
        write_ico(repo / "src" / "DelayStart.NotifyBroker" / "Assets" / "NotifyBroker.ico",
                  pack_ico([(s, scale(rgba, s)) for s in sizes]), sizes, note=broker_note)

    if "tray" in roles:
        tray_dir = repo / "src" / "DelayStart.Scheduler" / "Assets"
        write_ico(tray_dir / "Scheduler.ico", pack_ico([(s, scale(rgba, s)) for s in sizes]), sizes)
        write_ico(
            tray_dir / "SchedulerWarning.ico",
            pack_ico([(s, with_warning_badge(rgba, s)) for s in sizes]),
            sizes,
            note="含红色告警角标",
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
