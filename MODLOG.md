# MODLOG — Deadlock: Ability Draft (в стиле Street Brawl)

## Цель
Хост поднимает сервер, игроки заходят и выбирают героев, хост пишет `/draft` — у всех открывается выбор
способностей «1 из 3» на каждый из 4 слотов (4-й слот — ульты), каждое предложение можно заменить 2 раза.
Потом голосование за правила (Standard / Street Brawl) и старт матча с набранными наборами.
Готово = работает на локальном сервере, проверено в игре + короткий клип.

## Разведка (2026-10-04)
- Deadlock, Steam appid 1422450, Source 2. `steam.inf`: ClientVersion/ServerVersion **6745** (02.10.2026).
  В appmanifest висит ещё одно обновление (TargetBuildID 25689475 ≠ buildid 25658155) — после него Deadworks может снова отвалиться.
- Античит: VAC на официальных серверах. Клиент НЕ трогаем. Всё — серверный плагин на своём сервере (`-insecure`, LAN).
- Уже стоит с прошлой сессии (см. `..\scratch-2026-10-01-b401b6\MODLOG.md`): Deadworks v0.5.1 в папке игры,
  .NET SDK 10.0.401 в `%LOCALAPPDATA%\Microsoft\dotnet`, плагин ClashArena, читалка VRF (`tools/vrf`).
- `um` CLI: bash-обёртка берёт заглушку `python3` из WindowsApps; работает как `PYTHONPATH=<plugin root> python -m um ...`
  (`um scan` ок, `um kb` требует PyYAML). В базе знаний заметок по Deadlock/Source 2 нет.

