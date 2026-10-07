// TiltCheck — a throwaway harness that exercises the REAL Level2Format tilt extraction against a real
// NEXRAD volume from the same AWS bucket the app uses.
//
// Why: TryExtractTiltByAngle is a byte-format change, and a byte-mapped format can't be eyeballed for
// correctness. This re-scans the EXTRACTED output independently (by hunting Message-31 radial header
// signatures) rather than trusting the extractor's own bookkeeping, and reports, per designed tilt:
// how many radials came out, what elevation angles they actually carry, the azimuth coverage, and
// whether both halves of the split cut (DREF surveillance + DVEL Doppler) survived.
//
// Usage: dotnet run -- KTLX [2013/05/20] [20:20]

using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Anvil.Services;

// --gzprefix KEY [KEY…]: how much of a LEGACY .gz archive volume the base tilt actually needs. Downloads each key
// whole, then finds the smallest COMPRESSED prefix whose gunzipped bytes already hold a COMPLETE lowest tilt (the
// same TryExtractLowestTilt / …Uncompressed the app runs), and checks that tilt is byte-identical to the one cut
// from the whole file. The measurement behind "stream the .gz and stop early" (2026-10-02 load-time seeding).
if (args.Length > 0 && args[0] == "--gzprefix")
{
    using var gzHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    foreach (var gzKey in args.Skip(1))
    {
        var gzSite = gzKey.Split('/')[3];
        var gz = await gzHttp.GetByteArrayAsync("https://unidata-nexrad-level2.s3.amazonaws.com/" + gzKey);
        var full = Gunzip(gz, gz.Length);
        var (wholeTilt, walker) = Extract(full, gzSite);
        if (wholeTilt is null) { Console.WriteLine($"{gzKey}: no base tilt from the WHOLE file — skipped"); continue; }

        // Smallest compressed prefix (64 KB steps) whose decompressed bytes give a complete, identical tilt.
        long need = -1;
        for (var n = 64 * 1024; n < gz.Length + 64 * 1024; n += 64 * 1024)
        {
            var cut = Math.Min(n, gz.Length);
            var part = Gunzip(gz, cut);
            var (tilt, _, complete) = ExtractPrefix(part, gzSite, walker);
            if (complete && tilt is not null && tilt.AsSpan().SequenceEqual(wholeTilt)) { need = cut; break; }
        }
        Console.WriteLine($"{gzKey}: gz {gz.Length / 1e6:0.00} MB → {full.Length / 1e6:0.00} MB unzipped ({walker}); " +
            (need < 0 ? "NO prefix reproduced the tilt" : $"tilt complete + identical after {need / 1e6:0.00} MB = {100.0 * need / gz.Length:0}% of the download"));

        // The SHIPPING path (Level2RadarService.StreamGzBaseTiltAsync) over the same bytes: what it stops at and
        // whether its tilt is the whole file's.
        long streamed = 0;
        var (st, back) = await Level2RadarService.StreamGzBaseTiltAsync(new MemoryStream(gz), gzSite,
            (n, _) => { streamed += n; return Task.CompletedTask; }, false, CancellationToken.None);
        Console.WriteLine(st is not null
            ? $"   streamed: stopped after {streamed / 1e6:0.00} MB ({100.0 * streamed / gz.Length:0}%), tilt {(st.AsSpan().SequenceEqual(wholeTilt) ? "IDENTICAL" : "DIFFERENT ⚠️")}"
            : $"   streamed: no complete tilt — handed back the whole file ({back?.Length / 1e6:0.00} MB)");
    }
    return 0;

    // Gunzip the first `count` bytes; a truncated stream yields what it decoded before the cut.
    static byte[] Gunzip(byte[] gz, int count)
    {
        using var input = new MemoryStream(gz, 0, count);
        using var z = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buf = new byte[81920];
        try { int r; while ((r = z.Read(buf, 0, buf.Length)) > 0) output.Write(buf, 0, r); }
        catch (Exception) { /* truncated: keep what decoded */ }
        return output.ToArray();
    }

    static (byte[]? Tilt, string Walker) Extract(byte[] data, string site)
    {
        var t = Level2Format.TryExtractLowestTilt(data, site, out _);
        return t is not null ? (t, "ldm") : (Level2Format.TryExtractLowestTiltUncompressed(data, site, out _), "uncompressed");
    }

    static (byte[]? Tilt, string Walker, bool Complete) ExtractPrefix(byte[] data, string site, string walker)
    {
        bool complete;
        var t = walker == "ldm"
            ? Level2Format.TryExtractLowestTilt(data, site, out complete)
            : Level2Format.TryExtractLowestTiltUncompressed(data, site, out complete);
        return (t, walker, complete);
    }
}

