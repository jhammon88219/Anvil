using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.RingsSettingsConsoles
{
	/// <summary>
	/// One range ring's look card (see the XAML header): <see cref="Title"/> + the four rows over <see cref="Ring"/>.
	/// Declared three times by Settings → Rings &amp; Ruler.
	/// </summary>
	public sealed partial class RingsSettingsConsoleRingStyle : UserControl
	{
		public RingsSettingsConsoleRingStyle()
		{
			InitializeComponent();
		}

		/// <summary>The card's title ("Reflectivity reach" …).</summary>
		public string Title
		{
			get => (string)GetValue(TitleProperty);
			set => SetValue(TitleProperty, value);
		}

		public static readonly DependencyProperty TitleProperty =
			DependencyProperty.Register(nameof(Title), typeof(string), typeof(RingsSettingsConsoleRingStyle), new PropertyMetadata(""));

		/// <summary>The ring this card edits.</summary>
		public RingStyleViewModel? Ring
		{
			get => (RingStyleViewModel?)GetValue(RingProperty);
			set => SetValue(RingProperty, value);
		}

		public static readonly DependencyProperty RingProperty =
			DependencyProperty.Register(nameof(Ring), typeof(RingStyleViewModel), typeof(RingsSettingsConsoleRingStyle), new PropertyMetadata(null));

		/// <summary>False greys every row and Restore defaults (e.g. the distance rings while they're off).</summary>
		public bool IsEditable
		{
			get => (bool)GetValue(IsEditableProperty);
			set => SetValue(IsEditableProperty, value);
		}

		public static readonly DependencyProperty IsEditableProperty =
			DependencyProperty.Register(nameof(IsEditable), typeof(bool), typeof(RingsSettingsConsoleRingStyle), new PropertyMetadata(true));

		/// <summary>The Color row's entry in Services/Settings/SettingsText (its hint, greyed hint and About) — each ring's own
		/// ("ring.color.refl", "ring.color.distance"), else the shared "ring.color".</summary>
		public string ColorKey
		{
			get => (string)GetValue(ColorKeyProperty);
			set => SetValue(ColorKeyProperty, value);
		}

		public static readonly DependencyProperty ColorKeyProperty =
			DependencyProperty.Register(nameof(ColorKey), typeof(string), typeof(RingsSettingsConsoleRingStyle), new PropertyMetadata("ring.color"));

		/// <summary>The Color row's hint: the ring's own, or (greyed) what turns it on.</summary>
		public string HintFor(bool editable, string colorKey) => Anvil.Services.SettingsText.HintFor(colorKey, editable);
		public string Pct(double value) => $"{value:0}%";
		public string Px(double value) => value.ToString("0.0", CultureInfo.CurrentCulture) + " px";

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (Ring is { } ring && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, $"Restore {Title} defaults?",
				"Its color, opacity, thickness and line go back to the DEFAULT column."))
			{
				ring.RestoreDefaults();
			}
		}
	}
}
