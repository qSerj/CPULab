# Проверки 20.10.2026

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
