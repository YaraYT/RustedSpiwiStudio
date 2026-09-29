# Архитектурный разбор исходного 0.0.5-alpha

Исходный проект использовал Electron 38 + Node.js и отделял renderer от main/indexer/database/parser/translator. Для переноса сохранился этот логический разрез, но IPC и браузерная прослойка удалены.

## Маппинг

| Исходник | C# порт |
|---|---|
| Electron main | App + сервисы |
| preload/IPC | прямые интерфейсы C# |
| Node fs | System.IO |
| node:sqlite | Microsoft.Data.Sqlite |
| crypto SHA-256 | System.Security.Cryptography |
| zlib/ZIP | System.IO.Compression |
| renderer JS | Avalonia + view models |
| fetch | HttpClient |
| Promise | Task |
| cancellation flags | CancellationToken |
| rwimage:// | удалено, локальные Bitmap |

## Главное ядро

Resolver не делает физические копии inherited keys в SQLite. Он сначала строит эффективное представление секции, учитывая `copyFrom`, `@copyFromSection`, `@define`, template и локальные значения, и только затем создаёт image references. Это сохраняет смысл исходного индекса.

Повторяющиеся секции обрабатываются по принципу последнего определения, как в исходном парсере.

`.rwmod` извлекается потоково в cache, а не целиком в память. Перед распаковкой проверяется SHA-256 и решение пользователя хранится в `rwmod_decisions`.

## Revision 3 verification notes

The third compiler pass corrected App initialization, the Path name collision, dispatcher async unwrapping, NumericUpDown value types, and nullable SQL parameter handling.
