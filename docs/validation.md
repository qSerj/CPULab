# Проверки 20.10.2026

Обновление 22.10: выполнена локальная Windows-квалификация по SSH; результаты
описаны в конце документа. Записи ниже отражают состояние на 20.10.

Среда: Linux Debian 13 x64, .NET SDK 10.0.302. Аппаратного доступа,
драйверов и реальных нагрузок не было. Синтетический режим не является
проверкой датчиков, стабильности CPU или CLI установленного Cinebench.

Выполнено:

- `dotnet restore CpuLab.sln --locked-mode` с локальным кешем как source — успешно.
- `dotnet build CpuLab.sln --configuration Release --no-restore` — без ошибок/предупреждений.
- `dotnet test CpuLab.sln --configuration Release --no-build --no-restore` — 87 проверок успешно
  (38 базовых, 27 CSV, 12 процессных, 10 сквозной серии).
- Mock smoke: три попытки, 37 кадров, CSV/raw, собственные данные каждой попытки,
  analysis.json и report.html — успешно.
- Import legacy: все шесть точек, пустой raw, отсутствие искусственных рядов — успешно.
- HWiNFO CSV CLI на синтетическом примере: 3 строки, 6 каналов, отдельный HTML,
  raw и JSONL. Частичный хвост → `partial`, побайтовый raw сохранён;
  неверный dialect → код 2 до создания выходного каталога.
- `process-demo`: exit 0, stdout 57 байт, stderr 13 байт, журнал лимитов и результата,
  остановка собственной Linux-группы. Реальный запуск CLI прошёл в обычной песочнице.
- Process tests: gate до старта, argv/Unicode без shell, ненулевой exit,
  оба заполненных pipe, output limit, отмена, timeout с потомком, ранний выход root,
  аварийное завершение владельца, preflight и ошибка запуска binary — успешно.
  Известные PID проверены на прекращение выполнения.
- `probe-series` CLI: три последовательные попытки, fixture scores 995/1000/1005,
  единый журнал/HTML, SHA-256 компонентов и ссылки на исходные process logs.
- Фактическая авария CLI во второй попытке + `recover` (Linux, обычная песочница):
  собственные host/target прекратили выполнение, первая попытка сохранила score 995
  и неизменный SHA-256 stdout, вторая стала interrupted с null score/end,
  третья не запускалась; итоговый HTML создан.
- Сквозные тесты: ошибка второй попытки, отсутствие score, attempt/total timeout,
  общий лимит в паузе, отмена до/после старта, provenance, последовательность
  и recovery искусственно незавершённого snapshot — успешно.
- CLI: real mode отвергнут до создания сессии, отсутствие score → failed с отчётом,
  SIGINT/Ctrl+C в paced mock → код 130, cancelled и частичный отчёт — успешно.
- В браузере открыты HTML и график мощности: видна маркировка симуляции,
  три окна попыток, таблицы и подписи осей.
- Форматы JSON, локальные ссылки документации и `git diff --check` — без ошибок.

В тестах покрыты: детерминизм, partial failure, отмена до старта и во время попытки,
attempt/total timeout, потеря температуры, stale/gap, missing effective clocks,
recovery обрезанного хвоста и отказ при повреждении в середине/активном writer,
неравномерные интервалы/energy/coverage, граничные и дублирующие интервалы,
неизвестные значения, совместимость сравнения, HTML escaping, fixtures parser и schema.
CSV-тесты: UTF-8 BOM/CP1251/CP1252, quoting, локализованные числа без разделителей
тысяч, UTC offset, дубли названий, неравномерные timestamps, raw/hash,
повреждения и opt-in восстановление только незавершённой последней строки.

CI workflow подготовлен для Ubuntu и Windows, но удалённые jobs не выполнялись.
Windows Job Object реализован, его исполнение не проверено.
Не проверены Windows GUI, WMI/CPUID, HWiNFO SHM/CSV на стенде,
stdout/stderr и точные окна R23/7-Zip. Следующие проверки: [plan.md](plan.md).

## Windows-квалификация 22.10.2026

Дополнительный Windows 11 build 22631 стенд, SSH по ключу с проверенным отпечатком.
Оператор установил .NET SDK 10.0.401 / runtime 10.0.12; VSTest 18.7.0 x64.
На Linux собрана Release managed-сборка и передана по SSH/SFTP; SHA-256 архива
проверен до распаковки. Это реальное исполнение на Windows, не сборка исходников
на Windows и не запуск GitHub Actions.

