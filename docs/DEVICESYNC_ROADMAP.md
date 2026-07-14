---
title: DeviceSync — roadmap разработки
aliases:
  - DeviceSync Roadmap
  - План развития DeviceSync
tags:
  - devicesync
  - roadmap
  - windows
  - android
  - file-transfer
status: active
created: 2026-07-13
updated: 2026-07-13
---

# DeviceSync — roadmap разработки

> [!summary] Цель
> Построить безопасное приложение для связи Android и Windows: сначала надёжная передача файлов, затем двусторонний обмен, буфер обмена, уведомления, синхронизация папок и фоновая работа.

## Текущее состояние

- [x] Windows TCP-сервер запускается на `0.0.0.0:54321`.
- [x] Android обнаруживает Windows через mDNS/DNS-SD.
- [x] Работает ручное подключение по IP.
- [x] Работает QR-привязка устройств.
- [x] Совпадает проверочный код на обеих сторонах.
- [x] Исправлена совместимость ECDSA-подписей Android и Windows.
- [x] Добавлен launcher актуальной Windows-сборки.
- [x] Реализованы heartbeat, acknowledgement и reconnect уровня соединения.
- [ ] Основной TCP-трафик зашифрован.
- [ ] Реализована первая пользовательская функция.

## Главный маршрут

```mermaid
flowchart LR
    A["Рабочее подключение"] --> B["File Transfer V1"]
    B --> C["Android → Windows"]
    C --> D["Windows → Android"]
    D --> E["TLS и pinning"]
    E --> F["Возобновление передач"]
    F --> G["Общий буфер обмена"]
    G --> H["Уведомления"]
    H --> I["Синхронизация папок"]
    I --> J["Фоновая работа и релиз"]
```

---

# Этап 0. Зафиксировать рабочую базу

## Задачи

- [ ] Проверить `git status` в Windows- и Android-репозиториях.
- [ ] Исключить `bin/`, `obj/`, `build/`, `.gradle/` из Git, если они ещё отслеживаются.
- [ ] Закоммитить исправление DER-подписей Windows.
- [ ] Закоммитить увеличение Android timeout ожидания `pairing.accepted`.
- [ ] Закоммитить launcher актуальной Windows-сборки.
- [ ] Поставить тег стабильной базы, например `connection-v1-working`.
- [ ] Записать ручной smoke-test подключения.

## Критерии готовности

- [ ] Чистый `git status` в обоих репозиториях.
- [ ] Все Windows unit/integration tests проходят.
- [ ] Все Android unit tests проходят.
- [ ] После чистой установки устройства связываются через новый QR.
- [ ] После перезапуска выполняется аутентифицированное переподключение без нового QR.

---

# Этап 1. Спецификация File Transfer V1

## Новый документ

Создать:

- `protocol/FILE_TRANSFER_V1.md`

## Решения, которые нужно зафиксировать

- [ ] Направление первой версии: только Android → Windows.
- [ ] Максимальный размер файла V1, например 100 МБ.
- [ ] Размер блока, например 64 КиБ.
- [ ] Кодирование блока: Base64 в JSON для V1 или отдельный бинарный frame.
- [ ] Один активный transfer на соединение в первой версии.
- [ ] SHA-256 всего файла обязателен.
- [ ] Файл записывается в `.part` и переименовывается атомарно.
- [ ] Получатель явно принимает или отклоняет файл.
- [ ] Определить таймаут предложения, блока и завершения.
- [ ] Определить поведение при disconnect.
- [ ] Определить безопасную обработку имён файлов.

> [!warning] Base64 или бинарные frames
> Base64 проще для первой версии, но увеличивает трафик примерно на треть и создаёт дополнительные копирования. Для небольшого V1 это допустимо. Для больших файлов лучше добавить бинарный тип frame во второй версии.

## Предлагаемые сообщения

