# Памятка: что чинить после патча Deadlock

Плагин зависит от трёх вещей в игре. После крупного патча проверяй их по порядку — в логе сервера
(`game/citadel/console.log`, строки `[AD]`) и в `%TEMP%\abilitydraft.log` видно, что именно отвалилось.

## 1. Deadworks не запускается
Симптом: окно сервера сразу закрывается, `console.log` не создаётся. В выводе `deadworks.exe`:
`Failed to find signatures … Deadlock build NNNN is not supported`.
Что делать: скачать свежий релиз с https://github.com/Deadworks-net/deadworks/releases и распаковать в папку Deadlock поверх старого.
Потом пересобрать плагин: `dotnet build AbilityDraft -c Release`.

## 2. «signature not found» — функции родного экрана драфта
Плагин вызывает две функции из `server.dll`: «раздать карточки заново» (Reroll) и «перейти к следующему выбору / закончить драфт» (Advance). Их адреса ищутся по байтовым сигнатурам
из файла `game/bin/win64/managed/plugins/AbilityDraft.signatures.json`.
Что делать:
```
pip install capstone        # один раз
python tools\find_sigs.py   # сам находит функции и перезаписывает AbilityDraft.signatures.json
```
и перезапустить сервер. В логе должно появиться `native draft functions resolved: advance=… reroll=…`.

Если скрипт пишет `no code reference…` или `no call found…`, Valve переписала обработчик команд. Как найти руками (IDA/Ghidra):
1. Открой `game/citadel/bin/win64/server.dll`, найди строку `itemdraftreroll`.
2. Перейди по ссылке на неё: это длинная цепочка сравнений имён клиентских команд (`buyitem`, `sellitem`, `itemdraftreroll`, …).
3. У сравнения с `itemdraftreroll` есть переход `je` на ветку. В ветке последним вызовом перед `jmp` идёт `call` с героем в `rcx` —
   это функция «прокрутки». Аналогично `itemdraftskip` → функция пропуска.
4. Возьми первые 12–16 байт функции, относительные адреса замени на `?`, впиши в json.

5. Advance — это последний `call` внутри функции пропуска (Skip): сама Skip в релизе выключена проверкой-заглушкой,
   а функция, которую она вызывает в конце, работает и корректно закрывает экран у клиента после последнего выбора.

На билде 6745 (02.10.2026): skip = `server.dll+0x7f10a0`, reroll = `server.dll+0x7f12b0`, advance = `server.dll+0x7f0670`.

## 3. Карточки показывают мусор или сервер падает при драфте — смещения структуры драфта
Плагин пишет идентификаторы способностей прямо в сетевую структуру драфта героя. Смещения (билд 6745):
| Что | Где |
|---|---|
| `CCitadelPlayerPawn.m_ItemDraftRoundState` | берётся из схемы игры автоматически |
| число карточек / указатель на массив | состояние `+8` / `+16` |
| размер одной карточки (`ItemDraftOption_t`) | `248` байт |
| идентификатор предмета в карточке | карточка `+96` |
| «уже взято» / «редкий» | карточка `+240` / `+241` |

Проверка: на сервере в режиме Street Brawl выполни через мост (`%TEMP%\abilitydraft.cmd`) команду `draftdump <слот> upgrade_healbane`.
Она печатает смещения полей из схемы и сырые байты. Первая карточка должна содержать токен предмета, который игра только что раздала
(имена — в `console.log`, строки `hero_x rolled … upgrade_…`). Токен = MurmurHash2 от имени в нижнем регистре, seed `0x31415926`.
Если размер карточки или смещение токена изменились — поправь константы `OptionSize`, `OptionItemId`, `OptionUpgradeBits`, `OptionRare`, `StateId` в начале `AbilityDraft/NativeDraft.cs`.

## 4. Новые герои
`tools\dump-vdata.ps1 -Vrf <путь к vrf.dll>` пересобирает список способностей из файлов игры, затем `dotnet build`.
