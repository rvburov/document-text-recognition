#!/usr/bin/env python3
"""V4 drafting-font corpus: the whole Russian alphabet in four GOST-style fonts.

Every lowercase and uppercase Russian letter, digits and designation punctuation are
rendered many times; words with letters that OCR confuses on drafting fonts (б/в/д, ъ/ь,
з/3/Э, й/и, и/у, т and others) are drawn more often. Truths are authored text, never OCR output.

Words come from a plain word list given on the command line (the build used the stems of
the LibreOffice ru_RU Hunspell dictionary). Words used by the application's blind checks
(alphabet sweep lines and real-document truths) are excluded with --exclude so that these
checks stay blind. Output layout matches prepare.py: <out>/<split>/<id>.png + <id>.gt.txt.
"""
import argparse
import hashlib
import io
import json
import random
import re
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

LOWER = "абвгдежзийклмнопрстуфхцчшщъыьэюя"
UPPER = LOWER.upper()
CONFUSABLE = "бвдъьзэйиутжшщцфюяг"
ORG_LETTERS = "АБВГДЕЖИКЛМНПРСТУФХЦШЩЭЮЯ"  # ГОСТ 2.201: без Ё, З, Й, О, Ч, Ъ, Ы, Ь
CODES = ["СБ", "Э3", "ПЭ3", "ТУ", "ВП", "Д1", "ПЗ", "Э4", "К3", "Г3", "РЭ", "ПС", "ЭД", "ВО", "МЧ", "Э1", "Э5",
         "ПЭ4", "ТЭ5", "Э6", "С3", "Л3", "ИМ", "ФО", "ЗИ", "ТЧ", "УЧ"]
TYPES = ["Сборочный чертеж", "Схема электрическая принципиальная", "Перечень элементов", "Спецификация",
         "Чертеж общего вида", "Габаритный чертеж", "Монтажный чертеж", "Технические условия",
         "Пояснительная записка", "Схема электрическая структурная", "Схема электрическая функциональная",
         "Схема электрическая соединений", "Ведомость покупных изделий", "Руководство по эксплуатации",
         "Таблица соединений", "Электромонтажный чертеж", "Программа и методика испытаний", "Паспорт", "Этикетка"]

# Pinned fixture fonts (tests/Core.Tests/Fonts). Skew emulates the 15 degree italic of CAD GOST fonts.
FONTS = [
    ("gost-a-slanted", "GOST2.304-81TypeA-Slanted.ttf", "e83689021a988a15736a70799aa166345499de548664079c58435d54d7cd973e", 0.0),
    ("opengost-a-italic", "OpenGOSTtypeA-Regular.ttf", "3caec4e2a004eacf8aea6883781015fc2df9b7c0154e974cec97f22299d7fc0e", 0.268),
    ("opengost-b-italic", "OpenGOSTtypeB-Regular.ttf", "510f175c6856e7d9fc4ab2388266cf09860ef7e9ab10fc1baa6b64d09e4c63d9", 0.268),
    ("opengost-a-upright", "OpenGOSTtypeA-Regular.ttf", "3caec4e2a004eacf8aea6883781015fc2df9b7c0154e974cec97f22299d7fc0e", 0.0),
    ("opengost-b-upright", "OpenGOSTtypeB-Regular.ttf", "510f175c6856e7d9fc4ab2388266cf09860ef7e9ab10fc1baa6b64d09e4c63d9", 0.0),
]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def load_words(path, exclude):
    words = []
    for raw in Path(path).read_text(encoding="utf-8").splitlines():
        word = raw.split("/")[0].strip()
        if not re.fullmatch(f"[{LOWER}]{{3,14}}", word) or "ё" in word:
            continue
        if word in exclude:
            continue
        words.append(word)
    return sorted(set(words))


def excluded_words(paths):
    words = set()
    for path in paths:
        text = Path(path).read_text(encoding="utf-8").lower().replace("ё", "е")
        words.update(re.findall(f"[{LOWER}]+", text))
    return words


