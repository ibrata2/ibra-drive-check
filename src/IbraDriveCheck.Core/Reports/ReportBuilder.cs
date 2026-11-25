using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Storage;

namespace IbraDriveCheck.Core.Reports;

/// <summary>تصدير نتيجة فحص إلى CSV / JSON / HTML (الـ PDF والطباعة من الواجهة).</summary>
public static class ReportBuilder
{
    public const string Credit = "IBRA DRIVE CHECK — برمجة وتطوير ابراهيم الخزرجي — https://ibraprogram.netlify.app/";

    public static string ToJson(ScanResult r) => JsonSerializer.Serialize(r, Json.Options);

    /// <summary>صفوف (عنوان، قيمة) للملخص — تستخدم في كل الصيغ وفي الطباعة.</summary>
    public static List<(string Label, string Value)> Summary(ScanResult r, string lang)
    {
        string L(string k) => Strings.Get(lang, k);
        var d = r.Drive;
        var rows = new List<(string, string)>
        {
            (L("res.date"), r.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            (L("res.kind"), Strings.Kind(lang, r.Kind)),
            (L("res.verdict"), Strings.Verdict(lang, r.Verdict)),
            (L("res.drive"), $"{d.DisplayName} {d.Letters}".Trim()),
            (L("info.manufacturer"), Fmt.OrNA(d.Manufacturer, lang)),
            (L("info.model"), Fmt.OrNA(d.Model, lang)),
            (L("info.serial"), Fmt.OrNA(d.SerialNumber, lang)),
            (L("info.firmware"), Fmt.OrNA(d.Firmware, lang)),
            (L("info.kind"), Strings.Media(lang, d.Kind)),
            (L("info.bus"), Fmt.OrNA(d.BusType, lang)),
            (L("info.vidpid"), d.VendorId != null ? $"{d.VendorId}:{d.ProductId}" : L("common.notAvailable")),
            (L("info.fs"), Fmt.OrNA(d.FileSystems, lang)),
            (L("res.declared"), Fmt.Bytes(r.DeclaredBytes)),
        };
        if (r.VerifiedBytes is long v) rows.Add((L("res.verified"), Fmt.Bytes(v)));
        if (r.EstimatedRealBytes is long e) rows.Add((L("res.estimatedReal"), $"{Fmt.Bytes(e)} ({L("res.estimatedNote")})"));
        if (r.EstimatedFakeBytes is long f) rows.Add((L("res.estimatedFake"), $"{Fmt.Bytes(f)} ({L("res.estimatedNote")})"));
        if (r.Kind != ScanKind.Speed && r.Kind != ScanKind.Health)
        {
            rows.Add((L("res.coverage"), r.CoveragePercent.ToString("0.######", CultureInfo.InvariantCulture) + "%"));
            rows.Add((L("res.samples"), r.SampleCount.ToString(CultureInfo.InvariantCulture)));
            rows.Add((L("scan.goodCells"), (r.GoodCells + r.SlowCells).ToString(CultureInfo.InvariantCulture)));
            rows.Add((L("scan.badCells"), (r.FailedCells + r.WriteErrorCells + r.ReadErrorCells).ToString(CultureInfo.InvariantCulture)));
        }
        if (r.AvgReadMBps > 0) rows.Add((L("res.avgRead"), $"{Fmt.Speed(r.AvgReadMBps)} ({L("speed.min")} {Fmt.Speed(r.MinReadMBps)} / {L("speed.max")} {Fmt.Speed(r.MaxReadMBps)})"));
        if (r.AvgWriteMBps > 0) rows.Add((L("res.avgWrite"), $"{Fmt.Speed(r.AvgWriteMBps)} ({L("speed.min")} {Fmt.Speed(r.MinWriteMBps)} / {L("speed.max")} {Fmt.Speed(r.MaxWriteMBps)})"));
        if (r.RestoreOk is bool ok) rows.Add((L("res.restore"), ok ? L("res.restoreOk") : string.Format(L("res.restoreFail"), r.RestoreFailures)));
        rows.Add((L("res.duration"), Fmt.Duration(r.Duration)));

        if (r.Speed is SpeedResult s)
        {
            void S(string key, SpeedStat st, bool rnd)
            {
                if (!st.Measured) return;
                rows.Add((L(key), rnd
                    ? $"{Fmt.Speed(st.AvgMBps)} — {st.Iops:0} IOPS — {L("speed.latency")} {st.AvgLatencyMs:0.###} ms ({st.MinLatencyMs:0.###}–{st.MaxLatencyMs:0.###})"
                    : $"{Fmt.Speed(st.AvgMBps)} ({L("speed.min")} {Fmt.Speed(st.MinMBps)} / {L("speed.max")} {Fmt.Speed(st.MaxMBps)})"));
            }
            S("speed.seqRead", s.SeqRead, false);
            S("speed.seqWrite", s.SeqWrite, false);
            S("speed.randRead", s.RandRead4K, true);
            S("speed.randWrite", s.RandWrite4K, true);
        }

        if (r.Advice is PurchaseAdvice adv)
        {
            rows.Insert(3, (L("adv.report"), Strings.Level(lang, adv.Level)));
            foreach (var c in AdviceRows(adv, lang)) rows.Add(c);
        }

        if (r.Health is HealthInfo h)
        {
            rows.Add((L("health.status"), h.HealthStatus != null ? L("health." + h.HealthStatus) : L("common.notAvailable")));
            rows.Add((L("health.temp"), h.TemperatureC is double t ? $"{t:0} °C" : L("common.notAvailable")));
            rows.Add((L("health.hours"), h.PowerOnHours?.ToString(CultureInfo.InvariantCulture) ?? L("common.notAvailable")));
            rows.Add((L("health.predict"), h.PredictFailure is bool p ? (p ? L("common.yes") : L("common.no")) : L("common.notAvailable")));
            if (h.DataWrittenBytes is long dw) rows.Add((L("health.written"), Fmt.Bytes(dw)));
            if (h.PercentUsed is int pu) rows.Add((L("health.percentUsed"), pu + "%"));
            if (h.MediaErrors is long me) rows.Add((L("health.mediaErrors"), me.ToString(CultureInfo.InvariantCulture)));
        }
        return rows;
    }

    public static IEnumerable<(string Label, string Value)> AdviceRows(PurchaseAdvice adv, string lang)
    {
        string L(string k) => Strings.Get(lang, k);
        foreach (var c in adv.Checks)
            yield return ($"{L(c.TitleKey)}", $"[{L("check." + c.Status)}] {(c.Detail != null ? Strings.Format(lang, c.Detail) : "")}".Trim());
        if (adv.CompatibilityChecked)
        {
            foreach (var c in adv.Compatibility)
                yield return ($"{L("adv.compatTitle")} — {L(c.TitleKey)}", $"[{L("check." + c.Status)}] {(c.Detail != null ? Strings.Format(lang, c.Detail) : "")}".Trim());
            if (adv.System is SystemSnapshot sys)
                foreach (var row in SystemRows(sys, lang)) yield return ($"{L("adv.systemTitle")} — {row.Label}", row.Value);
        }
    }

    public static List<(string Label, string Value)> SystemRows(SystemSnapshot s, string lang)
    {
        string L(string k) => Strings.Get(lang, k);
        string NA = L("common.notAvailable");
        return new()
        {
            (L("sys.name"), s.ComputerName ?? NA),
            (L("sys.os"), s.Os ?? NA),
            (L("sys.cpu"), s.Cpu ?? NA),
            (L("sys.ram"), s.RamBytes is long ram ? Fmt.Bytes(ram) : NA),
            (L("sys.board"), string.Join(" ", new[] { s.BoardMaker, s.Board }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } b ? b : NA),
            (L("sys.bios"), s.Bios ?? NA),
            (L("sys.firmware"), s.FirmwareType ?? NA),
            (L("sys.link"), s.LinkGenCurrent is int g ? $"Gen{g} x{s.LinkWidthCurrent} / max Gen{s.LinkGenMax} x{s.LinkWidthMax}" : NA),
        };
    }

    public static string ToCsv(ScanResult r, string lang)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // حتى يفتح Excel العربية بشكل صحيح
        foreach (var (k, v) in Summary(r, lang)) sb.Append(Csv(k)).Append(',').Append(Csv(v)).Append("\r\n");
        foreach (var n in r.Notes) sb.Append(Csv(Strings.Get(lang, "res.notes"))).Append(',').Append(Csv(Strings.Format(lang, n))).Append("\r\n");
        sb.Append("\r\n");
        sb.Append("index,offset,offset_hex,length,state,read_mbps,write_mbps,read_latency_ms,write_latency_ms,alias_of_offset,note\r\n");
        for (int i = 0; i < r.Map.Length; i++)
        {
            var c = r.Map[i];
            sb.Append(i).Append(',').Append(c.Offset).Append(',').Append(Fmt.Hex(c.Offset)).Append(',').Append(c.Length).Append(',')
              .Append(c.State).Append(',')
              .Append(c.ReadMBps.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(c.WriteMBps.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(c.ReadLatencyMs.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(c.WriteLatencyMs.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(c.AliasOfOffset?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(Csv(c.NoteKey != null ? Strings.Get(lang, c.NoteKey) : ""))
              .Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string CellColor(CellState s) => s switch
    {
        CellState.Good => "#22C55E",
        CellState.Failed => "#EF4444",
        CellState.WriteError => "#F97316",
        CellState.ReadError => "#A855F7",
        CellState.Slow => "#EAB308",
        CellState.Testing => "#3B82F6",
        _ => "#9CA3AF",
    };

    public static string ToHtml(ScanResult r, string lang)
    {
        string L(string k) => WebUtility.HtmlEncode(Strings.Get(lang, k));
        string E(string s) => WebUtility.HtmlEncode(s);
        bool rtl = lang != "en";
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"{lang}\" dir=\"{(rtl ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\"><title>IBRA DRIVE CHECK — {L("rep.header")}</title>");
        sb.Append("<style>body{font-family:'Segoe UI',Tahoma,Arial,sans-serif;margin:32px;color:#1a1a1a}h1{color:#E60012;margin:0}h2{border-bottom:2px solid #E60012;padding-bottom:4px;margin-top:28px}" +
                  "table{border-collapse:collapse;width:100%}td{border-bottom:1px solid #eee;padding:6px 8px;vertical-align:top}td:first-child{color:#555;width:38%}" +
                  ".verdict{display:inline-block;padding:8px 16px;border-radius:10px;color:#fff;font-weight:700;margin:12px 0}" +
                  ".map{display:flex;flex-wrap:wrap;gap:2px;direction:ltr}.map i{width:9px;height:9px;border-radius:2px;display:block}" +
                  ".legend span{display:inline-flex;align-items:center;gap:6px;margin-inline-end:14px;font-size:13px}.legend i{width:12px;height:12px;border-radius:3px;display:inline-block}" +
                  "footer{margin-top:32px;color:#777;font-size:12px}</style></head><body>");
        sb.Append($"<h1>IBRA DRIVE CHECK</h1><div>{L("rep.header")}</div>");
        string vc = r.Verdict switch { Verdict.Genuine => "#16A34A", Verdict.FakeCapacity => "#E60012", Verdict.ReadWriteErrors => "#C2410C", Verdict.PoorPerformance => "#CA8A04", _ => "#6B7280" };
        sb.Append($"<div class=\"verdict\" style=\"background:{vc}\">{E(Strings.Verdict(lang, r.Verdict))}</div><div>{E(Strings.VerdictHint(lang, r.Verdict))}</div>");
        sb.Append("<h2>").Append(L("scan.result")).Append("</h2><table>");
        foreach (var (k, v) in Summary(r, lang)) sb.Append($"<tr><td>{E(k)}</td><td>{E(v)}</td></tr>");
        sb.Append("</table>");
        if (r.Notes.Count > 0)
        {
            sb.Append("<h2>").Append(L("res.notes")).Append("</h2><ul>");
            foreach (var n in r.Notes) sb.Append("<li>").Append(E(Strings.Format(lang, n))).Append("</li>");
            sb.Append("</ul>");
        }
        if (r.Map.Length > 0)
        {
            sb.Append("<h2>").Append(L("rep.map")).Append("</h2><div class=\"legend\">");
            foreach (CellState s in new[] { CellState.Good, CellState.Failed, CellState.WriteError, CellState.ReadError, CellState.Slow, CellState.Untested })
                sb.Append($"<span><i style=\"background:{CellColor(s)}\"></i>{E(Strings.Cell(lang, s))}</span>");
            sb.Append("</div><br><div class=\"map\">");
            foreach (var c in r.Map)
                sb.Append($"<i style=\"background:{CellColor(c.State)}\" title=\"{Fmt.Hex(c.Offset)} — {E(Strings.Cell(lang, c.State))}\"></i>");
            sb.Append("</div>");
        }
        sb.Append($"<footer>{L("rep.generated")} {E(Credit)}</footer></body></html>");
        return sb.ToString();
    }
}
