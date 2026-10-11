// --spec SITE yyyy/MM/dd [FULL]: the decode audit's open items, MEASURED (docs/RadarEngineSpecs/digests/decode.md).
//
// The audit compared our decode with the ROC ICDs and left questions only real data can answer:
//   A7  Does a cut's LAST radial ever carry a "bad data" status (0x82 / 0x84)? EndsItsCut only accepts 2 and 4,
//       so such a cut is never seen as finished (live waits for the next cut, ~15 s).
//   B7  Does Message 5's VCP SEQUENCING word (header halfword 9) ever say "truncated"? Then the volume scans fewer
//       cuts than its number promises, and we blame AVSET.
//   B8  Does any volume carry the BASE TILT / MPDA flags (header hw 10 bits 11-15, per-cut E15 bits 9-10)?
//   B9  Does our PlannedBasePassNumbers (angle + waveform) pick the same 0.5° passes as the spec's own per-cut
//       SAILS / MRLE / BASE TILT flags?
//   B10 Do Model Data records (Message 29) appear in archive volumes, or `M` chunks in the chunks bucket?
//
// Three passes:
//   1. EVERY volume of the day, metadata record only (8 KB range read, as --supp): B7, B8, B9, tallied by signature.
//   2. FULL whole volumes (default 4, spread over the day), every record decompressed: A7, B7 (cuts scanned), B10.
//   3. The chunks bucket's listing for the site (it keeps only recent volumes): chunk kinds, for B10.
// Read-only: nothing here writes to the app's cache.

using System.Xml.Linq;
using Anvil.Models;
using Anvil.Services;

static class SpecCheck
{
    const string Chunks = "https://unidata-nexrad-level2-chunks.s3.amazonaws.com/";
    static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

    public static async Task<int> RunAsync(string site, string day, int full)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil-TiltCheck/1.0");

        var keys = await ListVolumesAsync(http, site, day);
        Console.WriteLine($"== {site} {day}: {keys.Count} volumes (_V06)");
        if (keys.Count == 0) return 1;

