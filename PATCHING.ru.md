# Что чинить после патча Deadlock

[English](PATCHING.md) · **Русский**

Плагин зависит от трёх вещей внутри игры. После крупного патча проверяйте их в этом порядке. Что именно сломалось,
видно в логе сервера (`game/citadel/console.log`, строки с `[AD]`) и в `%TEMP%\abilitydraft.log`.

## 1. Deadworks не запускается
Симптом: окно сервера сразу закрывается, `console.log` не создаётся. `deadworks.exe` пишет
`Failed to find signatures … Deadlock build NNNN is not supported`.

Что делать: скачать свежий релиз с https://github.com/Deadworks-net/deadworks/releases и распаковать в папку
Deadlock поверх старого. Если собираете плагин из исходников, пересоберите его: `dotnet build AbilityDraft -c Release`.

## 2. «signature not found» — функции родного экрана драфта
Плагин вызывает две функции из `server.dll`: Reroll (раздать три новые карточки) и Advance (перейти к следующему
выбору или закончить драфт). Он находит их по байтовым сигнатурам из файла
`game/bin/win64/managed/plugins/AbilityDraft.signatures.json`.

Что делать:
```
pip install capstone        # один раз
python tools\find_sigs.py   # находит функции и перезаписывает AbilityDraft.signatures.json
```
и перезапустить сервер. В логе должно появиться `native draft functions resolved: advance=… reroll=…`.
Пока это не починено, плагин всё равно работает: драфт идёт текстовым меню вместо родного экрана.

Если скрипт пишет `no code reference…` или `no call found…`, Valve переписала обработчик команд.
Как найти функции вручную (IDA или Ghidra):
1. Откройте `game/citadel/bin/win64/server.dll` и найдите строку `itemdraftreroll`.
2. Перейдите по ссылке на неё: это длинная цепочка сравнений имён клиентских команд (`buyitem`, `sellitem`,
   `itemdraftreroll`, …).
3. У сравнения с `itemdraftreroll` есть переход `je` на ветку. Последний `call` в этой ветке перед её `jmp`,
   с героем в `rcx`, — это Reroll. Так же `itemdraftskip` → Skip.
4. Advance — последний `call` внутри Skip. Сама Skip в релизной сборке отключена проверкой-заглушкой, но функция,
   которую она вызывает в конце, работает и правильно закрывает экран у клиента после последнего выбора.
5. Возьмите первые 12–22 байта каждой функции, замените относительные адреса на `?` и впишите в json.

На билде 6745 (2 октября 2026): skip = `server.dll+0x7f10a0`, reroll = `server.dll+0x7f12b0`,
advance = `server.dll+0x7f0670`.

## 3. Карточки показывают мусор или сервер падает при драфте — раскладка структуры драфта
Плагин пишет идентификаторы способностей прямо в сетевое состояние драфта героя. Смещения на билде 6745:

| Что | Где |
|---|---|
| `CCitadelPlayerPawn.m_ItemDraftRoundState` | читается из схемы игры автоматически |
| число карточек / указатель на массив карточек | состояние `+8` / `+16` |
| идентификатор драфта (`-1`, когда драфта нет) | состояние `+112` |
| осталось раундов / всего раундов (заголовок «выбор N из M») | читается из схемы автоматически |
| размер одной карточки (`ItemDraftOption_t`) | `248` байт |
| идентификатор предмета в карточке | карточка `+96` |
| биты улучшения (бит 1 — плашка «усиленный») | карточка `+100` |
| «уже взято» / «редкий» | карточка `+240` / `+241` |

Проверка: когда сервер в Street Brawl, запишите `draftdump <слот> upgrade_healbane` в файл `%TEMP%\abilitydraft.cmd`.
Команда печатает смещения полей из схемы и сырые байты. В первой карточке должен лежать токен предмета, который игра
только что раздала (имена — в `console.log`, строки `hero_x rolled … upgrade_…`). Токен — это MurmurHash2 от имени
в нижнем регистре с seed `0x31415926`.

Если размер карточки или смещение токена изменились, поправьте константы `OptionSize`, `OptionItemId`,
`OptionUpgradeBits`, `OptionRare` и `StateId` в начале `AbilityDraft/NativeDraft.cs`.

