using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageManagers
{
	/// <summary>Settings → Storage's total + split bar (see the XAML header).</summary>
	public sealed partial class StorageManagerSettingsConsoleUsageBar : UserControl
	{
		public StorageManagerSettingsConsoleUsageBar()
		{
			InitializeComponent();
		}

		public StorageSettingsViewModel ViewModel
		{
			get => (StorageSettingsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(StorageSettingsViewModel), typeof(StorageManagerSettingsConsoleUsageBar),
				new PropertyMetadata(null));

		/// <summary>A segment's weight: its bytes as a star width (MB, so the numbers stay small). Zero = 0* (the
		/// column's MinWidth still draws a sliver).</summary>
		public GridLength Star(long bytes) => new(bytes / (1024.0 * 1024.0), GridUnitType.Star);

		/// <summary>A segment at rest, or while another row is hovered: 45% (dull but readable — the Source plate's grey-back);
		/// the hovered row's segment: full.</summary>
		public double SegmentOpacity(bool isDimmed) => isDimmed ? 0.45 : 1.0;
	}
}
