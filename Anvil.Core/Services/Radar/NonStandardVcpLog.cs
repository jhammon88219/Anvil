using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>
	/// Every scan pattern Anvil has seen that <see cref="VcpCatalog"/> doesn't list, per site — persisted to
	/// <c>%LocalAppData%\Anvil\Network\nonstandard-vcps.json</c>. Fed by <c>Level2RadarService</c> each time a
	/// frame's VCP parses as "unlisted" (<c>Level2Format.IsPlausibleUnlisted</c>).
	/// </summary>
	/// <remarks>
	/// ⚠️ THREAD-SAFE (a lock), unlike <c>SiteUsageStore</c>: frames are parsed on thread-pool threads — a
	/// replay backfill runs 12 at once — so <see cref="Record"/> is called from anywhere. <see cref="Changed"/>
	/// fires on the CALLER's thread; a VM must marshal to the UI thread.
	/// ⚠️ Saves only when something NEW is learned (a new frame time or pattern), never on a repeat — a live
	/// loop re-reads the same frame every poll. Unique temp + move, same as the other stores.
	/// Not in AppSettings: it's observed data, not a preference.
	/// </remarks>
	public sealed class NonStandardVcpLog
	{
		/// <summary>Frame times kept per sighting — enough for weeks of a test pattern at 5-min volumes.</summary>
		internal const int MaxFrameTimes = 2000;

		private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

		private readonly object _gate = new();
		private readonly ILogger<NonStandardVcpLog> _logger;
		private readonly string _filePath;
		private readonly Dictionary<string, NonStandardVcpSighting> _sightings;

		/// <param name="directory">TESTS ONLY. Null (the DI default) uses <c>%LocalAppData%\Anvil\Network</c>.</param>
		public NonStandardVcpLog(ILogger<NonStandardVcpLog> logger, string? directory = null)
		{
			_logger = logger;
			var dir = directory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "Network");
			Directory.CreateDirectory(dir);
			_filePath = Path.Combine(dir, "nonstandard-vcps.json");
			_sightings = Load();
		}

		/// <summary>Raised after a sighting is added or grows, with the site ICAO. Caller's thread.</summary>
		public event EventHandler<string>? Changed;

		/// <summary>A snapshot of every sighting, newest last-seen first.</summary>
		public IReadOnlyList<NonStandardVcpSighting> All
		{
			get
			{
				lock (_gate)
				{
					return _sightings.Values.OrderByDescending(s => s.LastSeenUtc).ToList();
				}
			}
		}

		/// <summary>A snapshot of one site's sightings, newest last-seen first.</summary>
		public IReadOnlyList<NonStandardVcpSighting> ForSite(string siteId) =>
			All.Where(s => string.Equals(s.SiteId, siteId, StringComparison.OrdinalIgnoreCase)).ToList();

		/// <summary>Note one frame on an unlisted pattern. A no-op for a catalogued or unparsed number.</summary>
		public void Record(string siteId, int vcp, DateTimeOffset frameTime, IReadOnlyList<float>? tilts)
		{
			if (vcp <= 0 || VcpCatalog.IsKnown(vcp) || string.IsNullOrEmpty(siteId))
			{
				return;
			}
			var stamp = TruncateToMinute(frameTime.ToUniversalTime());
			bool isNew;
			lock (_gate)
			{
				var key = $"{siteId.ToUpperInvariant()}/{vcp}";
				isNew = !_sightings.TryGetValue(key, out var s);
				if (s is null)
				{
					s = new NonStandardVcpSighting
					{
						SiteId = siteId.ToUpperInvariant(),
						Vcp = vcp,
						FirstSeenUtc = stamp,
						LastSeenUtc = stamp,
					};
					_sightings[key] = s;
				}
				else if (s.FrameTimes.Contains(stamp))
				{
					return; // a re-read of a frame we already have — nothing new, no save
				}

				s.FrameTimes.Add(stamp);
				s.FrameTimes.Sort();
				if (s.FrameTimes.Count > MaxFrameTimes)
				{
					s.FrameTimes.RemoveRange(0, s.FrameTimes.Count - MaxFrameTimes);
				}
				if (stamp < s.FirstSeenUtc) s.FirstSeenUtc = stamp;
				if (stamp >= s.LastSeenUtc)
				{
					s.LastSeenUtc = stamp;
					if (tilts is { Count: > 0 }) s.Tilts = tilts.ToList();
				}
				else if (s.Tilts.Count == 0 && tilts is { Count: > 0 })
				{
					s.Tilts = tilts.ToList();
				}
				Save();
			}
			if (isNew)
			{
				// Rare and worth noticing: a pattern nobody has told Anvil about.
				_logger.LogInformation("Non-standard VCP {Vcp} seen at {Site} ({Time:u})", vcp, siteId, stamp);
				RadarDiagnostics.Log("svc", "vcp.unlisted", ("site", siteId), ("vcp", vcp),
					("tilts", tilts is null ? "" : string.Join(",", tilts.Select(t => t.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)))));
			}
			Changed?.Invoke(this, siteId.ToUpperInvariant());
		}

		private static DateTimeOffset TruncateToMinute(DateTimeOffset t) =>
			new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);

		// ── persistence ──

		private Dictionary<string, NonStandardVcpSighting> Load()
		{
			var empty = new Dictionary<string, NonStandardVcpSighting>(StringComparer.OrdinalIgnoreCase);
			if (!File.Exists(_filePath)) return empty;
			try
			{
				var list = JsonSerializer.Deserialize<List<NonStandardVcpSighting>>(File.ReadAllText(_filePath));
				if (list is null) return empty;
				foreach (var s in list.Where(s => s.Vcp > 0 && !string.IsNullOrEmpty(s.SiteId)))
				{
					// A pattern added to the catalog since it was seen is no longer non-standard — drop it.
					if (!VcpCatalog.IsKnown(s.Vcp)) empty[$"{s.SiteId}/{s.Vcp}"] = s;
				}
				return empty;
			}
			catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
			{
				_logger.LogWarning(ex, "Non-standard VCP log unreadable; starting empty.");
				return empty;
			}
		}

		// Called under _gate.
		private void Save()
		{
			try
			{
				var json = JsonSerializer.Serialize(
					_sightings.Values.OrderBy(s => s.SiteId, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Vcp).ToList(),
					SerializerOptions);
				var temp = $"{_filePath}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
				File.WriteAllText(temp, json);
				File.Move(temp, _filePath, overwrite: true);
			}
			catch (Exception ex)
			{
				// Catch-all ON PURPOSE: Record runs inside a frame fetch — a log must never fail a frame.
				_logger.LogWarning(ex, "Failed to save the non-standard VCP log.");
			}
		}
	}
}
