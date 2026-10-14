// --passes SITE yyyy/MM/dd HH:mm [VOLUMES]: WHICH 0.5° PASSES THE APP MAKES FRAMES OF, on real archive volumes.
//
// Runs the app's own rule (Level2Format.PlannedBasePassNumbers → PlannedBasePasses = the frame count the loop plans,
// TryExtractBasePasses = the frames it actually cuts) and prints them beside the volume's Message 5 base-angle cuts and
// the cuts the volume really holds. Written for TDWR (2026-10-14): its storm mode repeats the surface scan every
// ~minute as a reflectivity+velocity cut, a shape the NEXRAD-built rule didn't expect. Works on NEXRAD (_V06) and
// TDWR (_V08) keys alike. Downloads whole volumes (TDWR ~1-3 MB, NEXRAD ~10-30 MB). Read-only.

using System.Xml.Linq;
using Anvil.Services;

static class PassCheck
{
    const string Bucket = "https://unidata-nexrad-level2.s3.amazonaws.com/";
    static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

    public static async Task<int> RunAsync(string site, string day, string hhmm, int volumes)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var list = XDocument.Parse(await http.GetStringAsync(
            $"{Bucket}?list-type=2&prefix={Uri.EscapeDataString($"{day}/{site}/")}&max-keys=1000"));
        var keys = list.Descendants(S3 + "Key").Select(k => k.Value)
            .Where(k => k.EndsWith("_V06", StringComparison.Ordinal) || k.EndsWith("_V08", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var want = hhmm.Replace(":", "");
        var first = keys.FindIndex(k => string.CompareOrdinal(k.Split('_')[^2], want) >= 0);
        if (first < 0) { Console.WriteLine($"{site} {day}: no _V06/_V08 volume at/after {hhmm}Z"); return 1; }

        var planned = 0;
        var cut = 0;
        for (var v = first; v < Math.Min(keys.Count, first + volumes); v++)
        {
            var raw = await http.GetByteArrayAsync(Bucket + keys[v]);
            var blocks = Level2Format.DecompressRecords(raw, site, out var icao);
            Console.WriteLine($"\n== {keys[v].Split('/')[^1]}  ({raw.Length / 1024} KB, {blocks.Count} records)");

            if (ScanPlan.TryRead(blocks, out var vcp, out var plan) && plan.Count > 0)
            {
                var lowest = plan.Min(c => c.Angle);
                var baseCuts = plan.Where(c => Math.Abs(c.Angle - lowest) <= 0.2);
                Console.WriteLine($"   VCP {vcp}, {plan.Count} cuts; base-angle cuts in the plan: " +
                                  string.Join(" ", baseCuts.Select(c => $"{c.Number}:{c.Angle:0.00}°w{c.Waveform}")));
            }
            else
            {
                Console.WriteLine("   Message 5 not readable");
            }

            var firstRadial = blocks.FindIndex(b => b.elev >= 1);
            if (firstRadial >= 0)
            {
                var cuts = Level2Format.GroupCuts(blocks, icao, firstRadial);
                var real = cuts.Where(c => !float.IsNaN(c.angle)).ToList();
                var low = real.Count > 0 ? real.Min(c => c.angle) : float.NaN;
                Console.WriteLine("   base-angle cuts in the volume:  " + string.Join("  ", real
                    .Where(c => Math.Abs(c.angle - low) <= 0.2)
                    .Select(c => $"{blocks[c.start].elev}:{c.angle:0.00}°{(c.hasRef ? "R" : "")}{(c.hasVel ? "V" : "")}" +
                                 (Level2Format.FirstRadialTime(blocks, c.start, c.end, icao) is { } t ? $"@{t:HH:mm:ss}" : ""))));
            }

            var numbers = Level2Format.PlannedBasePassNumbers(blocks);
            var count = Level2Format.PlannedBasePasses(blocks);
            var passes = Level2Format.TryExtractBasePasses(raw, site);
            planned += count;
            cut += passes.Count;
            Console.WriteLine($"   plan's pass numbers [{string.Join(",", numbers)}] → frames PLANNED {count}");
            Console.WriteLine($"   frames CUT {passes.Count}: " +
                              string.Join("  ", passes.Select(p => $"#{p.Pass}@{(p.Time is { } t ? t.ToString("HH:mm:ss") : "?")}")));
            // Which cuts each frame holds: a dual-PRF pass (VCP 112) must carry BOTH Doppler cuts.
            Console.WriteLine("   frame cuts (surveillance + Doppler [+ 2nd Doppler]): " + string.Join("  ",
                Level2Format.ListBasePasses(blocks, icao).Select(p =>
                    $"#{p.Pass}={blocks[p.Surveillance.Start].elev}" +
                    (p.Doppler is { } d ? $"+{blocks[d.Start].elev}" : "") +
                    (p.Doppler2 is { } d2 ? $"+{blocks[d2.Start].elev}" : ""))));
            if (passes.Count != count)
            {
                Console.WriteLine("   ⚠️ planned ≠ cut: the loop waits for frames that never arrive (or never plans ones it has)");
            }

            // Pass 1 is cached under the base name by EITHER path, so the prefix extractor must cut the same bytes.
            var prefixTilt = Level2Format.TryExtractLowestTilt(raw, site);
            var p1 = passes.FirstOrDefault(p => p.Pass == 1).Data;
            Console.WriteLine($"   prefix extractor vs pass #1: " + (prefixTilt is null || p1 is null ? "n/a"
                : prefixTilt.AsSpan().SequenceEqual(p1) ? $"IDENTICAL ({prefixTilt.Length / 1024} KB)"
                : $"⚠️ DIFFERENT ({prefixTilt.Length / 1024} KB vs {p1.Length / 1024} KB)"));

            // The live selector on the whole volume = what NowCast serves once the volume is complete.
            var live = Level2Format.SelectLatestSweep(raw[..24], blocks, icao);
            Console.WriteLine($"   live selector serves the scan at {(live.dataTime is { } lt ? lt.ToString("HH:mm:ss") : "?")}" +
                              $" (velocity complete: {live.velComplete})");
        }
        Console.WriteLine($"\nTOTAL over {Math.Min(volumes, keys.Count - first)} volume(s): planned {planned}, cut {cut}");
        return 0;
    }
}
