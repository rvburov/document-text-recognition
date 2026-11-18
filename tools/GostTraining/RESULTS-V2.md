# Recorded GOST adaptation pilot, 2026-11-18

The Tesseract specialist was actually fine-tuned on authored synthetic GOST Type A line images.
Its Russian LSTM starts from the pinned floating-point `tessdata_best/rus`; the production
English model is reused unchanged during recognition. Candidate weights remain in
`artifacts/gost-training/selected/rus_gost.traineddata` (5,053,706 bytes, SHA256
`26bab93eb188b4e1557be5ffc0e7cc9a33cac5a6249debe3a9141d53eef374ce`). The original application
`rus.traineddata` was not overwritten by these tools.

## Data and model selection

Train: 600 base images plus 160 separately frozen uppercase-alphabet images, 296 distinct
line texts. The Russian-only model used 632 images; 128 lines containing Latin characters
were excluded explicitly, not relabelled. Development has 76 images / 36 distinct texts;
evaluation has 150 images / 72 distinct texts. The same truth's renderings never cross splits.
The original 19 regression crop texts are absent from training.

Train/development use Skia; evaluation uses Pillow/FreeType and other DPI values. The pinned
font itself is shared, so this is a rasterizer holdout, not a font holdout or real-document study.
No expected text was derived from an OCR answer or changed after a prediction.

Whole-image training boxes overfit the padded raw-line geometry: their LSTM validation looked
good but ordinary PSM 6 recognition remained poor. Image-derived ink boxes with a 2-pixel margin
improved real API recognition. The selected chain was 500 samples with this geometry, then
1,500 samples with the frozen alphabet supplement. An attempted continuation with a 3,000-sample
limit stopped after 100 samples and had worse whole-line development accuracy; it was not
selected. **The recorded limit is not a claim that 3,000 training steps ran.**

Selection used development exact whole-line answers first. The integer candidate scored 70/76,
its floating version 69/76, and the later continuation 68/76. [selection.json](results/selection.json)
was written before evaluation was opened. The model was not changed after the holdout result.

## Actual C API recognition

All rows below use PSM 6 and both Russian + unchanged English, retain exact Cyrillic/Latin,
case, punctuation and `ё`, and collapse only whitespace. These are **line results**, not full
PDF/document acceptance rates. See the adjacent JSON files for every answer and confidence.

| Model | Development exact | Development designations | Evaluation exact | Evaluation designations | Evaluation names | Evaluation CER |
|---|---:|---:|---:|---:|---:|---:|
| Original production `rus+eng` | 35/76 | 0/32 | 61/150 | 0/64 | 61/86 | 15.90% |
| Unmodified float best `rus+eng` | 41/76 | 0/32 | 63/150 | 0/64 | 63/86 | 19.04% |
| Selected fine-tuned int `rus_gost+eng` | 70/76 | 30/32 | 91/150 | 19/64 | 72/86 | 4.48% |

The holdout improvement is real, but substantially smaller than the development improvement.
Missing dots, Cyrillic-letter confusions and some mixed-script brand errors remain. Name CER
alone moves from 1.92% (original) to 2.04% (specialist), despite more exact names. Keep the general
engine as the first attempt; the specialist is useful only as another attempt requiring exact
independent confirmation. **Confidence 0.30 is insufficient:** 56 incorrect holdout lines still
exceed it. No probability threshold makes this model safe as a standalone naming authority.

Selected-model evaluation median read time was 19.9 ms/line, p95 29.6 ms, model initialization
161.9 ms, in the shared Ubuntu 24.04 CPU environment. These C API timings include PNG decode,
exclude initial loading from each read, and are not the application's end-to-end PDF latency.

Records: [model card](results/model-card.json), [development answers](results/development-selected.json),
[holdout answers](results/evaluation-selected.json), [original baseline](results/evaluation-original.json),
[unmodified best baseline](results/evaluation-best-baseline.json).
