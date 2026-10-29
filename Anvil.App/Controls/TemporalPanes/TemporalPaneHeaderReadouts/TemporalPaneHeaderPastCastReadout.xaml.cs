using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes.TemporalPaneHeaderReadouts
{
	/// <summary>The PastCast header's Readout — the loaded replay window's time range (see the XAML header).</summary>
	public sealed partial class TemporalPaneHeaderPastCastReadout : UserControl
	{
		public TemporalPaneHeaderPastCastReadout()
		{
			InitializeComponent();
		}

		/// <summary>The range at 30; the placeholder at 16.</summary>
		public double RangeFontSize(bool loaded) => loaded ? 30 : 16;

		/// <summary>A placeholder dims to 40%.</summary>
		public double PlaceholderOpacity(bool loaded) => loaded ? 1.0 : 0.4;

		/// <summary>The radar subsystem; bound from the host.</summary>
		public RadarViewModel ViewModel
		{
			get => (RadarViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarViewModel), typeof(TemporalPaneHeaderPastCastReadout), new PropertyMetadata(null));
	}
}
