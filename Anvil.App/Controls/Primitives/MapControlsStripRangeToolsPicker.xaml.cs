using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One square key on the tools tier whose upward list ticks the range rings and the range ruler (see
	/// MapControlsStripRangeToolsPicker.xaml). <see cref="AreRingsOn"/> / <see cref="IsRulerOn"/> are two-way; the host
	/// binds them to Radar.RangeRings.RingsVisible / Radar.IsRangeRulerOn.
	/// </summary>
	public sealed partial class MapControlsStripRangeToolsPicker : UserControl
	{
		private bool _isFlyoutOpen;

		public MapControlsStripRangeToolsPicker()
		{
			InitializeComponent();
			// GoToState before Loaded can be a no-op — re-apply once the tree is up.
			Loaded += (_, _) => ApplyStates();
		}

		/// <summary>The master show/hide for every range ring (persisted by the host's VM).</summary>
		public bool AreRingsOn
		{
			get => (bool)GetValue(AreRingsOnProperty);
			set => SetValue(AreRingsOnProperty, value);
		}

		public static readonly DependencyProperty AreRingsOnProperty =
			DependencyProperty.Register(nameof(AreRingsOn), typeof(bool), typeof(MapControlsStripRangeToolsPicker),
				new PropertyMetadata(false, OnToolChanged));

		/// <summary>Whether the range ruler is armed.</summary>
		public bool IsRulerOn
		{
			get => (bool)GetValue(IsRulerOnProperty);
			set => SetValue(IsRulerOnProperty, value);
		}

		public static readonly DependencyProperty IsRulerOnProperty =
			DependencyProperty.Register(nameof(IsRulerOn), typeof(bool), typeof(MapControlsStripRangeToolsPicker),
				new PropertyMetadata(false, OnToolChanged));

		/// <summary>The ruler's row is live (a radar is on screen). An armed ruler stays ticked while it's false.</summary>
		public bool CanUseRuler
		{
			get => (bool)GetValue(CanUseRulerProperty);
			set => SetValue(CanUseRulerProperty, value);
		}

		public static readonly DependencyProperty CanUseRulerProperty =
			DependencyProperty.Register(nameof(CanUseRuler), typeof(bool), typeof(MapControlsStripRangeToolsPicker),
				new PropertyMetadata(true));

		private static void OnToolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((MapControlsStripRangeToolsPicker)d).ApplyStates();

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
			VisualStateManager.GoToState(this, AreRingsOn || IsRulerOn ? "Lit" : "Unlit", false);
			VisualStateManager.GoToState(this, _isFlyoutOpen ? "FlyoutOpen" : "FlyoutClosed", false);
		}
	}
}
