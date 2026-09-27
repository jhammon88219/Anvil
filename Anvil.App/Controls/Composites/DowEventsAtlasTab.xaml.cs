using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Anvil Atlas's DOW events tab (see the XAML header): the DOW frame library, moved here from the
	/// PastCast window. Bound to the coordinator <see cref="MapViewModel"/> — Show is a cross-subsystem act
	/// (<see cref="MapViewModel.ShowAtlasDowFrameAsync"/>); everything else is <c>ViewModel.Radar.Dow</c>.
	/// </summary>
	public sealed partial class DowEventsAtlasTab : UserControl
	{
		public DowEventsAtlasTab()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(DowEventsAtlasTab), new PropertyMetadata(null));

		// ⚠️ Import raises an event instead of showing the picker: a WinRT FileOpenPicker must be initialized
		// with a window HWND, and this is a UserControl inside the Atlas window's UserControl. The request
		// bubbles RadarAtlasWindow → MainWindow, the same chain the Map settings tab uses for the basemap folder.
		public event EventHandler? ImportRequested;

		// ── Presentation ──

		public string LibraryHeader(int count) =>
			"LIBRARY · " + count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		public string SelectedLabel(DowEvent? ev) => ev?.Label ?? string.Empty;

		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
		public Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
		public Visibility VisibleWhenText(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		// ── Actions ──

		private void OnImportClick(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);

		private async void OnShowClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await vm.ShowAtlasDowFrameAsync();
		}

		private async void OnClearClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await vm.Radar.Dow.ClearDowEventAsync();
		}

		private async void OnRemoveClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await vm.Radar.Dow.RemoveSelectedAsync();
		}
	}
}
