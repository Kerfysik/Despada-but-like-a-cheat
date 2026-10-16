<div align="center">

## Despada

Утилита для активного анализа клиента Space Station 14.

<p align="center"><a href="https://pay.cloudtips.ru/p/c2e4b0f4"><img src="https://ddepsadd.github.io/ddepsadd/btn-cloudtips.svg" width="50%" /></a><a href="https://ddepsadd.github.io/ddepsadd/monero.html"><img src="https://ddepsadd.github.io/ddepsadd/btn-monero.svg" width="50%" /></a></p>

![Despada](image.png)

</div>

## Roadmap

Порядок - это порядок работы, не обещание сроков.

- [x] Своя консоль: сессии, история, автодополнение, дерево в выводе
- [x] Сетевой лог: захват в обе стороны, фильтр, инспектор полей, байтовый поток
- [x] Активная сеть: повтор, черновики, сохранённые пакеты, правила за предохранителем
- [x] Поиск по всему (Ctrl+D): поля всех пакетов, байты, типы, команды
- [ ] Автодополнение значений в фильтре: `entity:`, `tick:`, значения полей
- [ ] Полноэкранный режим: док-раскладка, игра насквозь в центре
- [ ] Entity viewer: список и поиск сущностей, прототип, компоненты, связи с пакетами
- [ ] Component viewer: дерево полей компонента по рефлексии, живое обновление, правка позже
- [ ] Ивент-лог: шина событий с переходом к коду
- [ ] Декомпилятор внутри (ICSharpCode.Decompiler в процессе), ссылки "к коду" отовсюду
- [ ] Рантайм-хуки: правила "когда X где условие -> сделай Y" поверх пакетов, ивентов, тика, любого метода; свой мини-DSL в консоли
- [ ] Перекраска UI игры под стиль Despada из одного набора токенов
- [ ] Моё ревью, оно будет переносится и откладываться из-за нехватки времени и объёма работ

## Features

**Консоль.** Несколько сессий, история, автодополнение команд и аргументов, цветной вывод с раскрываемыми деревьями. Команды: `help`, `features`, `feature <id> [on|off]`, `hooks`, `bind`, `config`, `theme`, `glass`, `blur`, `session`, `clear`, `echo`.

**Network.** Каждое сообщение клиента в обе стороны. Список с фильтром (`type:`, `entity:`, `tick:`, текст, отрицание), пауза, статистика по типам. Инспектор: таблица полей, связанные пакеты, байтовый поток с привязкой байтов к полям, hex и текст. В консоли: `net.find`, `net.types`, `net.stats`, `net.pause`, `net.buffer`.

**Активная сеть.** Повтор пакета, черновики с правкой по полям и отправкой, сохранённые пакеты, правила "когда пришло X -> заблокировать, задержать, изменить, повторить".

**Поиск.** `Ctrl+D`: поля всех пакетов в памяти через индекс, байты по тексту и hex, пакеты по типу и сводке, типы, команды консоли.

**Как сделано.** Работа с движком через рефлексию, я не еблан, чтобы подвязывать под робуст. Одна DLL с build in зависимостями. Конфиг в JSON: `~/.config/Despada`, `%AppData%/Despada`. Тесты: `dotnet test Despada.Tests`.

> Код не закончен и меняется. Самостоятельного ревью я не делал. Используй на свой страх.

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
