using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The colour / opacity / thickness / line rows for ONE range ring (see the XAML header). Bound to that
	/// ring's <see cref="RingStyleViewModel"/>.
	/// </summary>
	public sealed partial class RingStyleEditor : UserControl
	{
		public RingStyleEditor()
		{
			InitializeComponent();
		}

		/// <summary>The ring this editor styles; bound from the host.</summary>
		public RingStyleViewModel ViewModel
		{
			get => (RingStyleViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RingStyleViewModel), typeof(RingStyleEditor), new PropertyMetadata(null));

		/// <summary>x:Bind readout: "55 %".</summary>
		public string Pct(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0} %");

		/// <summary>x:Bind readout: "1.3 px".</summary>
		public string Px(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0.0} px");
	}
}