// --uptime SITE [SITE…]: the data-uptime report (RadarUptimeService) against the LIVE archive, into a temp
// cache — listing only, no downloads. The real-data check for UptimeCalculator's gap/down rules.
if (args.Length > 0 && args[0] == "--uptime")
{
    var cache = Path.Combine(Path.GetTempPath(), "anvil-uptimecheck");
    var svc = new RadarUptimeService(new ArchiveVolumeLister(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarUptimeService>.Instance, cache, null, null);
    foreach (var id in args.Skip(1).DefaultIfEmpty("KTLX"))
    {
        var r = await svc.GetReportAsync(id);
        Console.WriteLine($"== {r.SiteId}  up {r.UpFraction:P2}  ({r.WindowStartUtc:u} → {r.WindowEndUtc:u})");
        Console.WriteLine("   days: " + string.Join(" ", r.Days.Select(d => d.UpFraction is { } f ? (f >= 0.995 ? "█" : f >= 0.9 ? "▓" : "░") : "?")));
        foreach (var h in r.Holes.Take(12))
        {
            Console.WriteLine($"   {h.Kind,-4} {h.StartUtc.ToLocalTime():MMM dd HH:mm} → {h.EndUtc.ToLocalTime():MMM dd HH:mm}  {h.Duration.TotalMinutes,6:0} min{(h.Ongoing ? "  ONGOING" : "")}");
        }
        if (r.Holes.Count > 12) Console.WriteLine($"   … {r.Holes.Count - 12} more");
    }
    return 0;
}

// --vcp SITE: the hourly scan-pattern sample (RadarScanPatternService) against the live archive — ~720
// 8 KB range reads the first time; the real-data check for ReadVcpAsync's prefix parse.
if (args.Length > 0 && args[0] == "--vcp")
{
    var svc = new RadarScanPatternService(new ArchiveVolumeLister(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarScanPatternService>.Instance,
        Path.Combine(Path.GetTempPath(), "anvil-vcpcheck"), null);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var r = await svc.GetReportAsync(args.Length > 1 ? args[1] : "KTLX");
    Console.WriteLine($"== {r.SiteId}  {r.SampledHours} sampled hours over {r.DaysDone} days in {sw.Elapsed.TotalSeconds:0} s");
    foreach (var s in r.Shares) Console.WriteLine($"   VCP {s.Vcp,-4} {s.Hours,4} h  {100.0 * s.Hours / Math.Max(1, r.SampledHours),5:0.0}%");
    return 0;
}

// --messages SITE [SITE…]: the IEM FTM history (RadarMessageHistoryService) for every bundled site, printed
// for the named ones — the real-data check for the month-scoped dates and the 3-letter site resolution.
if (args.Length > 0 && args[0] == "--messages")
{
    var assets = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Anvil.App", "Assets", "Radar");
    var sites = new List<Anvil.Models.RadarSite>();
    foreach (var (file, cls) in new[] { ("radar-sites.json", Anvil.Models.RadarSiteClass.Operational),
                                        ("tdwr-sites.json", Anvil.Models.RadarSiteClass.Tdwr),
                                        ("research-radar-sites.json", Anvil.Models.RadarSiteClass.Research) })
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, file)));
        foreach (var e in doc.RootElement.EnumerateArray())
            sites.Add(new Anvil.Models.RadarSite(e.GetProperty("id").GetString()!, "", 0, 0, cls));
    }
    var svc = new RadarMessageHistoryService(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarMessageHistoryService>.Instance,
        null, Path.Combine(Path.GetTempPath(), "anvil-messagecheck"), null);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var all = await svc.GetHistoryAsync(sites);
    Console.WriteLine($"{all.Count} messages for {all.Select(m => m.SiteId).Distinct().Count()} of {sites.Count} sites in {sw.ElapsedMilliseconds} ms");
    foreach (var id in args.Skip(1).DefaultIfEmpty("KTLX").Select(a => a.ToUpperInvariant()))
    {
        Console.WriteLine($"== {id}");
        foreach (var m in all.Where(m => m.SiteId == id))
        {
            var text = m.Message.Text.Replace('\n', ' ');
            Console.WriteLine($"   {m.Message.IssuedUtc.ToLocalTime():MMM dd HH:mm} {m.Message.Office}  {text[..Math.Min(90, text.Length)]}");
        }
    }
    return 0;
}

