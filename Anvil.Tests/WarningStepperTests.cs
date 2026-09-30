using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The NowCast tiles' ‹ › arrows (<see cref="WarningStepper"/>) — worst-first order, › wraps, ‹ is a BACK
	/// STACK that starts off, an expired current warning hands › its slot — and the fly-to targets
	/// <see cref="WarningService.TargetsOf"/> builds from the merged warning set.
	/// </summary>
	public class WarningStepperTests
	{
		private static readonly DateTimeOffset T0 = new(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);

		private static WarningTarget W(string id, int tier, int minutesAgo, string phenom = "TO", string place = "") =>
			new(id, phenom, tier, T0.AddMinutes(-minutesAgo), place, "{}", -98, 35, -97, 36);

		private static (WarningStepper Stepper, List<string> Flown) Make(params WarningTarget[] targets)
		{
			var flown = new List<string>();
			var s = new WarningStepper("TO", t => { flown.Add(t.Id); return Task.CompletedTask; });
			s.Update(targets);
			return (s, flown);
		}

		[Fact]
		public void Order_IsWorstFirst_ThenNewestFirst()
		{
			var ordered = WarningStepper.Order(new[] { W("a", 0, 1), W("b", 1, 30), W("c", 1, 5), W("d", 2, 60) });
			Assert.Equal(new[] { "d", "c", "b", "a" }, ordered.Select(t => t.Id));
		}

		[Fact]
		public void Forward_Wraps_AndBackIsOffUntilForwardIsUsed()
		{
			var (s, flown) = Make(W("a", 0, 1), W("b", 0, 2), W("sv", 2, 0, phenom: "SV"));
			Assert.True(s.CanForward);
			Assert.False(s.CanBack);
			Assert.False(s.IsActive);

			s.Forward(); s.Forward(); s.Forward();         // a, b, then WRAP to a (the SV one is another tile's)
			Assert.Equal(new[] { "a", "b", "a" }, flown);
			Assert.True(s.CanBack);
			Assert.Equal("1 of 2", s.PositionText);
		}

		[Fact]
		public void Back_RetracesVisited_NotThePreviousInTheList()
		{
			var (s, flown) = Make(W("a", 0, 1), W("b", 0, 2), W("c", 0, 3));
			s.Forward(); s.Forward(); s.Forward();          // a, b, c
			s.Back();                                       // → b (visited before c)
			s.Back();                                       // → a
			Assert.Equal(new[] { "a", "b", "c", "b", "a" }, flown);
			Assert.False(s.CanBack);
		}

		[Fact]
		public void CurrentExpires_ForwardTakesItsSlot_AndBackSkipsTheGone()
		{
			var (s, flown) = Make(W("a", 0, 1), W("b", 0, 2), W("c", 0, 3));
			s.Forward(); s.Forward();                       // a, b (on b; a is on the back stack)
			s.Update(new[] { W("b", 0, 2), W("c", 0, 3) }); // a expires
			s.Update(new[] { W("c", 0, 3) });               // …and b, the one we're on
			Assert.False(s.IsActive);
			Assert.Equal(string.Empty, s.PositionText);
			Assert.False(s.CanBack);                        // everything visited is gone

			s.Forward();                                    // b's slot (1) wraps onto the one left: c
			Assert.Equal("c", flown[^1]);
		}

		[Fact]
		public void Reset_ForgetsPlaceAndHistory_NoWarnings_NothingLive()
		{
			var (s, flown) = Make(W("a", 2, 1, place: "Cleveland, OK"));
			s.Forward();
			Assert.Equal("1 of 1 · Cleveland, OK · Emergency", s.PositionText);
			s.Reset();
			Assert.False(s.IsActive);
			Assert.False(s.CanBack);

			var (empty, _) = Make();
			Assert.False(empty.CanForward);
			empty.Forward();                                // a no-op, never a throw
		}

		[Fact]
		public void StateLines_CountEachWarningOnceInItsFirstCountysState_MostFirst()
		{
			var targets = new[]
			{
				W("a", 0, 1, place: "Cleveland, OK"), W("b", 0, 1, place: "Tulsa, OK"), W("c", 0, 1, place: "Kay, OK"),
				W("d", 0, 1, place: "Sumner, KS"), W("e", 0, 1, place: "Pawnee, KS"),
				W("f", 0, 1, place: ""),                                // CAP named no county → still counted
				W("sv", 0, 1, phenom: "SV", place: "Dallas, TX"),       // another tile's
			};
			var lines = WarningsViewModel.StatesOf(targets, "TO");
			Assert.Equal(new[] { ("Oklahoma", 3), ("Kansas", 2), ("Unknown", 1) }, lines.Select(l => (l.Name, l.Count)));
			Assert.Equal(6, lines.Sum(l => l.Count));                  // the lines add up to the tile's count
		}

		[Fact]
		public void TargetsOf_ReadsBboxPlaceAndSent_SkipsAFeatureWithNoCoordinates()
		{
			var feature = JsonNode.Parse("""
				{ "type": "Feature",
				  "geometry": { "type": "Polygon", "coordinates": [[[-97.6,35.2],[-97.2,35.2],[-97.4,35.6],[-97.6,35.2]]] },
				  "properties": { "phenom": "TO", "cap_id": "urn:1", "threat_tier": 1,
				                  "sent": "2026-09-30T15:02:00-05:00", "area": "Cleveland, OK; McClain, OK" } }
				""")!;
			var bare = JsonNode.Parse("""{ "type": "Feature", "geometry": null, "properties": { "cap_id": "urn:2" } }""")!;

			var t = Assert.Single(WarningService.TargetsOf(new[] { feature, bare }));
			Assert.Equal(("urn:1", "TO", 1, "Cleveland, OK"), (t.Id, t.Phenom, t.Tier, t.Place));
			Assert.Equal(new DateTimeOffset(2026, 9, 30, 20, 2, 0, TimeSpan.Zero), t.Sent);
			Assert.Equal((-97.6, 35.2, -97.2, 35.6), (t.West, t.South, t.East, t.North));
			Assert.Contains("Polygon", t.GeometryJson);
		}
	}
}
