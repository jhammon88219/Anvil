using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.SettingsSearch
{
	/// <summary>The Settings search results page (see the XAML header). The host fills it with <see cref="Show"/>.</summary>
	public sealed partial class SettingsSearchResults : UserControl
	{
		public SettingsSearchResults()
		{
			InitializeComponent();
		}

		/// <summary>The matches, one group per rail page, in rail order.</summary>
		public IReadOnlyList<SettingsSearchGroup> Groups
		{
			get => (IReadOnlyList<SettingsSearchGroup>)GetValue(GroupsProperty);
			private set => SetValue(GroupsProperty, value);
		}

		public static readonly DependencyProperty GroupsProperty =
			DependencyProperty.Register(nameof(Groups), typeof(IReadOnlyList<SettingsSearchGroup>), typeof(SettingsSearchResults),
				new PropertyMetadata(Array.Empty<SettingsSearchGroup>()));

		public Visibility NothingVisibility
		{
			get => (Visibility)GetValue(NothingVisibilityProperty);
			private set => SetValue(NothingVisibilityProperty, value);
		}

		public static readonly DependencyProperty NothingVisibilityProperty =
			DependencyProperty.Register(nameof(NothingVisibility), typeof(Visibility), typeof(SettingsSearchResults),
				new PropertyMetadata(Visibility.Collapsed));

		public string NothingText
		{
			get => (string)GetValue(NothingTextProperty);
			private set => SetValue(NothingTextProperty, value);
		}

		public static readonly DependencyProperty NothingTextProperty =
			DependencyProperty.Register(nameof(NothingText), typeof(string), typeof(SettingsSearchResults), new PropertyMetadata(string.Empty));

		/// <summary>Raised with the picked row's catalog key.</summary>
		public event EventHandler<string>? HitPicked;

		/// <summary>Show <paramref name="groups"/> for <paramref name="query"/> (empty = the "nothing matches" line).</summary>
		public void Show(string query, IReadOnlyList<SettingsSearchGroup> groups)
		{
			Groups = groups;
			NothingText = $"Nothing matches “{query}”. Try fewer or shorter words.";
			NothingVisibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		}

		private void OnRowPicked(object? sender, EventArgs e)
		{
			if (sender is SettingsSearchResultRow { Item.Hit: { } hit }) { HitPicked?.Invoke(this, hit.Key); }
		}
	}
}
