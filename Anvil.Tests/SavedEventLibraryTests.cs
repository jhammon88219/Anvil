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
		public void Add_PersistsAcrossInstances_AndListsUserFirst()
		{
			var dir = TempDir();
			var added = new SavedEventLibrary(dir, BuiltIns).Add("  Derecho  ", new[] { Leg(), Leg("KDMX", 180) }, "bow echo");

			var events = new SavedEventLibrary(dir, BuiltIns).GetEvents();
			Assert.Equal(2, events.Count);
			var mine = events[0];
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
