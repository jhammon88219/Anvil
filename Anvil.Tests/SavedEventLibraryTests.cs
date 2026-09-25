using System;
using System.IO;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="SavedEventLibrary"/>: the shipped built-ins parse clean, built-ins can't be deleted (by the
	/// library, not just the UI), user events round-trip, and the rules the Timeframe pickers impose (window
	/// length, 5-minute starts) are enforced. Every test uses a throwaway user folder.
	/// </summary>
	public class SavedEventLibraryTests
	{
		private static string TempDir() =>
			Path.Combine(Path.GetTempPath(), "AnvilSavedEventTests", Guid.NewGuid().ToString("N"));

		private const string BuiltIns = """
			{ "events": [
			  { "id": "b1", "name": "Built one", "source": "test",
			    "legs": [ { "site": "KTLX", "startUtc": "2013-05-31T22:30:00Z", "minutes": 120 } ] }
			] }
			""";

		private static SavedEventLeg Leg(string? site = "KDVN", int minutes = 360) =>
			new(site, new DateTimeOffset(2020, 8, 10, 16, 0, 0, TimeSpan.Zero), minutes);

		[Fact]
		public void ShippedBuiltIns_ParseWithoutProblems()
		{
			var lib = new SavedEventLibrary(TempDir());
			Assert.Empty(lib.Problems);
			Assert.NotEmpty(lib.GetEvents());
			Assert.All(lib.GetEvents(), e => Assert.True(e.IsBuiltIn));
		}

		[Fact]
		public void ShippedBuiltIns_AllNameAType()
		{
			Assert.All(new SavedEventLibrary(TempDir()).GetEvents(), e => Assert.NotEqual(SavedEventKind.Other, e.Kind));
		}

		[Fact]
		public void BuiltIns_GroupByType_ThenNewestFirst()
		{
			var lib = new SavedEventLibrary(TempDir(), """
				{ "events": [
				  { "id": "d", "type": "derecho", "name": "D",
				    "legs": [ { "site": "KDVN", "startUtc": "2020-08-10T16:30:00Z", "minutes": 180 } ] },
				  { "id": "h", "type": "Hurricane", "name": "H",
				    "legs": [ { "site": "KLIX", "startUtc": "2005-08-29T10:00:00Z", "minutes": 180 } ] },
				  { "id": "t-old", "type": "tornado", "name": "T old",
				    "legs": [ { "site": "KSGF", "startUtc": "2011-05-22T22:00:00Z", "minutes": 120 } ] },
				  { "id": "none", "name": "Untyped",
				    "legs": [ { "site": "KTLX", "startUtc": "2019-05-20T19:30:00Z", "minutes": 120 } ] },
				  { "id": "t-new", "type": "tornado", "name": "T new",
				    "legs": [ { "site": "KTLX", "startUtc": "2013-05-31T22:30:00Z", "minutes": 120 } ] }
				] }
				""");
			Assert.Empty(lib.Problems);
			Assert.Equal(new[] { "t-new", "t-old", "h", "d", "none" }, lib.GetEvents().Select(e => e.Id));
		}

		[Fact]
		public void UnknownType_IsSkipped_AndReported()
		{
			var lib = new SavedEventLibrary(TempDir(), """
				{ "events": [ { "id": "typo", "type": "hurricaine", "name": "Typo",
				  "legs": [ { "site": "KLIX", "startUtc": "2005-08-29T10:00:00Z", "minutes": 180 } ] } ] }
				""");
			Assert.Empty(lib.GetEvents());
			Assert.Contains("hurricaine", Assert.Single(lib.Problems));
		}

		[Fact]
		public void Durations_MatchTheTimeframePicker()
		{
			// SavedEventLeg keeps a copy so the library needn't reach into a view model; this is the tripwire.
			Assert.Equal(RadarViewModel.PastEventMinutesByIndex, SavedEventLeg.AllowedDurationMinutes);
		}

		[Fact]
		public void Remove_RefusesBuiltIn()
		{
			var dir = TempDir();
			var lib = new SavedEventLibrary(dir, BuiltIns);
			Assert.False(lib.Remove("b1"));
			Assert.Single(new SavedEventLibrary(dir, BuiltIns).GetEvents());
		}

		[Fact]
		public void Add_PersistsAcrossInstances()
		{
			var dir = TempDir();
			var added = new SavedEventLibrary(dir, BuiltIns).Add("  Derecho  ", new[] { Leg(), Leg("KDMX", 180) }, "bow echo");

			var events = new SavedEventLibrary(dir, BuiltIns).GetEvents();
			Assert.Equal(2, events.Count);
			var mine = Assert.Single(events, e => !e.IsBuiltIn);
			Assert.Equal(added.Id, mine.Id);
			Assert.False(mine.IsBuiltIn);
			Assert.Equal("Derecho", mine.Name);
			Assert.Equal("bow echo", mine.Notes);
			Assert.Equal(new[] { "KDVN", "KDMX" }, mine.Legs.Select(l => l.SiteId));
			Assert.Equal(Leg().StartUtc, mine.Legs[0].StartUtc);
		}

		[Fact]
		public void Remove_DeletesUserEvent_AndPersists()
		{
			var dir = TempDir();
			var lib = new SavedEventLibrary(dir, BuiltIns);
			var added = lib.Add("Mine", new[] { Leg() }, "");
			Assert.True(lib.Remove(added.Id));
			Assert.DoesNotContain(new SavedEventLibrary(dir, BuiltIns).GetEvents(), e => e.Id == added.Id);
		}

		[Fact]
		public void Add_AllowsWindowOnlyLeg()
		{
			var lib = new SavedEventLibrary(TempDir(), BuiltIns);
			Assert.Null(lib.Add("No site", new[] { Leg(site: null) }, "").Legs[0].SiteId);
		}

		[Theory]
		[InlineData("", 120, "Enter a name")]
		[InlineData("Ok", 90, "90 minutes")]
		public void Add_RejectsWhatThePickersCantHold(string name, int minutes, string expected)
		{
			var lib = new SavedEventLibrary(TempDir(), BuiltIns);
			var ex = Assert.Throws<ArgumentException>(() => lib.Add(name, new[] { Leg(minutes: minutes) }, ""));
			Assert.Contains(expected, ex.Message);
		}

		[Fact]
		public void Add_RejectsStartOffTheFiveMinuteMark()
		{
			var lib = new SavedEventLibrary(TempDir(), BuiltIns);
			var odd = new SavedEventLeg("KTLX", new DateTimeOffset(2013, 5, 31, 22, 32, 0, TimeSpan.Zero), 120);
			Assert.Throws<ArgumentException>(() => lib.Add("Odd", new[] { odd }, ""));
		}

		[Fact]
		public void UserFile_CannotPromoteItselfToBuiltIn()
		{
			var dir = TempDir();
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, "SavedEvents.json"), """
				{ "events": [ { "id": "sneaky", "name": "Sneaky", "isBuiltIn": true,
				  "legs": [ { "site": "KTLX", "startUtc": "2013-05-20T19:30:00Z", "minutes": 120 } ] } ] }
				""");

			var lib = new SavedEventLibrary(dir, BuiltIns);
			Assert.False(lib.GetEvents().Single(e => e.Id == "sneaky").IsBuiltIn);
			Assert.True(lib.Remove("sneaky"));
		}

		[Fact]
		public void CorruptUserFile_IsSetAside_NotOverwritten()
		{
			var dir = TempDir();
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, "SavedEvents.json"), "{ not json");

			var lib = new SavedEventLibrary(dir, BuiltIns);
			Assert.NotEmpty(lib.Problems);
			Assert.Single(Directory.GetFiles(dir, "SavedEvents.corrupt-*.json"));
		}

		// ── The Anvil Atlas redesign: typed custom events, key times ─────────────────────────────────

		[Fact]
		public void ShippedBuiltIns_EveryLegHasAKeyTime()
		{
			Assert.All(new SavedEventLibrary(TempDir()).GetEvents().SelectMany(e => e.Legs),
				leg => Assert.NotNull(leg.Key));
		}

		[Fact]
		public void CustomEvents_SitInTheirTypesGroup_ByDate()
		{
			const string builtIns = """
				{ "events": [
				  { "id": "t-2013", "type": "tornado", "name": "T 2013",
				    "legs": [ { "site": "KTLX", "startUtc": "2013-05-31T22:30:00Z", "minutes": 120 } ] },
				  { "id": "t-2011", "type": "tornado", "name": "T 2011",
				    "legs": [ { "site": "KSGF", "startUtc": "2011-05-22T22:00:00Z", "minutes": 120 } ] },
				  { "id": "h", "type": "hurricane", "name": "H",
				    "legs": [ { "site": "KLIX", "startUtc": "2005-08-29T10:00:00Z", "minutes": 180 } ] }
				] }
				""";
			var dir = TempDir();
			var chaseDay = Leg("KTLX", 120) with { StartUtc = new DateTimeOffset(2012, 4, 14, 22, 0, 0, TimeSpan.Zero) };
			var mine = new SavedEventLibrary(dir, builtIns).Add("Chase day", new[] { chaseDay }, "", SavedEventKind.Tornado);

			// Reloaded from disk: the TYPE round-trips, and the event sorts INTO the tornado group by date.
			Assert.Equal(new[] { "t-2013", mine.Id, "t-2011", "h" },
				new SavedEventLibrary(dir, builtIns).GetEvents().Select(e => e.Id));
		}

		[Fact]
		public void Key_ParsesFromJson()
		{
			var lib = new SavedEventLibrary(TempDir(), """
				{ "events": [ { "id": "k", "type": "hurricane", "name": "K",
				  "legs": [ { "site": "KLIX", "startUtc": "2005-08-29T10:00:00Z", "minutes": 180,
				              "key": { "startUtc": "2005-08-29T11:10:00Z", "place": "Buras" } } ] } ] }
				""");
			Assert.Empty(lib.Problems);
			var key = lib.GetEvents().Single().Legs[0].Key!;
			Assert.Equal(new DateTimeOffset(2005, 8, 29, 11, 10, 0, TimeSpan.Zero), key.StartUtc);
			Assert.Null(key.EndUtc);
			Assert.Equal("Buras", key.Place);
		}

		[Theory]
		// Inside the window, and a span that STARTS before it (the KILN derecho leg) — both overlap.
		[InlineData("2020-08-10T17:00:00Z", "2020-08-10T17:30:00Z", null)]
		[InlineData("2020-08-10T15:15:00Z", "2020-08-10T16:30:00Z", null)]
		// Wholly before / wholly after the 16:00–22:00 window, and an end before its start.
		[InlineData("2020-08-10T14:00:00Z", "2020-08-10T15:00:00Z", "replay window")]
		[InlineData("2020-08-10T22:30:00Z", null, "replay window")]
		[InlineData("2020-08-10T18:00:00Z", "2020-08-10T17:00:00Z", "after the start")]
		public void Key_MustOverlapItsLegsWindow(string start, string? end, string? expected)
		{
			var key = new SavedEventKey(DateTimeOffset.Parse(start), end is null ? null : DateTimeOffset.Parse(end));
			var problem = SavedEventLibrary.ValidateKey(Leg(), key); // Leg() = KDVN 16:00Z + 360 min
			if (expected is null)
			{
				Assert.Null(problem);
			}
			else
			{
				Assert.Contains(expected, problem);
			}
		}

		[Fact]
		public void SetLegKey_And_SetKind_RoundTripForYourOwn_AndRefuseBuiltIns()
		{
			var dir = TempDir();
			var lib = new SavedEventLibrary(dir, BuiltIns);
			var mine = lib.Add("Mine", new[] { Leg() }, "", SavedEventKind.Derecho);
			var key = new SavedEventKey(new DateTimeOffset(2020, 8, 10, 17, 30, 0, TimeSpan.Zero),
				new DateTimeOffset(2020, 8, 10, 17, 55, 0, TimeSpan.Zero), "Cedar Rapids");

			Assert.NotNull(lib.SetLegKey(mine.Id, 0, key));
			Assert.NotNull(lib.SetKind(mine.Id, SavedEventKind.Tornado));
			Assert.Null(lib.SetLegKey("b1", 0, key));
			Assert.Null(lib.SetKind("b1", SavedEventKind.Hurricane));

			var reloaded = new SavedEventLibrary(dir, BuiltIns).GetEvents().Single(e => e.Id == mine.Id);
			Assert.Equal(key, reloaded.Legs[0].Key);
			Assert.Equal(SavedEventKind.Tornado, reloaded.Kind);

			Assert.Throws<ArgumentException>(() =>
				lib.SetLegKey(mine.Id, 0, new SavedEventKey(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero))));
		}

		[Fact]
		public void PlaceNear_PicksTheDateNearestTheWindow_AcrossMidnight()
		{
			// A window that crosses local midnight: 11:30 PM → 1:30 AM local. "12:15 AM" belongs to the NEXT day.
			var startLocal = new DateTime(2020, 6, 1, 23, 30, 0, DateTimeKind.Unspecified);
			var startUtc = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal)).ToUniversalTime();
			var placed = SavedEventsViewModel.PlaceNear(new TimeSpan(0, 15, 0), startUtc, startUtc.AddMinutes(120));

			Assert.Equal(new DateTime(2020, 6, 2, 0, 15, 0), placed.ToLocalTime().DateTime);
		}

		[Fact]
		public void InvalidBuiltIn_IsSkipped_AndReported()
		{
			var lib = new SavedEventLibrary(TempDir(), """
				{ "events": [ { "id": "bad", "name": "Bad",
				  "legs": [ { "site": "KTLX", "startUtc": "2013-05-31T22:30:00Z", "minutes": 45 } ] } ] }
				""");
			Assert.Empty(lib.GetEvents());
			Assert.Single(lib.Problems);
		}
	}
}
