using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Anvil.Controls.LiveDataSettingsConsoles
{
	/// <summary>One row of the Source plate's details table: a trait and its value per source, lifted on hover (see the XAML header).</summary>
	public sealed partial class LiveDataSettingsConsoleSourceDetailRow : UserControl
	{
		public LiveDataSettingsConsoleSourceDetailRow()
		{
			InitializeComponent();
		}

		/// <summary>The trait, in the label column ("How fast").</summary>
		public string Label
		{
			get => (string)GetValue(LabelProperty);
			set => SetValue(LabelProperty, value);
		}

		public static readonly DependencyProperty LabelProperty =
			DependencyProperty.Register(nameof(Label), typeof(string), typeof(LiveDataSettingsConsoleSourceDetailRow), new PropertyMetadata(""));

		/// <summary>The IEM live feed's value.</summary>
		public string Iem
		{
			get => (string)GetValue(IemProperty);
			set => SetValue(IemProperty, value);
		}

		public static readonly DependencyProperty IemProperty =
			DependencyProperty.Register(nameof(Iem), typeof(string), typeof(LiveDataSettingsConsoleSourceDetailRow), new PropertyMetadata(""));

		/// <summary>NOAA Weather Wire's value.</summary>
		public string Nwws
		{
			get => (string)GetValue(NwwsProperty);
			set => SetValue(NwwsProperty, value);
		}

		public static readonly DependencyProperty NwwsProperty =
			DependencyProperty.Register(nameof(Nwws), typeof(string), typeof(LiveDataSettingsConsoleSourceDetailRow), new PropertyMetadata(""));

		/// <summary>Polling only's value.</summary>
		public string Polling
		{
			get => (string)GetValue(PollingProperty);
			set => SetValue(PollingProperty, value);
		}

		public static readonly DependencyProperty PollingProperty =
			DependencyProperty.Register(nameof(Polling), typeof(string), typeof(LiveDataSettingsConsoleSourceDetailRow), new PropertyMetadata(""));

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "PointerOver", false);

		private void OnPointerLeft(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Normal", false);
	}
}
