# OpenGOST type A и type B

Автор: Levon Melikyan, проект https://github.com/ingenium-am/open-gost-arm
(надмножество шрифтов OpenGOST, начертание по образцу шрифтов ГОСТ 2.304 в САПР).

Исходники `src/OpenGOSTtypeA-Regular.sfd` и `src/OpenGOSTtypeB-Regular.sfd` взяты с коммита
`4c8aca3fe4da3d7e7805aaf7c312b196d5274620` и собраны в TrueType без изменений командой
FontForge 20230101 (Ubuntu 24.04):

```sh
fontforge -lang=ff -c 'Open($1); Generate($2)' OpenGOSTtypeA-Regular.sfd OpenGOSTtypeA-Regular.ttf
```

- `OpenGOSTtypeA-Regular.ttf`: SHA256 `3caec4e2a004eacf8aea6883781015fc2df9b7c0154e974cec97f22299d7fc0e`.
- `OpenGOSTtypeB-Regular.ttf`: SHA256 `510f175c6856e7d9fc4ab2388266cf09860ef7e9ab10fc1baa6b64d09e4c63d9`.
- Лицензия: SIL Open Font License 1.1 (`OFL-OpenGOST.txt`), Reserved Font Names
  «OpenGOST type A» и «OpenGOST type B».

Шрифты используются только как тестовые данные: прогон алфавита (`Alphabet/`) и корпус
дообучения модели чертежного шрифта (`tools/GostCorpus/render_v4.py`). Наклонный вариант
получается наклоном прямого начертания на 15 градусов, как курсив ГОСТ в САПР.
Тесты проверяют SHA256 файлов; подмена шрифта запрещена.
