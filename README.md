# nVielLauncher

official nviel repo, pllslllllllllllllllllllllllssss join the tg: @nvielclient

Чёрно-белый лаунчер Minecraft на WPF (.NET 8) для Fabric 1.21.4: оффлайн-вход,
моды с Modrinth, сборки `.mrpack`, сервера, профили, обучение и диагностика падений.

## Запуск из исходников

Нужен .NET 8 SDK (Windows).

```powershell
dotnet build src\nVielLauncher\nVielLauncher.csproj -c Release
src\nVielLauncher\bin\Release\net8.0-windows\nVielLauncher.exe
```

## Один автономный exe

```powershell
.\build-release.ps1
```

Результат — `dist\nVielLauncher.exe` (self-contained, .NET у пользователя не нужен).
При запуске он устанавливается в `%APPDATA%\.nviel\app`, создаёт ярлыки и удаляет
исходный файл из «Загрузок».

## Моды

- `mods/*.jar` — пользовательские моды, в репозиторий не входят.
- `src/nVielLauncher/embedded/*.jar` — обязательные встроенные моды, тоже не входят.

Обе папки перечислены в `.gitignore`. Без них проект собирается, но не устанавливает
обязательные моды. Подробнее — `src/nVielLauncher/embedded/README.md`.

## Где хранятся данные

- Настройки и логи: `%LOCALAPPDATA%\nViel Launcher`
- Игра и установленный лаунчер: `%APPDATA%\.nviel`