// --supp SITE yyyy/MM/dd [SITE yyyy/MM/dd …]: every volume of each site-day, metadata record ONLY (8 KB range read,
// raw keys — .gz has no readable prefix), tallied by what Message 5 says about SAILS / MRLE / MPDA / base tilt and
// by which tilts its table actually repeats. The research behind the "Extra sweeps" row (2026-10-07): does the
// supplemental word match the table, and what does an MRLE volume look like?
if (args.Length > 0 && args[0] == "--supp")
{
    using var suppHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    for (var a = 1; a + 1 < args.Length; a += 2)
    {
        var sSite = args[a].ToUpperInvariant();
        var sDay = args[a + 1];
        var listXml = XDocument.Parse(await suppHttp.GetStringAsync(
            $"{Level2RadarService.BucketBase}?list-type=2&prefix={Uri.EscapeDataString($"{sDay}/{sSite}/")}&max-keys=1000"));
        var sKeys = listXml.Descendants(ns + "Key").Select(k => k.Value)
            .Where(k => k.EndsWith("_V06", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var tally = new SortedDictionary<string, (int n, string first, string last)>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(sKeys, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (k, ct) =>
        {
            string sig;
            try { sig = await SuppSignatureAsync(suppHttp, k, ct); }
            catch (Exception e) { sig = "ERR " + e.GetType().Name; }
            var hhmm = k[(k.LastIndexOf('_') - 6)..(k.LastIndexOf('_') - 2)];
            lock (tally)
            {
                tally[sig] = tally.TryGetValue(sig, out var t)
                    ? (t.n + 1, string.CompareOrdinal(hhmm, t.first) < 0 ? hhmm : t.first, string.CompareOrdinal(hhmm, t.last) > 0 ? hhmm : t.last)
                    : (1, hhmm, hhmm);
            }
        });
        Console.WriteLine($"== {sSite} {sDay}: {sKeys.Count} raw volumes");
        foreach (var (sig, t) in tally) Console.WriteLine($"   {t.n,4}× {t.first}-{t.last}Z  {sig}");
    }
    return 0;

    static async Task<string> SuppSignatureAsync(HttpClient http, string key, CancellationToken ct)
    {
        async Task<byte[]> Range(int bytes)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Level2RadarService.BucketBase + key);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, bytes - 1);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        var prefix = await Range(8 * 1024);
        var recordBytes = Math.Abs((prefix[24] << 24) | (prefix[25] << 16) | (prefix[26] << 8) | prefix[27]);
        if (recordBytes is <= 0 or > 512 * 1024) return "no metadata record";
        if (28 + recordBytes > prefix.Length) prefix = await Range(28 + recordBytes);
        var meta = Level2RadarService.DecompressChunk(prefix, isS: true);
        if (meta is null) return "metadata didn't decompress";
        var blocks = new List<(byte[] block, int elev)> { (meta, 0) };
        var build = Level2Format.ReadRdaBuildFromMetadata(blocks);
        for (var pos = 0; pos + 12 + 16 + 22 <= meta.Length; pos += 2432)
        {
            var type = meta[pos + 12 + 3];
            if (type == 31) break;
            if (type is not (5 or 7)) continue;
            var body = pos + 12 + 16;
            int Hw(int off) => (meta[off] << 8) | meta[off + 1];
            var vcp = Hw(body + 4);
            var supp = Hw(body + 18);
            var n = Hw(body + 6);
            // Per-cut: angle, waveform, E14 word. Repeats = a CS (w1) or batch/contiguous angle seen more than once.
            var cuts = new List<(double angle, int wf, int e14)>();
            for (var k = 0; k < n && body + 22 + k * 46 + 30 <= meta.Length; k++)
            {
                var c = body + 22 + k * 46;
                cuts.Add(((short)Hw(c) / 8.0 * 0.043945, meta[c + 3], Hw(c + 28))); // E15 = halfword 15 of the cut
            }
            // A tilt's "passes" = its non-Doppler cuts (w1 CS, w3 CDWO used alone, w4 batch); CD (w2) pairs with a CS.
            var repeats = cuts.Where(c => c.wf != 2)
                .GroupBy(c => Math.Round(c.angle * 10) / 10)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key:0.0}°×{g.Count()}");
            var flagged = cuts.Where(c => c.e14 != 0).Select(c => $"{c.angle:0.0}w{c.wf}:{c.e14:X}");
            return $"b{build:0.0} VCP {vcp} supp={supp:X4} [{DecodeSupp(supp)}]  repeats: {string.Join(",", repeats.DefaultIfEmpty("none"))}" +
                   $"  E15: {string.Join(" ", flagged.DefaultIfEmpty("all 0"))}";
        }
        return $"b{build:0.0} no Message 5";
    }

    static string DecodeSupp(int w) => w == 0 ? "0" : string.Join(" ", new[]
    {
        (w & 1) != 0 ? $"SAILS n={(w >> 1) & 7}" : null,
        (w & 0x10) != 0 ? $"MRLE n={(w >> 5) & 7}" : null,
        (w & 0x400) != 0 ? "MPDAcutsAdded" : null,
        (w & 0x800) != 0 ? "MPDA" : null,
        (w & 0x1000) != 0 ? $"BASETILT n={(w >> 13) & 7}" : null,
    }.Where(s => s is not null));
}

var positional = args.TakeWhile(a => !a.StartsWith("--")).ToArray();
var site = positional.Length > 0 ? positional[0].ToUpperInvariant() : "KTLX";
var day = positional.Length > 1 ? positional[1] : DateTime.UtcNow.ToString("yyyy/MM/dd");
var atTime = positional.Length > 2 ? positional[2] : null;
// --out <path>: write the extracted base tilt to disk. This is how a volume is MINTED FOR THE CORPUS
// (Anvil.App/Assets/RadarCorpus/) — the corpus holds our own single-tilt extractions, not raw archive
// volumes, so it has to come out of this exact code path or it would not be the bytes the app decodes.
var outPath = Array.FindIndex(args, a => a == "--out") is var oi && oi >= 0 && oi + 1 < args.Length
    ? args[oi + 1]
    : null;

