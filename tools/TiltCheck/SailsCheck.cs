// --sails SITE yyyy/MM/dd HH:mm [VOLUMES]: SAILS RESCANS IN THE ARCHIVE — measure before changing the frame model.
//
// The loop shows ONE frame per archive volume (TryExtractLowestTilt keeps the volume-START 0.5° pair), so a SAILS /
// MESO-SAILS volume's 1-3 extra 0.5° rescans never become frames (found 2026-10-08 on the Enid 2026 replay: 6 min
// between frames under MESO-SAILS ×3). Before building "one frame per 0.5° sweep", this proves on real volumes:
//   1. WHERE every cut sits — elevation number, angle, moments, first/last radial time, and its byte span in the
//      COMPRESSED file (what a range read would have to fetch);
//   2. that every 0.5° SURVEILLANCE cut has a Doppler companion (the pairing SelectLatestSweep does, orphans skipped);
//   3. that the live selector can already CUT each pair out of an archive volume: SelectLatestSweep over the records
//      up to that pair's companion must return that pair (its time, velocity complete), re-scanned independently;
//   4. the true 0.5° cadence (gaps between pairs, and across the volume boundary).
// Downloads whole volumes (~10-30 MB each). Read-only; nothing in the app changes.

using System.Text;
using System.Xml.Linq;
using Anvil.Services;

static class SailsCheck
{
    const string Bucket = "https://unidata-nexrad-level2.s3.amazonaws.com/";
    static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

    // One LDM record of an archive volume: its decompressed bytes, elevation number, and where it sat in the file.
    sealed record Rec(byte[] Block, int Elev, long FileStart, long FileEnd);

    sealed record Cut(int Index, int Number, float Angle, bool HasRef, bool HasVel, int Start, int End,
        DateTimeOffset? T0, DateTimeOffset? T1, int Radials, long FileStart, long FileEnd);

