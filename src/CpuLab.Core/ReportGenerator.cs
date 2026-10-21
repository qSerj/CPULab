using System.Globalization;
using System.Net;
using System.Text;

namespace CpuLab.Core;

public static class ReportGenerator
{
    private static string E(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
    private static string N(double? value, string format = "0.00") => value is { } v && double.IsFinite(v)
        ? v.ToString(format, CultureInfo.InvariantCulture) : "unknown / unavailable";

    public static void Write(string directory, RunData data, RunData? previous = null)
    {
        var analysis = Analyzer.Analyze(data);
        RunStore.AtomicJson(Path.Combine(directory, "analysis.json"), analysis);
        string temporary = Path.Combine(directory, "report.html.tmp");
        File.WriteAllText(temporary, Render(data, analysis, previous), new UTF8Encoding(false));
        File.Move(temporary, Path.Combine(directory, "report.html"), true);
    }

    public static string Render(RunData data, RunAnalysis analysis, RunData? previous = null)
    {
        var html = new StringBuilder("""
<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'">
<title>CPU Lab — журнал испытаний</title><style>
:root{color-scheme:light;--ink:#162b40;--muted:#536578;--line:#dbe4ed;--accent:#176c87}
*{box-sizing:border-box}body{margin:0;background:#f3f6fa;color:var(--ink);font:16px/1.55 system-ui,sans-serif}
main{max-width:1200px;margin:auto;padding:36px 24px}h1{font-size:36px;margin:0}h2{font-size:23px;margin-top:32px}
small,.muted{color:var(--muted)}.banner{padding:16px 20px;border-left:5px solid #b56a00;background:#fff1d9;margin:22px 0;border-radius:4px}
.card{background:white;border:1px solid var(--line);border-radius:12px;padding:22px;margin:18px 0;overflow-x:auto}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px}.value{font-size:28px;font-weight:650}
table{border-collapse:collapse;width:100%;font-size:14px}th,td{padding:10px 12px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{color:var(--muted)}
code{overflow-wrap:anywhere}a{color:var(--accent)}svg{width:100%;height:auto;min-width:480px}li{margin:5px 0}.tag{display:inline-block;padding:3px 9px;border:1px solid var(--line);border-radius:20px}
.legend{display:flex;flex-wrap:wrap;gap:16px;font-size:13px}.swatch{display:inline-block;width:20px;height:3px;vertical-align:middle;margin-right:5px}
@media print{body{background:white}.card{break-inside:avoid}main{max-width:none;padding:0}}
</style></head><body><main><header><p class="muted">CPU LAB / LOCAL EXPERIMENT JOURNAL</p><h1>Журнал испытаний</h1></header>
""");
        var banner = data.Metadata.Mode switch
        {
            "mock" => ("МАКЕТ · СИНТЕТИЧЕСКИЕ ДАННЫЕ", "CPU не нагружался. Показания и баллы сгенерированы программой и не относятся к стендам A/B."),
            "process-probe" => ("ПРОВЕРКА АВТОМАТИЗАЦИИ · ТЕСТОВЫЕ ПРОЦЕССЫ", "Запущена лёгкая программа-фикстура. Времена реальные, баллы — заданные константы. Датчики и производительность CPU не измерялись."),
            "legacy/manual" => ("LEGACY / MANUAL · НЕПОЛНЫЕ ДАННЫЕ", "Только ручные итоги из ТЗ. Исходной телеметрии и точных времён прогонов нет."),
            _ => ("НЕИЗВЕСТНЫЙ РЕЖИМ", "Происхождение данных требует проверки.")
        };
        html.Append($"<div class=\"banner\"><strong>{banner.Item1}</strong><br>{banner.Item2}");
        html.Append("</div>");
        html.Append($"<p><span class=\"tag\">{E(data.Metadata.Status)}</span> · {E(data.Metadata.TimeMode)} · schema {data.Metadata.SchemaVersion}</p>");
        html.Append($"<p>Session <code>{E(data.Metadata.SessionId)}</code><br>Создана: {E(data.Metadata.CreatedUtc.ToString("O"))}</p>");
        if (data.Metadata.Reason is { } reason) html.Append($"<div class=\"banner\">{E(reason)}</div>");
        if (data.Scenario is { } scenario) html.Append($"<p>Сценарий: {E(scenario.Name)}<br>Примечание: {E(scenario.Note)}</p>");
        html.Append("<section class=\"card\"><h2>Профиль и происхождение данных</h2>");
        html.Append($"<p>{E(data.Metadata.Profile.Description)}</p><table><tr><th>Поле</th><th>Значение</th><th>Источник / статус</th></tr>");
        foreach (var entry in data.Metadata.Inventory.Fields.Concat(data.Metadata.Profile.Fields))
            html.Append($"<tr><td>{E(entry.Key)}</td><td>{E(entry.Value.Value ?? "unknown / unavailable")} {E(entry.Value.Unit)}</td><td>{E(entry.Value.Origin)} / {E(entry.Value.Status)}<br>{E(entry.Value.Source)}<br>{E(entry.Value.Reason)}</td></tr>");
        html.Append("</table></section>");
        var stats = analysis.Series;
        html.Append($"<section class=\"card\"><h2>Результаты серии</h2><div class=\"grid\"><div>Завершено<div class=\"value\">{stats.ValidCount}/{stats.AttemptCount}</div></div><div>Средний балл<div class=\"value\">{N(stats.Mean)}</div></div><div>Медиана<div class=\"value\">{N(stats.Median)}</div></div><div>CV, %<div class=\"value\">{N(stats.CoefficientOfVariation * 100)}</div></div></div>");
        html.Append($"<p class=\"muted\">min / max: {N(stats.Min)} / {N(stats.Max)} · sample SD: {N(stats.StandardDeviation)}. Единицы указаны для каждой попытки; fixture_points и synthetic_points — тестовые константы.</p>");
        html.Append("<table><tr><th>Прогон</th><th>Статус / причина</th><th>Балл</th><th>Начало / конец UTC</th><th>Окно / telemetry</th></tr>");
        foreach (var a in data.Attempts)
        {
            html.Append($"<tr><td>{E(a.Id)}</td><td>{E(a.Status)}<br>{E(a.Reason)}</td><td>{N(a.Score)}<br>{E(a.ScoreUnit)}</td><td>{E(a.StartUtc.ToString("O"))}<br>{E(a.EndUtc?.ToString("O") ?? "unknown")}</td><td>{E(a.WindowOrigin)}<br><code>{E(a.TelemetryFile)}</code>");
            if (a.ProcessResultDirectory is { } process)
                html.Append($"<br><a href=\"./{E(process)}/process-result.json\">Процесс</a> · <a href=\"./{E(process)}/stdout.bin\">stdout</a> · <a href=\"./{E(process)}/stderr.bin\">stderr</a>");
            html.Append("</td></tr>");
        }
        html.Append("</table></section>");

        if (data.Frames.Count > 0)
        {
            html.Append("<h2>Временные ряды</h2><p class=\"muted\">Ось X: секунды времени эксперимента. Заливка: окна попыток; точки: конец интервала. Пропуски не соединены.</p>");
            html.Append(Chart(data, "Package Power", ["package_power"], "W"));
            html.Append(Chart(data, "Температура CPU", ["temperature"], "°C"));
            html.Append(Chart(data, "Core Clock / Effective Clock · P/E", ["core_clock", "effective_clock"], "MHz"));
        }
        foreach (var a in analysis.Attempts)
        {
            html.Append($"<section class=\"card\"><h2>{E(a.Attempt.Id)} · качество и агрегаты</h2><ul>");
            foreach (string warning in a.Warnings) html.Append($"<li>{E(warning)}</li>");
            if (a.Metrics.Length == 0)
            {
                html.Append("<li>Телеметрия unavailable: датчики не подключены, средняя мощность и энергия неизвестны.</li></ul></section>");
                continue;
            }
            html.Append("</ul><table><tr><th>Канал</th><th>Среднее по времени</th><th>Медиана по времени</th><th>Observed max</th><th>Coverage</th><th>Причина / энергия</th></tr>");
            foreach (var metric in a.Metrics)
                html.Append($"<tr><td>{E(metric.SensorId)}<br>{E(metric.Unit)}</td><td>{N(metric.Mean)}</td><td>{N(metric.Median)}</td><td>{N(metric.Max)}</td><td>{N(metric.Coverage * 100)}%</td><td>{E(metric.Reason)}{(metric.EnergyJ is null ? "" : "<br>Интеграл по покрытым интервалам: " + N(metric.EnergyJ) + " J")}</td></tr>");
            html.Append("</table></section>");
        }
        html.Append("<section class=\"card\"><h2>Ограничения</h2><ul><li>VID, физический Vcore, thermal/power/current/CEP flags: unavailable — живой провайдер не подключён.</li><li>CEP не диагностирован. Частота Core Clock не заменяет Effective Clock.</li><li>Points / mean W не вычисляется для синтетических или ручных неполных данных.</li>");
        foreach (var warning in analysis.Warnings) html.Append($"<li>{E(warning)}</li>");
        html.Append("</ul></section>");
        if (previous is not null)
        {
            var comparison = Analyzer.Compare(data, previous);
            html.Append($"<section class=\"card\"><h2>Сравнение с сессией {E(previous.Metadata.SessionId)}</h2><p>{E(comparison.Reason)}</p><p>Изменение среднего балла: {N(comparison.ScoreChangePercent)}%</p></section>");
        }
        if (data.Legacy is { } legacy)
        {
            html.Append("<section class=\"card\"><h2>Исторические ручные результаты · стенд A</h2><p>legacy/manual · Cinebench R23 MC · нет raw, точной даты и средней мощности; погрешность неизвестна. С синтетическими баллами не сравниваются.</p><table><tr><th>AC/DC, mΩ</th><th>R23 points</th><th>Peak Package W</th><th>Max °C</th></tr>");
            foreach (var row in legacy.Entries)
                html.Append($"<tr><td>{N(row.AcMohm)} / {N(row.DcMohm)}</td><td>{N(row.Score, "0")}</td><td>{N(row.PeakPackagePowerW)}</td><td>{N(row.MaxTemperatureC, "0")}</td></tr>");
            html.Append("</table><p>Источник: <code>legacy.json</code>. Происхождение: user; сырая телеметрия unavailable.</p></section>");
        }
        if (data.Metadata.Mode == "process-probe")
            html.Append("<p><a href=\"probe-scenario.json\">Сценарий серии</a> · <a href=\"probe-provenance.json\">Компоненты и SHA-256</a></p>");
        html.Append("<footer><p>Артефакты: <a href=\"metadata.json\">metadata.json</a> · <a href=\"benchmarks.json\">benchmarks.json</a> · <a href=\"samples.csv\">samples.csv</a> · <a href=\"events.jsonl\">events.jsonl</a> · <a href=\"analysis.json\">analysis.json</a></p><p class=\"muted\">CPU Lab 0.1 · автономный HTML, внешние ресурсы не требуются.</p></footer></main></body></html>");
        return html.ToString();
    }

    private static string Chart(RunData data, string title, string[] metrics, string unit)
    {
        string[] palette = ["#176c87", "#ca6b24", "#7656a3", "#389466"];
        var selected = data.Sensors.Where(s => metrics.Contains(s.Metric)).ToArray();
        var values = data.Frames.SelectMany(f => f.Samples).Where(s => selected.Any(d => d.Id == s.SensorId)
            && s.Validity == "valid" && s.Value.HasValue && double.IsFinite(s.Value.Value)).ToArray();
        if (values.Length == 0) return $"<section class=\"card\"><h3>{E(title)}</h3><p>unknown / unavailable — no valid samples</p></section>";
        DateTimeOffset start = data.Frames.Min(f => f.Samples.Min(s => s.IntervalStartUtc));
        double duration = Math.Max(1, (data.Frames.Max(f => f.ReceivedUtc) - start).TotalSeconds);
        double min = Math.Min(0, values.Min(s => s.Value!.Value)), max = Math.Max(min + 1, values.Max(s => s.Value!.Value) * 1.08);
        double X(DateTimeOffset t) => 65 + (t - start).TotalSeconds / duration * 865;
        double Y(double v) => 230 - (v - min) / (max - min) * 205;
        string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        var svg = new StringBuilder($"<section class=\"card\"><h3>{E(title)}</h3><svg viewBox=\"0 0 960 270\" role=\"img\" aria-label=\"{E(title)}\">");
        foreach (var a in data.Attempts)
        {
            DateTimeOffset end = a.EndUtc ?? a.LastObservedUtc ?? a.StartUtc;
            svg.Append($"<rect x=\"{F(X(a.StartUtc))}\" y=\"25\" width=\"{F(Math.Max(0, X(end) - X(a.StartUtc)))}\" height=\"205\" fill=\"#edf3f8\"><title>{E(a.Id)} / {E(a.Status)}</title></rect>");
        }
        for (int step = 0; step <= 4; step++)
        {
            double v = min + (max - min) * step / 4;
            svg.Append($"<line x1=\"65\" x2=\"930\" y1=\"{F(Y(v))}\" y2=\"{F(Y(v))}\" stroke=\"#dbe4ed\"/><text x=\"4\" y=\"{F(Y(v) + 4)}\" fill=\"#536578\" font-size=\"12\">{F(v)}</text>");
        }
        for (int step = 0; step <= 4; step++)
            svg.Append($"<text x=\"{F(65 + 865.0 * step / 4)}\" y=\"252\" text-anchor=\"middle\" fill=\"#536578\" font-size=\"12\">{F(duration * step / 4)} s</text>");
        svg.Append($"<text x=\"5\" y=\"16\" fill=\"#536578\" font-size=\"12\">{E(unit)}</text>");
        for (int i = 0; i < selected.Length; i++)
        {
            string color = palette[i % palette.Length];
            var points = data.Frames.SelectMany(f => f.Samples).Where(s => s.SensorId == selected[i].Id).OrderBy(s => s.IntervalStartUtc);
            var segment = new List<string>();
            DateTimeOffset? lastEnd = null;
            void Flush()
            {
                if (segment.Count > 0) svg.Append($"<polyline points=\"{string.Join(' ', segment)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2.2\"/>");
                segment.Clear();
            }
            foreach (var sample in points)
            {
                if (sample.Validity != "valid" || sample.Value is null || !double.IsFinite(sample.Value.Value)) { Flush(); lastEnd = null; continue; }
                if (lastEnd is { } e && sample.IntervalStartUtc != e) Flush();
                segment.Add($"{F(X(sample.IntervalEndUtc))},{F(Y(sample.Value.Value))}");
                svg.Append($"<circle cx=\"{F(X(sample.IntervalEndUtc))}\" cy=\"{F(Y(sample.Value.Value))}\" r=\"2\" fill=\"{color}\"><title>{E(selected[i].Id)}: {N(sample.Value)} {E(unit)}</title></circle>");
                lastEnd = sample.IntervalEndUtc;
            }
            Flush();
        }
        svg.Append("</svg><div class=\"legend\">");
        for (int i = 0; i < selected.Length; i++) svg.Append($"<span><span class=\"swatch\" style=\"background:{palette[i % palette.Length]}\"></span>{E(selected[i].Id)}</span>");
        return svg.Append("</div></section>").ToString();
    }
}
