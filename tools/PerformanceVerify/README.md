# Сравнение качества до и после оптимизации

Проверяет **все** PDF, перечисленные в `verification.json` выбранного набора, настоящими
Tesseract и PaddleOCR. Перед каждым документом устанавливает одинаковый seed OpenCV 1729.
Сохраняет ожидаемое и фактическое имя, причины отказа, тексты обоих OCR и SHA-256 каждого
вырезанного поля. Показатели уверенности в используемом формате диагностик отсутствуют.

В наборе из `PerformanceSamples` встречаются намеренно не исключённые отказы OCR. Поэтому
код возврата 0 означает отсутствие **ошибочно принятых имён**, а не распознавание всех PDF.
Количество точных результатов и отказов всегда записывается в JSON. Для проверки отсутствия
регрессии сравниваются полные массивы `Samples` старого и нового ядра.

```sh
# Набор создаётся один раз; при отказах генератор возвращает 1, но сохраняет весь набор и отчёт.
dotnet run --project tools/PerformanceSamples -c Release -- artifacts/performance-diverse-samples

# Текущее ядро, поставляемые настройки OCR.
dotnet run --project tools/PerformanceVerify -c Release -- \
  artifacts/performance-diverse-samples artifacts/performance/diverse-candidate-48.json

# Замороженная копия старого Core. В ней разрешён только InternalsVisibleTo
# для DocumentTextRecognition.Performance; алгоритм не изменяется.
dotnet build tools/PerformanceVerify -c Release \
  -p:CoreProjectPath=/absolute/path/to/baseline/src/Core/Core.csproj \
  -p:ArtifactsPath=/absolute/path/to/baseline-verify
dotnet /absolute/path/to/baseline-verify/bin/PerformanceVerify/release/DocumentTextRecognition.Performance.dll \
  artifacts/performance-diverse-samples artifacts/performance/diverse-baseline-48.json
```

Выходные каталоги сборки старого и нового ядра разделены, чтобы не смешивать нативные
библиотеки. Для Linux используется поддерживаемое окружение Ubuntu 24.04 и зависимости из
`scripts/ci-ocr-deps.sh`. Сравнение проводится на одних и тех же созданных PDF и шаблонах;
повторная генерация перед вторым запуском не нужна.

```python
import json

with open("artifacts/performance/diverse-baseline-48.json") as source:
    before = json.load(source)
with open("artifacts/performance/diverse-candidate-48.json") as source:
    after = json.load(source)
assert before["Samples"] == after["Samples"]
print(before["Correct"], before["Rejected"], before["Incorrect"])
```

Исходник этого инструмента использован для опубликованного сравнения 48 PDF. Он размещался
во временном каталоге `artifacts/diverse-compare`; после замера перенесён сюда без изменения
логики проверки (добавлены подсказка по аргументам и создание папки отчёта). Совпадение на
синтетическом наборе не доказывает равенство точности на любых сканах.

Для отдельного эксперимента поддерживается необязательный параметр `--known-lines true/false`
после двух путей. Он задаёт режим известных текстовых строк только для этой проверки;
при отсутствии параметра сохраняется значение ядра по умолчанию. Запрошенный и фактический
варианты записываются в `Configuration`. Старый вызов с двумя путями продолжает работать.

```sh
dotnet run --project tools/PerformanceVerify -c Release -- \
  artifacts/performance-diverse-samples artifacts/performance/known-lines-off.json --known-lines false
dotnet run --project tools/PerformanceVerify -c Release -- \
  artifacts/performance-diverse-samples artifacts/performance/known-lines-on.json --known-lines true
```

При изменении способа OCR сравниваются все ожидаемые имена: ни один ранее правильный результат
не должен потеряться, ошибочно принятых имён быть не должно. Улучшения и сохраняющиеся отказы
отражаются в отчёте отдельно; строки не исключаются из набора.

Для явного выбора ONNX-backend добавляются `--onnx true` и
`--onnx-models artifacts/onnx-models`. Конвертация описана в `tools/OnnxPerformance/README.md`;
графы уже включены в обычную сборку. При отсутствии `--onnx` используется штатный ONNX;
`--onnx false --known-lines false` воспроизводит прежний native режим.
Пути и запрошенный/фактический выбор backend записываются в `Configuration`.

```sh
dotnet run --project tools/PerformanceVerify -c Release -- \
  artifacts/performance-diverse-samples artifacts/performance/onnx-48.json \
  --known-lines false --onnx true --onnx-models artifacts/onnx-models
```

Интеграционные тесты `OnnxBackendTests` дополнительно сравнивают native и ONNX на одинаковых
вырезанных пикселях и при одинаковом `PreferKnownLines`. Для них явно задаётся переменная
`DTR_ONNX_MODELS_DIR` с абсолютным путём к конвертированным моделям. Переменная
`DTR_ONNX_DIVERSE_SAMPLES_DIR` включает отдельные проверки всех 48 PDF подготовленного набора.
Без этих переменных дополнительные сравнения пропускаются с указанной причиной; обычные
тесты проверяют штатный ONNX с включенным чтением одиночных строк. Если путь задан, но неверен, проверка завершается
ошибкой и не скрывает отсутствие моделей.