| Тип | Направление | Назначение |
|---|---|---|
| `file.offer` | отправитель → получатель | Предложение файла и метаданные |
| `file.accept` | получатель → отправитель | Разрешение начать передачу |
| `file.reject` | получатель → отправитель | Отказ с причиной |
| `file.chunk` | отправитель → получатель | Последовательный блок данных |
| `file.complete` | отправитель → получатель | Все блоки отправлены |
| `file.received` | получатель → отправитель | Файл проверен и сохранён |
| `file.cancel` | обе стороны | Отмена пользователем |
| `file.error` | обе стороны | Ошибка протокола или файловой системы |

## Минимальные payload

### `file.offer`

```json
{
  "transferId": "uuid",
  "fileName": "photo.jpg",
  "sizeBytes": 1258291,
  "mimeType": "image/jpeg",
  "sha256": "base64url-sha256",
  "chunkSize": 65536
}
```

### `file.chunk`

```json
{
  "transferId": "uuid",
  "index": 0,
  "offset": 0,
  "data": "base64"
}
```

### `file.complete`

```json
{
  "transferId": "uuid",
  "totalChunks": 20,
  "sizeBytes": 1258291
}
```

## Инварианты протокола

- `transferId` уникален и одинаков во всех сообщениях передачи.
- `offset` следующего блока равен количеству уже принятых байт.
- Принятое число байт никогда не превышает `sizeBytes`.
- `file.complete` допустим только после получения ровно `sizeBytes`.
- Итоговый SHA-256 обязан совпасть с `file.offer.sha256`.
- Повторный `transferId` не должен перезаписывать существующий файл.
- Неизвестная или завершённая передача отклоняет новые chunks.

## Критерии готовности

- [ ] Документ описывает happy path.
- [ ] Описаны reject, cancel, timeout, disconnect и checksum mismatch.
- [ ] Android и Windows используют одинаковые имена и типы полей.
- [ ] Есть минимум один полный JSON test vector.

---

# Этап 2. Общие модели протокола

## Windows

Изменить:

- `windows/src/DeviceSync.Protocol/ProtocolMessageTypes.cs`
- `windows/src/DeviceSync.Protocol/Payloads.cs`
- `windows/src/DeviceSync.Protocol/SupportedCapabilities.cs`

Добавить capability:

```text
file-transfer-v1
```

Добавить payload-модели:

- `FileOfferPayload`
- `FileAcceptPayload`
- `FileRejectPayload`
- `FileChunkPayload`
- `FileCompletePayload`
- `FileReceivedPayload`
- `FileCancelPayload`
- `FileErrorPayload`

Тесты:

- `windows/tests/DeviceSync.Protocol.Tests/FileTransferPayloadTests.cs`

## Android

Изменить:

- `app/src/main/java/com/example/devicesync/core/protocol/ProtocolMessageType.kt`
- общий список capabilities Android.

Создать:

- `app/src/main/java/com/example/devicesync/core/protocol/FileTransferPayloads.kt`

Тесты:

- `app/src/test/java/com/example/devicesync/core/protocol/FileTransferPayloadsTest.kt`

## Проверки

- [ ] CamelCase JSON совпадает на обеих платформах.
- [ ] Большие значения `sizeBytes` используют 64-битный тип (`long`/`Long`).
- [ ] Неизвестные поля игнорируются.
- [ ] Обязательные поля валидируются.
- [ ] Добавлены общие JSON test vectors в `protocol/test-vectors/file-transfer/`.

---

# Этап 3. Windows принимает файл

## Новые файлы Application

Создать:

- `windows/src/DeviceSync.Application/FileTransferAbstractions.cs`
- `windows/src/DeviceSync.Application/IncomingFileTransfer.cs`
- `windows/src/DeviceSync.Application/IncomingFileTransferManager.cs`
- `windows/src/DeviceSync.Application/FileTransferEvents.cs`

## `IncomingFileTransfer`