var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
const string bucket = "https://unidata-nexrad-level2.s3.amazonaws.com/";
XNamespace s3 = "http://s3.amazonaws.com/doc/2006-03-01/";

Console.WriteLine($"== TiltCheck {site} {day} ==");

// ---- find a volume key -----------------------------------------------------------------------
var listUrl = $"{bucket}?list-type=2&prefix={Uri.EscapeDataString($"{day}/{site}/")}&max-keys=1000";
var xml = XDocument.Parse(await http.GetStringAsync(listUrl));
// Historical keys are gzip-wrapped ("..._V06.gz"); modern ones are raw. Both decode to the same AR2V.
var keys = xml.Descendants(s3 + "Key").Select(k => k.Value)
    .Where(k => k.EndsWith("_V06", StringComparison.Ordinal) || k.EndsWith("_V06.gz", StringComparison.Ordinal))
    .OrderBy(k => k, StringComparer.Ordinal)
    .ToList();

if (keys.Count == 0)
{
    Console.WriteLine("no _V06 keys found for that site/day");
    return 1;
}

// Volumes land on arbitrary seconds, so match the time as a PREFIX of the key's time field
// ("18" = the first volume of hour 18) rather than an exact string.
var key = keys[^1];
if (atTime is not null)
{
    var want = "_" + atTime.Replace(":", "");
    key = keys.FirstOrDefault(k => k[(k.LastIndexOf('/') + 1)..].Contains(want, StringComparison.Ordinal)) ?? keys[^1];
}

Console.WriteLine($"volume: {key}  ({keys.Count} keys that day)");

var raw = await http.GetByteArrayAsync(bucket + key);
Console.WriteLine($"downloaded: {raw.Length / (1024.0 * 1024.0):0.0} MB");
if (key.EndsWith(".gz", StringComparison.Ordinal))
{
    using var gzIn = new MemoryStream(raw);
    using var gz = new System.IO.Compression.GZipStream(gzIn, System.IO.Compression.CompressionMode.Decompress);
    using var gzOut = new MemoryStream();
    gz.CopyTo(gzOut);
    raw = gzOut.ToArray();
    Console.WriteLine($"gunzipped:  {raw.Length / (1024.0 * 1024.0):0.0} MB");
}

// ---- the base tilt: the path that already shipped, as a control ------------------------------
var baseTilt = Level2Format.TryExtractLowestTilt(raw, site, out var baseComplete)
    ?? Level2Format.TryExtractLowestTiltUncompressed(raw, site, out baseComplete); // legacy .gz, as the app does
if (baseTilt is null)
{
    Console.WriteLine("FAIL: base-tilt extraction returned null");
    return 1;
}
Console.WriteLine($"base tilt (existing path): {baseTilt.Length / (1024.0 * 1024.0):0.00} MB, complete={baseComplete}");
if (outPath is not null)
{
    File.WriteAllBytes(outPath, baseTilt);
    Console.WriteLine($"wrote {outPath} ({baseTilt.Length:N0} bytes)");
}

// ---- the designed tilt table, read from the base tilt's carried metadata ---------------------
var failuresEarly = 0;
var (vcpNum, sweepsNum) = Level2Format.ReadModeFromExtractedTilt(baseTilt);
Console.WriteLine($"VCP (ReadModeFromExtractedTilt): {vcpNum}  sweeps={sweepsNum}  known={Level2Format.IsKnownVcp(vcpNum)}");
Console.WriteLine($"RDA build (Message 2): {Level2Format.ReadRdaBuildFromExtractedTilt(baseTilt)}");
DumpMessage5Supplemental(baseTilt);
var appMode = Level2Format.DescribeMode(vcpNum, sweepsNum,
    Level2Format.SailsExisted(Level2Format.ReadRdaBuildFromExtractedTilt(baseTilt), DateTimeOffset.UtcNow),
    Level2Format.ReadMrleTiltsFromExtractedTilt(baseTilt));
Console.WriteLine($"app mode line: \"{appMode}\"  -> scan row \"{Anvil.ViewModels.RadarViewModel.ScanStrategyText(appMode)}\"");

