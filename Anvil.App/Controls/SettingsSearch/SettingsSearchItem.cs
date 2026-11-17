using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Anvil.Services;

namespace Anvil.Controls.SettingsSearch
{
	/// <summary>
	/// One line of "Find a setting": a MATCH (a <see cref="SettingsSearchHit"/>) in the rail box's suggestions or on the results
	/// page — or, in the suggestions only, the FOOTER line ("Enter — all 9 results"). One type so the AutoSuggestBox has one
	/// template; the two halves show by <see cref="MatchVisibility"/> / <see cref="FooterVisibility"/>.
	/// </summary>
	public sealed class SettingsSearchItem
	{
		private SettingsSearchItem(SettingsSearchHit? hit, string footer)
		{
			Hit = hit;
			FooterText = footer;
		}

		public static SettingsSearchItem Match(SettingsSearchHit hit) => new(hit, string.Empty);

		public static SettingsSearchItem Footer(int total) =>
			new(null, total == 1 ? "Enter — the 1 result" : $"Enter — all {total} results");

		/// <summary>The match; null on the footer.</summary>
		public SettingsSearchHit? Hit { get; }

		public bool IsFooter => Hit is null;
		public string FooterText { get; }

		public string TitleBefore => Hit?.TitleBefore ?? string.Empty;
		public string TitleMatch => Hit?.TitleMatch ?? string.Empty;
		public string TitleAfter => Hit?.TitleAfter ?? string.Empty;
		public string SnippetBefore => Hit?.SnippetBefore ?? string.Empty;
		public string SnippetMatch => Hit?.SnippetMatch ?? string.Empty;
		public string SnippetAfter => Hit?.SnippetAfter ?? string.Empty;
		public string Breadcrumb => Hit?.Place.Breadcrumb ?? string.Empty;
		public string Console => Hit?.Place.Console ?? string.Empty;

		public Visibility MatchVisibility => IsFooter ? Visibility.Collapsed : Visibility.Visible;
		public Visibility FooterVisibility => IsFooter ? Visibility.Visible : Visibility.Collapsed;

		// The box shows the setting's title if a suggestion's text is ever read (UpdateTextOnSelect is off, so it isn't).
		public override string ToString() => Hit?.Title ?? string.Empty;
	}

	/// <summary>The results page's matches for ONE rail page (a plate).</summary>
	public sealed class SettingsSearchGroup
	{
		public SettingsSearchGroup(string page, IReadOnlyList<SettingsSearchItem> items)
		{
			Page = page;
			Items = items;
		}

		public string Page { get; }
		public IReadOnlyList<SettingsSearchItem> Items { get; }
	}
}
