using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The Settings wording rules (docs/ui-patterns.md "Hints") held by the ONE catalog, Services/Settings/SettingsText, and the
	/// XAML's keys checked against it — a typo'd AboutKey would otherwise show an empty About box with no error anywhere.
	/// </summary>
	public class SettingsTextTests
	{
		[Fact]
		public void Keys_are_unique()
		{
			var dupes = SettingsText.All.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
			Assert.Empty(dupes);
		}

		[Fact]
		public void Every_about_fits_the_three_line_box()
		{
			foreach (var e in SettingsText.All)
			{
				Assert.False(string.IsNullOrWhiteSpace(e.About), $"{e.Key}: no About text");
				Assert.True(e.About.Length <= SettingsText.AboutMaxLength,
					$"{e.Key}: About is {e.About.Length} characters (max {SettingsText.AboutMaxLength}) — it would trim in the 3-line box");
			}
		}

		[Fact]
		public void Hints_are_one_short_line_with_no_full_stop()
		{
			foreach (var (key, hint) in AllHints())
			{
				Assert.True(hint.Length <= SettingsText.HintMaxLength, $"{key}: hint \"{hint}\" is over {SettingsText.HintMaxLength} characters");
				Assert.False(hint.EndsWith('.'), $"{key}: hint \"{hint}\" ends with a full stop");
				Assert.DoesNotContain(" — ", hint);   // two facts join with " · ", never a dash
				Assert.DoesNotContain('\n', hint);
			}
		}

		[Fact]
		public void Wording_uses_US_spelling()
		{
			var british = new Regex(@"\b(colour|favourite|grey)", RegexOptions.IgnoreCase);
			foreach (var e in SettingsText.All)
			{
				foreach (var text in new[] { e.Title, e.About }.Concat(AllHints().Where(h => h.Key == e.Key).Select(h => h.Text)))
				{
					Assert.False(british.IsMatch(text), $"{e.Key}: \"{text}\" — Settings uses US spelling");
				}
			}
		}

		[Fact]
		public void A_row_whose_hint_follows_the_pick_has_at_least_two_choices()
		{
			foreach (var e in SettingsText.All.Where(e => e.Choices is not null))
			{
				Assert.True(e.Choices!.Count >= 2, $"{e.Key}: Choices needs one hint per choice");
			}
		}

		[Fact]
		public void Helpers_fall_back_to_empty_text()
		{
			Assert.Equal(string.Empty, SettingsText.HintOf("no.such.key"));
			Assert.Equal(string.Empty, SettingsText.ChoiceOf("poll.check", 9));
			Assert.Equal("On a steady timer", SettingsText.ChoiceOf("poll.check", 1));
			Assert.Equal("Only with Fixed time", SettingsText.HintFor("poll.every", on: false));
		}

		// Every key the App's XAML names — AboutKey="…" on a row and the 'key' literals passed to SettingsText from x:Bind —
		// must be in the catalog.
		[Fact]
		public void Every_key_the_xaml_names_is_in_the_catalog()
		{
			var app = Path.Combine(RepoRoot(), "Anvil.App");
			var named = new Regex(@"(?:AboutKey|ColorKey)=""([^""{]+)""|SettingsText\.\w+\('([^']+)'");
			var missing = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
				.SelectMany(f => named.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f),
					Key: m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)))
				.Where(k => SettingsText.Get(k.Key) is null)
				.Select(k => $"{k.File}: {k.Key}")
				.ToList();
			Assert.Empty(missing);
		}

		private static (string Key, string Text)[] AllHints() => SettingsText.All
			.SelectMany(e => new[] { e.Hint, e.HintOff }.Concat(e.Choices ?? Array.Empty<string>())
				.Where(h => !string.IsNullOrEmpty(h)).Select(h => (e.Key, h!)))
			.ToArray();

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
