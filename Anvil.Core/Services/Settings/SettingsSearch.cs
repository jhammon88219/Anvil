using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Services
{
	/// <summary>Where a Settings row lives: its rail page's name and its console's title ("General" › "Units").</summary>
	public sealed record SettingsPlace(string Page, string Console)
	{
		public string Breadcrumb => $"{Page} › {Console}";
	}

	/// <summary>
	/// One match for "Find a setting". The title and the snippet each come split around the FIRST matched word
	/// (Before · Match · After) so the UI bolds it with three declared Runs — no inlines built in code.
	/// </summary>
	public sealed record SettingsSearchHit(
		string Key,
		SettingsPlace Place,
		string TitleBefore, string TitleMatch, string TitleAfter,
		string SnippetBefore, string SnippetMatch, string SnippetAfter)
	{
		public string Title => TitleBefore + TitleMatch + TitleAfter;
	}

	/// <summary>
	/// "Find a setting" (the Settings window's rail search, 2026-11-17, canvas artboards 14–15). Searches the ONE catalog,
	/// <see cref="SettingsText"/> — every row's Title, Hint, HintOff, Choices and About — plus its page and console names, so a
	/// search for "dark" finds Theme with no keyword list to keep. EVERY query word must appear somewhere (prefix of a word,
	/// case-blind); ranked title first, then console / page, then hint, then About; catalog order breaks ties.
	/// </summary>
	public static class SettingsSearch
	{
		// ── WHERE EACH ROW LIVES ── by key prefix, a few keys exact. Page = the rail entry's Label; Console = its plate's Title.
		// ⚠️ Mirrors the App's XAML (which console file names which AboutKey) — SettingsSearchTests checks every plate title.
		private static readonly Dictionary<string, SettingsPlace> ByPrefix = new(StringComparer.Ordinal)
		{
			["startup"] = new("General", "Startup"),
			["units"] = new("General", "Units"),
			["window"] = new("General", "Window"),
			["theme"] = new("Appearance", "Theme"),
			["edges"] = new("Appearance", "Edges"),
			["poll"] = new("Radar", "Live polling"),
			["memory"] = new("Radar", "Memory"),
			["sites"] = new("Radar", "Site keys"),
			["scrubber"] = new("Radar", "Scrubber"),
			["hatch"] = new("Radar", "Hatch scrubber"),
			["rings"] = new("Rings & Ruler", "Rings"),
			// The ring LOOK console is declared three times (Reflectivity reach · Velocity reach · Distance rings) with the
			// same keys; a shared key lands on the FIRST. Only the colours differ per ring (exact keys below).
			["ring"] = new("Rings & Ruler", "Reflectivity reach"),
			["labels"] = new("Rings & Ruler", "Distance labels"),
			["ruler"] = new("Rings & Ruler", "Ruler"),
			["live"] = new("Live Data", "Source"),
			["wire"] = new("Live Data", "Weather Wire account"),
			["polling"] = new("Live Data", "Polling"),
			["conn"] = new("Live Data", "Connection"),
			["storage"] = new("Storage", "Disk usage"),
			["map"] = new("Storage", "Map"),
			["offline"] = new("Storage", "Offline map"),
			["online"] = new("Storage", "Online map"),
		};

		private static readonly Dictionary<string, SettingsPlace> ByKey = new(StringComparer.Ordinal)
		{
			["ring.color"] = new("Rings & Ruler", "Velocity reach"),
			["ring.color.refl"] = new("Rings & Ruler", "Reflectivity reach"),
			["ring.color.distance"] = new("Rings & Ruler", "Distance rings"),
		};

		/// <summary>The suggestions list's length (the rest are one Enter away).</summary>
		public const int SuggestionCount = 6;

		/// <summary>Where <paramref name="key"/>'s row lives, or null for a key with no place (SettingsSearchTests forbids one).</summary>
		public static SettingsPlace? PlaceOf(string key)
		{
			if (ByKey.TryGetValue(key, out var exact)) { return exact; }
			var dot = key.IndexOf('.');
			return dot > 0 && ByPrefix.TryGetValue(key[..dot], out var place) ? place : null;
		}

		/// <summary>Every match for <paramref name="query"/>, best first (empty for a blank query).</summary>
		public static IReadOnlyList<SettingsSearchHit> Find(string? query)
		{
			var words = (query ?? string.Empty)
				.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(w => w.Length > 0)
				.ToArray();
			if (words.Length == 0) { return Array.Empty<SettingsSearchHit>(); }

			var scored = new List<(int Score, int Order, SettingsSearchHit Hit)>();
			var order = 0;
			foreach (var entry in SettingsText.All)
			{
				order++;
				var place = PlaceOf(entry.Key);
				if (place is null) { continue; }

				var hints = new[] { entry.Hint, entry.HintOff }.Concat(entry.Choices ?? Array.Empty<string>())
					.Where(h => !string.IsNullOrEmpty(h)).Select(h => h!).ToArray();
				var score = 0;
				var all = true;
				foreach (var word in words)
				{
					var s = WordScore(word, entry.Title, place, hints, entry.About);
					if (s == 0) { all = false; break; }
					score += s;
				}
				if (!all) { continue; }

				var (tb, tm, ta) = Split(entry.Title, words);
				var snippetSource = SnippetSource(entry, hints, words);
				var (sb, sm, sa) = Split(snippetSource, words);
				scored.Add((score, order, new SettingsSearchHit(entry.Key, place, tb, tm, ta, sb, sm, sa)));
			}

			return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Order).Select(s => s.Hit).ToList();
		}

		// A word's best place: title start 100 · a title word 80 · anywhere in the title 60 · console 40 · page 30 · hint 20 · About 10.
		private static int WordScore(string word, string title, SettingsPlace place, string[] hints, string about)
		{
			if (title.StartsWith(word, StringComparison.OrdinalIgnoreCase)) { return 100; }
			if (WordStartIndex(title, word) >= 0) { return 80; }
			if (title.Contains(word, StringComparison.OrdinalIgnoreCase)) { return 60; }
			if (WordStartIndex(place.Console, word) >= 0) { return 40; }
			if (WordStartIndex(place.Page, word) >= 0) { return 30; }
			if (hints.Any(h => WordStartIndex(h, word) >= 0)) { return 20; }
			if (WordStartIndex(about, word) >= 0) { return 10; }
			return 0;
		}

		// The index of the first place `word` starts a word in `text` (start of text, or after a non-letter/digit), else -1.
		private static int WordStartIndex(string text, string word)
		{
			for (var i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0;
				i = text.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
			{
				if (i == 0 || !char.IsLetterOrDigit(text[i - 1])) { return i; }
			}
			return -1;
		}

		// The line shown under a result on the results page: the About SENTENCE holding a query word, else the hint holding
		// one, else the About's first sentence.
		private static string SnippetSource(SettingsTextEntry entry, string[] hints, string[] words)
		{
			var sentences = entry.About.Split(new[] { ". ", "; " }, StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Trim().TrimEnd('.') + ".").ToArray();
			foreach (var word in words)
			{
				var sentence = sentences.FirstOrDefault(s => WordStartIndex(s, word) >= 0);
				if (sentence is not null) { return sentence; }
			}
			foreach (var word in words)
			{
				var hint = hints.FirstOrDefault(h => WordStartIndex(h, word) >= 0);
				if (hint is not null) { return hint; }
			}
			return sentences.FirstOrDefault() ?? string.Empty;
		}

		// Split `text` around the first query word found at a word start (else anywhere), else no match.
		private static (string Before, string Match, string After) Split(string text, string[] words)
		{
			foreach (var word in words)
			{
				var i = WordStartIndex(text, word);
				if (i < 0) { i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); }
				if (i >= 0) { return (text[..i], text.Substring(i, word.Length), text[(i + word.Length)..]); }
			}
			return (text, string.Empty, string.Empty);
		}
	}
}