Хранит:

- `TransferId`
- `SenderDeviceId`
- `FileName`
- `SafeFileName`
- `MimeType`
- `SizeBytes`
- `ExpectedSha256`
- `ReceivedBytes`
- `NextChunkIndex`
- `TemporaryPath`
- `DestinationPath`
- `State`
- `StartedAtUtc`
- `LastActivityAtUtc`
- `Error`

Состояния:

```text
Offered
WaitingForUser
Accepted
Receiving
Verifying
Completed
Rejected
Cancelled
Failed
```

## `IncomingFileTransferManager`

Обязан:

- [ ] Проверять metadata `file.offer`.
- [ ] Нормализовать имя через `Path.GetFileName`.
- [ ] Запрещать пустое имя и traversal (`../`, абсолютные пути).
- [ ] Проверять максимальный размер.
- [ ] Проверять свободное место.
- [ ] Запрашивать решение UI.
- [ ] Создавать уникальный `.part`.
- [ ] Писать chunks потоково.
- [ ] Проверять index, offset и длину.
- [ ] Обновлять SHA-256 во время записи.
- [ ] Flush/close перед финальной проверкой.
- [ ] Сравнивать итоговый hash.
- [ ] Атомарно переименовывать файл.
- [ ] Удалять `.part` при ошибке или отмене.
- [ ] Публиковать события прогресса.

## Infrastructure

Изменить:

- `windows/src/DeviceSync.Infrastructure/ClientSession.cs`
- `windows/src/DeviceSync.Infrastructure/TcpDeviceServer.cs`

`ClientSession` должен только маршрутизировать сообщения и отправлять ответы. Файловая логика остаётся в manager.

## DI

Изменить:

- `windows/src/DeviceSync.App/App.xaml.cs`

Зарегистрировать manager и необходимые файловые сервисы как singleton/scoped согласно модели одного активного соединения.

## Windows-тесты

Создать:

- `windows/tests/DeviceSync.Application.Tests/IncomingFileTransferManagerTests.cs`
- `windows/tests/DeviceSync.IntegrationTests/FileTransferLoopbackTests.cs`

Сценарии:

- [ ] Успешный файл из нескольких blocks.
- [ ] Файл нулевой длины — разрешить или явно запретить.
- [ ] Неверный chunk index.
- [ ] Неверный offset.
- [ ] Chunk превышает заявленный размер.
- [ ] `complete` пришёл слишком рано.
- [ ] SHA-256 не совпал.
- [ ] Пользователь отклонил предложение.
- [ ] Пользователь отменил активную передачу.
- [ ] Отправитель отключился.
- [ ] Имя содержит traversal.
- [ ] Итоговое имя уже существует.
- [ ] `.part` очищается после любой ошибки.

## Критерии готовности

- [ ] Manager проходит unit tests без UI и реального TCP.
- [ ] Интеграционный клиент передаёт файл через loopback.
- [ ] Файл не загружается целиком в память.
- [ ] Повреждённый файл никогда не появляется под итоговым именем.

---

# Этап 4. Windows UI входящей передачи

## Изменить

- `windows/src/DeviceSync.App/MainViewModel.cs`
- `windows/src/DeviceSync.App/MainWindow.xaml`

## При необходимости создать

- `windows/src/DeviceSync.App/IncomingFileWindow.xaml`
- `windows/src/DeviceSync.App/IncomingFileWindow.xaml.cs`
- `windows/src/DeviceSync.App/IncomingFileViewModel.cs`

## UI должен показывать

- имя устройства;
- имя файла;
- размер;
- MIME-тип;
- путь сохранения;
- прогресс в процентах и байтах;
- скорость;
- статус;
- ошибку.

## Команды

- [ ] Принять.
- [ ] Отклонить.
- [ ] Выбрать папку.
- [ ] Отменить.
- [ ] Открыть файл после завершения.
- [ ] Открыть папку.