// Message 5's RPG-use words (ICD 2620002Y Table XI): header halfword 9 = VCP sequencing, 10 = VCP supplemental
// (SAILS bit 0 + count bits 1-3, MRLE bit 4 + count bits 5-7, MPDA bit 11, base tilt bit 12 + count 13-15), and
// each cut's E15 supplemental word (SAILS cut bit 0 + seq 1-3, MRLE cut bit 4 + seq 5-7, MPDA cut 9, base tilt 10).
// The ICD notes these are byte-swapped by the RDA; archive volumes read correctly big-endian (--supp, 2026-10-07),
// the swapped order is printed only as a cross-check.
static void DumpMessage5Supplemental(byte[] tilt)
{
    var block = tilt[24..];
    for (var pos = 0; pos + 12 + 16 + 22 <= block.Length; pos += 2432)
    {
        var type = block[pos + 12 + 3];
        if (type == 31) break;
        if (type is not (5 or 7)) continue;
        var body = pos + 12 + 16;
        int Hw(int off) => (block[off] << 8) | block[off + 1];
        int Sw(int off) => (block[off + 1] << 8) | block[off];
        Console.WriteLine($"Msg5 hw9 seq={Hw(body + 16):X4} (swapped {Sw(body + 16):X4})  hw10 supp={Hw(body + 18):X4} (swapped {Sw(body + 18):X4})");
        var n = (block[body + 6] << 8) | block[body + 7];
        var cuts = new List<string>();
        for (var k = 0; k < n && body + 22 + k * 46 + 30 <= block.Length; k++)
        {
            var c = body + 22 + k * 46;
            var angle = (short)Hw(c) / 8.0 * 0.043945;
            cuts.Add($"{angle:0.00}/w{block[c + 3]}:{Hw(c + 28):X4}|{Sw(c + 28):X4}");
        }
        Console.WriteLine("Msg5 cut E15 (be|swapped): " + string.Join(" ", cuts));
        return;
    }
}

// Raw Message 5 header fields, straight from the table parse, to see whether the designed table we
// read actually belongs to the VCP the volume reports.
if (Level2Format.TryReadElevationTable(
        new List<(byte[] block, int elev)> { (baseTilt[24..], 0) }, out var rawCuts))
{
    Console.WriteLine($"Msg5 elevation table: {rawCuts.Count} cuts -> " +
        string.Join(", ", rawCuts.Select(c => $"{c.angle:0.00}/w{c.waveform}")));
}

// The Scan/Tilt readout rows split DescribeMode's string at the "0.5°" sweep token: everything before
// it is the VOLUME's scan strategy (Scan row), and the Tilt row shows the rendered elevation instead.
// SAILS must land on the LEFT of that split — it counts re-scans of the BASE tilt, so it's true
// whichever tilt is on screen. It used to sit right of the token, which meant selecting 0.9° silently
// dropped it from the UI. Mirrors RadarControls.xaml.cs's SweepIndex.
// Checked for this volume's real sweep count AND a synthetic SAILS ×3, since whether a given volume
// happens to be running SAILS is luck of the draw and the SAILS branch is the one that regressed.
foreach (var sw in new[] { sweepsNum > 0 ? sweepsNum : 1, 3 })
{
    var modeString = Level2Format.DescribeMode(vcpNum, sw);
    var splitAt = modeString.IndexOf("0.5°", StringComparison.Ordinal);
    var scanRow = splitAt > 0 ? modeString[..splitAt].TrimEnd(' ', '·') : modeString;
    Console.WriteLine($"mode (sweeps={sw}) : \"{modeString}\"");
    Console.WriteLine($"     -> Scan row : \"{scanRow}\"   Tilt row : the selected elevation");
    if (sw > 1 && !scanRow.Contains("SAILS", StringComparison.Ordinal))
    {
        Console.WriteLine("     FAIL: SAILS falls right of the split -> it vanishes on any non-base tilt");
        failuresEarly++;
    }
}

var angles = Level2Format.ReadElevationAnglesFromExtractedTilt(baseTilt);
Console.WriteLine($"VCP tilt table ({angles.Count} distinct): {string.Join(", ", angles.Select(a => $"{a:0.0}°"))}");
if (angles.Count == 0)
{
    Console.WriteLine("FAIL: no elevation table parsed from the base tilt's metadata");
    return 1;
}

// ---- GROUND TRUTH: what cuts does the volume ACTUALLY contain? -------------------------------
// The designed table (Message 5) says what the VCP intends to scan; the antenna reports something
// slightly different per radial. Matching designed->actual is the whole job, so print both.
Console.WriteLine();
Console.WriteLine("actual cuts in the volume (grouped by elevation number):");
Console.WriteLine($"{"num",4} {"records",8} {"min",6} {"median",7} {"max",6}  REF  VEL");
var actualCuts = ScanCuts(raw, site);
foreach (var c in actualCuts)
{
    Console.WriteLine($"{c.num,4} {c.count,8} {c.min,6:0.00} {c.median,7:0.00} {c.max,6:0.00}  " +
        $"{(c.hasRef ? "yes" : "-"),3}  {(c.hasVel ? "yes" : "-"),3}");
}

// ---- per-tilt extraction + INDEPENDENT verification ------------------------------------------
Console.WriteLine();
Console.WriteLine($"{"tilt",6} {"MB",6} {"radials",8} {"angles found",16} {"az span",8} {"gaps",5} {"REF",4} {"VEL",4}  verdict");
Console.WriteLine(new string('-', 86));

