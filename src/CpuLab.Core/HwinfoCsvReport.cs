using System.Globalization;
using System.Net;
using System.Text;

namespace CpuLab.Core;

public static class HwinfoCsvReport
{
    public static string Render(CsvImportData data)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value ?? "unknown");
        static string N(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
        var html = new StringBuilder("""
<!doctype html><html lang="ru"><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
<title>CPU Lab — импорт CSV</title><style>
body{font:16px system-ui,sans-serif;background:#f3f6fa;color:#182b40;margin:0}
main{max-width:1100px;margin:auto;padding:32px}h1{margin-top:0}
.notice{background:#fff1c9;padding:16px;border-radius:10px}
table{border-collapse:collapse;width:100%;background:white;margin:24px 0}
th,td{text-align:left;padding:12px;border-bottom:1px solid #dce3eb;overflow-wrap:anywhere}
th{background:#e5edf6}a{color:#135fa8}.scroll{overflow-x:auto}li{margin:8px 0}
</style><main><h1>Импорт телеметрии HWiNFO CSV</h1>
""");
        html.Append($"<p class=\"notice\"><strong>{(data.Profile.EvidenceKind == "synthetic-fixture" ? "СИНТЕТИЧЕСКИЙ ПРИМЕР" : "ВНЕШНИЙ ЛОГ — ПРОИСХОЖДЕНИЕ ЗАЯВЛЕНО ПОЛЬЗОВАТЕЛЕМ")}</strong><br>");
        html.Append("Предварительный импорт. Идентификация оборудования и проверка на стенде не выполнены.</p>");
        html.Append($"<p>Строк данных: <strong>{data.Observations.Count}</strong>. Каналов: <strong>{data.Channels.Length}</strong>. ");
        html.Append($"Версия источника: {H(data.Profile.SourceVersion)}.</p>");
        html.Append($"<p>Время UTC по указанному смещению: {H(data.Observations[0].TimestampUtc.ToString("O"))} — ");
        html.Append($"{H(data.Observations[^1].TimestampUtc.ToString("O"))}.</p>");
        html.Append("<p>Здесь показаны только минимум и максимум числовых ячеек. Средняя мощность, энергия, результаты бенчмарков и границы нагрузок неизвестны.</p>");
        html.Append("<div class=\"scroll\"><table><thead><tr><th>Столбец / локальный ID</th><th>Исходное имя</th><th>Единица из заголовка</th><th>Числовых / всего</th><th>Min</th><th>Max</th></tr></thead><tbody>");
        for (int i = 0; i < data.Channels.Length; i++)
        {
            var channel = data.Channels[i];
            double[] values = data.Observations.Select(o => o.Values[i].Value).OfType<double>().ToArray();
            html.Append($"<tr><td>{channel.ColumnIndex + 1}<br>{H(channel.Id)}</td><td>{H(channel.Header)}</td><td>{H(channel.Unit)}</td>");
            html.Append($"<td>{values.Length} / {data.Observations.Count}</td><td>{(values.Length == 0 ? "unknown" : N(values.Min()))}</td>");
            html.Append($"<td>{(values.Length == 0 ? "unknown" : N(values.Max()))}</td></tr>");
        }
        html.Append("</tbody></table></div><p>Одинаковые названия остаются отдельными столбцами. ID действуют только внутри этого файла.</p><ul>");
        foreach (string warning in data.Warnings) html.Append($"<li>{H(warning)}</li>");
        html.Append("""
</ul><p><a href="source.csv">Оригинал CSV</a> · <a href="import.json">Происхождение и SHA-256</a> ·
<a href="profile.json">Профиль импорта</a> · <a href="channels.json">Каталог столбцов</a> ·
<a href="observations.jsonl">Нормализованные наблюдения</a></p></main></html>
""");
        return html.ToString();
    }
}
