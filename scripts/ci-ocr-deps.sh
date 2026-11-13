#!/usr/bin/env bash
# Компоненты для тестов распознавания в проверках на GitHub (Linux): системный Tesseract с
# русским и английским и шрифт DejaVu для синтетических сканов. Программа для Windows приносит
# Tesseract сама; на Linux он нужен только для проверок.
set -euo pipefail
sudo apt-get update -qq
sudo apt-get install -y -qq tesseract-ocr tesseract-ocr-rus tesseract-ocr-eng fonts-dejavu-core
tesseract --version | head -1
