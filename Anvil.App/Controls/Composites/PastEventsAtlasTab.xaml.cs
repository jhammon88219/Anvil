using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Anvil Atlas's Past events tab (see the XAML header): the saved-event library as a master–detail
	/// browser. Bound to the coordinator <see cref="MapViewModel"/> — Play is a cross-subsystem act
	/// (<see cref="MapViewModel.PlayAtlasEvent"/>); everything else is <c>ViewModel.SavedEvents</c>.
	/// </summary>
	public sealed partial class PastEventsAtlasTab : UserControl
	{
		public PastEventsAtlasTab()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(PastEventsAtlasTab),
				new PropertyMetadata(null, OnViewModelChanged));

		// The grouped list's CollectionViewSource can't x:Bind, so its Source is handed over here — BEFORE
		// x:Bind's own DP listener runs, so the sections exist when SelectedItem pushes the selection (the same
		// ordering the sites tab relies on).
		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var tab = (PastEventsAtlasTab)d;
			((CollectionViewSource)tab.Resources["EventGroupsSource"]).Source =
				(e.NewValue as MapViewModel)?.SavedEvents.Groups;
		}

		// ── Presentation ──────────────────────────────────────────────────────────────────────────────

		/// <summary>The type's STAND-IN colour (row bar + badge dot) until rows get type icons. ⚠️ DATA colours,
		/// literal — the map's own TO red, a hurricane blue, a derecho amber — never themed.</summary>
		public static Brush KindBrush(SavedEventKind kind) => new SolidColorBrush(kind switch
		{
			SavedEventKind.Tornado => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xE5, 0x3E, 0x3E),
			SavedEventKind.Hurricane => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x3B, 0x82, 0xF6),
			SavedEventKind.Derecho => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xE0, 0x9A, 0x1F),
			_ => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x6E, 0x76, 0x81),
		});

		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
		public Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
		public Visibility VisibleWhenText(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
		public Visibility VisibleWhenCustomNotEditing(bool custom, bool editing) => custom && !editing ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The tiles give way to the replace question or the editor (one row, one thing at a time).</summary>
		public Visibility TilesVisible(bool confirming, bool editing) => confirming || editing ? Visibility.Collapsed : Visibility.Visible;

		public string LegNumber(int index) => (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

		// The selection can be far down the list (opened from PastCast on the picked event) — keep it on screen.
		private void OnListLoaded(object sender, RoutedEventArgs e) => ScrollToSelection((ListView)sender);
		private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e) => ScrollToSelection((ListView)sender);

		private static void ScrollToSelection(ListView list)
		{
			if (list.SelectedItem is { } item)
			{
				list.ScrollIntoView(item);
			}
		}

		// ── Actions ───────────────────────────────────────────────────────────────────────────────────

		private void OnPlayClick(object sender, RoutedEventArgs e) => ViewModel?.PlayAtlasEvent(confirmed: false);

		private void OnReplaceClick(object sender, RoutedEventArgs e) => ViewModel?.PlayAtlasEvent(confirmed: true);

		private void OnKeepClick(object sender, RoutedEventArgs e) => ViewModel?.SavedEvents.CancelReplace();

		private void OnLegClick(object sender, RoutedEventArgs e)
		{
			if ((sender as FrameworkElement)?.Tag is SavedEventLegRow leg) ViewModel?.SavedEvents.ShowAtlasLeg(leg);
		}

		private void OnEditClick(object sender, RoutedEventArgs e) => ViewModel?.SavedEvents.BeginEdit();

		private void OnCancelEditClick(object sender, RoutedEventArgs e) => ViewModel?.SavedEvents.CancelEdit();

		private void OnSaveEditClick(object sender, RoutedEventArgs e) => ViewModel?.SavedEvents.SaveEdit();

		private void OnClearTimeClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.SavedEvents is { } events)
			{
				events.EditKeyStart = null;
				events.EditKeyEnd = null;
			}
		}
	}
}
