using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageManagers
{
	/// <summary>Settings → Storage's table (see the XAML header): the header row + one declared row per kind.</summary>
	public sealed partial class StorageManagerSettingsConsoleDataTable : UserControl
	{
		private bool _namesAligned;

		public StorageManagerSettingsConsoleDataTable()
		{
			InitializeComponent();
			Loaded += (_, _) => AlignNames();
			SizeChanged += (_, _) => AlignNames();
		}

		public StorageSettingsViewModel ViewModel
		{
			get => (StorageSettingsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(StorageSettingsViewModel), typeof(StorageManagerSettingsConsoleDataTable),
				new PropertyMetadata(null));

		// The rows are separate controls, so their NAME columns can't size together on their own: measure each row's
		// name (its column still Auto) and give all five the widest. Names are fixed text, so once is enough — but a
		// page that is still hidden lays out to 0, so wait until every name has a width.
		private void AlignNames()
		{
			if (_namesAligned) return;
			var rows = new[]
			{
				StorageManagerSettingsConsoleDataTableRowMaps,
				StorageManagerSettingsConsoleDataTableRowRadarVolumes, StorageManagerSettingsConsoleDataTableRowRadarDiagnostics,
				StorageManagerSettingsConsoleDataTableRowWeatherData, StorageManagerSettingsConsoleDataTableRowLogs,
				StorageManagerSettingsConsoleDataTableRowUsageHistory,
			};
			double widest = 0;
			foreach (var row in rows)
			{
				var w = row.NaturalNameWidth;
				if (w <= 0) return; // not laid out yet — the next SizeChanged retries
				widest = Math.Max(widest, w);
			}
			foreach (var row in rows) row.SetNameColumnWidth(Math.Ceiling(widest));
			_namesAligned = true;
		}
	}
}
