using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// THE single home of SPC colour + level truth: every official risk level, probability step and
	/// Conditional Intensity Group, with the colours and names NOAA publishes.
	///
	/// <para>
	/// ⚠️⚠️ <b>Nothing in here is authored.</b> The data is harvested by
	/// <c>tools/make_spc_catalog.py</c> from NOAA's own ArcGIS layer symbology into the embedded
	/// <c>Assets/spc-risk-catalog.json</c>. It replaced three hand-typed tables that had drifted badly —
	/// a 15% wind risk rendered RED where SPC renders it yellow, fire weather matched nothing at all and
	/// drew flat grey, and the days 4-8 products reused a palette that is not theirs. Re-run the
	/// generator after an NWS service change and commit the diff; never hand-edit a hex.
	/// </para>
	///
	/// <para>
	/// ⚠️ <b>THE PRECEDENCE RULE: the FEED wins, this is the FALLBACK.</b> Where a feature carries its
	/// own <c>fill</c>/<c>stroke</c> (the live convective <c>.lyr.geojson</c>), render those. This
	/// catalog supplies (a) legend rows for levels not issued today, (b) colours for the two feeds that
	/// publish none — fire weather (keyed on <c>dn</c>) and the IEM past-outlook archive (keyed on a
	/// threshold string). Inverting that is how a stale local table came to override published truth.
	/// </para>
	///
	/// <para>
	/// ⚠️ <b>Offline by construction.</b> The JSON is an <c>EmbeddedResource</c> compiled into
	/// Anvil.Core, so there is no file to locate, no network call and no cache to warm — it is present
	/// on a machine that has never been online, which is the point of the app. The cost is that it is
	/// frozen at build time; <c>SpcRiskCatalogTests</c> carries the opt-in live diff that catches a
	/// service change in development instead of leaving it to be noticed months later (SPC's
	/// conditional-intensity change shipped in March 2026 and went unnoticed here until September).
	/// </para>
	/// </summary>
	public static class SpcRiskCatalog
	{
		private const string ResourceName = "Anvil.Assets.spc-risk-catalog.json";

		// Parsed once on first use. A record-only payload, so sharing it across threads is safe.
		private static readonly Lazy<CatalogDocument> Document = new(Load, isThreadSafe: true);

		/// <summary>When the embedded catalog was harvested from NOAA (UTC, ISO-8601).</summary>
		public static string GeneratedUtc => Document.Value.GeneratedUtc;

		/// <summary>The NWS service change the catalog was last reconciled against.</summary>
		public static string ServiceChange => Document.Value.ServiceChange;

		/// <summary>
		/// The full official scale for a product family, least severe first. Never null — a product with
		/// no published scale returns an empty one rather than making every caller null-check.
		/// </summary>
		public static SpcProductScale ScaleFor(SpcOutlookType type) =>
			Document.Value.Scales.TryGetValue(type, out var scale)
				? scale
				: new SpcProductScale(type, Array.Empty<SpcRiskLevel>());

		/// <summary>
		/// The level a feature's short code names — the feed's <c>LABEL</c> or IEM's <c>threshold</c>
		/// ("MRGL", "0.15", "CIG1", "ELEV"). Null when the product has no such level.
		/// <para>
		/// ⚠️ Legacy <c>"SIGN"</c> maps to CIG1. Before NWS service change 26-11 (2026-03-02) SPC drew a
		/// single significant-severe area under that label; every archived outlook older than that — i.e.
		/// everything PastCast can replay — still carries it, so dropping the alias would silently stop
		/// hatching historical outlooks.
		/// </para>
		/// </summary>
		public static SpcRiskLevel? Level(SpcOutlookType type, string? code)
		{
			if (string.IsNullOrWhiteSpace(code))
			{
				return null;
			}

			var wanted = code.Trim();
			if (string.Equals(wanted, LegacySignificantCode, StringComparison.OrdinalIgnoreCase))
			{
				wanted = "CIG1";
			}

			var levels = ScaleFor(type).Levels;
			foreach (var level in levels)
			{
				if (string.Equals(level.Code, wanted, StringComparison.OrdinalIgnoreCase))
				{
					return level;
				}
			}

			// Probability codes are numeric strings, and the two sources spell them differently
			// ("0.15" live, "0.150000" has been seen from ArcGIS). Fall back to a numeric compare so a
			// formatting difference is not mistaken for an unknown level.
			if (double.TryParse(wanted, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
			{
				foreach (var level in levels)
				{
					if (level.Kind == SpcLevelKind.Solid &&
						double.TryParse(level.Code, NumberStyles.Float, CultureInfo.InvariantCulture, out var known) &&
						Math.Abs(known - value) < 1e-6)
					{
						return level;
					}
				}
			}

			return null;
		}

		/// <summary>
		/// The SOLID level carrying this <c>dn</c> ordinal — the join for the two feeds that ship an
		/// ordinal and no colours (fire weather, and the days 4-8 outlooks).
		/// <para>
		/// ⚠️ Solid levels only, deliberately. <see cref="SpcRiskLevel.Dn"/> is unique within a kind, not
		/// within a product: the tornado outlook has both a 2% level and CIG2 at <c>dn == 2</c>, so an
		/// unscoped lookup could colour a probability area as an intensity group.
		/// </para>
		/// </summary>
		public static SpcRiskLevel? Level(SpcOutlookType type, double dn)
		{
			foreach (var level in ScaleFor(type).Levels)
			{
				if (level.Kind == SpcLevelKind.Solid && Math.Abs(level.Dn - dn) < 1e-6)
				{
					return level;
				}
			}

			return null;
		}

		/// <summary>The pre-2026 single significant-severe label the CIG groups replaced.</summary>
		public const string LegacySignificantCode = "SIGN";

		/// <summary>Every product family the catalog carries a scale for.</summary>
		public static IEnumerable<SpcOutlookType> Products => Document.Value.Scales.Keys;

		// ── Loading ─────────────────────────────────────────────────────────────────────────────────

		private sealed record CatalogDocument(
			string GeneratedUtc,
			string ServiceChange,
			IReadOnlyDictionary<SpcOutlookType, SpcProductScale> Scales);

		private static CatalogDocument Load()
		{
			// An embedded resource cannot go missing at runtime, so a failure here is a broken BUILD.
			// Say so plainly rather than degrading to blank legends, which is the shape of bug that let
			// the old hardcoded tables stay wrong for months.
			using var stream = typeof(SpcRiskCatalog).GetTypeInfo().Assembly
				.GetManifestResourceStream(ResourceName)
				?? throw new InvalidOperationException(
					$"Embedded SPC risk catalog '{ResourceName}' is missing. It is generated by " +
					"tools/make_spc_catalog.py and declared as an EmbeddedResource in Anvil.Core.csproj.");

			using var json = JsonDocument.Parse(stream);
			var root = json.RootElement;

			var scales = new Dictionary<SpcOutlookType, SpcProductScale>();
			foreach (var entry in root.GetProperty("scales").EnumerateArray())
			{
				var typeName = entry.GetProperty("type").GetString();
				if (!Enum.TryParse<SpcOutlookType>(typeName, ignoreCase: false, out var type))
				{
					// A product this build has no enum member for. Skipping keeps a catalog generated
					// against a newer Anvil loadable instead of failing the whole file.
					continue;
				}

				var levels = entry.GetProperty("levels").EnumerateArray().Select(ReadLevel).ToArray();
				scales[type] = new SpcProductScale(type, levels);
			}

			return new CatalogDocument(
				root.GetProperty("_generatedUtc").GetString() ?? string.Empty,
				root.GetProperty("_serviceChange").GetString() ?? string.Empty,
				scales);
		}

		private static SpcRiskLevel ReadLevel(JsonElement element) => new(
			element.GetProperty("code").GetString() ?? string.Empty,
			element.GetProperty("dn").GetDouble(),
			element.GetProperty("officialName").GetString() ?? string.Empty,
			element.GetProperty("fill").GetString() ?? string.Empty,
			element.GetProperty("stroke").GetString() ?? string.Empty,
			Enum.Parse<SpcLevelKind>(element.GetProperty("kind").GetString()!),
			Enum.Parse<SpcHatchPattern>(element.GetProperty("hatch").GetString()!));
	}
}