    public static async Task<int> RunAsync(string site, string day, string hhmm, int volumes)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var list = XDocument.Parse(await http.GetStringAsync(
            $"{Bucket}?list-type=2&prefix={Uri.EscapeDataString($"{day}/{site}/")}&max-keys=1000"));
        var keys = list.Descendants(S3 + "Key").Select(k => k.Value)
            .Where(k => k.EndsWith("_V06", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var want = hhmm.Replace(":", "");
        var first = keys.FindIndex(k => string.CompareOrdinal(KeyHhmm(k), want) >= 0);
        if (first < 0) { Console.WriteLine($"{site} {day}: no modern _V06 volume at/after {hhmm}Z"); return 1; }

        DateTimeOffset? prevLastPair = null;
        var cadence = new List<double>();
        for (var v = first; v < Math.Min(keys.Count, first + volumes); v++)
        {
            var raw = await http.GetByteArrayAsync(Bucket + keys[v]);
            var pairTimes = Measure(keys[v], raw, site);
            foreach (var t in pairTimes)
            {
                if (prevLastPair is { } p) cadence.Add((t - p).TotalSeconds);
                prevLastPair = t;
            }
        }
        if (cadence.Count > 0)
        {
            var s = cadence.OrderBy(x => x).ToList();
            Console.WriteLine($"\n== 0.5° CADENCE across all volumes (pair to pair, volume boundaries included): " +
                              $"median {s[s.Count / 2]:0} s, min {s[0]:0} s, max {s[^1]:0} s  (n={s.Count})");
            Console.WriteLine($"   one frame per volume today = {(cadence.Count + 1) / Math.Max(1, volumes)}× fewer 0.5° frames than scanned");
        }
        return 0;
    }

    // --sailsload SITE yyyy-MM-ddTHH:mmZ MINUTES [FOCUS]: a PastCast load through the APP'S OWN service — list the window,
    // read every volume's planned passes (GetBasePassCountsAsync), choose the frames (ReplayFramePlan), then fetch +
    // cache every frame (EnsureCachedAsync), cold and again warm. Proves frame times, sources, bytes and the one-download-
    // per-volume sharing on real data. Caches to the UNPACKAGED %LocalAppData%\Anvil\RadarLevel2 (not the app's).
    public static async Task<int> LoadAsync(string siteId, DateTimeOffset start, int minutes, DateTimeOffset? focus)
    {
        var settings = new SettingsService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsService>.Instance, Path.Combine(Path.GetTempPath(), "anvil-sailsload-settings"));
        var svc = new Level2RadarService(Microsoft.Extensions.Logging.Abstractions.NullLogger<Level2RadarService>.Instance, settings,
            new NonStandardVcpLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<NonStandardVcpLog>.Instance, Path.Combine(Path.GetTempPath(), "anvil-sailsload-vcp")));
        var site = new Anvil.Models.RadarSite(siteId, siteId, 0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var keys = await svc.GetKeysForWindowAsync(site, start, start.AddMinutes(minutes));
        var passes = await svc.GetBasePassCountsAsync(site, keys);
        Console.WriteLine($"== {siteId} {start:yyyy-MM-dd HH:mm}Z +{minutes} min: {keys.Count} volumes, passes " +
                          $"{string.Join(",", keys.Select(k => passes[k]))} ({sw.ElapsedMilliseconds} ms to list + probe)");
        var (frames, choice) = Anvil.ViewModels.ReplayFramePlan.Choose(keys, passes, minutes, focus, 40);
        Console.WriteLine($"   plan: {frames.Count} frames, {choice}");

        foreach (var pass in new[] { "cold", "warm" })
        {
            sw.Restart();
            var before = Level2RadarService.TotalBytesDownloaded;
            var vols = new Anvil.Models.RadarVolume?[frames.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, frames.Count), new ParallelOptions { MaxDegreeOfParallelism = 12 },
                async (i, ct) => vols[i] = await svc.EnsureCachedAsync(site, frames[i], null, i == 0, ct));
            Console.WriteLine($"   {pass}: {vols.Count(v => v is not null)}/{frames.Count} frames in {sw.Elapsed.TotalSeconds:0.0} s, " +
                              $"{(Level2RadarService.TotalBytesDownloaded - before) / 1e6:0.0} MB downloaded");
            if (pass == "cold")
            {
                DateTimeOffset? prev = null;
                for (var i = 0; i < frames.Count; i++)
                {
                    var v = vols[i];
                    var gap = v is not null && prev is { } p ? $"+{(v.VolumeTime - p).TotalSeconds:0}s" : "";
                    Console.WriteLine($"     {i,2} {frames[i][(frames[i].LastIndexOf('/') + 1)..],-30} " +
                                      (v is null ? "NULL ⚠️" : $"{v.VolumeTime:HH:mm:ss} {gap,6}  {v.Source,-10} {Path.GetFileName(new Uri(v.LocalUrl).AbsolutePath)}"));
                    if (v is not null) prev = v.VolumeTime;
                }
                var times = vols.Where(v => v is not null).Select(v => v!.VolumeTime).ToList();
                Console.WriteLine($"   times strictly increasing: {times.Zip(times.Skip(1)).All(t => t.Second > t.First)}");
            }
        }
        return 0;
    }

    static string KeyHhmm(string key)
    {
        var name = key[(key.LastIndexOf('/') + 1)..];   // KVNX20260424_012012_V06
        var u = name.IndexOf('_');
        return u >= 0 && name.Length >= u + 5 ? name[(u + 1)..(u + 5)] : "";
    }

    // Returns the surveillance time of every 0.5° pair in the volume, in order.
    static List<DateTimeOffset> Measure(string key, byte[] raw, string site)
    {
        var pairTimes = new List<DateTimeOffset>();
        Console.WriteLine($"\n-- {key}  ({raw.Length / 1e6:0.00} MB)");
        var header = raw[..24];
        var icao = Encoding.ASCII.GetBytes(site);
        var recs = SplitRecords(raw, ref icao);
        if (recs.Count == 0) { Console.WriteLine("   no LDM records"); return pairTimes; }

        if (!ScanPlan.TryRead(new List<(byte[], int)> { (recs[0].Block, 0) }, out var vcp, out var plan))
        {
            Console.WriteLine("   no readable Message 5");
        }
        else
        {
            var planned = plan.Where(c => c.Angle < 0.7 && c.Waveform != 2).Select(c =>
                $"#{c.Number}{(c.IsSails ? $" SAILS{c.SailsSequence}" : "")}{(c.IsMrle ? " MRLE" : "")}");
            Console.WriteLine($"   VCP {vcp}: {plan.Count} planned cuts; planned 0.5° passes (non-Doppler): {string.Join(", ", planned)}");
        }

        // Group consecutive same-elevation-NUMBER records into cuts — SelectLatestSweep's own grouping.
        var cuts = new List<Cut>();
        var firstRadial = recs.FindIndex(r => r.Elev >= 1);
        for (var i = Math.Max(0, firstRadial); i < recs.Count;)
        {
            var start = i;
            var num = recs[i].Elev;
            var angles = new List<float>();
            bool hasRef = false, hasVel = false;
            var radials = new List<RegimeCheck.Radial>();
            while (i < recs.Count && recs[i].Elev == num)
            {
                var a = Level2Format.ElevationAngleOf(recs[i].Block, icao);
                if (!float.IsNaN(a)) angles.Add(a);
                hasRef |= Level2Format.HasMoment(recs[i].Block, Level2Format.Dref);
                hasVel |= Level2Format.HasMoment(recs[i].Block, Level2Format.Dvel);
                radials.AddRange(RegimeCheck.ScanRadials(recs[i].Block, icao, i).Where(r => r.Elev == num));
                i++;
            }
            angles.Sort();
            var t = radials.Select(r => r.Time).OrderBy(x => x).ToList();
            cuts.Add(new Cut(cuts.Count, num, angles.Count > 0 ? angles[angles.Count / 2] : float.NaN, hasRef, hasVel,
                start, i, t.Count > 0 ? t[0] : null, t.Count > 0 ? t[^1] : null, radials.Count,
                recs[start].FileStart, recs[i - 1].FileEnd));
        }

        Console.WriteLine("   cut  num  angle  mom  radials  first     last      sec   file MB");
        foreach (var c in cuts)
        {
            Console.WriteLine($"   {c.Index,3}  {c.Number,3}  {(float.IsNaN(c.Angle) ? "  ?  " : c.Angle.ToString("0.00").PadLeft(5))}  " +
                              $"{(c.HasRef ? "R" : "-")}{(c.HasVel ? "V" : "-")}   {c.Radials,6}  {T(c.T0)}  {T(c.T1)}  " +
                              $"{(c.T0 is { } a && c.T1 is { } b ? (b - a).TotalSeconds : 0),4:0}   {c.FileStart / 1e6,5:0.00}-{c.FileEnd / 1e6,5:0.00}");
        }

        // 0.5° pairs: every base-angle SURVEILLANCE cut (R, no V) + the next real-angle cut if it is a same-angle V cut.
        const float tiltTol = 0.12f; // SelectLatestSweep's SAILS tolerance
        var refCuts = cuts.Where(c => c.HasRef && !float.IsNaN(c.Angle)).ToList();
        if (refCuts.Count == 0) { Console.WriteLine("   no reflectivity cuts"); return pairTimes; }
        var baseAngle = refCuts.Min(c => c.Angle);
        var surv = cuts.Where(c => c.HasRef && !c.HasVel && !float.IsNaN(c.Angle) && Math.Abs(c.Angle - baseAngle) <= tiltTol).ToList();
        var combined = surv.Count == 0; // clear-air: one combined R+V cut per base pass
        if (combined) surv = cuts.Where(c => c.HasRef && !float.IsNaN(c.Angle) && Math.Abs(c.Angle - baseAngle) <= tiltTol).ToList();
        Console.WriteLine($"   base {baseAngle:0.00}°: {surv.Count} {(combined ? "combined" : "surveillance")} pass(es)");

        var legacy = Level2Format.TryExtractLowestTilt(raw, site, out _);
        for (var p = 0; p < surv.Count; p++)
        {
            var s = surv[p];
            Cut? dop = null;
            if (!combined)
            {
                foreach (var n in cuts.Skip(s.Index + 1))
                {
                    if (float.IsNaN(n.Angle)) continue;                              // orphan filler
                    if (Math.Abs(n.Angle - baseAngle) <= tiltTol && n.HasVel) dop = n;
                    break;
                }
            }
            var endRec = (dop ?? s).End;

            // The live selector, fed the records up to this pair's end: it must return THIS pair.
            var blocks = recs.Take(endRec).Select(r => (r.Block, r.Elev)).ToList();
            var sel = Level2Format.SelectLatestSweep(header, blocks, icao);
            var outRadials = sel.data is null ? new List<RegimeCheck.Radial>()
                : RegimeCheck.ScanRadials(sel.data, icao, 0).ToList();
            var byNum = string.Join(" ", outRadials.GroupBy(r => r.Elev).OrderBy(g => g.Key).Select(g => $"#{g.Key}:{g.Count()}"));
            var timeOk = sel.dataTime is { } dt && s.T0 is { } st && Math.Abs((dt - st).TotalSeconds) < 1;
            var verdict = sel.data is null ? "NO DATA ⚠️"
                : !timeOk ? $"WRONG PAIR ⚠️ (t={T(sel.dataTime)})"
                : !sel.velComplete ? "velocity INCOMPLETE ⚠️"
                : "OK";
            var vsLegacy = p == 0 && legacy is not null
                ? $"; legacy extract {legacy.Length / 1e6:0.00} MB, radials {string.Join(" ", RegimeCheck.ScanRadials(legacy, icao, 0).GroupBy(r => r.Elev).OrderBy(g => g.Key).Select(g => $"#{g.Key}:{g.Count()}"))}"
                : "";
            Console.WriteLine($"   pair {p + 1}: surv cut {s.Index} #{s.Number} {T(s.T0)}" +
                              (combined ? " (combined)" : dop is null ? "  NO COMPANION ⚠️" : $" + dop cut {dop.Index} #{dop.Number} {T(dop.T0)}-{T(dop.T1)}") +
                              $"  file to {(dop ?? s).FileEnd / 1e6:0.00} MB | selector: {verdict}, {(sel.data?.Length ?? 0) / 1e6:0.00} MB, radials {byNum}{vsLegacy}");
            if (s.T0 is { } t0) pairTimes.Add(t0);
        }
        // THE APP'S EXTRACTOR (Level2Format.TryExtractBasePasses) vs the proofs above: pass 1 must equal today's
        // TryExtractLowestTilt bytes' radials, every pass must carry its surveillance + Doppler 720/720, and its time
        // must be the measured first radial. And the PLANNED count (Message 5, what the frame list is built from).
        var plannedPasses = Level2Format.PlannedBasePasses(new List<(byte[], int)> { (recs[0].Block, 0) });
        var passes = Level2Format.TryExtractBasePasses(raw, site);
        Console.WriteLine($"   extractor: {passes.Count} pass(es), planned {plannedPasses}{(plannedPasses == passes.Count ? "" : "  ⚠️ PLAN ≠ VOLUME")}");
        for (var p = 0; p < passes.Count; p++)
        {
            var radials = RegimeCheck.ScanRadials(passes[p].Data, icao, 0).ToList();
            var byNum = string.Join(" ", radials.GroupBy(r => r.Elev).OrderBy(g => g.Key).Select(g => $"#{g.Key}:{g.Count()}"));
            var want = p < surv.Count ? surv[p].T0 : null;
            var timeOk = passes[p].Time is { } pt && want is { } wt && Math.Abs((pt - wt).TotalSeconds) < 1;
            var same = p == 0 && legacy is not null ? (passes[p].Data.AsSpan().SequenceEqual(legacy) ? ", bytes = legacy" : ", bytes ≠ legacy")
                : "";
            Console.WriteLine($"     pass {p + 1}: {T(passes[p].Time)} {(timeOk ? "time OK" : "TIME ⚠️")}, {passes[p].Data.Length / 1e6:0.00} MB, radials {byNum}{same}");
        }

        for (var p = 1; p < pairTimes.Count; p++)
        {
            Console.Write(p == 1 ? "   gaps between pairs:" : "");
            Console.Write($" {(pairTimes[p] - pairTimes[p - 1]).TotalSeconds:0}s");
            if (p == pairTimes.Count - 1) Console.WriteLine();
        }
        return pairTimes;
    }

    static string T(DateTimeOffset? t) => t is { } x ? x.ToString("HH:mm:ss") : "   ?    ";

    // The archive file's LDM records (24-byte header, then [4-byte control word][bzip2 block] repeated), each
    // decompressed, with its elevation number — the same walk TryExtractLowestTilt does, without stopping.
    static List<Rec> SplitRecords(byte[] raw, ref byte[] icao)
    {
        var recs = new List<Rec>();
        var resolved = false;
        long pos = 24;
        while (pos + 4 <= raw.Length)
        {
            var cw = (raw[pos] << 24) | (raw[pos + 1] << 16) | (raw[pos + 2] << 8) | raw[pos + 3];
            var size = Math.Abs(cw);
            if (size <= 0 || pos + 4 + size > raw.Length) break;
            byte[] block;
            try
            {
                using var input = new MemoryStream(raw, (int)pos + 4, size, writable: false);
                using var bz = new SharpCompress.Compressors.BZip2.BZip2Stream(input,
                    SharpCompress.Compressors.CompressionMode.Decompress, false);
                using var output = new MemoryStream();
                bz.CopyTo(output);
                block = output.ToArray();
            }
            catch { break; }
            if (!resolved && Level2Format.HasMoment(block, Level2Format.Dref))
            {
                if (Level2Format.IndexOf(block, icao) < 0 && Level2Format.TryDetectIcao(block, out var real)) icao = real;
                resolved = true;
            }
            recs.Add(new Rec(block, Level2Format.ElevationOf(block, icao), pos, pos + 4 + size));
            pos += 4 + size;
        }
        return recs;
    }
}
