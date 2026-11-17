using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>"Find a setting" (Services/Settings/SettingsSearch): every row has a place, the places match the XAML, and the
	/// ranking puts a title match first.</summary>
	public class SettingsSearchTests
	{
		private static readonly string[] RailPages = { "General", "Appearance", "Radar", "Rings & Ruler", "Live Data", "Storage" };

		// A console folder → its rail page.
		private static readonly Dictionary<string, string> PageOfFolder = new()
		{
			["GeneralSettingsConsoles"] = "General",
			["AppearanceSettingsConsoles"] = "Appearance",
			["RadarSettingsConsoles"] = "Radar",
			["RingsSettingsConsoles"] = "Rings & Ruler",
			["LiveDataSettingsConsoles"] = "Live Data",
			["StorageSettingsConsoles"] = "Storage",
			["StorageManagers"] = "Storage",
		};

		[Fact]
		public void Every_catalog_row_has_a_place_on_a_rail_page()
		{
			foreach (var entry in SettingsText.All)
			{
				var place = SettingsSearch.PlaceOf(entry.Key);
				Assert.True(place is not null, $"{entry.Key}: no place — add its prefix to SettingsSearch.ByPrefix");
				Assert.Contains(place!.Page, RailPages);
			}
		}

		// Each console file's literal plate Title (and its folder's page) must be where SettingsSearch says its keys live.
		// Skipped: a plate whose title is bound (the ring look ×3) and a file with no plate of its own.
		[Fact]
		public void Places_match_the_consoles_that_name_the_keys()
		{
			var keyAttr = new Regex(@"(?:AboutKey|ColorKey)=""([^""{]+)""");
			var plateTitle = new Regex(@"SettingsConsolePlate[^>]*\bTitle=""([^""{]+)""");
			var wrong = new List<string>();
			foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "Anvil.App", "Controls"), "*.xaml", SearchOption.AllDirectories))
			{
				var folder = Path.GetFileName(Path.GetDirectoryName(file))!;
				if (!PageOfFolder.TryGetValue(folder, out var page)) { continue; }
				var text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline);
				var title = plateTitle.Match(text);
				foreach (Match k in keyAttr.Matches(text))
				{
					var place = SettingsSearch.PlaceOf(k.Groups[1].Value);
					if (place is null) { wrong.Add($"{Path.GetFileName(file)}: {k.Groups[1].Value} has no place"); continue; }
					if (place.Page != page) { wrong.Add($"{Path.GetFileName(file)}: {k.Groups[1].Value} page {place.Page}, file is {page}"); }
					if (title.Success && place.Console != title.Groups[1].Value)
					{
						wrong.Add($"{Path.GetFileName(file)}: {k.Groups[1].Value} console {place.Console}, plate is {title.Groups[1].Value}");
					}
				}
			}
			Assert.Empty(wrong);
		}

		[Fact]
		public void A_title_match_ranks_first_and_is_split_for_bolding()
		{
			var hits = SettingsSearch.Find("time");
			Assert.NotEmpty(hits);
			Assert.Equal("units.zone", hits[0].Key);
			Assert.Equal("Time", hits[0].TitleMatch);
			Assert.Equal(" zone", hits[0].TitleAfter);
			Assert.Equal("General › Units", hits[0].Place.Breadcrumb);
		}

		[Fact]
		public void Every_word_must_match_and_words_match_at_a_word_start()
		{
			Assert.Equal("startup.homeSite", SettingsSearch.Find("home site")[0].Key);
			Assert.Empty(SettingsSearch.Find("time zebra"));
			Assert.Empty(SettingsSearch.Find("   "));
			// "olors" is inside "colors" but starts no word — only the title may match mid-word.
			Assert.DoesNotContain(SettingsSearch.Find("olors"), h => h.Key == "theme.theme");
		}

		[Fact]
		public void Words_in_the_about_text_find_a_row_and_give_its_snippet()
		{
			var theme = SettingsSearch.Find("matching basemap").FirstOrDefault(h => h.Key == "theme.theme");
			Assert.NotNull(theme);
			Assert.Equal("matching", theme!.SnippetMatch, ignoreCase: true);
		}

		private static string RepoRoot()
		{
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			{
				if (File.Exists(Path.Combine(dir.FullName, "Anvil.slnx"))) { return dir.FullName; }
			}
			throw new InvalidOperationException("Anvil.slnx not found above the test output folder");
		}
	}
}