## Правила UX

- Не принимать файл автоматически в V1.
- Не блокировать UI во время записи и hash verification.
- Не показывать путь `.part` как готовый файл.
- Повторное нажатие кнопок должно быть безопасным.

---

# Этап 5. Android отправляет файл

## Новые core-файлы

Создать:

- `app/src/main/java/com/example/devicesync/core/transfer/OutgoingFileTransfer.kt`
- `app/src/main/java/com/example/devicesync/core/transfer/FileTransferManager.kt`
- `app/src/main/java/com/example/devicesync/core/transfer/FileMetadataReader.kt`
- `app/src/main/java/com/example/devicesync/core/transfer/FileTransferState.kt`

## `FileMetadataReader`

Через `ContentResolver` получает:

- display name;
- размер;
- MIME-тип;
- InputStream для `content://` URI.

Нельзя преобразовывать `content://` URI в обычный filesystem path.

## `FileTransferManager`

Обязан:

- [ ] Проверить активное аутентифицированное соединение.
- [ ] Получить metadata.
- [ ] Вычислить SHA-256 потоково.
- [ ] Отправить `file.offer`.
- [ ] Дождаться `file.accept` или `file.reject`.
- [ ] Повторно открыть stream после вычисления hash.
- [ ] Читать блоки фиксированного размера.
- [ ] Отправлять `file.chunk` последовательно.
- [ ] Обновлять прогресс.
- [ ] Отправить `file.complete`.
- [ ] Дождаться `file.received`.
- [ ] Поддержать cancel.
- [ ] Корректно реагировать на disconnect.

> [!important] Два чтения URI
> Для V1 SHA-256 можно вычислить первым проходом, затем заново открыть `InputStream` для отправки. Нужно корректно сообщить ошибку, если provider не позволяет повторное открытие.

## Интеграция с соединением

Изменить:

- `app/src/main/java/com/example/devicesync/core/network/ConnectionManager.kt`
- `app/src/main/java/com/example/devicesync/DeviceSyncApplication.kt`

Нужен безопасный способ:

- отправлять file protocol messages через существующий writer;
- получать ответы transfer manager без конкурирующего чтения socket;
- маршрутизировать входящие сообщения по `type` и `transferId`.

Не допускается, чтобы `FileTransferManager` и `ConnectionManager` одновременно напрямую читали один InputStream.

## Android UI

Создать:

- `app/src/main/java/com/example/devicesync/feature/send_file/SendFileScreen.kt`
- `app/src/main/java/com/example/devicesync/feature/send_file/SendFileViewModel.kt`

Изменить navigation-файлы приложения.

Использовать Activity Result API:

```kotlin
ActivityResultContracts.OpenDocument()
```

Экран должен показывать:

- выбранный файл;
- компьютер-получатель;
- ожидание подтверждения Windows;
- прогресс;
- скорость;
- отмену;
- успешное завершение;
- понятную ошибку.

## Android-тесты

Создать:

- `app/src/test/java/com/example/devicesync/core/transfer/FileTransferManagerTest.kt`
- `app/src/test/java/com/example/devicesync/feature/send_file/SendFileViewModelTest.kt`

Сценарии:

- [ ] Правильный `file.offer`.
- [ ] Ожидание `file.accept`.
- [ ] Обработка `file.reject`.
- [ ] Правильное разбиение на chunks.
- [ ] Последний chunk короче остальных.
- [ ] `file.complete` после всех chunks.
- [ ] Ожидание `file.received`.
- [ ] Cancel.
- [ ] Disconnect.
- [ ] URI невозможно открыть повторно.
- [ ] Ошибка metadata provider.

---

# Этап 6. End-to-end Android → Windows

## Ручной сценарий

