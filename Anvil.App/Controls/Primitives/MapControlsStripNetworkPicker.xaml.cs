using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One square key on the tools tier whose upward list ticks which radar networks' site keys show on the map
	/// (see MapControlsStripNetworkPicker.xaml). <see cref="ShowNexrad"/> / <see cref="ShowTdwr"/> /
	/// <see cref="ShowResearch"/> are two-way; the host binds them to the radar VM's persisted flags.
	/// </summary>
	public sealed partial class MapControlsStripNetworkPicker : UserControl
	{
		private bool _isFlyoutOpen;

		public MapControlsStripNetworkPicker()
		{
			InitializeComponent();
			// GoToState before Loaded can be a no-op — re-apply once the tree is up.
			Loaded += (_, _) => ApplyStates();
		}

		/// <summary>Whether the operational NEXRAD sites' keys show.</summary>
		public bool ShowNexrad
		{
			get => (bool)GetValue(ShowNexradProperty);
			set => SetValue(ShowNexradProperty, value);
		}

		public static readonly DependencyProperty ShowNexradProperty =
			DependencyProperty.Register(nameof(ShowNexrad), typeof(bool), typeof(MapControlsStripNetworkPicker),
				new PropertyMetadata(true, OnShownChanged));

		/// <summary>Whether the TDWR airport radars' keys show.</summary>
		public bool ShowTdwr
		{
			get => (bool)GetValue(ShowTdwrProperty);
			set => SetValue(ShowTdwrProperty, value);
		}

		public static readonly DependencyProperty ShowTdwrProperty =
			DependencyProperty.Register(nameof(ShowTdwr), typeof(bool), typeof(MapControlsStripNetworkPicker),
				new PropertyMetadata(true, OnShownChanged));

		/// <summary>Whether the research radars' keys show.</summary>
		public bool ShowResearch
		{
			get => (bool)GetValue(ShowResearchProperty);
			set => SetValue(ShowResearchProperty, value);
		}

		public static readonly DependencyProperty ShowResearchProperty =
			DependencyProperty.Register(nameof(ShowResearch), typeof(bool), typeof(MapControlsStripNetworkPicker),
				new PropertyMetadata(true, OnShownChanged));

		// An unticked row's glyph fades rather than going grey: the hue is data, and a grey would be a theme brush.
		public double GlyphOpacity(bool shown) => shown ? 1.0 : 0.4;

		private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((MapControlsStripNetworkPicker)d).ApplyStates();

		private void OnFlyoutOpened(object? sender, object e)
		{
			_isFlyoutOpen = true;
			ApplyStates();
		}

		private void OnFlyoutClosed(object? sender, object e)
		{
			_isFlyoutOpen = false;
			ApplyStates();
		}

		// ONE place both state groups land.
		private void ApplyStates()
		{
			VisualStateManager.GoToState(this, ShowNexrad || ShowTdwr || ShowResearch ? "SomeShown" : "NoneShown", false);
			VisualStateManager.GoToState(this, _isFlyoutOpen ? "FlyoutOpen" : "FlyoutClosed", false);
		}
	}
}
