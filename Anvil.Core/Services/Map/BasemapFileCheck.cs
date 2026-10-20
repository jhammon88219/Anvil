using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The three checks on a map file the user brings (the setup window's "Use a map file you already have", Settings →
	/// Basemap's Add a map file…), each as a line in plain words: can Anvil read it · which radars it covers · its
	/// detail, size and date. Only an unreadable file — or one in another map style — is refused.
	/// </summary>
	public sealed record BasemapFileCheck(bool CanUse, string ReadLine, string CoverLine, string DetailLine)
	{
		/// <summary>Read the file's header + metadata (a few KB) and judge it. <paramref name="radars"/> = (lat, lon) of the
		/// NEXRAD sites the cover line counts.</summary>
		public static BasemapFileCheck Inspect(string path, IReadOnlyCollection<(double Lat, double Lon)> radars)
		{
			PmtilesHeader header;
			byte[] metadata;
			long size;
			DateTime written;
			try
			{
				using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				var first = new byte[PmtilesHeader.FirstReadLength];
				var n = file.Read(first, 0, first.Length);
				header = PmtilesHeader.Parse(first.AsSpan(0, n));
				var raw = new byte[checked((int)Math.Min(header.MetadataLength, 16 * 1024 * 1024))];
				file.Position = (long)header.MetadataOffset;
				file.ReadExactly(raw);
				metadata = PmtilesHeader.Decompress(raw, header.InternalCompression);
				size = file.Length;
				written = File.GetLastWriteTime(path);
			}
			catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException
				or ArgumentException or OverflowException)
			{
				return new BasemapFileCheck(false, "This isn't a map file Anvil can read.", "", "");
			}

			if (header.TileType != 1 || !IsProtomapsMap(metadata))
			{
				return new BasemapFileCheck(false, "This map is drawn a different way than Anvil's maps, so Anvil can't use it.", "", "");
			}

			// A map Anvil downloaded names its areas (BasemapFileNames) — its header's bounds are the WORLD (the overview it
			// carries), so they'd claim every radar. Any other file: its header's bounds.
			double w = header.MinLonE7 / 1e7, s = header.MinLatE7 / 1e7, e = header.MaxLonE7 / 1e7, n2 = header.MaxLatE7 / 1e7;
			var named = BasemapFileNames.TryParse(path);
			var inside = named is not null
				? radars.Count(r => named.Areas.Any(a => a.Contains(r.Lat, r.Lon)))
				: radars.Count(r => r.Lat >= s && r.Lat <= n2 && r.Lon >= w && r.Lon <= e);
			var cover = inside == radars.Count ? $"Covers all {radars.Count} NEXRAD radars."
				: inside == 0 ? "Covers none of the NEXRAD radars — it's a map of somewhere else."
				: $"Covers {inside} of {radars.Count} NEXRAD radars.";

			var detail = header.MaxZoom >= BasemapExtractPlanner.FullMaxZoom ? "Full detail"
				: header.MaxZoom >= BasemapExtractPlanner.StandardMaxZoom ? "Standard detail"
				: "Less detail than Standard";
			const double Gib = 1024.0 * 1024 * 1024; // Explorer's units, so the size matches what the user sees there
			var sizeWords = size >= Gib
				? string.Create(CultureInfo.CurrentCulture, $"{size / Gib:0.0} GB")
				: string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, Math.Round(size / (1024.0 * 1024))):0} MB");
			var line = $"{detail} · {sizeWords} · {written.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
			return new BasemapFileCheck(true, "A map Anvil can read.", cover, line);
		}

		// Anvil's styles read Protomaps' layers (earth, water, roads, places…) — a map without them would draw blank.
		private static bool IsProtomapsMap(byte[] metadata)
		{
			try
			{
				using var doc = JsonDocument.Parse(metadata);
				if (!doc.RootElement.TryGetProperty("vector_layers", out var layers) || layers.ValueKind != JsonValueKind.Array) return false;
				var ids = layers.EnumerateArray()
					.Select(l => l.TryGetProperty("id", out var id) ? id.GetString() : null)
					.OfType<string>().ToHashSet(StringComparer.Ordinal);
				return ids.Contains("earth") && ids.Contains("water") && ids.Contains("roads");
			}
			catch (JsonException)
			{
				return false;
			}
		}
	}
}
