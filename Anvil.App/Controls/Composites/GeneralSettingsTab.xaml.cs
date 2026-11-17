using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>Settings → General (see the XAML header): the Startup, Units and Window sections.</summary>
	public sealed partial class GeneralSettingsTab : UserControl, Primitives.ISettingsPage
	{
		public GeneralSettingsTab()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(GeneralSettingsTab), new PropertyMetadata(null));

		public string ResetAllToolTip => "Startup, Units and Window back to their DEFAULT columns (asks first)";

		public async void ResetAll()
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Reset all General settings?",
				"Startup, Units and Window go back to their defaults — the DEFAULT column on this page. Your home site is cleared. " +
				"This can't be undone."))
			{
				vm.ResetGeneral();
			}
		}
	}
}
