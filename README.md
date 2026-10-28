<div align="center">

## Despada 

Утилита для активного анализа клиента Space Station 14: сеть, ESP/HUD и защита от захвата экрана. это дополненый Despada в нем пока нет обоходов анти читов что я планирую делать так же хочу сказать что весь код пишет ии не я я проверял все функции вроде как все работает я не буду тут описывать функции Despada только что я (ии) добавил линк на оригиналный Despada: https://github.com/ddepsadd/Despada хочу сказать ему огромное спасибо за что он уже сделал 

</div>

## Что это

Despada загружается в процесс клиента SS14 и даёт три вещи: разбор сетевого трафика в обе стороны, информацию о том, что происходит вокруг твоего персонажа, и защиту от скриншотов и стрима.

Движок не подвязан под сборки Robust/Content: компоненты и поля читаются рефлексией по именам (`Despada.Core.Bind`), поэтому одна и та же DLL работает на разных форках и сборках клиента. На части серверов член структуры — поле, на другой — свойство; биндинг сам выбирает доступный вариант, а если компонента на форке нет, соответствующая метка просто не рисуется.

## Возможности

### Player ESP

Подписи над каждым игроком, которого клиент получил по PVS:

- имя, ckey, роль из ID-карты, дистанция в метрах;
- полоски стамины (розовая) и HP (зелёная → красная, в крите красная) с процентом здоровья;
- метки: антагонисты (Agent, Heretic, Vampire, Flesh cult, Zombie, Changeling, Cosmic cult, Devil, Blob, Thief), друг, приоритет, боевой режим, контрабанда, импланты, оружие, No slip;
- стрелки у кромки экрана показывают направление на игроков вне окна;
- друг/приоритет задаются списками ckey в настройках.

Шрифт подписей — игровой Boxfont Round (вшит в DLL). Цвета каждой строки настраиваются.

### HUD и иконки

Иконки статусов — антагонист, должность, майндшилд, криминальная запись, синдикат, химический анализ, электричество — включаются игровыми HUD-компонентами на локальном теле с `NetSyncEnabled = false`. Рисует их сама игра, на сервер ничего не уходит. Строка `icon components N/7` в табе честно показывает, какие компоненты есть на конкретном форке.

### Visuals

No FOV, FullBright и Zoom (0.5–3) — только локальный вид, через `ContentEye`/`Eye`/`Examiner` с `NetSyncEnabled = false`.

### Инструменты

- **StorageViewer** — что несёт игрок под курсором: руки, карманы, спина, пояс, слот костюма, ID, обувь, импланты; на один уровень внутрь сумок. `F8` закрепляет панель на теле, повторное нажатие отпускает.
- **ClickDamage** — суммарный урон и разбивка по типам (Blunt/Slash/Burn/Toxin…).
- **Ammo** — патроны в оружии и магазинах: заряжено/ёмкость.
- **Trails** — затухающий след за игроками, радуга или свой цвет, длина и толщина настраиваются.

### Защита

Stream-режим: игра видна в захвате, оверлей — нет; окно-зеркало прозрачно для мыши (клики уходят в игру). Постоянный анти-скрин-граб клиента.

### Хоткеи

Работают без фокуса на меню и вне его:

| Клавиша | Действие |
| --- | --- |
| `F6` | No FOV вкл/выкл |
| `F7` | FullBright вкл/выкл |
| `F8` | Закрепить/отпустить инспектор на теле под курсором |
| `+` / `-` | Zoom ±0.1 (0.5–3) |

## Сборка

```sh
dotnet build Despada/Despada.csproj -c Release
dotnet test Despada.Tests/Despada.Tests.csproj -c Release
```

На Windows то же самое делает `Despada/build.bat`. Готовая DLL — `Despada/bin/Release/net10.0/Despada.dll`, загружается инжектором в процесс клиента (например, MusyaLoader или же marsey).

## Конфиг

JSON в `%AppData%/Despada` (Windows) или `~/.config/Despada` (Linux). Настройки фич сохраняются с задержкой в секунду.

## Roadmap

Порядок — это порядок работы, не обещание сроков.

- [x] Player ESP: подписи, HP/стамина, роль, дистанция, стрелки за край экрана
- [x] HUD-иконки через игровые компоненты, Boxfont Round
- [x] StorageViewer, ClickDamage, Ammo, трейлы
- [x] Stream-режим и постоянный анти-скрин-граб
- [x] Глобальные хоткеи F6/F7/F8/+/-
- [ ] Контейнеры на земле, компонентный ESP (туррели, ловушки, аномалии), доступы
- [ ] Окно списка игроков: роль, позиция, метка антага
- [ ] aimbot 


## Как сделано

Работа с движком — рефлексия и биндинги, без ссылок на Robust/Content. Одна DLL со встроенными зависимостями (ImGui.NET, cimgui, шрифты). Harmony используется только инфраструктурой (рендер, ввод), игровая логика не патчится. Тесты: `dotnet test Despada.Tests`.

## License

Copyright (C) 2026 ddepsadd

Despada is free software: you can redistribute it and/or modify it under the
terms of the GNU Affero General Public License as published by the Free
Software Foundation, either version 3 of the License, or (at your option) any
later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License along
with this program. If not, see <https://www.gnu.org/licenses/>.

`SPDX-License-Identifier: AGPL-3.0-or-later`

Compiled binaries built from this source — including obfuscated, repacked or
renamed ones — are covered works. Distributing them requires shipping the
Corresponding Source under the same license, keeping all copyright and license
notices intact, and preserving attribution.

Third-party components: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