class Lines:
    def __init__(self, words, rng):
        self.rng = rng
        self.words = words
        # Words rich in confusable letters are drawn more often.
        weights = [1 + 3 * sum(c in CONFUSABLE for c in w) for w in words]
        self.cumulative = []
        total = 0
        for w in weights:
            total += w
            self.cumulative.append(total)
        self.total = total

    def word(self):
        import bisect
        return self.words[bisect.bisect_right(self.cumulative, self.rng.random() * self.total)]

    def name(self):
        rng = self.rng
        parts = [self.word() for _ in range(rng.choice((1, 2, 2, 2, 3)))]
        parts[0] = parts[0].capitalize()
        if rng.random() < 0.15:
            parts.append(str(rng.randint(10, 999999)))
        if rng.random() < 0.08 and len(parts) > 1:
            parts[0] = parts[0] + "-" + parts.pop(1)
        return " ".join(parts)

    def upper_name(self):
        return " ".join(self.word() for _ in range(self.rng.choice((1, 1, 2)))).upper()

    def designation(self):
        rng = self.rng
        org = "".join(rng.choice(ORG_LETTERS) for _ in range(4))
        value = f"{org}.{rng.randint(0, 999999):06d}.{rng.randint(0, 999):03d}"
        if rng.random() < 0.2:
            value += f"-{rng.randint(1, 99):02d}"
        if rng.random() < 0.6:
            value += (" " if rng.random() < 0.4 else "") + rng.choice(CODES)
        return value

    def drill(self):
        rng = self.rng
        chunks = []
        for _ in range(rng.randint(2, 4)):
            letters = [rng.choice(CONFUSABLE if rng.random() < 0.7 else LOWER) for _ in range(rng.randint(2, 5))]
            chunk = "".join(letters)
            chunks.append(chunk.upper() if rng.random() < 0.25 else chunk)
        return " ".join(chunks)

    def line(self):
        # No random-letter drills: on GOST fonts many lowercase letters are scaled capitals, and
        # Tesseract normalizes line height, so a lowercase-only line of random letters is
        # indistinguishable from capitals and teaches the model to guess case (measured in V4a).
        # Every name starts with a capital; rare letters come from weighted real words.
        r = self.rng.random()
        if r < 0.62: return "name", self.name()
        if r < 0.74: return "name", self.upper_name()
        if r < 0.92: return "designation", self.designation()
        return "type", self.rng.choice(TYPES)


