# Повторное использование чужих наработок

Подробные версии, SHA и источники: [research.md](research.md).
Здесь зафиксировано решение, что именно переиспользовать в CPU Lab.

| Наработка | Использование | Что собственное / что не копируем |
|---|---|---|
| HWiNFO | Внешний установленный provider; SHM/Pro logs после квалификации | Свой адаптер и нормализация; движок/SDK/ключи не распространяются |
| LibreHardwareMonitorLib | Возможный .NET provider после основного HWiNFO потока | Не переносим AMD effective clocks на Intel; не включаем controls |
| CinebenchBatch | Свидетельство CLI/stdout и пример организационного подхода | Batch-код не скопирован; свой adapter с timeout/result contract |
| Maxon Cinebench | Внешняя разрешённая установка, один известный MC вызов | Не распространяем binaries; не считаем GUI ranking результатом прогона |
| 7-Zip | Внешний конечный benchmark; собственный консервативный parser | Не создаём compression workload с нуля и не связываем библиотеку |
| y-cruncher | Следующий adapter на основе штатного bench/config/stress CLI | Не выдумываем stress engine и структуру config; версию config фиксируем |
| Prime95 | Возможный отдельный bounded stress adapter | Нельзя считать внешний kill завершением вычисления |
| OCCT Enterprise | Возможная покупная альтернатива целому runner/report pipeline | Не объявляем бесплатный CLI и не обходим редакции |
| agoose77/throttlestop | Учебное чтение MSR encoding и опасности mailbox queries | Код и вызовы wrmsr не включены в CPU Lab |
| Intel PCM | Дополнительный источник PMU для отдельного этапа | Нет собственного raw MSR writer; не включаем неподтверждённые счётчики |
| UEFITool / IFRExtractor-RS | Офлайн-инструменты для будущего каталога BIOS | Нет записи NVRAM, модификации или поставки образов firmware |

Парсеры в текущем коде используют общую форму документированного результата.
Конструируемые тестовые строки явно обозначены как фикстуры, без приписывания
их реальному стенду или версии executable. Ни один чужой нагрузочный движок не запущен.

## Уже подключённые зависимости

Runtime Core/CLI пока используют только стандартную библиотеку .NET 10; сторонних
runtime NuGet-пакетов нет. Не копировались исходники GPL batch-файла или MSR-утилиты.

Тестовые зависимости (версии проверены по локальным NuGet nuspec и зафиксированы lock):

| Пакет | Версия | Лицензия | Назначение |
|---|---|---|---|
| xunit | 2.9.3 | Apache-2.0 | Проверки данных, runner, анализатора и parser |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | Test discovery/execution |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | VSTest host |

Лицензии/зависимости: [xUnit](https://www.nuget.org/packages/xunit/2.9.3),
[runner](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.4),
[test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1).
Транзитивные пакеты перечислены в `tests/CpuLab.Tests/packages.lock.json`.
До поставки бинарного release выполнить notices/dependency review его фактического состава.

Лицензия собственного кода CPU Lab ещё не выбрана пользователем. Публичная видимость
репозитория сама по себе не даёт MIT/GPL лицензии. Подключение MPL/LGPL компонентов и
поставка бинарников будут отдельным решением с сохранением notices/source obligations.
