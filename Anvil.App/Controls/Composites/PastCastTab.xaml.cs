using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The PastCast window's body (see the XAML header) — the Timeframe card + pickers, the historical SPC
	/// outlook and that day's storm reports. Bound to the coordinator <see cref="MapViewModel"/>; the
	/// timeframe drives <c>ViewModel.Radar</c> and the historical outlook drives <c>ViewModel.PastOutlook</c>.
	///
	/// ⚠️ THERE IS NO WIDGET-SYNC CODE HERE ANY MORE, and that is the point of the pickers. This class used
	/// to carry ~110 lines converting between the view model's <c>PastEventTime</c> (a TimeSpan) and an
	/// editable hour combo, an editable minute combo and an AM/PM checkbox pair — a `_syncing` re-entry
	/// guard, a push and a pull, a text parser, four handlers, and a Visibility callback that existed
	/// because an editable combo will not render a programmatically-set value until it is realized. A
	/// TimePicker binds to that TimeSpan directly, so all of it went. Everything left below is presentation
	/// for the summary card, which has no state of its own.
	/// </summary>
	public sealed partial class PastCastTab : UserControl
	{
		public PastCastTab()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(PastCastTab),
				new PropertyMetadata(null));

		// ===== The summary card =====
		// Three states, and the card's whole job is telling them apart:
		//   nothing loaded  → "Not loaded yet",         Load is the accent action
		//   loaded, clean   → the load's own status,    Load steps back to a plain button
		//   loaded, dirty   → "Selection changed…",     Load lights again, and the card's edge with it
		// The dirty state is the one the old trailing status line could not show: that text was written by
		// the load and never revisited, so after an edit it described a window that was no longer selected.

		/// <summary>The card's footer line.</summary>
		public string CardFooter(bool loaded, bool dirty, string status) =>
			!loaded ? "Not loaded yet" :
			dirty ? "Selection changed — press Load" :
			status;

		/// <summary>Footer colour: accent while the selection is ahead of what is loaded, quiet otherwise.</summary>
		public Brush? FooterBrush(bool dirty) => Resource(dirty
			? "AccentTextFillColorPrimaryBrush"
			: "TextFillColorTertiaryBrush");

		/// <summary>The card's edge, which picks up the accent in the dirty state so the change reads from
		/// the card's shape and not only from its text.</summary>
		public Brush? CardStroke(bool dirty) => Resource(dirty
			? "AccentFillColorDefaultBrush"
			: "CardStrokeColorDefaultSolidBrush");

		/// <summary>Load is accent whenever pressing it would DO something — that is, always except when a
		/// window is loaded and the pickers still agree with it.</summary>
		public Style? LoadStyle(bool loaded, bool dirty) =>
			Lookup(loaded && !dirty ? "DefaultButtonStyle" : "AccentButtonStyle") as Style;

		// ⚠️ TryGetValue, never the indexer: a ResourceDictionary's indexer THROWS on a missing key, and
		// these run the moment the panel opens — a renamed system brush would take the window down rather
		// than draw the wrong colour. Null is a survivable answer for both a Brush and a Style.
		private static Brush? Resource(string key) => Lookup(key) as Brush;

		private static object? Lookup(string key) =>
			Application.Current.Resources.TryGetValue(key, out var value) ? value : null;

		private async void OnLoadClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.Radar is not RadarViewModel radar)
			{
				return;
			}
			// On a successful load, hand focus back to the map so the user can click radar sites.
			if (await radar.LoadSelectedPastEventAsync())
			{
				FocusMap();
			}
		}

		// Return focus to the map WebView so the user can immediately interact with it. Only finds it when
		// this body shares the main window's XamlRoot; hosted in its own OS window (its own XamlRoot) the
		// lookup misses and this is a no-op, same as it was before the split.
		private void FocusMap()
		{
			if (XamlRoot?.Content is FrameworkElement root &&
				root.FindName("MainMapWebView") is Control map)
			{
				map.Focus(FocusState.Programmatic);
			}
		}

		// x:Bind helper: collapse a card line that has nothing to say, so the card closes up rather than
		// leaving a gap where the context or footer would be.
		public Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
	}
}