var failures = failuresEarly;
for (var i = 0; i < angles.Count; i++)
{
    var target = angles[i];
    // Tilt 0 goes through the base path (null angle) exactly as the app routes it.
    var buf = i == 0
        ? Level2Format.TryExtractLowestTilt(raw, site)
        : Level2Format.TryExtractTiltByAngle(raw, site, target, out _);

    if (buf is null)
    {
        // NOT necessarily a bug: a VCP's designed table can promise tilts its volumes don't contain
        // (KTLX VCP 212 designs 17 cuts to 19.5° but ships 12, topping at 6.4°). Cross-check against the
        // "actual cuts" dump above — if no cut sits near this angle, null is the CORRECT answer and the
        // app falls back to the base tilt. It's only a failure if a cut IS there and we missed it.
        var exists = actualCuts.Any(c => Math.Abs(c.median - target) <= 0.25);
        Console.WriteLine($"{target,5:0.0}° {"-",6} {"-",8} {"-",16} {"-",8} {"-",5} {"-",4} {"-",4}  " +
            (exists ? "FAIL: cut exists but extraction returned null" : "absent from volume (app falls back to base)"));
        if (exists) failures++;
        continue;
    }

    var radials = ScanRadials(buf, site);
    var found = radials.Select(r => (float)Math.Round(r.angle, 1)).Distinct().OrderBy(a => a).ToList();
    var (span, gaps) = AzimuthCoverage(radials.Select(r => r.az).ToList());
    var hasRef = Contains(buf, "DREF");
    var hasVel = Contains(buf, "DVEL");

    // The extracted tilt is correct when every radial in it sits at the requested elevation and the
    // sweep is a full circle. A stray angle means we spilled into a neighbouring cut.
    var offTarget = found.Where(a => Math.Abs(a - target) > 0.3f).ToList();
    var verdict = offTarget.Count > 0 ? $"FAIL: off-target {string.Join("/", offTarget)}"
        : radials.Count == 0 ? "FAIL: no radials"
        : span < 350 ? $"WARN: partial sweep ({span:0}°)"
        : !hasRef ? "FAIL: no reflectivity"
        : gaps > 2 ? $"WARN: {gaps} az gaps"
        : "ok";
    if (verdict.StartsWith("FAIL")) failures++;

    Console.WriteLine($"{target,5:0.0}° {buf.Length / (1024.0 * 1024.0),6:0.00} {radials.Count,8} " +
        $"{string.Join("/", found.Select(a => $"{a:0.0}")),16} {span,7:0}° {gaps,5} {(hasRef ? "yes" : "NO"),4} {(hasVel ? "yes" : "-"),4}  {verdict}");
}

// ---- LIVE PATH: SelectLatestSweep at a target tilt, against a SIMULATED in-progress volume ----
// The live frame comes from the chunks bucket, where a volume ARRIVES INCREMENTALLY and we decide per
// poll which tilts are servable. We can model that exactly: SelectLatestSweep works on decompressed
// blocks, so truncating a finished archive volume to the first N% of its records is what the radar had
// produced N% of the way through the scan. That makes the freshness claim testable — a radar scans
// bottom-up, so the bottom tilts should become servable early and the top ones only at the end.
Console.WriteLine();
Console.WriteLine("live-path simulation — which tilts SelectLatestSweep can serve from a partial volume:");
var (liveHeader, liveBlocks, liveIcao) = DecompressAll(raw, site);
Console.WriteLine($"  (volume = {liveBlocks.Count} records; ICAO '{Encoding.ASCII.GetString(liveIcao)}')");
Console.WriteLine();
Console.Write($"{"% of volume",12} |");
foreach (var a in angles) Console.Write($"{a,6:0.0}°");
Console.WriteLine();
Console.WriteLine(new string('-', 14 + 7 * angles.Count));

foreach (var pct in new[] { 20, 40, 60, 80, 100 })
{
    var take = Math.Max(1, liveBlocks.Count * pct / 100);
    var partial = liveBlocks.Take(take).ToList();
    Console.Write($"{pct,10}%  |");
    foreach (var a in angles)
    {
        // Ask for each tilt as the live path would. "yes" = a complete sweep WITH complete velocity,
        // i.e. exactly what BuildLiveFrameAsync requires before it will serve a frame.
        var sel = Level2Format.SelectLatestSweep(liveHeader, partial, liveIcao, a);
        var ok = sel.complete && sel.data is not null && sel.velComplete;
        Console.Write($"{(ok ? "yes" : "·"),7}");
    }
    Console.WriteLine();
}

