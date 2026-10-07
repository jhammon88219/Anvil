// --regime SITE [VOLUMES]: REGIME-AWARE POLLING, step 1 — MEASURE before building the scheduler.
//
// For the last VOLUMES complete volumes in the live CHUNKS bucket (default 3), prints per planned cut:
//   the PLAN (Message 5 via ScanPlan: angle, SAILS/MRLE flags, azimuth rate → sweep seconds),
//   the REALITY (first + last radial collection time of that elevation number, from every chunk, decompressed),
//   the GAP before it (antenna move/settle — what the plan's sweep times leave out), and
//   the ARRIVAL (S3 LastModified of the chunk holding the cut's last radial − that radial's time) = upload latency.
// Then a model check: predicted end = volume start + Σ planned sweeps + (cuts so far × the volume's mean gap).
// No live waiting: the bucket keeps recent complete volumes, so this runs in seconds against real data.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Anvil.Services;

static class RegimeCheck
{
    const string Chunks = "https://unidata-nexrad-level2-chunks.s3.amazonaws.com/";
    static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

    sealed record Obj(string Key, int Seq, char Kind, DateTimeOffset Start, DateTimeOffset Modified);
    sealed record Radial(DateTimeOffset Time, int Elev, int Status, int Seq);

    public static async Task RunAsync(string site, int volumes)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil-TiltCheck/1.0");

        var folders = await FoldersAsync(http, site);
        if (folders.Count == 0) { Console.WriteLine($"{site}: no chunk folders"); return; }
        var (newest, newestStart) = await NewestAsync(http, site, folders);
        Console.WriteLine($"== {site}: newest volume folder {newest} (started {newestStart:HH:mm:ss}Z, in progress — skipped)");

        var allGaps = new List<double>();
        var allLatency = new List<double>();
        var allErr = new List<double>();
        for (var back = 1; back <= volumes; back++)
        {
            var folder = ((newest - back - 1) % 999 + 999) % 999 + 1; // 1..999, wrapping
            var objs = await ListAsync(http, site, folder);
            if (objs.Count == 0) { Console.WriteLine($"\n-- folder {folder}: empty"); continue; }
            var start = objs.Max(o => o.Start);
            var vol = objs.Where(o => o.Start == start).OrderBy(o => o.Seq).ToList();
            await MeasureVolumeAsync(http, site, folder, vol, allGaps, allLatency, allErr);
        }