1. Запустить Windows через `DeviceSync (latest)`.
2. Запустить актуальную Android-сборку.
3. Убедиться, что устройства аутентифицированы.
4. Выбрать небольшой `.txt` файл.
5. Принять предложение на Windows.
6. Дождаться 100%.
7. Сравнить SHA-256 исходного и сохранённого файла.

## Матрица проверки

| Сценарий | Ожидаемый результат |
|---|---|
| 1 КБ TXT | Успешно |
| 1 МБ JPEG | Успешно |
| Файл с кириллицей | Имя сохранено безопасно |
| Файл с длинным именем | Корректная валидация |
| Одинаковое имя | Создано уникальное имя или запрошено решение |
| Отмена Android | `.part` удалён |
| Отмена Windows | Android прекращает чтение |
| Wi-Fi выключен | Обе стороны показывают disconnect |
| Повреждённый chunk | Файл отклонён по SHA-256 |

## Definition of Done для File Transfer V1

- [ ] Реальный файл передаётся Android → Windows.
- [ ] Пользователь подтверждает получение.
- [ ] Есть прогресс и отмена.
- [ ] Используется потоковая обработка.
- [ ] Проверяется SHA-256.
- [ ] Нет traversal и произвольной перезаписи.
- [ ] Ошибки не оставляют готовый повреждённый файл.
- [ ] Unit и integration tests зелёные.
- [ ] Протокол документирован.

---

# Этап 7. Шифрование транспорта

Текущая привязка аутентифицирует устройства, но plaintext TCP не защищает содержимое передаваемых файлов.

Опорный документ:

- `protocol/TLS_PINNING_PLAN.md`

## Задачи

- [ ] Выбрать TLS-сертификат Windows или pinning публичного ключа.
- [ ] Связать TLS identity с уже сохранённой pairing identity.
- [ ] Передать fingerprint во время безопасной привязки.
- [ ] Использовать `SslStream` на Windows.
- [ ] Использовать TLS socket/engine на Android.
- [ ] Запретить fallback на plaintext после миграции.
- [ ] Обработать смену identity как security event.
- [ ] Добавить тест MITM/неверного сертификата.

## Критерии готовности

- [ ] Wireshark не видит JSON и содержимое файлов.
- [ ] Неверный сертификат блокирует соединение.
- [ ] Перезапуск приложений не меняет identity.
- [ ] Старая доверенная запись корректно мигрирует или требует повторной привязки.

---

# Этап 8. Windows → Android

После стабилизации первого направления переиспользовать те же protocol messages.

## Windows

- [ ] Добавить выбор файла.
- [ ] Создать `OutgoingFileTransferManager`.
- [ ] Добавить UI исходящей передачи.

## Android

- [ ] Создать `IncomingFileTransferManager`.
- [ ] Использовать MediaStore/Storage Access Framework.
- [ ] Показывать запрос принятия.
- [ ] Сохранять без прямого доступа к произвольным путям.
- [ ] Добавить уведомление фоновой передачи.

---

# Этап 9. Возобновление и очередь передач

## File Transfer V2

- [ ] `file.resume.request`.
- [ ] `file.resume.accepted`.
- [ ] Persistent transfer metadata.
- [ ] Resume с последнего подтверждённого offset.
- [ ] Hash блоков или Merkle/chunk hashes.
- [ ] Очередь нескольких файлов.
- [ ] Ограничение параллелизма.
- [ ] Retry с backoff.
- [ ] Идемпотентность сообщений.
- [ ] Очистка устаревших `.part`.

---

# Этап 10. Остальные функции

## 10.1 Общий буфер обмена

- [ ] `clipboard.update`.
- [ ] Текст и ссылки в первой версии.
- [ ] Защита от циклической пересылки.
- [ ] Метка источника и revision ID.
- [ ] Настройка включения/выключения.
- [ ] Ограничение размера.

## 10.2 Отправка текста и ссылок

- [ ] Android share target.
- [ ] Windows toast/action.
- [ ] Открытие URL только после действия пользователя.
- [ ] История последних элементов.

