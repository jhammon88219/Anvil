// --hrrr [yyyyMMddHH] [--png DIR]: the HRRR data path (HrrrService + Grib2Decoder) against the LIVE bucket. Finds the
// newest run (or the one named), prints which hours have landed, then decodes composite reflectivity at f00 and +1 h
// and 2–5 km updraft helicity for the first hour: grid, corners, value range, and WHERE the max is — compare those
// against Pivotal / COD for the same run and hour. --png writes each field as a picture (north up, grid space) to eyeball.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;

static class HrrrCheck
{
	public static async Task<int> RunAsync(string[] args)
	{
		var svc = new HrrrService();
		var pngAt = Array.IndexOf(args, "--png");
		var pngDir = pngAt >= 0 && pngAt + 1 < args.Length ? args[pngAt + 1] : null;
		var named = args.Skip(1).FirstOrDefault(a => a.Length == 10 && a.All(char.IsDigit));

		var sw = Stopwatch.StartNew();
		HrrrRunStatus? run = named is null
			? await svc.FindLatestRunAsync()
			: await svc.GetRunStatusAsync(DateTimeOffset.ParseExact(named, "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
		if (run is null) { Console.WriteLine("No HRRR run with f00 in the last 6 h."); return 1; }
		Console.WriteLine($"== HRRR {run.RunUtc:yyyy-MM-dd HH}Z  (listed in {sw.ElapsedMilliseconds} ms)");
		Console.WriteLine($"   hourly  in: {Hours(run.HourlyIn)} of f00–f{run.LastHour:D2}{(run.IsComplete ? "  complete" : "")}");
		Console.WriteLine($"   15-min  in: {Hours(run.SubHourlyIn)}");

		var picks = new List<(HrrrField Field, int Lead)> { (HrrrFields.CompositeReflectivity, 0) };
		if (run.SubHourlyIn.Contains(1)) picks.Add((HrrrFields.CompositeReflectivity, 60));
		if (run.SubHourlyIn.Contains(1)) picks.Add((HrrrFields.CompositeReflectivity, 45));
		if (run.HourlyIn.Contains(1)) picks.Add((HrrrFields.UpdraftHelicity25, 60));
		if (run.HourlyIn.Contains(24)) picks.Add((HrrrFields.CompositeReflectivity, 24 * 60)); // past the 15-min file's f18

		foreach (var (field, lead) in picks)
		{
			sw.Restart();
			var f = await svc.GetFieldAsync(run.RunUtc, field, lead);
			var ms = sw.ElapsedMilliseconds;
			Report(field, f, ms);
			if (pngDir is not null)
			{
				Directory.CreateDirectory(pngDir);
				var path = Path.Combine(pngDir, $"hrrr-{f.RunUtc:yyyyMMddHH}-{field.Id}-{lead:D4}.png");
				WritePng(path, f, field.IsHourlyMax);
				Console.WriteLine($"   png {path}");
			}
		}

		// The page's frames (what the ForeCast Short Range section draws): sizes + build time, cold then cached.
		if (run.SubHourlyIn.Contains(1)) await Frame(svc, run, HrrrFields.CompositeReflectivity, 60, false);
		if (run.HourlyIn.Contains(3)) await Frame(svc, run, HrrrFields.UpdraftHelicity25, 180, true);

		// --whole: the console's whole-run download — every reflectivity frame to f18, 3 at a time (as
		// ModelPlaybackViewModel does), then the UH run-so-far series IN ORDER (each frame one decode on the last).
		if (args.Contains("--whole"))
		{
			var leads = ModelPlaybackViewModel.LeadsOf(run, HrrrFields.CompositeReflectivity, extended: false)
				.Where(l => ModelPlaybackViewModel.HasLanded(run, HrrrFields.CompositeReflectivity, l, false)).ToList();
			sw.Restart();
			var next = 0;
			async Task Worker() { while (next < leads.Count) await svc.GetFrameAsync(run.RunUtc, HrrrFields.CompositeReflectivity, leads[next++], false); }
			await Task.WhenAll(Worker(), Worker(), Worker());
			Console.WriteLine($"== whole run: {leads.Count} reflectivity frames in {sw.Elapsed.TotalSeconds:0.0} s");
			var hours = ModelPlaybackViewModel.LeadsOf(run, HrrrFields.UpdraftHelicity25, extended: false)
				.Where(l => ModelPlaybackViewModel.HasLanded(run, HrrrFields.UpdraftHelicity25, l, true)).ToList();
			sw.Restart();
			foreach (var l in hours) await svc.GetFrameAsync(run.RunUtc, HrrrFields.UpdraftHelicity25, l, true);
			Console.WriteLine($"== whole run: {hours.Count} UH run-so-far frames in {sw.Elapsed.TotalSeconds:0.0} s");
		}
		return 0;
	}

	private static async Task Frame(HrrrService svc, HrrrRunStatus run, HrrrField field, int lead, bool runSoFar)
	{
		for (var pass = 0; pass < 2; pass++)
		{
			var sw = Stopwatch.StartNew();
			var json = await svc.GetFrameAsync(run.RunUtc, field, lead, runSoFar);
			var file = Path.Combine(svc.CacheDirectory, "hrrr", run.RunUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture),
				$"{field.Id}{(runSoFar ? "-run" : "")}-{lead:D4}.bin");
			Console.WriteLine($"== frame {field.Id}{(runSoFar ? " run so far" : "")} {lead} min ({(pass == 0 ? "build" : "cached")}) {sw.ElapsedMilliseconds} ms  "
				+ $"frame {new FileInfo(file).Length / 1024} KB, mesh {new FileInfo(Path.Combine(svc.CacheDirectory, "hrrr", "mesh.json")).Length / 1024} KB");
			if (pass == 0) Console.WriteLine($"   {json[..Math.Min(json.Length, 160)]}");
		}
	}

	private static string Hours(IReadOnlyList<int> hours) => hours.Count == 0 ? "none" : $"f{hours[0]:D2}–f{hours[^1]:D2} ({hours.Count})";

	private static void Report(HrrrField field, GribField f, long ms)
	{
		var g = f.Grid;
		Console.WriteLine($"-- {field.Name}  lead {f.Lead.TotalMinutes:0} min, valid {f.ValidUtc:MM-dd HH:mm}Z"
			+ (f.IntervalStartUtc is { } s ? $" (max since {s:HH:mm}Z)" : "") + $"  [{ms} ms]");
		Console.WriteLine($"   param {f.Discipline}/{f.Category}/{f.Number}  grid {g.Nx}×{g.Ny} @ {g.Dx / 1000:0.###} km, LoV {g.LoV:0.###}, latin {g.Latin1:0.###}/{g.Latin2:0.###}");
		var (swLat, swLon) = g.ToLatLon(0, 0);
		var (neLat, neLon) = g.ToLatLon(g.Nx - 1, g.Ny - 1);
		Console.WriteLine($"   corners SW {swLat:0.000}, {swLon:0.000}   NE {neLat:0.000}, {neLon:0.000}");

		float min = float.MaxValue, max = float.MinValue;
		int maxAt = 0, nan = 0;
		for (var k = 0; k < f.Values.Length; k++)
		{
			var v = f.Values[k];
			if (float.IsNaN(v)) { nan++; continue; }
			if (v < min) min = v;
			if (v > max) { max = v; maxAt = k; }
		}
		var (mLat, mLon) = g.ToLatLon(maxAt % g.Nx, maxAt / g.Nx);
		Console.WriteLine($"   range {min:0.0} … {max:0.0}  at {mLat:0.00}, {mLon:0.00}   missing {nan}");
		var thresholds = field.IsHourlyMax ? new[] { 25f, 75f, 150f } : new[] { 20f, 40f, 55f };
		Console.WriteLine("   " + string.Join("   ", thresholds.Select(t => $"≥{t:0}: {f.Values.Count(v => v >= t)}")));
		// Round trip: the projection both ways must land back on the same cell.
		var (ri, rj) = g.ToGrid(mLat, mLon);
		Console.WriteLine($"   round trip cell {maxAt % g.Nx},{maxAt / g.Nx} → {ri:0.000},{rj:0.000}");
	}

	// A plain RGB PNG in grid space, north up (the grid's rows run south → north, so rows are written in reverse).
	private static void WritePng(string path, GribField f, bool helicity)
	{
		var (w, h) = (f.Grid.Nx, f.Grid.Ny);
		var raw = new byte[h * (1 + w * 3)];
		for (var row = 0; row < h; row++)
		{
			var o = row * (1 + w * 3);
			var j = h - 1 - row;
			for (var i = 0; i < w; i++)
			{
				var (r, gg, b) = Colour(f.At(i, j), helicity);
				raw[o + 1 + i * 3] = r; raw[o + 2 + i * 3] = gg; raw[o + 3 + i * 3] = b;
			}
		}
		using var fs = File.Create(path);
		fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
		var ihdr = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
		BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
		ihdr[8] = 8; ihdr[9] = 2;
		Chunk(fs, "IHDR", ihdr);
		using (var ms = new MemoryStream())
		{
			using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
			Chunk(fs, "IDAT", ms.ToArray());
		}
		Chunk(fs, "IEND", Array.Empty<byte>());
	}

	private static (byte, byte, byte) Colour(float v, bool helicity)
	{
		if (float.IsNaN(v)) return (255, 0, 255);
		if (helicity) return v < 25 ? ((byte)20, (byte)20, (byte)20) : v < 75 ? ((byte)200, (byte)200, (byte)0) : v < 150 ? ((byte)255, (byte)120, (byte)0) : ((byte)255, (byte)0, (byte)255);
		if (v < 10) return (20, 20, 20);
		if (v < 20) return (0, 120, 160);
		if (v < 35) return (0, 190, 0);
		if (v < 45) return (240, 220, 0);
		if (v < 55) return (255, 120, 0);
		if (v < 65) return (220, 0, 0);
		return (255, 0, 255);
	}

	private static void Chunk(Stream s, string type, byte[] data)
	{
		Span<byte> len = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
		s.Write(len);
		var typed = new byte[4 + data.Length];
		System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
		data.CopyTo(typed, 4);
		s.Write(typed);
		BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(typed));
		s.Write(len);
	}

	private static uint Crc32(byte[] data)
	{
		var crc = 0xFFFFFFFFu;
		foreach (var b in data)
		{
			crc ^= b;
			for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
		}
		return ~crc;
	}
}