        Console.WriteLine("\n== SUMMARY over all volumes");
        Console.WriteLine($"   overhead per cut    : {Stats(allGaps)} s");
        Console.WriteLine($"   upload latency      : {Stats(allLatency)} s   (cut's last radial → its chunk in the bucket)");
        Console.WriteLine($"   model error (ends)  : {Stats(allErr)} s   (planned sweeps + the volume's median overhead per cut)");
    }

    static async Task MeasureVolumeAsync(HttpClient http, string site, int folder, List<Obj> vol,
        List<double> allGaps, List<double> allLatency, List<double> allErr)
    {
        Console.WriteLine($"\n-- folder {folder}: volume {vol[0].Start:yyyy-MM-dd HH:mm:ss}Z, {vol.Count} chunks " +
                          $"({vol.Count(o => o.Kind == 'E')} E)");
        var blocks = new (Obj obj, byte[]? block)[vol.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, vol.Count), new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (i, ct) =>
            {
                var bytes = await http.GetByteArrayAsync(Chunks + vol[i].Key, ct);
                blocks[i] = (vol[i], Level2RadarService.DecompressChunk(bytes, vol[i].Kind == 'S'));
            });

        var s = blocks.FirstOrDefault(b => b.obj.Kind == 'S').block;
        if (s is null || !ScanPlan.TryRead(new List<(byte[], int)> { (s, 0) }, out var vcp, out var plan))
        {
            Console.WriteLine("   no readable Message 5 in the S chunk");
            return;
        }

        var icao = Encoding.ASCII.GetBytes(site);
        var probe = blocks.Select(b => b.block).FirstOrDefault(b => b is not null && b.Length > 0 && Level2Format.IndexOf(b, icao) >= 0);
        if (probe is null && blocks.Select(b => b.block).FirstOrDefault(b => b is not null) is { } any && Level2Format.TryDetectIcao(any, out var detected)) icao = detected;

        var radials = new List<Radial>();
        foreach (var (obj, block) in blocks)
        {
            if (block is not null) radials.AddRange(ScanRadials(block, icao, obj.Seq));
        }
        if (radials.Count == 0) { Console.WriteLine("   no radials"); return; }
        var modified = blocks.ToDictionary(b => b.obj.Seq, b => b.obj.Modified);
        var byElev = radials.GroupBy(r => r.Elev).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Time).ToList());
        var t0 = byElev.TryGetValue(1, out var first) ? first[0].Time : radials.Min(r => r.Time);

        Console.WriteLine($"   VCP {vcp}, {plan.Count} planned cuts, start {t0:HH:mm:ss.f}Z");
        Console.WriteLine("   cut  angle  w flags      rate°/s  plan s | real s  gap s | end+s  arrive s");

        var rows = new List<(ScanPlan.Cut cut, double? end, double? sweep)>();
        DateTimeOffset? prevEnd = null;
        foreach (var cut in plan)
        {
            if (!byElev.TryGetValue(cut.Number, out var rs))
            {
                Console.WriteLine($"   {cut.Number,3}  {cut.Angle,5:0.00}  {cut.Waveform} {Flags(cut),-9}  {cut.AzimuthRate,6:0.00}  {cut.SweepSeconds,6:0.0} |   (no radials)");
                rows.Add((cut, null, null));
                continue;
            }
            var startT = rs[0].Time;
            var endR = rs[^1];
            var real = (endR.Time - startT).TotalSeconds;
            double? gap = prevEnd is { } pe ? (startT - pe).TotalSeconds : null;

            var arrive = (modified[endR.Seq] - endR.Time).TotalSeconds;
            allLatency.Add(arrive);
            Console.WriteLine($"   {cut.Number,3}  {cut.Angle,5:0.00}  {cut.Waveform} {Flags(cut),-9}  {cut.AzimuthRate,6:0.00}  {cut.SweepSeconds,6:0.0} | " +
                              $"{real,6:0.0} {(gap is { } gg ? gg.ToString("0.0", CultureInfo.InvariantCulture) : "  -"),6} | " +
                              $"{(endR.Time - t0).TotalSeconds,5:0} {arrive,8:0}");
            rows.Add((cut, (endR.Time - t0).TotalSeconds, real));
            prevEnd = endR.Time;
        }

        // OVERHEAD = what a cut costs beyond its planned sweep: (its end − the previous cut's end) − its plan.
        // (The "gap" column alone over-counts: first→last radial already falls one radial short of the plan.)
        var overheads = new List<double>();
        for (var i = 1; i < rows.Count; i++)
        {
            if (rows[i].end is { } e && rows[i - 1].end is { } pe2) overheads.Add(e - pe2 - rows[i].cut.SweepSeconds);
        }
        var overhead = overheads.Count > 0 ? overheads.OrderBy(x => x).ElementAt(overheads.Count / 2) : 0;
        allGaps.AddRange(overheads);
        double planned = 0;
        var errs = new List<double>();
        for (var i = 0; i < rows.Count; i++)
        {
            planned += rows[i].cut.SweepSeconds + (i > 0 ? overhead : 0);
            if (rows[i].end is { } e) errs.Add(e - planned);
        }
        allErr.AddRange(errs);
        var total = byElev.Values.Max(l => l[^1].Time) - t0;
        Console.WriteLine($"   volume took {total.TotalSeconds:0} s; Σ planned sweeps {plan.Sum(c => c.SweepSeconds):0} s; overhead/cut {overhead:0.00} s; " +
                          $"model end error {Stats(errs)} s");
    }

    // Message 31 headers in a decompressed chunk: ICAO, ms-of-day (+4), Julian date (+8), azimuth (+12, float),
    // radial status (+21), elevation number (+22), elevation angle (+24, float).
    static IEnumerable<Radial> ScanRadials(byte[] buf, byte[] icao, int seq)
    {

        for (var p = 0; p + 28 <= buf.Length; p++)
        {
            if (buf[p] != icao[0] || buf[p + 1] != icao[1] || buf[p + 2] != icao[2] || buf[p + 3] != icao[3]) continue;
            var ms = ((uint)buf[p + 4] << 24) | ((uint)buf[p + 5] << 16) | ((uint)buf[p + 6] << 8) | buf[p + 7];
            var julian = (buf[p + 8] << 8) | buf[p + 9];
            if (ms > 86_400_000 || julian <= 0) continue;
            var az = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(p + 12, 4));
            var ang = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(p + 24, 4));
            var elev = buf[p + 22];
            if (az is < 0f or >= 360f || ang is < -2f or > 75f || elev is < 1 or > 40) continue;
            yield return new Radial(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(julian - 1).AddMilliseconds(ms),
                elev, buf[p + 21], seq);
            p += 24;
        }
    }

    static string Flags(ScanPlan.Cut c) =>
        (c.IsSails ? $"SAILS{c.SailsSequence} " : "") + (c.IsMrle ? "MRLE" : "");

    static string Stats(List<double> v)
    {
        if (v.Count == 0) return "n/a";
        var s = v.OrderBy(x => x).ToList();
        return $"median {s[s.Count / 2]:0.0}  min {s[0]:0.0}  max {s[^1]:0.0}  (n={s.Count})";
    }

    static async Task<List<int>> FoldersAsync(HttpClient http, string site)
    {
        var nums = new List<int>();
        string? token = null;
        do
        {
            var url = $"{Chunks}?list-type=2&prefix={site}/&delimiter=/&max-keys=1000" +
                      (token is null ? "" : $"&continuation-token={Uri.EscapeDataString(token)}");
            var doc = XDocument.Parse(await http.GetStringAsync(url));
            foreach (var p in doc.Descendants(S3 + "Prefix").Select(e => e.Value))
            {
                var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out var n)) nums.Add(n);
            }
            token = doc.Root?.Element(S3 + "IsTruncated")?.Value == "true" ? doc.Root?.Element(S3 + "NextContinuationToken")?.Value : null;
        }
        while (token is not null);
        return nums.Distinct().OrderBy(n => n).ToList();
    }

    // The newest volume ends one run of present folder numbers (the service's rule): peek each run's end.
    static async Task<(int folder, DateTimeOffset start)> NewestAsync(HttpClient http, string site, List<int> nums)
    {
        var ends = new List<int>();
        for (var i = 0; i < nums.Count; i++)
        {
            if (i == nums.Count - 1 || nums[i + 1] != nums[i] + 1) ends.Add(nums[i]);
        }
        (int, DateTimeOffset) best = (nums[^1], DateTimeOffset.MinValue);
        foreach (var f in ends)
        {
            var objs = await ListAsync(http, site, f);
            if (objs.Count > 0 && objs.Max(o => o.Start) is var t && t > best.Item2) best = (f, t);
        }
        return best;
    }

    static async Task<List<Obj>> ListAsync(HttpClient http, string site, int folder)
    {
        var list = new List<Obj>();
        string? token = null;
        do
        {
            var url = $"{Chunks}?list-type=2&prefix={site}/{folder}/&max-keys=1000" +
                      (token is null ? "" : $"&continuation-token={Uri.EscapeDataString(token)}");
            var doc = XDocument.Parse(await http.GetStringAsync(url));
            foreach (var c in doc.Descendants(S3 + "Contents"))
            {
                var key = c.Element(S3 + "Key")!.Value;
                var name = key[(key.LastIndexOf('/') + 1)..]; // 20260614-235853-001-S
                var parts = name.Split('-');
                if (parts.Length != 4 || !int.TryParse(parts[2], out var seq) || parts[3].Length != 1) continue;
                if (!DateTimeOffset.TryParseExact(parts[0] + parts[1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)) continue;
                var modified = DateTimeOffset.Parse(c.Element(S3 + "LastModified")!.Value, CultureInfo.InvariantCulture);
                list.Add(new Obj(key, seq, parts[3][0], start, modified));
            }
            token = doc.Root?.Element(S3 + "IsTruncated")?.Value == "true" ? doc.Root?.Element(S3 + "NextContinuationToken")?.Value : null;
        }
        while (token is not null);
        return list;
    }
}