## Маршрут
Loader API: серверный плагин Deadworks (C#, .NET 10). Причина: нужна логика (драфт, подмена способностей, смена режима),
API это даёт (`CCitadelPlayerPawn.AddAbility/RemoveAbility`, `OnGameStateChanging`, `UI.Panel`, `OnAbilityAttempt`); клиент не модифицируется.

## Факты (источник — файлы игры и API)
- Способности героя: `scripts/heroes.vdata` → `m_mapBoundAbilities.ESlot_Signature_1..4`; тип — `m_eAbilityType`
  (`EAbilityType_Ultimate` для ульты) в `scripts/abilities.vdata`; иконка — `m_strAbilityImage`.
  Выпущенные герои = `m_bDisabled=false && m_bInDevelopment=false` и есть 4 сигнатуры: 38 героев, 152 способности (38 ульт).
  У Билли (punkgoat) ульта стоит в `Signature_4` как и у всех, хотя называется `ability_punkgoat_tether`, а `ability_punkgoat_ult` — обычная.
- Имена: loose-файлы `game/citadel/resource/localization/citadel_heroes/citadel_heroes_<lang>.txt` (ключ = имя способности),
  герои — `citadel_gc_hero_names` (ключ `hero_x:n`, в русском префикс `#|m|#`).
- `EAbilitySlot`: Signature1..4 = 0..3. `InputButton.Ability1..4`, `Reload`.
- Без лобби сервер на `dl_midtown` сам пролетает WaitingForPlayersToJoin → … → PreGameWait → GameInProgress за секунду при загрузке карты.
  `OnGameStateChanging(...)=false` вето на движковые переходы; `GameRules.ChangeGameState` из плагина вето не подлежит.
- Режим: `GameRules.GameMode` только на чтение (`ECitadelGameMode.StreetBrawl=4`). В server.dll рядом с `citadel_coop_sandbox` /
  `citadel_one_on_one_match` лежит cvar `citadel_gamemode_streetbrawl_enabled` → гипотеза: читается при загрузке карты, нужен reload карты.
  Ещё: `citadel_item_draft_enabled` («1=only street brawl 2=always»), `citadel_street_brawl_reset`, `citadel_street_brawl_advance_state`.
- UI-панели Deadworks (`UI.Panel`) видят только игроки, зашедшие через лаунчер Deadworks (`UI.HasClientBootstrap(slot)`).
  Поэтому драфт дублируется в чат + клавиши: 1-3 взять, R+1-3 заменить; голосование 1/2.

## Код
- `AbilityDraft/DraftPlugin.cs` — фазы Lobby → Drafting → Voting → Starting → Match, драфт, голосование, ввод.
- `AbilityDraft/Ui.cs` — панель (карточки, кнопки) + чат. `Match.cs` — старт матча. `Debug.cs` — лог и тестовый мост.
- `AbilityDraft/AbilityPool.g.cs` — генерится `tools/gen_pool.py` из vdata локальной установки (только имена/id).
- Лог плагина: `%TEMP%\abilitydraft.log`; лог сервера: `game/citadel/console.log`.
- Мост для тестов без клиента: строки в `%TEMP%\abilitydraft.cmd` (`state`, `cvars <s>`, `sv <cmd>`, `bot <name>`, `draft`,
  `pick <slot> <n>`, `reroll <slot> <n>`, `vote <slot> s|b`, `forcevote s|b`, `kits`, `cast <slot> <1-4>`).

## Изменения на машине
- `game/bin/win64/managed/plugins/ClashArena.{dll,pdb}` перенесены в `managed/plugins-disabled/` (он перестраивает dl_midtown под свою арену).
  Вернуть: перенести обратно в `plugins/`.
- В `managed/plugins/` кладётся `AbilityDraft.dll` (сборка `dotnet build AbilityDraft -c Release`).

## Грабли
1. 2026-10-04: Deadworks v0.5.1 не стартует на билде 6745: `Failed to find signatures: CCitadelGameRules::BuildGameSessionManifest`,
   процесс молча выходит, `console.log` не создаётся. Чинится обновлением Deadworks (v0.5.3 от 03.10.2026 — «updated signatures + offsets»).
   Диагностика: запустить `deadworks.exe` с `-RedirectStandardOutput`.

2. `static readonly` поле в одном файле partial-класса, зависящее от поля из другого файла (`AbilityPool.All` в `.g.cs`), —
   порядок инициализации не определён, плагин падал на загрузке (`type initializer threw`). Решение: `Lazy<>`.
3. `OnGameStateChanging` зовётся каждый тик, пока переход запрещён, — не логировать внутри.
4. `GameRules.GameMode` / `MatchMode` на сервере без лобби всегда читаются как `Invalid` (и в Standard, и в Street Brawl) — не оракул.
   Оракул режима — строки `Street Brawl Round N pre rolls` / `hero_x beginning draft` в `console.log`.
5. Фейк-клиенты (`Server.CreateFakeClient`) заходят с командой 0 и без героя: нужно `ChangeTeam` + `SelectHero`; при `changelevel` они пропадают.
   Третьему боту `SelectHero(Lash)` дал pawn с `HeroID=0` без способностей — такие места в драфт не берём.
6. `ExecuteAbilityBySlot` возвращает 4, пока способность не разблокирована (уровень 1); после `UpgradeBits=1` — 0.

## Проверено на сервере (боты, 2026-10-04, Deadworks v0.5.3, билд 6745)
- Deadworks обновлён до v0.5.3 (копия v0.5.1 в `data/deadworks-v0.5.1-backup`, gitignored).
- Лобби удерживается в `PreGameWait` (вето на `GameInProgress`), после голосования `ChangeGameState(GameInProgress)`.
- Драфт 4 ботов: 4 раунда, рероллы 2 на карточку (третий отклоняется), ульты только в 4-м раунде, набор применяется
  (`RemoveAbility` ×4, затем `AddAbility(name, slot)`), держится после старта матча. Чужие способности кастуются (результат 0).
- **Street Brawl без перезагрузки карты**: `citadel_gamemode_streetbrawl_enabled 1` + `citadel_street_brawl_reset` + `ChangeGameState(GameInProgress)`
  на живой карте → `Street Brawl Round 1 pre rolls`, драфт предметов; герои, команды и наборы сохраняются.
  (С перезагрузкой карты тоже работает, но теряются герои и фейк-клиенты.)
- Standard: тот же путь без cvar.

## Проверено клиентом (2026-10-04, один человек + бот, клиент и сервер на одном ноутбуке)
- Запуск клиента напрямую `game/bin/win64/deadlock.exe -novid -windowed -console` (не через Steam — иначе встанет ожидающее обновление игры
  и Deadworks снова отвалится). Консоль — клавиша `` ` ``, `connect localhost:27067`. `+connect` в аргументах запуска не срабатывает.
- На сервере без лобби клиент сам получает команду и случайного героя; штатный выбор героя/команды тоже есть.
- Чат-команды из консоли клиента: `dw_draft`, `dw_vote` и т.д. (`say` в Deadlock нет). В чате — `/draft`.
- Драфт: меню над прицелом (world text), R+1 заменяет карточку (x2 → x1), 1-3 берут, 4-й раунд — ульты, иконки новых способностей
  появляются в HUD. Голосование клавишей 2 → баннер STREET BRAWL → через 5 с штатная заставка «Уличная стычка» и «Выбор предметов. Раунд 1».
  То есть живое переключение `citadel_gamemode_streetbrawl_enabled` + `citadel_street_brawl_reset` клиентский HUD переживает.
- Смена карты (`changelevel`) с подключённым клиентом работает, клиент возвращается через ~60 с.
- Клип: `showcase/ability-draft.mp4` (45 с, ускорено x2.1). Скриншоты: `data/c*.png`.

## Грабли клиента
7. **8 ГБ ОЗУ**: клиент + сервер на одной машине уходят в своп, сервер тормозит и сам выкидывает игрока:
   `ProcessMessages has exhausted its CPU time budget` → `NETWORK_DISCONNECT_OVERFLOW`. Лечится
   `+net_limit_sv_message_process_time_ms_drop_burst 60000 +net_limit_sv_message_process_time_ms_drop_rate 60000` (уже в `tools/start-server.ps1`).
8. Без лаунчера Deadworks панели `UI.Panel` не видны. Чат — маленький пузырь, гаснет через несколько секунд. `HudAnnounce` — крупный баннер на ~3 с.
   Постоянное меню сделано через `CPointWorldText`: `reorientMode: 1` кладёт текст боком и огромным — не использовать. Рабочий рецепт:
   каждый кадр `Teleport(eye + forward*260 + up*70, angles: (0, yaw + 270, 90 - pitch))`, scale 0.2, fontSize 64, центр по обеим осям,
   чужие меню прячутся в `OnCheckTransmit`.
9. `CCitadelPlayerPawn.ViewAngles` возвращает мусор (`<4E-45 0 -3.9E+21>`); рабочие — `CameraAngles` и `EyeAngles`.
10. Клиенту без лаунчера Deadworks шлёт служебный субтитр-рукопожатие вида `u-x*-h425x8po4eu0a…v0.5.00.1a` внизу экрана — это ядро Deadworks, не плагин.
11. Горячая перезагрузка плагина на ~1-2 с снимает вето с `GameInProgress` — матч стартует сам. В бою не страшно, при разработке помнить.
12. `um win shot/record --exe deadlock.exe` может схватить крошечное окно консоли (198x34); для записи указывать `--title Deadlock`.
    `um win shot --scale` требует Pillow (не стоит). Каждый вызов `um win drive` стоит ~3-4 с на компиляцию PowerShell — закладывать в тайминг дубля.
13. Короткое нажатие (120 мс) R+цифра иногда приходит как одна цифра без Reload; 150-180 мс стабильно.

## Не проверено
- Панель UI для игроков с лаунчером Deadworks (код есть в `Ui.cs`, клиента с лаунчером на машине нет).
- Несколько живых игроков одновременно; полный матч до конца; все 152 способности на всех героях (выборочно кастовались ~10).

## Следующий шаг
Сыграть с друзьями (`tools\host.ps1`), собрать отзывы по балансу пула и по способностям, которые ломаются на чужих героях.

## Эксперимент: штатный экран «Выбор предметов» со способностями (2026-10-04, не закончен)
Цель — показать драфт способностей родным экраном Street Brawl без клиентских файлов.
- `citadel_item_draft_force_draw "<имена>"` (нужен `sv_cheats 1`) подставляет в драфт **только предметы** по локализованному имени
  (`Healbane, Suppressor` работают). Имена способностей (локализованные и внутренние) молча игнорируются.
- Состояние драфта сетевое и лежит на pawn: `CCitadelPlayerPawn.m_ItemDraftRoundState` (pawn+0x1028 на билде 6745),
  `ItemDraftRoundState_t`: `m_vecOptions` (+8: count int, +16: указатель), `m_nID` +112, `m_nDraftsRemaining` +116, `m_nDraftsTotal` +120.
  Элемент `ItemDraftOption_t` — 248 байт: `m_Item` +48, `m_BonusItem1` +112, `m_BonusItem2` +176, `m_bHasBeenDrafted` +240, `m_bRare` +241.
  `ItemDraftItem_t`: `m_unItemID` +48 (т.е. токен предмета = элемент+96), `m_nUpgradeBits` +52, `m_nAbilityLevel` +56.
- Токен = MurmurHash2 от имени в нижнем регистре с seed **0x31415926** (`MurmurHash2.HashLowerCase(name, 0x31415926)`);
  `HashStringCaseless` из API даёт другой seed и не подходит.
- Клиент выбирает карточку командой **`buyitem <upgrade_name>`**, реролл — **`itemdraftreroll`** (видно в `OnClientConCommand`).
  Значит, выбор можно перехватить плагином и выдать способность самому.
- Сервер сам способность из драфта не выдаёт: бот с подменёнными токенами (`citadel_bot_purchase_random_draft_option`) ничего не получил.
- **Не выяснено главное:** рисует ли клиентский экран карточку для токена способности. Запись в память вектора не помечает поле
  изменённым, клиенту нужен полный апдейт (`cl_fullupdate`) или запись в тот же тик, что и ролл. Две попытки сорвались:
  набор `cl_fullupdate` ушёл не в консоль, а в экран драфта как хоткеи (и «купил» предметы), а потом
  `citadel_street_brawl_reset` перестал запускать новую фазу драфта посреди идущего раунда.
- Инструменты остались в `Debug.cs`: `draftdump <slot> [имена]`, `draftset <slot> <i> 248 <ability>`, `autopatch <slot> a b c`, `abil`;
  трассировка клиентских команд — файл `%TEMP%\abilitydraft.trace` при загрузке плагина.
- Следующий шаг: свежая карта → Street Brawl с первого раунда → `autopatch` до ролла → смотреть экран.

### Продолжение (2026-10-04, ночь): родной экран РИСУЕТ способности, но клиент падает
- Рабочая последовательность: свежая карта (лобби в PreGameWait) → игрок с героем → `autopatch <slot> a b c` →
  `citadel_gamemode_streetbrawl_enabled 1` + `citadel_street_brawl_reset` + `go`. Ролл и запись токенов попадают в один тик
  (`autopatch wrote option N tick=…` в ту же секунду, что и `beginning draft`), полный апдейт (`cl_fullupdate`) НЕ нужен.
- Клиент показал штатный экран «Выбор предметов»: три карточки с иконками и названиями способностей
  («Усыпляющий кинжал», «Рука-захват», «Сотрясающий взрыв»), кнопка «Прокрутить». Скриншот: `showcase/native-draft-abilities.png`.
  Третья карточка получила плашку «Усиленный» — это флаг редкости исходного предмета (`m_bRare`/upgrade bits), его надо обнулять.
- **Клиент падает (access violation, `deadlock_2026_1004_0242…mdmp`, `…0247…mdmp`)**: в первом прогоне через ~38 с после появления экрана
  без моих действий, во втором — в момент клика по карточке (через ~15 с). Воспроизвелось 2 из 2. Рабочая гипотеза: наведение/клик строит
  подсказку предмета (цена, тир, улучшения) для способности, у которой этих данных нет. Команда `buyitem` до сервера не дошла.
- `citadel_street_brawl_reset` перезапускает фазу драфта только из лобби/при первом старте; посреди идущего раунда — нет.
- Вывод: подмена токена «в лоб» непригодна для игры. Чтобы родной экран работал без падений, карточка должна быть настоящим предметом:
  нужны свои предметы-«обёртки» (`upgrade_ad_<ability>` с иконкой и именем способности) в `abilities.vdata` — то есть VPK на клиенте и сервере
  (компиляция vdata → нужен CSDK). Тогда сервер сам раздаёт их в драфте (`citadel_item_draft_force_draw` / пул), клиент шлёт `buyitem`,
  плагин перехватывает и ставит способность в слот.

### Гибрид «родной экран + выбор клавишами» (2026-10-04, ~03:00) — работает наполовину
Уточнение от пользователя: клиент падал именно при **клике** по карточке способности; наведение безопасно.
Режим `native <slot>` в `Debug.cs`: при каждом ролле плагин пишет в три опции случайные способности (4-й раунд — ульты), сбрасывает `m_bRare`.
- Работает: карточки способностей на штатном экране; **клавиши способностей 1-3 доходят до `OnAbilityAttempt` и на экране драфта** —
  плагин ставит способность в слот (иконка в HUD меняется сразу); штатная кнопка «Прокрутить» шлёт `itemdraftreroll`, сервер роллит заново,
  подмена попадает в тот же тик, клиент показывает новые способности. Клиент не падает, пока не кликаешь по карточке.
- Не работает **переход к следующему выбору**. Экран обновляется только когда сам движок роллит (старт драфта, реролл, покупка). Перепробовано:
  - запись новых токенов + `SchemaAccessor<long>("CCitadelPlayerPawn","m_ItemDraftRoundState").Set(...)` той же величиной — клиент ничего не получает;
  - запись `m_nDraftsTotal`/`m_nDraftsRemaining` (+120/+116) — заголовок «Выбор 1 из 3» не меняется (по той же причине);
  - `Server.ClientCommand(slot, "itemdraftskip")` и `"itemdraftreroll"` — клиент команды, присланные сервером, не исполняет (в трассе их нет);
  - `SetCurrency(EItemDraftRerolls, 5)` — счётчик «Осталось 0 прокручиваний» не изменился.
- Что осталось бы попробовать на этом пути: вызвать нативную функцию ролла/реролла контроллера Street Brawl напрямую (нужна сигнатура в server.dll).
- Решение по договорённости с пользователем: если гибрид не взлетел — делать предметы-обёртки (VPK, нужен CSDK).

### Родной экран драфта заработал целиком (2026-10-04, 03:15)
- В `server.dll` диспетчер клиентских команд героя: ветка `itemdraftreroll` → `if (GetCurrency(pawn, 4) > 0) Reroll(pawn)`;
  `itemdraftskip` → `Skip(pawn)`; `itemdraftclear` (только с читами). Обе функции принимают pawn в `rcx`.
  Skip начинается с проверки функцией-заглушкой `xor al,al; ret` — в релизе всегда выходит, бесполезна.
  Reroll требует: валюта `EItemDraftRerolls` > 0, `m_nDraftsRemaining` ≥ 1, поле pawn+0x10b8 ≠ 0; сам списывает одну прокрутку.
- `tools/find_sigs.py` находит обе функции по строкам команд и пишет сигнатуры в `managed/plugins/AbilityDraft.signatures.json`;
  `Native.cs` сканирует server.dll в памяти процесса и вызывает их через `delegate* unmanaged<IntPtr, void>`.
- Цикл (`native <slot>` в `Debug.cs`): ролл → подмена трёх токенов на способности → игрок жмёт клавишу способности 1-3 →
  `AddAbility` в слот раунда → `SetCurrency(rerolls + 1)` + `Native.Reroll(pawn)` → новые карточки (клиент перерисовывает с анимацией).
  После 4-го выбора подмена выключается, тот же Reroll раздаёт настоящие предметы, штатный драфт предметов идёт дальше.
- Проверено клиентом: 4 раунда (в 4-м только ульты), набор `[viper_venom, nano_dash, smoke_bomb, infinity_slash]` на Wraith,
  потом обычные предметы; у игрока остаётся его собственная «прокрутка» (кнопка работает и на карточках способностей). Клиент не упал.
  Скриншоты: `showcase/native-draft-*.png`.
- ГРАБЛИ: после `connect` консоль клиента остаётся открытой поверх игры — клавиши уходят в неё. Закрыть перед вводом.
- Остаётся: клик мышью по карточке способности роняет клиент (не проверено, спасает ли флаг `m_bHasBeenDrafted`);
  режим живёт в отладочной команде, не встроен в основной сценарий `/draft`; боты; Standard без экрана предметов
  (`citadel_item_draft_enabled 2` не проверен); длительность фазы покупки (~60 с на 4 способности + 3 предмета).
