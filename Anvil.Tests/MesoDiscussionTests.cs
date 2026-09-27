using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Mesoscale discussions: the text split and the index → page build. Every fixture is REAL IEM output
	/// (fetched 2026-09-27): MD 0725 (2013, the all-caps era), MD 0647 and MPD 0230 (2024, mixed case, WPC's
	/// bare-name signature), and both kinds' indexes for 2024-05-06/07.
	/// </summary>
	public class MesoDiscussionTests
	{
		private static string Fixture(string name) =>
			File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

		private static DateTimeOffset Z(int y, int mo, int d, int h, int m) => new(y, mo, d, h, m, 0, TimeSpan.Zero);

		[Fact]
		public void Text_2013AllCaps_SplitsIntoItsSections()
		{
			var t = MesoDiscussionTextParser.Parse(Fixture("md-2013-0725.txt"));

			Assert.Equal("LOWER MI", t.AreasAffected);
			Assert.Equal("SEVERE POTENTIAL...WATCH LIKELY", t.Concerning);
			Assert.Equal("80 PERCENT", t.WatchProbability);
			Assert.StartsWith("COVERAGE OF TSTMS SHOULD INCREASE", t.Summary);
			Assert.EndsWith("WITHIN THE NEXT HOUR.", t.Summary);
			// The teletype wrap is undone: one paragraph, no newlines inside it.
			Assert.DoesNotContain('\n', t.Summary);
			Assert.StartsWith("STRONG HEATING IS OCCURRING", t.Discussion);
			Assert.EndsWith("GIVEN THE DEGREE OF INSTABILITY PRESENT.", t.Discussion);
			Assert.Equal("ROGERS/KERR", t.Forecasters);
			Assert.Equal("DTX · APX · IWX · GRR", t.Attn);
			Assert.DoesNotContain("LAT...LON", t.Discussion);
			Assert.DoesNotContain("PLEASE SEE", t.Discussion);
		}

		[Fact]
		public void Text_2024MixedCase_MatchesLabelsCaseBlind()
		{
			var t = MesoDiscussionTextParser.Parse(Fixture("md-2024-0647.txt"));

			Assert.Equal("northwest/north-central KS and south-central NE", t.AreasAffected);
			Assert.Equal("Severe potential...Tornado Watch likely", t.Concerning);
			Assert.Equal("95 percent", t.WatchProbability);
			Assert.EndsWith("severe wind gusts is expected.", t.Discussion);
			Assert.Equal("Grams/Smith", t.Forecasters);
		}

		[Fact]
		public void Text_WpcMpd_LiftsTheBareNameSignature_AndBothAttnLines()
		{
			var t = MesoDiscussionTextParser.Parse(Fixture("mpd-2024-0230.txt"));

			Assert.Equal("Portions of Eastern Montana and Northeastern Wyoming", t.AreasAffected);
			Assert.Equal("Heavy rainfall...Flash flooding possible", t.Concerning);
			Assert.Equal("", t.WatchProbability);
			Assert.Equal("Wegman", t.Forecasters);
			Assert.EndsWith("additional flash flooding is possible.", t.Discussion); // the name is NOT left in the prose
			Assert.Contains("\n\n", t.Discussion);                                   // its paragraphs are kept
			Assert.Equal("BYZ · GGW · RIW · UNR · MBRFC · NWC", t.Attn);
		}

		private static (string Json, System.Collections.Generic.List<MesoDiscussion> List) Build(DateTimeOffset start, DateTimeOffset end) =>
			MesoDiscussionService.Build(new[]
			{
				(DiscussionKinds.Spc, Fixture("mcd-index-20240506.json")),
				(DiscussionKinds.Wpc, Fixture("mpd-index-20240506.json")),
			}, start, end);

		[Fact]
		public void Build_KeepsWhatOverlapsTheWindow_BothKinds_OldestFirst()
		{
			var (_, list) = Build(Z(2024, 5, 7, 2, 0), Z(2024, 5, 7, 3, 30));

			// SPC MD 0664 (01:00–02:30) overlaps; 0646 (12:12–13:15 the day before) does not.
			Assert.Contains(list, d => d.Kind == DiscussionKinds.Spc && d.Number == 664);
			Assert.DoesNotContain(list, d => d.Number == 646 && d.Kind == DiscussionKinds.Spc);
			Assert.All(list, d => Assert.True(d.Expires > Z(2024, 5, 7, 2, 0) && d.Issued < Z(2024, 5, 7, 3, 30)));
			Assert.Equal(list.OrderBy(d => d.Issued).Select(d => d.Key), list.Select(d => d.Key));
		}

		[Fact]
		public void Build_CarriesTheIndexFields_AndTheSamePageFeatures()
		{
			var (json, list) = Build(Z(2024, 5, 6, 15, 0), Z(2024, 5, 6, 18, 0));

			var md647 = list.Single(d => d.Kind == DiscussionKinds.Spc && d.Number == 647);
			Assert.Equal(95, md647.WatchProbability);
			Assert.Equal("SEVERE POTENTIAL...TORNADO WATCH LIKELY", md647.Concerning);
			Assert.Equal("MD 0647", md647.Label);
			Assert.Equal("mcd-2024-647", md647.Key);
			Assert.Equal("https://www.spc.noaa.gov/products/md/2024/md0647.html", md647.WebUrl);
			Assert.True(md647.IsInEffectAt(Z(2024, 5, 6, 16, 0)));
			Assert.False(md647.IsInEffectAt(Z(2024, 5, 6, 18, 0))); // expiry is exclusive

			var features = JsonDocument.Parse(json).RootElement.GetProperty("features");
			Assert.Equal(list.Count, features.GetArrayLength());
			var f = features.EnumerateArray().Single(x => x.GetProperty("properties").GetProperty("key").GetString() == "mcd-2024-647");
			Assert.Equal("mcd", f.GetProperty("properties").GetProperty("kind").GetString());
			Assert.Equal(95, f.GetProperty("properties").GetProperty("prob").GetInt32());
			Assert.Equal(md647.Issued.ToUnixTimeMilliseconds(), f.GetProperty("properties").GetProperty("t0").GetInt64());

			var mpd = list.First(d => d.Kind == DiscussionKinds.Wpc);
			Assert.Null(mpd.WatchProbability);
			Assert.StartsWith("MPD ", mpd.Label);
		}

		[Fact]
		public void EveryKind_HasAUsableDefinition()
		{
			// The catalog is the one place a kind is defined; a malformed entry would fail far from here.
			Assert.Equal(DiscussionKinds.All.Count, DiscussionKinds.All.Select(k => k.Id).Distinct().Count());
			foreach (var k in DiscussionKinds.All)
			{
				Assert.Matches("^#[0-9A-F]{6}$", k.Fill);
				Assert.Matches("^#[0-9A-F]{6}$", k.Outline);
				Assert.StartsWith("nws/", k.ApiPath);
				Assert.Same(k, DiscussionKinds.ById(k.Id));
				Assert.StartsWith("https://", string.Format(System.Globalization.CultureInfo.InvariantCulture, k.WebUrlFormat, 2024, 7));
			}
		}
	}
}