- Первое исполнение: 86/87. Legacy-тест читал raw до Dispose RunStore;
  на Windows reader не разрешал существующий writer. CLI уже закрывает store
  до чтения. Исправлен только жизненный цикл store в тесте, FileShare не ослаблен.
- После исправления: **87/87 на Linux и 87/87 на Windows**, без пропусков.
  Сборка без ошибок и предупреждений. Локальному VSTest потребовался выход из
  ограничения сокетов песочницы для собственного loopback IPC.
- Реально пройдены gate, аргументы/Unicode, stdout/stderr, output limit,
  отмена, timeout, остановка root/потомка, ранний выход root и авария владельца.
  Windows Job проверен на собственных лёгких ProcessProbe.
- CLI process-demo: completed, exit 0, cleanup windows-job-empty.
- CLI probe-series: три последовательных completed, fixture_points 995/1000/1005,
  журнал и HTML созданы и возвращены на машину разработки вместе с исходными
  process logs. Это синтетические баллы, а не измерение производительности CPU.
- Исходный и исправленный TRX, stdout и пакетные хеши сохранены под
  runs/remote-access/windows-qualification/ и не коммитятся.
- Сняты read-only CIM inventory и 120 с штатных CPU-счётчиков Windows:
  54 наблюдения, raw JSONL, CSV и HTML. Аппаратная температура/мощность/effective
  clocks этим не измерены; это отдельная фоновая запись, не нагрузочная сессия.

Проверка относится к одному окружению. Стенды A/B, другие версии Windows,
HWiNFO SHM/реальный CSV, GUI и настоящие benchmark adapters не квалифицированы.
HWiNFO/OCCT, драйверы датчиков и реальные нагрузки на этом этапе не запускались.

### HWiNFO pilot, 22.10.2026 (отдельно от ProcessSupervisor)

После отдельного разрешения оператора выполнено 180 s фонового наблюдения
без benchmark. HWiNFO 8.54-6090 + MIT Hwinfo.SharedMemory.Net 4.0.0:
87 polls, 18 групп, 437 channels, 172 s между первым/последним provider timestamp.
Рабочий стол session 2 через одноразовый Interactive task + обычный ShellExecute.
Штатный provider остановлен по сохранённому Process handle; задача удалена.
ProcessHost/gate не менялись: UIAccess launch и workload adapter не квалифицированы.

Есть ограничения: три начальных unavailable; binary snapshots отклонены cap
16 MiB (errors=1 плюс отдельный финальный отказ); ANSI mojibake в русском;
расхождения Value с provider Min/Max. Это частичный telemetry pilot, не полностью
успешная квалификация provider. JSONL/CSV с полными ID/units/timestamps и
пересчитанные статистики сохранены локально, хеши исходных файлов проверены.
Подробности и primary sources в [research.md](research.md).


### Подготовка R23 (автоматическая нагрузка ещё не выполнена)

Отдельный локальный prototype собран без ошибок/предупреждений. Его семь
синтетических guard checks и три проверки только на ProcessProbe прошли на
Linux и Windows: нормальный выход, timeout, отмена по синтетической температуре.
Windows cleanup во всех случаях windows-job-empty. Это проверка software stop
на собственном лёгком процессе, не проверка реального Cinebench или датчиков
под нагрузкой. Prototype и его исходные результаты остаются в ignored runs/.
Оператор отдельно запускал Cinebench вручную; балл и телеметрия этого прогона
не получены. Агент автоматическую R23-серию до остановки из-за связи не запускал.


### Первый реальный одиночный R23, 22.10.2026

После изменения оператором сценария выполнен **один** R23 MC с пределом процесса
90 s, TjMax-stop 100 °C и без power cutoff; baseline/post по 20 s. До старта
прошли 8 новых синтетических guard fixtures и normal/timeout/cancel ProcessProbe
в Windows, у всех cleanup=windows-job-empty.

Результат: **21 797.56 R23 points** из нового stdout, exit 0, процесс 43.010 s,
stderr empty, output не обрезан, cleanup windows-job-empty. HWiNFO и одноразовая
задача убраны; аппаратные настройки/защиты не менялись. Получены 87 reads /
44 уникальных polls, 7 начальных unavailable. В process window 21 poll:
CPU Package max 79 °C, Package Power max 150.508 W. Статистики process window
отделены от косвенного subset CPU Usage >=95%; точных render markers нет.

Исходные логи и HTML/CSV/JSON сохранены приватно под runs/, хеши сверены.
Это первое runtime-подтверждение конкретного R23 stdout, не квалификация всех
вариантов формата и не проверка длительной стабильности. Ограничения ANSI reader
и накопительных provider aggregates остаются; production adapter в CLI не добавлен.