def render(text, font_path, skew, cap_mm, rng, dpi=300):
    probe = ImageFont.truetype(str(font_path), 200)
    box = probe.getbbox("Н")
    size = max(8, round(200 * cap_mm * dpi / 25.4 / (box[3] - box[1])))
    font = ImageFont.truetype(str(font_path), size)
    left, top, right, bottom = font.getbbox(text)
    pad = round(2.0 * dpi / 25.4)
    width, height = right - left + 2 * pad, bottom - top + 2 * pad
    extra = int(abs(skew) * height) + 2
    image = Image.new("L", (width + extra, height), 255)
    ImageDraw.Draw(image).text((pad - left, pad - top), text, font=font, fill=0)
    if skew:
        # Output (x, y) samples input (x - skew * (height - y), y): the top leans right like
        # an italic CAD font, the baseline stays in place; the extra width is on the right.
        image = image.transform(image.size, Image.AFFINE, (1, skew, -skew * height, 0, 1, 0),
                                resample=Image.BICUBIC, fillcolor=255)
    blur = rng.choice((0, 0, 0.4, 0.7, 1.0))
    if blur:
        image = image.filter(ImageFilter.GaussianBlur(blur))
    noise = rng.choice((0, 0, 6, 10))
    if noise:
        random_noise = Image.effect_noise(image.size, noise)
        image = ImageChops.add(image, random_noise, scale=1.0, offset=-128)
    if rng.random() < 0.5:
        buffer = io.BytesIO()
        image.save(buffer, "JPEG", quality=rng.choice((70, 80, 90)))
        image = Image.open(io.BytesIO(buffer.getvalue())).convert("L")
    # Same white margin the application adds before OCR (FieldCrop.Prepare: 24 px).
    framed = Image.new("L", (image.width + 48, image.height + 48), 255)
    framed.paste(image, (24, 24))
    return framed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--words", type=Path, required=True)
    parser.add_argument("--fonts", type=Path, required=True, help="tests/Core.Tests/Fonts")
    parser.add_argument("--exclude", type=Path, action="append", default=[])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--train", type=int, default=7000)
    parser.add_argument("--development", type=int, default=600)
    parser.add_argument("--seed", type=int, default=20261119)
    args = parser.parse_args()

    for _, file, digest, _ in FONTS:
        if sha(args.fonts / file) != digest:
            raise SystemExit(f"Font hash mismatch: {file}")
    exclude = excluded_words(args.exclude)
    words = load_words(args.words, exclude)
    rng = random.Random(args.seed)
    rng.shuffle(words)
    cut = len(words) // 10
    # Train and development words are disjoint; evaluation stays outside this corpus.
    pools = {"development": words[:cut], "train": words[cut:]}
    manifest = {"schema_version": 1, "seed": args.seed, "words_file": str(args.words), "words_sha256": sha(args.words),
                "excluded_word_count": len(exclude), "fonts": [f[:2] + (f[2],) for f in FONTS], "splits": {}}
    seen = set()
    for split, count in (("train", args.train), ("development", args.development)):
        lines = Lines(pools[split], random.Random(f"{args.seed}-{split}"))
        out = args.output / split
        out.mkdir(parents=True, exist_ok=True)
        records = []
        made = 0
        while made < count:
            kind, text = lines.line()
            if text in seen:
                continue
            seen.add(text)
            key, file, _, skew = FONTS[made % len(FONTS)]
            cap = rng.uniform(3.5, 7.0) if kind == "designation" else rng.uniform(2.5, 5.0)
            image = render(text, args.fonts / file, skew, cap, rng)
            stem = f"{split}-{made:05d}-{key}"
            image.save(out / f"{stem}.png")
            (out / f"{stem}.gt.txt").write_text(text + "\n", encoding="utf-8")
            records.append({"id": stem, "kind": kind, "font": key, "cap_mm": round(cap, 2), "text": text})
            made += 1
        counts = {c: sum(r["text"].count(c) for r in records) for c in LOWER + UPPER + "0123456789"}
        manifest["splits"][split] = {"lines": len(records), "character_counts": counts,
                                      "kinds": {k: sum(r["kind"] == k for r in records) for k in ("name", "designation", "type", "drill")}}
        (args.output / f"{split}.json").write_text(json.dumps(records, ensure_ascii=False, indent=1), encoding="utf-8")
    (args.output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({s: {"lines": v["lines"], "min_letter": min(v["character_counts"].values())} for s, v in manifest["splits"].items()},
                     ensure_ascii=False))


def render_supplement():
    """Re-render the frozen V3 training truths (designations and names) in all V4 fonts."""
    parser = argparse.ArgumentParser(description="Render V3 train truths with V4 fonts")
    parser.add_argument("--v3-train", type=Path, required=True)
    parser.add_argument("--fonts", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seed", type=int, default=20261120)
    args = parser.parse_args()
    rng = random.Random(args.seed)
    record = json.loads(args.v3_train.read_text(encoding="utf-8"))
    texts = []
    for document in record["Documents"]:
        for key in ("Designation", "Name"):
            value = document.get(key)
            if value:
                texts.extend(value.split("\n") if key == "Name" else [value])
    out = args.output / "train"
    out.mkdir(parents=True, exist_ok=True)
    for index, text in enumerate(sorted(set(texts))):
        for variant in range(2):
            key, file, _, skew = FONTS[(index * 2 + variant) % len(FONTS)]
            cap = rng.uniform(3.0, 6.0)
            stem = f"v3sup-{index:04d}-{variant}-{key}"
            render(text, args.fonts / file, skew, cap, rng).save(out / f"{stem}.png")
            (out / f"{stem}.gt.txt").write_text(text + "\n", encoding="utf-8")
    print(len(texts), "truths")


if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1 and sys.argv[1] == "supplement":
        sys.argv.pop(1)
        render_supplement()
    else:
        main()
