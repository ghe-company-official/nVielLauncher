# nVielLauncher

Официальный репозиторий nViel. Telegram: [@nvielclient](https://t.me/nvielclient).

Чёрно-белый лаунчер Minecraft на WPF (.NET 8) для Fabric 1.21.4: оффлайн-вход,
моды с Modrinth, сборки `.mrpack`, сервера, профили, обучение и диагностика падений.

Текущая версия: **1.0.2 Global**.

## Возможности

- один экземпляр лаунчера: повторный запуск показывает уже открытое окно;
- инстансы: отдельные моды, миры и конфиги; версии, библиотеки и ресурсы игры общие,
  поэтому переключение не запускает повторную загрузку;
- экран запуска с этапами, логом, отменой, повтором и разбором ошибок;
- проверка конфликтов модов: дубликаты, несовместимости, отсутствующие и отключённые
  зависимости, моды для другой версии Minecraft;
- резервные копии миров и конфигов: автоматически перед запуском, вручную, с
  восстановлением и страховочной копией перед откатом;
- включение и выключение установленных модов (файл переименовывается в `.jar.disabled`);
- автоматическая установка обязательных зависимостей модов и кнопка «Зависимости»;
- Java 21 скачивается автоматически; SHA-256 пакета Temurin проверяется до распаковки;
- nVielRE — отдельный режим восстановления: Java, файлы Minecraft/Fabric, обязательные
  моды, журнал операций и быстрые действия;
- архив диагностики с логами, crash-report, версией, Java и обезличенными настройками;
- portable-режим (`--portable` или `portable.flag` рядом с exe);
- экспорт и импорт настроек в JSON;
- автообновление из приватного GitHub Releases с проверкой detached-подписи ECDSA,
  размера, PE-заголовка, версии и SHA-256;
- health-check после обновления и автоматический откат на предыдущую версию.

## nVielRE

Открывается кнопкой **nVielRE** в боковой панели или в **Настройки → Восстановление**.
Тот же exe можно запустить с флагом `--re`. Режим не запускает игру и использует отдельный
single-instance mutex, поэтому может работать параллельно с обычным лаунчером.

Кнопка **Отчёт** в консоли nVielRE собирает ZIP-архив в
`%LOCALAPPDATA%\nViel Launcher\reports` (в portable-режиме — в `data\reports`).
Имена профилей, серверов и пути пользователя маскируются. Отправка на сервер —
только по явному согласию и только если задан `NVIEL_REPORT_ENDPOINT`.

## Обновления

При запуске и затем раз в 6 часов лаунчер проверяет последний GitHub Release приватного
`ghe-company-official/nVielLauncher-releases`. Если доступна новая версия, появляется
окно **«Хотите установить обновление?»** с описанием релиза. В настройках раздела
обновлений нет.

Перед установкой проверяются:

1. размер файла и PE-заголовок;
2. имя продукта `nViel Launcher` и версия файла не ниже версии релиза;
3. SHA-256 (если GitHub вернул digest);
4. detached-подпись ECDSA P-256 по SHA-256.

Подпись обязательна. Релиз без asset `nVielLauncher.exe.sig` отклоняется. После запуска
новая версия пишет health-marker; если окно не поднялось, PowerShell-помощник
восстанавливает `nVielLauncher.exe.old` и запускает предыдущую версию.

Скачивание идёт в `.part`-файл и продолжается с HTTP `Range` после отмены или обрыва.
Bearer-токен GitHub никогда не пересылается на signed storage URL и не пишется в логи.

### Каналы

- `stable` — обычный `/releases/latest`;
- `beta` — задайте `NVIEL_UPDATE_CHANNEL=beta`, лаунчер выберет последний prerelease.

### Zero-touch relay (без токена на клиенте)

Если не хочется раздавать GitHub-токен пользователям, поднимите небольшой relay и задайте
у клиента `NVIEL_UPDATE_PROXY=https://updates.example.com/nviel`. Клиент запросит:

```http
GET https://updates.example.com/nviel/latest?channel=stable
```

Ответ:

```json
{
  "version": "1.0.2",
  "tag": "v1.0.2",
  "name": "nViel Launcher 1.0.2 Global",
  "notes": "Что нового...",
  "download_url": "https://updates.example.com/nviel/nVielLauncher.exe",
  "signature_url": "https://updates.example.com/nviel/nVielLauncher.exe.sig",
  "size": 84226825,
  "sha256": "…",
  "release_url": "https://github.com/…/releases/tag/v1.0.2"
}
```

Relay проксирует приватный GitHub и сам хранит токен. Клиент по-прежнему проверяет
ECDSA-подпись, поэтому relay не может подменить exe. Без relay используются переменные
`NVIEL_UPDATE_TOKEN`, `GITHUB_TOKEN`, `GH_TOKEN` или Git Credential Manager.

## Закрытый репозиторий обновлений

1. Приватный репозиторий: `ghe-company-official/nVielLauncher-releases`.
2. Fine-grained token для лаунчера: только этот репозиторий, `Contents: Read`.
3. Для публикации нужен отдельный token с `Contents: Read and write`; в лаунчер его не вставляйте.
4. Соберите и подпишите exe, создайте Release с тегом версии (например, `v1.0.2`) и
   прикрепите **два** asset: `nVielLauncher.exe` и `nVielLauncher.exe.sig`.

Публичный исходный репозиторий `nVielLauncher` не используется для обновлений и не
содержит exe/jar/секретов.

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

Результат — `dist\nVielLauncher.exe` и `dist\nVielLauncher.exe.sig`
(self-contained, .NET у пользователя не нужен). При запуске exe устанавливается в
`%APPDATA%\.nviel\app`, создаёт ярлыки и удаляет исходный файл из «Загрузок».

Скрипт подписывает файл ключом из `%APPDATA%\.nviel-signing\update-private-key.b64`
и сверяет подпись публичным ключом. Приватный ключ **не** хранится в репозитории.
Если ключей ещё нет, создайте их один раз (публичную часть затем вставьте в
`AppInfo.UpdatePublicKeyBase64`):

```powershell
dotnet run --project tools\ReleaseSigner -- generate `
  "$env:APPDATA\.nviel-signing\update-private-key.b64" `
  "$env:APPDATA\.nviel-signing\update-public-key.b64"
```

Для локальной неподписанной сборки используйте `.\build-release.ps1 -NoSign`; такой
файл обновление не примет. Authenticode-сертификат опционален: задайте
`NVIEL_SIGN_CERT_PATH` (и `NVIEL_SIGN_CERT_PASSWORD`) при наличии `signtool.exe`.

## Перенос из старых лаунчеров

- при первом запуске nViel ищет установленные лаунчеры и предлагает перенести данные;
- поддержаны официальный Minecraft Launcher, TLauncher, Legacy Launcher,
  LExplosion (NightWorld), Modrinth App, Prism/MultiMC/PolyMC, ATLauncher,
  GDLauncher, CurseForge, XMCL, HMCL, Lunar/Badlion/Feather и SKLauncher;
- копируются только миры, моды, ресурспаки, шейдеры, конфиги, `options.txt`
  и `servers.dat`; аккаунты, токены и файлы лаунчеров не переносятся;
- общая папка `.minecraft` импортируется один раз, даже если её используют
  официальный лаунчер и TLauncher;
- **Экспорт данных** сохраняет выбранные данные в ZIP;
- **Удалить старые** сначала создаёт резервную копию в
  `%LOCALAPPDATA%\nViel Launcher\migration\backups`, затем открывает штатные
  деинсталляторы. nViel не удаляет папки лаунчеров напрямую;
- перенос можно открыть позже в **Настройки → Перенос из старых лаунчеров**.

## Portable и перенос настроек

- `nVielLauncher.exe --portable` или файл `portable.flag` рядом с exe;
- данные хранятся в `data` рядом с exe, игра — в `data\game` (если путь не задан вручную);
- **Настройки → Данные и перенос настроек**: экспорт/импорт JSON и переключение portable;
- при импорте проверяются базовые значения RAM, разрешения и пути;
- настройки обновлений в JSON не хранятся.

## Моды и Java

- `mods/*.jar` — пользовательские моды, в репозиторий не входят;
- `src/nVielLauncher/embedded/*.jar` — обязательные встроенные моды, тоже не входят;
- обязательные моды принудительно восстанавливаются и не показываются в UI/логах;
- зависимости Modrinth (`required`) ставятся при установке мода, при включении и перед
  запуском игры; при офлайн-ошибке запуск не блокируется;
- Temurin JRE скачивается с api.adoptium.net; SHA-256 и размер берутся из метаданных
  Adoptium и проверяются до распаковки.

## Тесты и CI

```powershell
dotnet test nVielLauncher.sln -c Release
```

Тесты проверяют версии, ECDSA-подпись, отсутствие update/token-полей в настройках,
health-marker, импорт/экспорт данных старых лаунчеров и реальную проверку
`dist\nVielLauncher.exe`, если сборка есть локально.
GitHub Actions workflow `.github/workflows/ci.yml` собирает solution и запускает тесты
на `windows-latest` без секретов.

## Где хранятся данные

- Настройки и логи: `%LOCALAPPDATA%\nViel Launcher`
- Игра и установленный лаунчер: `%APPDATA%\.nviel`
- Portable: папка `data` рядом с exe
