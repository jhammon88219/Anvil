using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The product label stamped in one map pane's corner (see PaneWatermark.xaml). Bind <see cref="Text"/>
	/// to that pane's short product label; the host places it over the pane and decides when it shows.
	/// </summary>
	public sealed partial class PaneWatermark : UserControl
	{
		public PaneWatermark()
		{
			InitializeComponent();
		}

		/// <summary>The pane's short product label ("Ref" / "Vel" / "SRV" / "CC" …).</summary>
		public string Text
		{
			get => (string)GetValue(TextProperty);
			set => SetValue(TextProperty, value);
		}

		public static readonly DependencyProperty TextProperty =
			DependencyProperty.Register(nameof(Text), typeof(string), typeof(PaneWatermark), new PropertyMetadata(string.Empty));
	}
}
