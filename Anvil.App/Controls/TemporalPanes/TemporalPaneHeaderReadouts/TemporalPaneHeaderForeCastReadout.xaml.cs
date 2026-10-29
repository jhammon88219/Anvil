using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalWindows.TemporalWindowHeaderReadouts
{
	/// <summary>The ForeCast header's Readout — highest risk over the refresh countdown (see the XAML header).</summary>
	public sealed partial class TemporalWindowHeaderForeCastReadout : UserControl
	{
		public TemporalWindowHeaderForeCastReadout()
		{
			InitializeComponent();
		}

		/// <summary>"No areas" / "No outlook" dims to 40% (PastCast's placeholder rule).</summary>
		public double PeakOpacity(bool hasPeak) => hasPeak ? 1.0 : 0.4;

		/// <summary>The outlook subsystem; bound from the host.</summary>
		public OutlookViewModel ViewModel
		{
			get => (OutlookViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(OutlookViewModel), typeof(TemporalWindowHeaderForeCastReadout), new PropertyMetadata(null));
	}
}
