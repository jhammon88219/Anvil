using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The amber key of a reset (see the XAML header). Shows <see cref="Text"/> and raises <see cref="Click"/>; the host
	/// confirms and resets.
	/// </summary>
	public sealed partial class CautionResetButton : UserControl
	{
		public CautionResetButton()
		{
			InitializeComponent();
		}

		/// <summary>The key's words, e.g. "Reset all General settings".</summary>
		public string Text
		{
			get => (string)GetValue(TextProperty);
			set => SetValue(TextProperty, value);
		}

		public static readonly DependencyProperty TextProperty =
			DependencyProperty.Register(nameof(Text), typeof(string), typeof(CautionResetButton), new PropertyMetadata(""));

		/// <summary>Raised on a click — the host asks first (Dialogs/ConfirmResetDialog), then resets.</summary>
		public event RoutedEventHandler? Click;

		private void OnKeyClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
	}
}
