# Tesseract adaptation pilot for the pinned GOST Type A font

The frozen V3 measurements and model identity are in [RESULTS.md](RESULTS.md); the earlier
single-rasterizer pilot remains documented separately in [RESULTS-V2.md](RESULTS-V2.md).

These tools create and measure an **experimental model under `artifacts/`**. They never replace
`ocr/tessdata`, alter authored answers, transliterate Cyrillic/Latin lookalikes, or add expected
organization prefixes to a dictionary. The parent `GostCorpus` manifest fixes document truths
before recognition; train/development/evaluation contain disjoint line truths and document
prefixes. Evaluation is not used to choose a checkpoint. The first pilot held out Pillow;
the later V3 corpus uses both Skia and Pillow in every split with disjoint text values.

Prerequisites: Python 3 (Pillow for optional tight ink boxes) and Tesseract training tools (`tesseract`,
`combine_tessdata`, `lstmtraining`, `lstmeval`, `merge_unicharsets`, `combine_lang_model`). The
recorded experiment uses Ubuntu 24.04 / Tesseract 5.3.4, matching the repository test environment.

The original `tessdata_fast/rus` is integer-only and cannot be fine-tuned. The floating-point
`artifacts/tessdata-best/{rus,eng}.traineddata` sources are pinned to tessdata_best commit
`e12c65a915945e4c28e237a9b52bc4a8f39a0cec`; `prepare.py` checks their SHA256. Source license and
URLs are recorded in `artifacts/tessdata-best/source.json` and `LICENSE`.

The first pilot keeps the Russian model's original charset. Ground-truth lines containing
unsupported Latin characters are explicitly excluded from LSTMF training/development and listed
in `preparation.json`; no labels are changed. At inference the candidate is paired with the
unchanged production English model using `-l rus_gost+eng`, and **all** development/evaluation
lines, including mixed brand names, are scored. An optional `--charset cyrillic-latin` experiment
builds a larger recoder and initializes new Latin output weights; it is not required by the
first pilot and must use a separate preparation/output directory.

```sh
python3 tools/GostTraining/fetch_base.py artifacts/tessdata-best
python3 tools/GostTraining/fetch_langdata.py artifacts/gost-training/langdata
python3 tools/GostTraining/prepare.py \
  --corpus artifacts/gost-corpus --output artifacts/gost-training \
  --base-data artifacts/tessdata-best --langdata artifacts/gost-training/langdata \
  --charset russian --jobs 2
python3 tools/GostTraining/train.py \
  --prepared artifacts/gost-training --base-data artifacts/tessdata-best \
  --run pilot-rus-500 --iterations 500 --learning-rate 0.0001
python3 tools/GostTraining/evaluate.py \
  --corpus artifacts/gost-corpus --split development \
  --tessdata artifacts/gost-training/runs/pilot-rus-500/int \
  --output artifacts/gost-training/runs/pilot-rus-500/development-int.json
```

`prepare.py` writes whole-line character boxes from ground truth, then calls Tesseract's
`lstm.train` conversion. Evaluation images never become training samples. `train.py` limits
OpenMP to one thread, saves the command/log/timing/input hashes, exports both float and integer
models, and runs LSTM development validation. Checkpoint choice must use development results.
A later continuation uses a new `--run` name and `--continue-from .../rus_gost_checkpoint`;
`--iterations` is the trainer limit for that invocation. The log records actual sample counters;
changing the training list can reset them on continuation, so report the entire run chain.
`--target-error-rate -1` disables the internal train-error stopping shortcut while keeping the
explicit iteration bound. This lets a new mixed-renderer corpus complete a full pass even when
the first small batch happens to contain only already-known glyph shapes.

After a model is selected, score it once on evaluation using `--split evaluation
--final-evaluation`. The explicit flag helps prevent accidental tuning to the held-out answers.
The evaluator preserves exact Unicode spelling, punctuation, case and `ё`; only whitespace is
collapsed. It records every raw OCR answer, exact-match, character error rate and (C API backend)
actual Tesseract text-line confidence. The default C API reader reuses one model pair and reports
initialization separately; read timings include PNG decoding. `--backend cli` starts a fresh
process per line and includes model loading. Neither measurement is the full application's
PDF pipeline latency.

`geometry_grid.py` records a fixed 3×3 grid (PSM 6/7/13 and full/tight-ink/tight-ink+8px)
on **development only**, including word errors and symbol confidence. The derived images do
not change source files. `evaluate.py --designation-ink8-psm13` implements one explicit role
policy derived from that development experiment: designation lines use PSM 13 with ink bounds
and eight white pixels, while other roles retain PSM 6 and original images. This is image
preprocessing; it does not replace, guess, or correct recognized characters.
That destructive white margin is retained only as a diagnostic ablation. The selected V3
`--designation-preserve8-psm13` policy copies original source pixels into the expanded margin,
uses white only beyond the source image, and falls back to PSM 6 if weak ink lies outside it.
The standalone evaluator assumes authored one-line crops; the production single-line geometry
checks require separate application tests.

For a new blind experiment, keep evaluation files in separate custody and prepare with
`--evaluation-isolated`; the tool then reads only train/development. Record candidate model
variants, inference policies and the selection criterion before scoring development. Freeze
the final model hash and policy before requesting the separate evaluation corpus.

Training geometry is explicit. `--box-geometry full` uses the entire padded line image, useful
for raw-line PSM 13. `--box-geometry ink --box-padding 2` uses image-derived ink bounds, leaving
GT unchanged, and better matches normal PSM 6 line extraction. Keep different geometry experiments
in separate output directories. `--supplement-train artifacts/gost-alphabet-v2/train` adds only
the separately frozen training alphabet supplement; it never adds development/evaluation lines.

A synthetic font pilot is evidence about this pinned font/rendering distribution only. Keep
models as candidates until independent documents, mixed-script labels, another rasterizer,
float/integer consistency and the application's two-engine agreement have been checked.