        // ---- 1. Metadata of every volume ---------------------------------------------------------------------
        var tally = new SortedDictionary<string, (int n, string first, string last)>(StringComparer.Ordinal);
        var truncated = 0; var seqActive = 0; var baseTilt = 0; var mpda = 0; var disagree = 0;
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (k, ct) =>
        {
            Meta m;
            try { m = await ReadMetaAsync(http, k, ct); }
            catch (Exception e) { m = new Meta("ERR " + e.GetType().Name, 0, false, false, false, false); }
            var hhmm = HhMm(k);
            lock (tally)
            {
                tally[m.Signature] = tally.TryGetValue(m.Signature, out var t)
                    ? (t.n + 1, string.CompareOrdinal(hhmm, t.first) < 0 ? hhmm : t.first, string.CompareOrdinal(hhmm, t.last) > 0 ? hhmm : t.last)
                    : (1, hhmm, hhmm);
                if (m.Truncated) truncated++;
                if (m.SequenceActive) seqActive++;
                if (m.BaseTilt) baseTilt++;
                if (m.Mpda) mpda++;
                if (m.PassesDisagree) disagree++;
            }
        });
        Console.WriteLine("\n-- 1. Message 5 of every volume (count, first-last HHmm, signature)");
        Console.WriteLine("   seq = VCP SEQUENCING hw 9 · supp = hw 10 · cuts = per-cut E15 flags (S=SAILS M=MRLE P=MPDA B=BASE TILT)");
        Console.WriteLine("   ours = PlannedBasePassNumbers · spec = pass 1 + flagged cuts at the lowest tilt");
        foreach (var (sig, t) in tally) Console.WriteLine($"   {t.n,4}× {t.first}-{t.last}Z  {sig}");
        Console.WriteLine($"   B7 sequence active: {seqActive}  truncated: {truncated}   B8 BASE TILT: {baseTilt}  MPDA: {mpda}" +
                          $"   B9 pass lists disagree: {disagree} of {keys.Count}");

        // ---- 2. Whole volumes ----------------------------------------------------------------------------------
        Console.WriteLine($"\n-- 2. {Math.Min(full, keys.Count)} whole volumes: radial status, cut ends, Message 29");
        var statusTotals = new SortedDictionary<int, int>();
        var a7Hits = 0; var endsItsCutMisses = 0; var msg29 = 0;
        foreach (var k in Spread(keys, full))
        {
            byte[] raw;
            try { raw = await http.GetByteArrayAsync(Level2RadarService.BucketBase + k); }
            catch (Exception e) { Console.WriteLine($"   {Name(k)}: download failed ({e.GetType().Name})"); continue; }
            var blocks = Level2Format.DecompressRecords(raw, site, out var icao);
            var planned = ScanPlan.TryRead(blocks, out var vcp, out var plan) ? plan.Count : 0;

            var lastByElev = new SortedDictionary<int, (int status, int record)>();
            var statuses = new SortedDictionary<int, int>();
            var vol29 = 0;
            for (var r = 0; r < blocks.Count; r++)
            {
                var b = blocks[r].block;
                if (b.Length > Level2Format.CtmHeaderSize + 3 && b[Level2Format.CtmHeaderSize + 3] == 29) vol29++;
                foreach (var rad in RegimeCheck.ScanRadials(b, icao, r))
                {
                    statuses[rad.Status] = statuses.GetValueOrDefault(rad.Status) + 1;
                    lastByElev[rad.Elev] = (rad.Status, r);
                }
            }
            msg29 += vol29;
            foreach (var (s, n) in statuses) statusTotals[s] = statusTotals.GetValueOrDefault(s) + n;

            // A cut is finished by its LAST radial: 2 or 4 (or 0x82 / 0x84 with the bad-data bit). Ask EndsItsCut too.
            var notes = new List<string>();
            foreach (var (elev, (status, record)) in lastByElev)
            {
                var ours = Level2Format.EndsItsCut(blocks[record].block, icao, elev);
                if ((status & 0x80) != 0) { a7Hits++; notes.Add($"cut {elev} ends 0x{status:X2}"); }
                else if (status is not (2 or 4)) notes.Add($"cut {elev} ends {status}");
                if (!ours && (status & 0x7F) is 2 or 4) { endsItsCutMisses++; notes.Add($"EndsItsCut=false on cut {elev}"); }
            }
            Console.WriteLine($"   {Name(k)}: VCP {vcp}, {blocks.Count} records, cuts scanned {lastByElev.Count} / planned {planned}" +
                              (vol29 > 0 ? $", Message 29 ×{vol29}" : "") +
                              $"  status {string.Join(" ", statuses.Select(s => $"{s.Key:X2}×{s.Value}"))}" +
                              (notes.Count > 0 ? "\n      " + string.Join("; ", notes) : ""));
        }
        Console.WriteLine($"   A7 cuts ending with a bad-data status: {a7Hits}   EndsItsCut missed a finished cut: {endsItsCutMisses}" +
                          $"   B10 Message 29 records: {msg29}");
        Console.WriteLine($"   all status bytes: {string.Join(" ", statusTotals.Select(s => $"{s.Key:X2}×{s.Value}"))}");

        // ---- 3. Chunks bucket ----------------------------------------------------------------------------------
        var kinds = await ChunkKindsAsync(http, site);
        Console.WriteLine($"\n-- 3. chunks bucket {site}/ (recent volumes only): " +
                          string.Join("  ", kinds.Select(k => $"{k.Key}×{k.Value}")) +
                          (kinds.ContainsKey('M') ? "   ⚠️ M (Model Data) chunks present" : "   no M chunks"));
        return 0;
    }

    sealed record Meta(string Signature, int Vcp, bool SequenceActive, bool Truncated, bool BaseTilt, bool Mpda)
    {
        public bool PassesDisagree { get; init; }
    }

    // The metadata record alone (as --supp): Message 5's header words + per-cut flags, and the two pass lists.
    static async Task<Meta> ReadMetaAsync(HttpClient http, string key, CancellationToken ct)
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
        if (recordBytes is <= 0 or > 512 * 1024) return new Meta("no metadata record", 0, false, false, false, false);
        if (28 + recordBytes > prefix.Length) prefix = await Range(28 + recordBytes);
        var meta = Level2RadarService.DecompressChunk(prefix, isS: true);
        if (meta is null) return new Meta("metadata didn't decompress", 0, false, false, false, false);
        var blocks = new List<(byte[] block, int elev)> { (meta, 0) };

        for (var pos = 0; pos + 12 + 16 + 22 <= meta.Length; pos += Level2Format.RadarDataSize)
        {
            var type = meta[pos + 12 + 3];
            if (type == 31) break;
            if (type is not (5 or 7)) continue;
            var body = pos + 12 + 16;
            int Hw(int off) => (meta[off] << 8) | meta[off + 1];
            var vcp = Hw(body + 4);
            var n = Hw(body + 6);
            var seq = Hw(body + 16);  // hw 9: VCP SEQUENCING
            var supp = Hw(body + 18); // hw 10: VCP SUPPLEMENTAL
            var cuts = new List<(int num, double angle, int wf, int e15)>();
            for (var k = 0; k < n && body + 22 + k * 46 + 30 <= meta.Length; k++)
            {
                var c = body + 22 + k * 46;
                cuts.Add((k + 1, (short)Hw(c) / 8.0 * 0.043945, meta[c + 3], Hw(c + 28)));
            }
            if (cuts.Count == 0) return new Meta($"VCP {vcp} n={n} no cuts read", vcp, false, false, false, false);

            // The spec's own pass list: the first non-Doppler cut at the lowest tilt, plus every SAILS / MRLE / BASE TILT
            // flagged non-Doppler cut at that tilt. Same angle window as the app (SailsTiltTol).
            var lowest = cuts.Min(c => c.angle);
            var atBase = cuts.Where(c => c.wf != 2 && Math.Abs(c.angle - lowest) <= Level2Format.SailsTiltTol).ToList();
            var spec = atBase.Take(1).Concat(atBase.Skip(1).Where(c => (c.e15 & (0x1 | 0x10 | 0x400)) != 0))
                .Select(c => c.num).ToList();
            var ours = Level2Format.PlannedBasePassNumbers(blocks);
            var agree = ours.SequenceEqual(spec);

            var flagged = cuts.Where(c => c.e15 != 0).Select(c => $"{c.num}:{c.angle:0.0}w{c.wf}:{CutFlags(c.e15)}");
            var sig = $"VCP {vcp} n={n} seq=[{SeqText(seq)}] supp=[{SuppText(supp)}] cuts=[{string.Join(" ", flagged.DefaultIfEmpty("none"))}]" +
                      $" ours={Join(ours)} spec={Join(spec)} {(agree ? "agree" : "⚠️ DIFF")}";
            return new Meta(sig, vcp, (seq & 0x2000) != 0, (seq & 0x4000) != 0, (supp & 0x1000) != 0 || cuts.Any(c => (c.e15 & 0x400) != 0),
                (supp & 0x800) != 0 || cuts.Any(c => (c.e15 & 0x200) != 0)) { PassesDisagree = !agree };
        }
        return new Meta("no Message 5", 0, false, false, false, false);
    }

    // hw 9 (Table XI footnote 15): bits 0-4 cuts in the truncated VCP, 5-6 max SAILS cuts, 13 sequence active, 14 truncated.
    static string SeqText(int w) => w == 0 ? "0" : string.Join(" ", new[]
    {
        (w & 0x2000) != 0 ? "ACTIVE" : null,
        (w & 0x4000) != 0 ? "TRUNCATED" : null,
        $"cuts={w & 0x1F}",
        $"maxSails={(w >> 5) & 3}",
        (w & ~0x607F) != 0 ? $"other=0x{w & ~0x607F:X}" : null,
    }.Where(s => s is not null));

    // hw 10 (footnote 16): 0 SAILS, 1-3 SAILS cuts, 4 MRLE, 5-7 MRLE cuts, 10 MPDA cuts added, 11 MPDA, 12 BASE TILT, 13-15 base tilts.
    static string SuppText(int w) => w == 0 ? "0" : string.Join(" ", new[]
    {
        (w & 1) != 0 ? $"SAILS n={(w >> 1) & 7}" : null,
        (w & 0x10) != 0 ? $"MRLE n={(w >> 5) & 7}" : null,
        (w & 0x400) != 0 ? "MPDAcutsAdded" : null,
        (w & 0x800) != 0 ? "MPDA" : null,
        (w & 0x1000) != 0 ? $"BASETILT n={(w >> 13) & 7}" : null,
    }.Where(s => s is not null));

    // E15 (footnote 17): 0 SAILS (1-3 its sequence), 4 MRLE (5-7 its sequence), 9 MPDA cut, 10 BASE TILT cut.
    static string CutFlags(int w) =>
        ((w & 1) != 0 ? $"S{(w >> 1) & 7}" : "") + ((w & 0x10) != 0 ? $"M{(w >> 5) & 7}" : "") +
        ((w & 0x200) != 0 ? "P" : "") + ((w & 0x400) != 0 ? "B" : "") +
        ((w & ~0x6FF) != 0 ? $"?{w & ~0x6FF:X}" : "");

    static string Join(List<int> v) => v.Count == 0 ? "[]" : "[" + string.Join(",", v) + "]";
    static string Name(string key) => key[(key.LastIndexOf('/') + 1)..];
    static string HhMm(string key) => key[(key.LastIndexOf('_') - 6)..(key.LastIndexOf('_') - 2)];

    static IEnumerable<string> Spread(List<string> keys, int n)
    {
        if (n <= 0) yield break;
        if (n >= keys.Count) { foreach (var k in keys) yield return k; yield break; }
        for (var i = 0; i < n; i++) yield return keys[(int)((i + 0.5) * keys.Count / n)];
    }

    static async Task<List<string>> ListVolumesAsync(HttpClient http, string site, string day)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var url = $"{Level2RadarService.BucketBase}?list-type=2&prefix={Uri.EscapeDataString($"{day}/{site}/")}&max-keys=1000" +
                      (token is null ? "" : $"&continuation-token={Uri.EscapeDataString(token)}");
            var doc = XDocument.Parse(await http.GetStringAsync(url));
            keys.AddRange(doc.Descendants(S3 + "Key").Select(k => k.Value).Where(k => k.EndsWith("_V06", StringComparison.Ordinal)));
            token = doc.Root?.Element(S3 + "IsTruncated")?.Value == "true" ? doc.Root?.Element(S3 + "NextContinuationToken")?.Value : null;
        }
        while (token is not null);
        return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    // Kind letter of every chunk key under SITE/ (…/yyyyMMdd-HHmmss-seq-K). Capped at 50 pages (50,000 keys).
    static async Task<SortedDictionary<char, int>> ChunkKindsAsync(HttpClient http, string site)
    {
        var kinds = new SortedDictionary<char, int>();
        string? token = null;
        for (var page = 0; page < 50; page++)
        {
            var url = $"{Chunks}?list-type=2&prefix={site}/&max-keys=1000" +
                      (token is null ? "" : $"&continuation-token={Uri.EscapeDataString(token)}");
            var doc = XDocument.Parse(await http.GetStringAsync(url));
            foreach (var key in doc.Descendants(S3 + "Key").Select(e => e.Value))
            {
                var name = key[(key.LastIndexOf('/') + 1)..];
                var dash = name.LastIndexOf('-');
                if (dash >= 0 && dash == name.Length - 2) kinds[name[^1]] = kinds.GetValueOrDefault(name[^1]) + 1;
            }
            token = doc.Root?.Element(S3 + "IsTruncated")?.Value == "true" ? doc.Root?.Element(S3 + "NextContinuationToken")?.Value : null;
            if (token is null) break;
        }
        return kinds;
    }
}
