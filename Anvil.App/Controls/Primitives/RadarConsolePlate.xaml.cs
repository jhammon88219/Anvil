using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The casing of the floating radar console: one plate with a centred tab, drawn by <see cref="Primitives.PlateOutline"/>,
	/// and two content slots the host fills. See the XAML header.
	/// </summary>
	public sealed partial class RadarConsolePlate : UserControl
	{
		public RadarConsolePlate()
		{
			InitializeComponent();
		}

		/// <summary>What stands in the tab (the site picker).</summary>
		public object? TabContent
		{
			get => GetValue(TabContentProperty);
			set => SetValue(TabContentProperty, value);
		}

		public static readonly DependencyProperty TabContentProperty =
			DependencyProperty.Register(nameof(TabContent), typeof(object), typeof(RadarConsolePlate), new PropertyMetadata(null));

		/// <summary>What fills the console body (the time module / the model console).</summary>
		public object? ConsoleContent
		{
			get => GetValue(ConsoleContentProperty);
			set => SetValue(ConsoleContentProperty, value);
		}

		public static readonly DependencyProperty ConsoleContentProperty =
			DependencyProperty.Register(nameof(ConsoleContent), typeof(object), typeof(RadarConsolePlate), new PropertyMetadata(null));

		// Tell the outline where the tab landed. Raised by the plate's AND the tab slot's SizeChanged: the tab is centred,
		// so its left edge moves when either width changes. ActualOffset is the slot's arranged (pixel-snapped) position
		// in the layout Grid, which fills this control — so it is already in the outline's coordinates.
		private void OnShapeChanged(object sender, SizeChangedEventArgs e)
		{
			PlateOutline.TabLeft = RadarConsolePlateTabSlot.ActualOffset.X;
			PlateOutline.TabWidth = RadarConsolePlateTabSlot.ActualWidth;
			PlateOutline.TabHeight = RadarConsolePlateTabSlot.ActualHeight;
		}
	}
}
