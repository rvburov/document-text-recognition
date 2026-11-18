# GOST Type A specialist: final frozen V3 result

The integer candidate is `rus_gost.traineddata`, 5,053,706 bytes, SHA256
`c92b7c889f0bd4fb07ca90148c011be7a1865be2b8bba036fa1a86d875a5add0`.
After selection on development, its single final blind run read **107/120 lines exactly**:
51/60 designations and 56/60 names. These are standalone line-crop measurements; they do not
state the percentage of documents safely renamed by the application.

The font is the pinned GOST 2.304-81 Type A Slanted v0.7.6 implementation by Sergey S. Betke.
Russian weights start from pinned `tessdata_best/rus`; the original English model is unchanged.
Source hashes, licensing, every training run, policy and limitations are in the
[model card](results/v3/model-card.json). The model is an additional specialist requiring
independent OCR confirmation; the original general Russian model remains available.

## Training and selection

The inherited V1/V2 model used 760 authored training images / 296 different line texts. V3 adds
1,200 images / 600 new line texts: every text rendered once with Skia and once with Pillow.
Training-only Russian LSTMF counts are 632 inherited and 1,056 new; Latin-containing lines are
excluded from Russian training without changing their labels. All mixed-script lines remain
in recognition evaluation with `rus_gost+eng`.

The selected chain presents 500 + 1,500 + 1,500 + 1,500 samples to the trainer, with 257 reported
learning updates in total. An iteration limit is not a count of optimizer updates. The two V3
runs took approximately 101 and 104 seconds on the validation CPU. Source images and authored
Unicode answers were never corrected from OCR output.

V3 development is 240 images / 120 unique lines; blind evaluation is 120 images / 60 unique
lines. Values and designation prefixes are disjoint across splits and earlier corpora. Every
split contains both rasterizers; this is a new-text holdout, not an unseen-font or unseen-renderer
study. The earlier V1 evaluation is now historical evidence; see [V2 results](RESULTS-V2.md).

The development-only 3×3 geometry experiment motivated a separate designation policy. To retain
faint accents, final preprocessing expands the gray<200 ink box by eight pixels and copies all
original pixels into that margin, using white only outside the source image. Weak gray<245 ink
outside the expanded box falls back to the original image. Designations then use PSM 13; names
use the original crop with PSM 6. There is no character whitelist or post-OCR letter correction.

The production implementation additionally requires two `KnownSingleLineRegion` checks. Those
application gates are not reproduced by this standalone known-line evaluator. End-to-end PDF
qualification must measure their actual refusals, crop mapping and two-engine agreement.

| Candidate and policy | Development exact | Designations | Names | CER |
|---|---:|---:|---:|---:|
| V2 int, original PSM 6 | 126/240 | 56/120 | 70/120 | 4.92% |
| V2 int, preserved margin | 135/240 | 65/120 | 70/120 | 4.03% |
| V3 first 1,500, int, preserved margin | 187/240 | 108/120 | 79/120 | 2.59% |
| V3 first 1,500, float, preserved margin | 188/240 | 109/120 | 79/120 | 2.38% |
| V3 total 3,000, float, preserved margin | 199/240 | 113/120 | 86/120 | 1.75% |
| **Selected V3 total 3,000, int, preserved margin** | **200/240** | **114/120** | **86/120** | **1.58%** |

The [selection record](results/v3/v3-selection.json) was frozen before the fixture agent released
blind images. All compared policies and model formats are recorded there. The destructive
white-margin experiment remains a diagnostic ablation; it is not eligible for deployment.
The model and policy were not changed after the blind result.

## One final blind run

| Scope | Exact lines | CER |
|---|---:|---:|
| All lines | 107/120 | 2.49% |
| Designations | 51/60 | 2.84% |
| Names | 56/60 | 2.11% |
| Pillow | 55/60 | 2.20% |
| Skia | 52/60 | 2.78% |

Exact scoring preserves Unicode script, case, punctuation and `ё`; only whitespace is collapsed.
Each pair of renderer images shares one truth, so 120 images are not 120 independent texts.
The four blind name errors and nine designation errors remain failures. **All 13 incorrect
lines still exceed Tesseract confidence 0.30.** Confidence alone cannot authorize a filename.

The fine-tuning examples cover all 33 Russian uppercase letters, digits, dots and hyphens.
Lowercase `вгжхщэю` do not occur in the selected fine-tuning data; the pretrained model knows
these characters, but this experiment does not establish special-font coverage for each of them.
Latin weights were not retrained. The same pinned font is shared across splits, and real scans,
CAD font differences, frame removal and PDF text-layer trust need separate application evidence.

Recorded read time was median 15.6 ms/line and p95 30.3 ms, with two model-pair instances initialized
in 302 ms. This shared-CPU correctness run is not a throughput benchmark. Read timings include
PNG decoding and symbol-confidence extraction, exclude crop preparation, and do not represent
full PDF loading, dual-engine agreement or naming latency.

Every selected answer and symbol confidence: [development](results/v3/development-selected.json),
[blind evaluation](results/v3/evaluation-selected.json). No further model selection or tuning used
these blind answers.