## 10.3 Уведомления Android на Windows

- [ ] Запрос Notification Listener permission.
- [ ] Фильтрация приложений.
- [ ] `notification.posted` / `notification.removed`.
- [ ] Windows toast notifications.
- [ ] Не передавать sensitive notifications по умолчанию.

## 10.4 Синхронизация папок

- [ ] Явно выбранные папки.
- [ ] Manifest файлов.
- [ ] Сравнение размера, времени и hash.
- [ ] Конфликты без silent overwrite.
- [ ] Удаление только после отдельной настройки.
- [ ] Ограничение по Wi-Fi/заряду.

## 10.5 Фоновая работа

- [ ] Android foreground service для активных передач.
- [ ] WorkManager для отложенных задач.
- [ ] Windows автозапуск.
- [ ] Tray icon.
- [ ] Восстановление состояния после reboot.
- [ ] Контроль энергопотребления.

---

# Архитектурные правила

## Общие

- Протокол документируется раньше реализации.
- Сетевой слой не содержит UI-логики.
- UI не читает и не пишет socket напрямую.
- Большие данные обрабатываются потоково.
- Каждый transfer имеет уникальный ID и state machine.
- Ошибки имеют стабильный технический code и понятный user message.
- Секреты и приватные ключи никогда не пишутся в логи.

## Windows-слои

```text
DeviceSync.Protocol        — сообщения и сериализация
DeviceSync.Application     — transfer state machine и use cases
DeviceSync.Infrastructure  — TCP, filesystem, persistence
DeviceSync.App             — WPF UI и DI
```

## Android-слои

```text
core.protocol   — сообщения и сериализация
core.network    — единственный владелец socket read/write
core.transfer   — transfer state machine
core.data       — Room/DataStore persistence
feature.*       — Compose UI и ViewModel
```

---

# Наблюдаемость и диагностика

## Структурированные события

Добавить безопасные логи:

```text
FILE_OFFER_SENT transferId size
FILE_OFFER_RECEIVED transferId size
FILE_TRANSFER_ACCEPTED transferId
FILE_CHUNK_PROGRESS transferId received total
FILE_HASH_VERIFIED transferId
FILE_TRANSFER_COMPLETED transferId duration
FILE_TRANSFER_FAILED transferId code
```

Не логировать:

- содержимое chunks;
- pairing secret;
- приватные ключи;
- полный пользовательский путь без необходимости;
- чувствительные clipboard/notification payloads.

---

# Рекомендуемый порядок коммитов

1. `docs: specify file transfer v1`
2. `protocol: add file transfer messages and payloads`
3. `windows: add incoming transfer state machine`
4. `windows: route file transfer messages`
5. `windows: add incoming transfer UI`
6. `android: add outgoing transfer manager`
7. `android: add send file UI`
8. `test: add cross-platform file transfer vectors`
9. `integration: verify android to windows transfer`
10. `security: encrypt transport with pinned TLS`

Каждый коммит должен собираться и проходить соответствующие тесты.

---

# Ближайшие действия

> [!todo] Следующая рабочая сессия
> Выполнить только Этапы 0–2. Не начинать UI до утверждения протокола и успешных cross-platform payload tests.

- [ ] Зафиксировать текущую рабочую версию соединения.
- [ ] Создать `protocol/FILE_TRANSFER_V1.md`.
- [ ] Утвердить Base64 chunks для V1.
- [ ] Добавить типы сообщений на Windows и Android.
- [ ] Добавить payload-модели.
- [ ] Добавить общие JSON test vectors.
- [ ] Проверить сериализацию test vectors обеими платформами.

## Связанные заметки

- [[PROTOCOL_V1]]
- [[PAIRING_V1]]
- [[AUTH_V1]]
- [[DISCOVERY_V1]]
- [[TLS_PINNING_PLAN]]
- [[FILE_TRANSFER_V1]]
