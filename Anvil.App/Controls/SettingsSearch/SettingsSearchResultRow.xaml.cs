using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Anvil.Controls.SettingsSearch
{
	/// <summary>One match on the Settings search results page (see the XAML header). Raises <see cref="Picked"/>.</summary>
	public sealed partial class SettingsSearchResultRow : UserControl
	{
		public SettingsSearchResultRow()
		{
			InitializeComponent();
			KeyDown += OnKeyDown;
		}

		public SettingsSearchItem? Item
		{
			get => (SettingsSearchItem?)GetValue(ItemProperty);
			set => SetValue(ItemProperty, value);
		}

		public static readonly DependencyProperty ItemProperty =
			DependencyProperty.Register(nameof(Item), typeof(SettingsSearchItem), typeof(SettingsSearchResultRow), new PropertyMetadata(null));

		/// <summary>Raised on a click, Enter or Space.</summary>
		public event EventHandler? Picked;

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Hovered", true);

		private void OnPointerExited(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "NotHovered", true);

		private void OnTapped(object sender, TappedRoutedEventArgs e) => Picked?.Invoke(this, EventArgs.Empty);

		private void OnKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is VirtualKey.Enter or VirtualKey.Space)
			{
				e.Handled = true;
				Picked?.Invoke(this, EventArgs.Empty);
			}
		}
	}
}