## 4. Street Brawl или переход в Standard ведут себя иначе
Плагин опирается на эти серверные переменные и команды:
- `citadel_gamemode_streetbrawl_enabled` — режим следует за ней на лету;
- `citadel_street_brawl_reset` — запускает матч Street Brawl на текущей карте (при любом значении переменной);
- `citadel_active_lane` — Street Brawl ставит одну линию и обратно не возвращает; плагин сбрасывает её в `0`.

Standard после драфта — это перезагрузка карты, потому что Street Brawl насовсем убирает уокеров и казармы
боковых линий.

## 5. Новые герои
Пул способностей собирается из файлов игры и вшивается в плагин, поэтому новый герой сам не появится.
Герои с пометкой «отключён» или «в разработке» в пул не попадают.

1. Декомпилируйте `scripts/heroes.vdata_c` и `scripts/abilities.vdata_c` из `game/citadel/pak01_dir.vpk` через
   [Source 2 Viewer](https://valveresourceformat.github.io/) и сохраните как `data/heroes.vdata` и
   `data/abilities.vdata`.
2. `python tools\gen_pool.py data "<папка Deadlock>" AbilityDraft\AbilityPool.g.cs`
3. `dotnet build AbilityDraft -c Release`

## 6. Выпуск нового релиза
```
dotnet build AbilityDraft -c Release
python tools\find_sigs.py
python tools\package.py v0.1.1
```
Архив появится в `dist/`. Файл сигнатур в нём соответствует тому билду игры, на котором он собран.

## 7. Перестали работать прокачка или предметы «на способность» — таблица способностей героя
Плагин записывает набранный набор в серверную таблицу родных способностей героя
(`CitadelHeroData_t.m_mapBoundAbilities`), поэтому прокачку и предметы «на способность» движок обрабатывает сам.
Поле находится через схему; раскладка внутри него зашита в начале `AbilityDraft/HeroBinding.cs` (сборка 6759):
массив узлов по смещению `+0x10`, число узлов по `+0x1c`; узел занимает `0x28` байт, слот — в младших 16 битах
по `+0x10`, указатель на имя способности по `+0x18`, отпечаток имени по `+0x20`.

Узел перезаписывается, только если он читается как «имя и отпечаток этого имени». Если раскладка сдвинулась, в логе
сервера будет `hero table: no trusted entry for …`, ничего не запишется, а плагин вернётся к запасной схеме: сам
отвечает на команды прокачки и покупки (`Training.cs`, `Imbue.cs`; для предметов тогда нужен клиентский аддон).

Чтобы посмотреть память, напишите `herodata Inferno 160` в `%TEMP%\abilitydraft.cmd` и пройдите по указателю из
`+0x10` командой `peek <адрес> 640`: рядом со слотами будут видны имена способностей.

## 8. Клиентский аддон
В `clientside/pak01_dir.vpk` лежит копия данных способностей игры, и он устаревает с каждым патчем, который трогает
предметы или способности. Пересоберите его (`tools/build_cards.py`, см. README), скопируйте результат поверх
`clientside/pak01_dir.vpk` и обновите номер сборки в `clientside/README.md`.

- Компилятор ресурсов берётся из CSDK 12 и старше игры; он запускается с ключом
  `-danger_mode_ignore_schema_mismatches`, как это делает и лаунчер самого CSDK.
- Мост интерфейса Deadworks скачивается у Deadworks и сверяется с опубликованной контрольной суммой. Когда
  Deadworks обновит свой загрузчик, удалите `data/deadworks-bootstrap.vpk` и соберите заново.
- `addon/panorama/scripts/abilitydraft_hud.js` опирается на имена из интерфейса самой игры: панели `hud_signature`,
  `CitadelAbilityIcon`, `CitadelHudAbilityUpgradePips`, `AbilityUnlock1-3`, `OptionsContainer` и классы стилей
  `isUnlocked`, `canAffordUpgrade`, `hasAbilityUpgrade`, `selected`, `dismiss`. Если после патча перестали работать
  меню TAB или анимация выбора, декомпилируйте `panorama/layout/citadel_hud_ability_upgrade_pips.vxml_c`,
  `panorama/styles/citadel_hud_ability_upgrade_pips.vcss_c` и `panorama/styles/citadel_item_draft_panel.vcss_c`
  и сравните.