// The base tilt must behave identically whether asked for by angle or by the null default — that's the
// contract that keeps the existing live path unchanged.
var byNull = Level2Format.SelectLatestSweep(liveHeader, liveBlocks, liveIcao, null);
var byAngle = Level2Format.SelectLatestSweep(liveHeader, liveBlocks, liveIcao, angles[0]);
Console.WriteLine();
Console.WriteLine($"base tilt via null   : data={byNull.data?.Length ?? 0}B complete={byNull.complete} vel={byNull.velComplete} sweeps={byNull.sweeps} vcp={byNull.vcp}");
Console.WriteLine($"base tilt via {angles[0]:0.00}° : data={byAngle.data?.Length ?? 0}B complete={byAngle.complete} vel={byAngle.velComplete} sweeps={byAngle.sweeps} vcp={byAngle.vcp}");
if (byNull.data?.Length != byAngle.data?.Length || byNull.sweeps != byAngle.sweeps)
{
    Console.WriteLine("FAIL: asking for the base tilt BY ANGLE differs from the null default");
    failures++;
}

// Higher tilts must still report the volume's SAILS count (a base-tilt property), not their own.
foreach (var a in angles.Skip(1).Take(3))
{
    var s = Level2Format.SelectLatestSweep(liveHeader, liveBlocks, liveIcao, a);
    if (s.data is not null && s.sweeps != byNull.sweeps)
    {
        Console.WriteLine($"FAIL: tilt {a:0.0}° reports sweeps={s.sweeps}, volume says {byNull.sweeps}");
        failures++;
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL TILTS OK" : $"{failures} TILT(S) FAILED");
return failures == 0 ? 0 : 1;

// ---- helpers ---------------------------------------------------------------------------------

// Independently re-derives the radials in an extracted buffer by hunting Message-31 header
// signatures — deliberately NOT reusing the extractor's bookkeeping, so this can catch it lying.
// Same signature test as Level2Format.TryDetectIcao: 4 alphanumeric ICAO bytes followed by a valid
// ms-of-day, azimuth, elevation number and elevation angle.
static List<(float az, float angle, int elev)> ScanRadials(byte[] buf, string siteId)
{
    var hits = new List<(float, float, int)>();
    var span = buf.AsSpan();

    // Anchor on the buffer's ACTUAL ICAO (a few radars write a different callsign than their bucket
    // key, e.g. KCRI -> NOK5). Accepting any 4 alphanumeric bytes instead let moment data masquerade
    // as a radial header, which showed up as phantom 0.0° radials in every tilt.
    var icao = Encoding.ASCII.GetBytes(siteId);
    if (Level2Format.IndexOf(buf, icao) < 0 && Level2Format.TryDetectIcao(buf, out var real)) icao = real;

    for (var p = 0; p + 28 <= buf.Length; p++)
    {
        var match = true;
        for (var k = 0; k < 4; k++)
        {
            if (buf[p + k] != icao[k]) { match = false; break; }
        }
        if (!match) continue;

        var ms = ((uint)buf[p + 4] << 24) | ((uint)buf[p + 5] << 16) | ((uint)buf[p + 6] << 8) | buf[p + 7];
        if (ms > 86_400_000) continue;
        var az = BinaryPrimitives.ReadSingleBigEndian(span.Slice(p + 12, 4));
        var ang = BinaryPrimitives.ReadSingleBigEndian(span.Slice(p + 24, 4));
        var elev = buf[p + 22];
        if (az >= 0f && az < 360f && ang >= -2f && ang <= 75f && elev >= 1 && elev <= 32)
        {
            hits.Add((az, ang, elev));
            p += 24; // a radial header is not nested inside another
        }
    }
    return hits;
}

// Azimuth coverage of a sweep: total degrees spanned and how many >5° holes it has. A complete
// sweep is ~360° with no holes; a still-scanning Doppler shows up as a narrow span (the wedge bug).
static (double span, int gaps) AzimuthCoverage(List<float> azimuths)
{
    if (azimuths.Count == 0) return (0, 0);
    var sorted = azimuths.Distinct().OrderBy(a => a).ToList();
    if (sorted.Count < 2) return (0, 0);
    var gaps = 0;
    double covered = 0;
    for (var i = 1; i < sorted.Count; i++)
    {
        var d = sorted[i] - sorted[i - 1];
        if (d > 5) gaps++; else covered += d;
    }
    var wrap = 360 - sorted[^1] + sorted[0];
    if (wrap > 5) gaps++; else covered += wrap;
    return (covered, gaps);
}

// Walks the raw volume's bzip2 LDM records and reports every cut (grouped by elevation number) with
// the min/median/max of its radials' reported angles — the ground truth the extractor must match
// designed angles against.
static List<(int num, int count, double min, double median, double max, bool hasRef, bool hasVel)> ScanCuts(byte[] raw, string siteId)
{
    var result = new List<(int, int, double, double, double, bool, bool)>();
    var icao = Encoding.ASCII.GetBytes(siteId);
    var resolved = false;
    const int headerSize = 24;
    var pos = headerSize;

    var num = -1;
    var angles = new List<double>();
    var count = 0;
    var hasRef = false;
    var hasVel = false;

    void Flush()
    {
        if (count == 0) return;
        angles.Sort();
        result.Add((num, count, angles.Count > 0 ? angles[0] : double.NaN,
            angles.Count > 0 ? angles[angles.Count / 2] : double.NaN,
            angles.Count > 0 ? angles[^1] : double.NaN, hasRef, hasVel));
    }

    while (pos + 4 <= raw.Length)
    {
        var cw = (raw[pos] << 24) | (raw[pos + 1] << 16) | (raw[pos + 2] << 8) | raw[pos + 3];
        pos += 4;
        var size = Math.Abs(cw);
        if (size <= 0 || pos + size > raw.Length)
        {
            Console.WriteLine($"  !! walk STOPPED at byte {pos - 4:N0}/{raw.Length:N0} ({100.0 * pos / raw.Length:0.0}%): " +
                $"control word {cw} -> size {size} (overruns by {pos + size - raw.Length:N0})");
            break;
        }

        byte[] block;
        try
        {
            using var bi = new MemoryStream(raw, pos, size, writable: false);
            using var bz = new SharpCompress.Compressors.BZip2.BZip2Stream(bi, SharpCompress.Compressors.CompressionMode.Decompress, false);
            using var bo = new MemoryStream(1024 * 1024);
            bz.CopyTo(bo);
            block = bo.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  !! walk STOPPED at byte {pos:N0}/{raw.Length:N0} ({100.0 * pos / raw.Length:0.0}%): {ex.GetType().Name}: {ex.Message}");
            break;
        }
        pos += size;

        var r = Level2Format.HasMoment(block, Level2Format.Dref);
        if (!resolved && r)
        {
            if (Level2Format.IndexOf(block, icao) < 0 && Level2Format.TryDetectIcao(block, out var real))
            {
                Console.WriteLine($"  (data ICAO is '{Encoding.ASCII.GetString(real)}', not '{siteId}')");
                icao = real;
            }
            resolved = true;
        }

        var e = Level2Format.ElevationOf(block, icao);
        var a = Level2Format.ElevationAngleOf(block, icao);
        var v = Level2Format.HasMoment(block, Level2Format.Dvel);
        if (e < 1) continue; // metadata

        if (count > 0 && e != num)
        {
            Flush();
            angles.Clear(); count = 0; hasRef = false; hasVel = false;
        }
        num = e; count++; hasRef |= r; hasVel |= v;
        if (!double.IsNaN(a)) angles.Add(a);
    }
    Flush();
    return result;
}

// Decompresses every LDM record into the (block, elev) shape SelectLatestSweep consumes — the same
// shape BuildLiveFrameAsync assembles from chunks — plus the 24-byte AR2V header and the resolved
// per-radial ICAO. Metadata records are kept (tagged elev 0); SelectLatestSweep needs them for its
// Message 5 / VCP reads and copies them into the output buffer.
static (byte[] header, List<(byte[] block, int elev)> blocks, byte[] icao) DecompressAll(byte[] raw, string siteId)
{
    const int headerSize = 24;
    var header = raw[..headerSize];
    var icao = Encoding.ASCII.GetBytes(siteId);
    var resolved = false;
    var blocks = new List<(byte[], int)>();
    var pos = headerSize;

    while (pos + 4 <= raw.Length)
    {
        var cw = (raw[pos] << 24) | (raw[pos + 1] << 16) | (raw[pos + 2] << 8) | raw[pos + 3];
        pos += 4;
        var size = Math.Abs(cw);
        if (size <= 0 || pos + size > raw.Length) break;

        byte[] block;
        try
        {
            using var bi = new MemoryStream(raw, pos, size, writable: false);
            using var bz = new SharpCompress.Compressors.BZip2.BZip2Stream(bi, SharpCompress.Compressors.CompressionMode.Decompress, false);
            using var bo = new MemoryStream(1024 * 1024);
            bz.CopyTo(bo);
            block = bo.ToArray();
        }
        catch { break; }
        pos += size;

        if (!resolved && Level2Format.HasMoment(block, Level2Format.Dref))
        {
            if (Level2Format.IndexOf(block, icao) < 0 && Level2Format.TryDetectIcao(block, out var real)) icao = real;
            resolved = true;
        }
        blocks.Add((block, Level2Format.ElevationOf(block, icao)));
    }

    // The ICAO can resolve partway through (on the first radial), leaving earlier blocks tagged with a
    // stale elevation. Re-tag them all, exactly as BuildLiveFrameAsync does after detecting it.
    for (var i = 0; i < blocks.Count; i++)
    {
        blocks[i] = (blocks[i].Item1, Level2Format.ElevationOf(blocks[i].Item1, icao));
    }
    return (header, blocks, icao);
}

static bool Contains(byte[] hay, string needle)
{
    var n = Encoding.ASCII.GetBytes(needle);
    for (var i = 0; i <= hay.Length - n.Length; i++)
    {
        var k = 0;
        while (k < n.Length && hay[i + k] == n[k]) k++;
        if (k == n.Length) return true;
    }
    return false;
}
