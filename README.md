# CPU Lab

Локальный стенд для воспроизводимого исследования Intel CPU под Windows 11:
серии нагрузок, синхронная телеметрия и HTML-отчёты.

Статус: фаза 0 выполнена по документации и исходникам; план согласован.
Готовы **mock-ядро, журнал, анализ, HTML-макет, предварительный CSV-импорт,
управление тестовыми процессами и тесты**.
Команда `probe-series` проверяет целиком серию из трёх лёгких процессов с общим отчётом.
Живые датчики, запуск бенчмарков и изменение аппаратных настроек не реализованы.

## Быстрый старт без стенда

Нужен .NET 10 SDK. Работает на Linux и Windows, без прав администратора.

```bash
dotnet restore CpuLab.sln --locked-mode
dotnet build CpuLab.sln --no-restore
dotnet test CpuLab.sln --no-build
dotnet run --project src/CpuLab.Cli --no-build -- mock --scenario scenarios/mock-demo.json --legacy fixtures/legacy/manual-results.json
```

Команда выведет путь к `report.html`. Синтетические три прогона выполняются
быстро: часы эксперимента виртуальные, на компьютер нагрузка не подаётся.
Параметр `--realtime` добавляет паузы для проверки Ctrl+C.

```bash
dotnet run --project src/CpuLab.Cli --no-build -- import-legacy --file fixtures/legacy/manual-results.json
dotnet run --project src/CpuLab.Cli --no-build -- import-hwinfo --file fixtures/hwinfo/synthetic.csv --profile fixtures/hwinfo/profile.json
dotnet run --project src/CpuLab.Cli --no-build -- process-demo
dotnet run --project src/CpuLab.Cli --no-build -- probe-series --scenario scenarios/probe-series.json
dotnet run --project src/CpuLab.Cli --no-build -- report --run <каталог-сессии>
dotnet run --project src/CpuLab.Cli --no-build -- recover --run <незавершённая-сессия>
```

Сессии создаются в `runs/` и не попадают в Git. `recover` сохраняет статус
`interrupted` и не возобновляет нагрузку. Полная справка: `dotnet run --project
src/CpuLab.Cli -- --help`.

## Документация

- [Единый каталог инструментов и правила транзита](docs/toolkit.md)
- [Подключение свежего Windows-ПК по SSH](docs/windows-remote.md)
- [Техническое задание](docs/Codex_CPU_Lab_TZ.md)
- [Инструкции для агентов](AGENTS.md)
- [Техническая разведка и первоисточники](docs/research.md)
- [Архитектура MVP](docs/architecture.md)
- [Работа с mock, форматы и ограничения](docs/development.md)
- [Импорт HWiNFO CSV: запуск, профиль и границы поддержки](docs/hwinfo-csv.md)
- [Управление процессами: демо, остановка дерева и ограничения](docs/process-supervisor.md)
- [Сквозная серия из трёх тестовых процессов](docs/probe-series.md)
- [План дальнейших работ](docs/plan.md)
- [Фактически выполненные проверки](docs/validation.md)
- [Использование чужих наработок и лицензии](docs/reuse.md)

Лицензия собственного проекта пока не выбрана. Сторонние программы, драйверы и
прошивки не поставляются. Фикстуры парсеров не являются логами реального Windows-стенда.
