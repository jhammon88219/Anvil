using System;
using System.Globalization;
using System.Text.Json;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Colours the two SPC feeds that publish NO symbology of their own, turning each into the schema
	/// <c>outlook.js</c> expects: per-feature <c>fill</c>/<c>stroke</c> hex + a <c>LABEL</c> (a CIG code
	/// ⇒ hatched, anything else ⇒ solid fill) + <c>ISSUE_ISO</c>/<c>EXPIRE_ISO</c> for the times readout.
	/// That is what lets both reuse the live render path with zero JS change.
	///
	/// <para>The two, and how each is keyed:</para>
	/// <list type="bullet">
	/// <item><see cref="TryBuildProduct"/> — the IEM past-outlook archive, which carries a bare
	/// <c>category</c>/<c>threshold</c> string ("MRGL", "0.15", "SIGN").</item>
	/// <item><see cref="TryColorizeByDn"/> — the live NOAA ArcGIS fire-weather feed, which carries only a
	/// numeric <c>dn</c> ordinal and no label at all.</item>
	/// </list>
	///
	/// <para>
	/// ⚠️ Every colour comes from <see cref="SpcRiskCatalog"/> — NOAA's published symbology, harvested by
	/// <c>tools/make_spc_catalog.py</c>. This file used to hold THREE hand-typed tables (categorical,
	/// fire, and one shared probability ramp) and all three had drifted: the probability ramp was SPC's
	/// stroke colours shifted a level, so a 15% wind risk drew RED where SPC draws it yellow; the fire
	/// table was keyed on strings the live feed never emits. Don't reintroduce a local table — add to the
	/// catalog and regenerate.
	/// </para>
	/// </summary>
	public static class SpcOutlookColors
	{
		// Last-resort neutral, matching outlook.js's own coalesce. Reached only by a level the catalog
		// has never seen — which, after a service change, is the signal to re-run the generator.
		private const string UnknownFill = "#888888";
		private const string UnknownStroke = "#555555";

		/// <summary>
		/// Builds one product's renderer-ready GeoJSON from an IEM outlook collection, keeping only the
		/// features for <paramref name="type"/> and colouring each. Returns false (with an empty
		/// document) when the issuance carried nothing for this product.
		/// </summary>
		public static bool TryBuildProduct(JsonElement iemRoot, SpcOutlookType type,
			out string geoJson, out SpcOutlookTimes? times)
		{
			geoJson = string.Empty;
			times = null;

			if (iemRoot.ValueKind != JsonValueKind.Object ||
				!iemRoot.TryGetProperty("features", out var features) ||
				features.ValueKind != JsonValueKind.Array)
			{
				return false;
			}

			var wantCategory = CategoryFor(type); // null ⇒ fire (take every feature)
			var buffer = new System.Buffers.ArrayBufferWriter<byte>();
			using var writer = new Utf8JsonWriter(buffer);
			var written = 0;
			DateTimeOffset? issue = null, product = null, expire = null;

			writer.WriteStartObject();
			writer.WriteString("type", "FeatureCollection");
			writer.WritePropertyName("features");
			writer.WriteStartArray();

			foreach (var f in features.EnumerateArray())
			{
				if (!f.TryGetProperty("properties", out var props) ||
					!f.TryGetProperty("geometry", out var geom))
				{
					continue;
				}

				var category = GetStr(props, "category");
				if (wantCategory is not null &&
					!string.Equals(category, wantCategory, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var threshold = GetStr(props, "threshold") ?? string.Empty;
				var (fill, stroke, label) = Style(type, threshold);

				// First feature's times represent the issuance (all share them).
				issue ??= ParseIso(GetStr(props, "issue"));
				product ??= ParseIso(GetStr(props, "product_issue"));
				expire ??= ParseIso(GetStr(props, "expire"));

				writer.WriteStartObject();
				writer.WriteString("type", "Feature");
				writer.WritePropertyName("properties");
				writer.WriteStartObject();
				writer.WriteString("fill", fill);
				writer.WriteString("stroke", stroke);
				writer.WriteString("LABEL", label);
				writer.WriteString("threshold", threshold);
				if (issue is { } iss) writer.WriteString("ISSUE_ISO", iss.ToString("O"));
				if (expire is { } exp) writer.WriteString("EXPIRE_ISO", exp.ToString("O"));
				writer.WriteEndObject();
				writer.WritePropertyName("geometry");
				geom.WriteTo(writer);
				writer.WriteEndObject();
				written++;
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
			writer.Flush();

			if (written == 0)
			{
				return false;
			}

			geoJson = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
			// Issued = the product-issuance time if present (else the valid start); valid window = issue→expire.
			times = new SpcOutlookTimes(product ?? issue, issue, expire);
			return true;
		}

		/// <summary>
		/// Adds <c>fill</c>/<c>stroke</c>/<c>LABEL</c> to every feature of a GeoJSON collection whose
		/// only styling key is a numeric <c>dn</c> ordinal — NOAA's ArcGIS fire-weather layers. Existing
		/// properties are preserved (the times readout reads <c>valid</c>/<c>expire</c> from them).
		/// <para>
		/// ⚠️ This is why fire weather used to draw FLAT GREY at every risk level: the ArcGIS GeoJSON
		/// query returns attributes only, never symbology, so <c>outlook.js</c>'s
		/// <c>['coalesce', ['get','fill'], '#888888']</c> was the whole colour story — Elevated,
		/// Critical and Extreme were indistinguishable on the map.
		/// </para>
		/// <para>
		/// ⚠️ Feature property names here are LOWERCASE (<c>dn</c>), unlike the SPC convective feed's
		/// uppercase <c>DN</c>. Both spellings are accepted so one helper serves either shape.
		/// </para>
		/// </summary>
		/// <returns>False when the document is not a feature collection, or no feature could be
		/// coloured — in which case the caller should leave the cached file untouched.</returns>
		public static bool TryColorizeByDn(string geoJson, SpcOutlookType type, out string colorized)
		{
			colorized = string.Empty;
			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(geoJson);
			}
			catch (JsonException)
			{
				return false;
			}

			using (doc)
			{
				var root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object ||
					!root.TryGetProperty("features", out var features) ||
					features.ValueKind != JsonValueKind.Array)
				{
					return false;
				}

				var buffer = new System.Buffers.ArrayBufferWriter<byte>();
				using var writer = new Utf8JsonWriter(buffer);
				var matched = 0;

				writer.WriteStartObject();
				writer.WriteString("type", "FeatureCollection");
				writer.WritePropertyName("features");
				writer.WriteStartArray();

				foreach (var f in features.EnumerateArray())
				{
					if (f.ValueKind != JsonValueKind.Object)
					{
						continue;
					}

					var level = TryReadDn(f, out var dn) ? SpcRiskCatalog.Level(type, dn) : null;
					if (level is not null)
					{
						matched++;
					}

					writer.WriteStartObject();
					foreach (var member in f.EnumerateObject())
					{
						if (!member.NameEquals("properties"))
						{
							member.WriteTo(writer);
						}
					}

					writer.WritePropertyName("properties");
					writer.WriteStartObject();
					if (f.TryGetProperty("properties", out var props) &&
						props.ValueKind == JsonValueKind.Object)
					{
						foreach (var p in props.EnumerateObject())
						{
							// Drop any pre-existing styling keys so ours are authoritative and can't
							// be duplicated into an invalid object.
							if (!p.NameEquals("fill") && !p.NameEquals("stroke") && !p.NameEquals("LABEL"))
							{
								p.WriteTo(writer);
							}
						}
					}
					writer.WriteString("fill", level?.Fill ?? UnknownFill);
					writer.WriteString("stroke", level?.Stroke ?? UnknownStroke);
					writer.WriteString("LABEL", level?.Code ?? string.Empty);
					writer.WriteEndObject();
					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
				writer.Flush();

				if (matched == 0)
				{
					return false;
				}

				colorized = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
				return true;
			}
		}

		/// <summary>Reads a feature's severity ordinal, accepting either spelling and either JSON type.</summary>
		private static bool TryReadDn(JsonElement feature, out double dn)
		{
			dn = 0;
			if (!feature.TryGetProperty("properties", out var props) ||
				props.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			if (!props.TryGetProperty("dn", out var value) && !props.TryGetProperty("DN", out value))
			{
				return false;
			}

			return value.ValueKind switch
			{
				JsonValueKind.Number => value.TryGetDouble(out dn),
				JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float,
					CultureInfo.InvariantCulture, out dn),
				_ => false,
			};
		}

		// The IEM `category` value each product type maps to; null means "fire" (take all F-collection features).
		private static string? CategoryFor(SpcOutlookType type) => type switch
		{
			SpcOutlookType.Categorical => "CATEGORICAL",
			SpcOutlookType.Tornado => "TORNADO",
			SpcOutlookType.Wind => "WIND",
			SpcOutlookType.Hail => "HAIL",
			SpcOutlookType.ProbabilisticCombined => "ANY SEVERE", // Day 2-3 combined probabilistic
			_ => null, // FireWeather / ExtendedFireWeather
		};

		/// <summary>
		/// Resolves one IEM feature's fill/stroke/LABEL from its threshold code.
		/// <para>
		/// ⚠️ The emitted <c>LABEL</c> is the catalog's own code, and that is what drives the hatching:
		/// <c>outlook.js</c> routes a feature to a hatch layer when its LABEL is a CIG code. Archived
		/// outlooks predating NWS service change 26-11 carry the legacy <c>"SIGN"</c>, which the catalog
		/// aliases to CIG1 — so a 2011 replay still hatches, under the group SPC would draw today.
		/// </para>
		/// </summary>
		private static (string Fill, string Stroke, string Label) Style(SpcOutlookType type, string threshold)
		{
			var level = SpcRiskCatalog.Level(type, threshold);
			if (level is null)
			{
				return (UnknownFill, UnknownStroke, string.Empty);
			}

			// Only a conditional-intensity level may carry a hatching LABEL; a solid level's label must
			// stay clear of the "CIG"/"SIG" substrings outlook.js filters on.
			return (level.Fill, level.Stroke, level.IsConditionalIntensity ? level.Code : string.Empty);
		}

		private static string? GetStr(JsonElement obj, string name) =>
			obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

		private static DateTimeOffset? ParseIso(string? s) =>
			s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
				? dt
				: null;
	}
}
