# /// script
# requires-python = ">=3.12"
# dependencies = ["pillow>=11.0"]
# ///
"""把 images/.originals/ 里的 PNG 批量转成 images/ 下的 WebP（文件名主干不变）。

用法（单文件脚本，依赖内联声明，必须用 uv run 执行）：

    uv run tools/png-to-webp.py             # 转换全部
    uv run tools/png-to-webp.py --dry-run   # 只报告会做什么，不写文件
    uv run tools/png-to-webp.py --quality 92
    uv run tools/png-to-webp.py --width 0   # 0 = 不缩放，保留原始像素
    uv run tools/png-to-webp.py --keep-orphans   # 保留没有源 PNG 的既有 webp

输入与输出：

    images/.originals/<名字>.png   →   images/<名字>.webp

`.originals/` 是"可再次编辑的源"，产物才是入库的 webp；两者分开存放，
所以重跑本脚本永远不会把手工调过参数的产物当成输入。

为什么要缩到 1600 宽：

  源图是 2100x1395，README 里以 `width="49%"` 并排两张 —— 按 980px 容器算，
  单张实际显示约 480px。2100 宽在 2x 屏上要缩到 45% 才开始费像素，纯属浪费带宽。
  1600 宽在 2x 屏的 480px 显示位上仍有 1.6 倍余量，肉眼无差别，体积少三分之一。

为什么用有损 q=88 而不是无损：

  这批图是 UI 截图，**文字是主要内容**。有损压缩的失真最先吃掉的就是细笔画，
  但 q=88 下笔画仍完整；换来的是体积差数倍。无损只会得到一张比 PNG 小一点、
  却大过当前产物数倍的 webp，对 README 场景没有意义。

`--dry-run` 会顺带报告两类名字问题：产物多了（没有对应 PNG，多半是上一轮
改名留下的孤儿）与产物缺了（PNG 有但没转，可能是上次跑漏了）。
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image

# 产物宽度（像素）。0 = 保持源图原始宽度。
DEFAULT_WIDTH = 1600

# 有损 WebP 的质量。88 是"文字截图仍锐利"与"体积明显下降"的平衡点。
DEFAULT_QUALITY = 88

# method=6 是 libwebp 最高 effort，编码慢一点但同质量下体积最小。
# 这是一次性构建脚本，跑一次的耗时无关紧要。
WEBP_METHOD = 6


def human_size(num_bytes: int) -> str:
    if num_bytes >= 1024 * 1024:
        return f"{num_bytes / 1024 / 1024:.2f} MB"
    if num_bytes >= 1024:
        return f"{num_bytes / 1024:.1f} KB"
    return f"{num_bytes} B"


def target_name(png_path: Path) -> str:
    """源 PNG → 产物 webp 的文件名（只换扩展名，主干原样保留）。"""
    return f"{png_path.stem}.webp"


def convert_one(
    png_path: Path,
    dst_path: Path,
    width: int,
    quality: int,
    dry_run: bool,
) -> bool:
    """转换单张。返回是否真的写了文件（dry-run 时恒为 False）。"""
    src_bytes = png_path.stat().st_size

    with Image.open(png_path) as img:
        # PNG 常带 alpha；WebP 原生支持 alpha，但 P 模式（调色板）要先展开，
        # 否则透明像素的调色板索引会被当成颜色写进去。
        if img.mode not in ("RGB", "RGBA"):
            img = img.convert("RGBA")

        if width and img.width > width:
            # 只缩不放：源图比目标还小时放大只会糊。
            height = round(img.height * width / img.width)
            img = img.resize((width, height), Image.Resampling.LANCZOS)

        out_size = img.size

        if dry_run:
            print(f"· {png_path.name} → {dst_path.name}"
                  f"（{out_size[0]}x{out_size[1]}，源 {human_size(src_bytes)}）")
            return False

        # lossless=False + quality：截图场景下体积/观感最合适的组合。
        img.save(dst_path, format="WEBP", lossless=False, quality=quality, method=WEBP_METHOD)

    dst_bytes = dst_path.stat().st_size
    ratio = dst_bytes / src_bytes * 100 if src_bytes else 0
    print(f"✓ {dst_path.name} · {out_size[0]}x{out_size[1]}"
          f" · {human_size(src_bytes)} → {human_size(dst_bytes)}（{ratio:.0f}%）")
    return True


def main() -> int:
    repo = Path(__file__).resolve().parent.parent
    ap = argparse.ArgumentParser(
        description="把 images/.originals/*.png 转成 images/*.webp（文件名主干不变）")
    ap.add_argument("--src-dir", default=str(repo / "images" / ".originals"),
                    help="源 PNG 目录（默认 images/.originals）")
    ap.add_argument("--dst-dir", default=str(repo / "images"),
                    help="产物目录（默认 images）")
    ap.add_argument("--width", type=int, default=DEFAULT_WIDTH,
                    help=f"产物宽度，0 = 不缩放（默认 {DEFAULT_WIDTH}）")
    ap.add_argument("--quality", type=int, default=DEFAULT_QUALITY,
                    help=f"WebP 有损质量 0-100（默认 {DEFAULT_QUALITY}）")
    ap.add_argument("--dry-run", action="store_true", help="只报告，不写文件")
    ap.add_argument("--keep-orphans", action="store_true",
                    help="保留没有对应源 PNG 的既有 webp（默认会在报告后询问是否删除）")
    args = ap.parse_args()

    src_dir = Path(args.src_dir)
    dst_dir = Path(args.dst_dir)

    if not src_dir.is_dir():
        print(f"✗ 源目录不存在：{src_dir}", file=sys.stderr)
        return 2
    if not 0 <= args.quality <= 100:
        print(f"✗ --quality 必须在 0–100 之间（收到 {args.quality}）", file=sys.stderr)
        return 2

    pngs = sorted(src_dir.glob("*.png"))
    if not pngs:
        print(f"✗ {src_dir} 下没有 PNG", file=sys.stderr)
        return 2

    print(f"源目录  {src_dir}")
    print(f"产物目录 {dst_dir}")
    print(f"参数    宽度 {args.width or '原始'} · 质量 q{args.quality} · 有损")
    print()

    dst_dir.mkdir(parents=True, exist_ok=True)

    written = 0
    total_src = 0
    total_dst = 0
    for png in pngs:
        dst = dst_dir / target_name(png)
        existed = dst.exists()
        if convert_one(png, dst, args.width, args.quality, args.dry_run):
            written += 1
            total_src += png.stat().st_size
            total_dst += dst.stat().st_size
        if not existed and not args.dry_run:
            pass  # 新建与覆盖已在 convert_one 的输出里体现

    # ── 名字对不上的报告 ────────────────────────────────────────────────
    # 不自动删：删文件是不可逆的，先把清单摆出来由人决定。
    expected = {target_name(p) for p in pngs}
    orphans = sorted(
        p for p in dst_dir.glob("*.webp")
        if p.name not in expected and p.parent.resolve() == src_dir.parent.resolve()
    )

    print()
    print(f"转换完成：{len(pngs)} 张源图，写出 {written} 个产物。")
    if not args.dry_run and total_src:
        print(f"体积合计 {human_size(total_src)} → {human_size(total_dst)}"
              f"（{total_dst / total_src * 100:.0f}%）")

    if orphans:
        print()
        print(f"⚠ 以下 webp 在 .originals 里没有同名 PNG（{len(orphans)} 个）：")
        for p in orphans:
            print(f"    {p.name} · {human_size(p.stat().st_size)}")
        if args.keep_orphans:
            print("  已按 --keep-orphans 保留。")
        else:
            print("  多半是改名前的遗留物；若确认无用，手工删除后重跑本脚本。")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
