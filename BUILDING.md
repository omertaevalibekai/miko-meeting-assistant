# Сборка из исходников

Нужны Windows 10/11 x64, .NET Framework 4.8 и PowerShell 5.1 или новее. Используется системный C#-компилятор; отдельные .NET SDK, Python и Node.js не нужны.

В папке репозитория:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\Miko.exe
```

Скрипт загружает NAudio 2.2.1 с NuGet, проверяет SHA-256 и собирает приложение. Такой вариант поддерживает запись и OpenAI Whisper API. Ключ вводится только в окне «⋯ → Настройки»; его нельзя добавлять в исходники.

Чтобы включить также локальную Whisper base без интернета:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -WithLocalWhisper
```

Первый запуск этого скрипта скачает whisper.cpp b5130 и модели (~150 МБ). Архивы и модели проверяются по закреплённым SHA-256. Дальнейшие сборки используют кеш `.deps/downloads`. Исходные адреса и лицензии перечислены в `DEPENDENCIES.md` и `licenses/`.

Сборка с уже установленными зависимостями, без сети:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
```

## Проверки

Эти проверки не включают микрофон и не отправляют запросы в OpenAI. API проверяется подменным HTTP-обработчиком.

```powershell
Start-Process .\Miko.exe -ArgumentList '--self-test work\core-tests' -Wait
Start-Process .\Miko.exe -ArgumentList '--api-tests work\api-tests' -Wait
Start-Process .\Miko.exe -ArgumentList '--ui-test work\ui-tests' -Wait
```

Результаты: `work/core-tests/results.txt`, `work/api-tests/api-results.txt`, `work/ui-tests/ui-results.txt`. Для проверки локального движка нужны локальные зависимости и отдельный тестовый WAV; команды описаны в `TEST-RESULTS.md`.

## Структура

- `source/` — WPF-интерфейс, маскоты, запись WASAPI, обработка и тесты.
- `scripts/` — восстановление закреплённых зависимостей.
- `preview/` — изображения интерфейса и журналы тестов без пользовательских данных.
- `licenses/` — лицензии сторонних компонентов.

Бинарные файлы, модели, записи встреч, настройки, черновики, ключи и кеш исключены через `.gitignore`. Готовая программа распространяется архивом в GitHub Releases; извлекать и переносить нужно всю папку архива.
