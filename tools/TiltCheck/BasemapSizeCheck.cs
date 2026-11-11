// --basemap-sizes: what each offline-map area REALLY costs on the newest Protomaps build — the numbers behind
// BasemapAreas' StandardMegabytes / FullMegabytes, the world overview and the OVERLAPS (BasemapAreas.Overlaps).
// Plans only (BasemapExtractPlanner over range reads of the build's directories) — no tile data is downloaded.
//
// Every plan carries the world overview, as a real download does, so an area's number is its cost ON TOP of the
// overview (its own zoom 0-7 tiles are already in the overview), and an overlap is what two areas share:
//   overlap(A,B) = cost(A) + cost(B) - cost(A+B)
// Paste the printed lines into BasemapAreas when they drift.

using System.Diagnostics;
using Anvil.Models;
using Anvil.Services;

static class BasemapSizeCheck
{
	public static async Task<int> RunAsync()
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil (offline map size check)");
		var build = await ProtomapsBuilds.LatestAsync(http, CancellationToken.None);
		var source = new HttpPmtilesSource(http, build);
		Console.WriteLine($"== Protomaps build {ProtomapsBuilds.DayOf(build)}  ({build})");

		foreach (var (label, zoom) in new[] { ("Standard", BasemapExtractPlanner.StandardMaxZoom), ("Full", BasemapExtractPlanner.FullMaxZoom) })
		{
			Console.WriteLine();
			Console.WriteLine($"-- {label} (zoom {zoom})");
			var sw = Stopwatch.StartNew();
			async Task<double> Mb(params BasemapArea[] areas)
			{
				var plan = await BasemapExtractPlanner.PlanAsync(source, areas, zoom, CancellationToken.None,
					overviewZoom: BasemapAreas.WorldOverviewZoom);
				return plan.TileDataBytes / 1e6;
			}

			var world = await Mb();
			Console.WriteLine($"   world overview      {world,10:0.0} MB");

			var cost = new Dictionary<string, double>();
			foreach (var a in BasemapAreas.All)
			{
				cost[a.Code] = await Mb(a) - world;
				Console.WriteLine($"   {a.Code,-4} {a.Name,-38} {cost[a.Code],10:0.0} MB  (table: {(zoom == BasemapExtractPlanner.FullMaxZoom ? a.FullMegabytes : a.StandardMegabytes)})");
			}

			// Every pair whose boxes touch: what they share.
			var all = BasemapAreas.All;
			for (var i = 0; i < all.Count; i++)
			for (var j = i + 1; j < all.Count; j++)
			{
				if (!Touch(all[i], all[j])) continue;
				var both = await Mb(all[i], all[j]) - world;
				Console.WriteLine($"   overlap {all[i].Code}+{all[j].Code,-4}                          {cost[all[i].Code] + cost[all[j].Code] - both,10:0.0} MB");
			}

			var everything = await Mb(all.ToArray());
			Console.WriteLine($"   ALL areas + overview {everything,10:0.0} MB   ({sw.Elapsed.TotalSeconds:0} s)");
		}
		return 0;
	}

	private static bool Touch(BasemapArea a, BasemapArea b) =>
		a.Boxes.Any(x => b.Boxes.Any(y => x.West <= y.East && y.West <= x.East && x.South <= y.North && y.South <= x.North));
}
