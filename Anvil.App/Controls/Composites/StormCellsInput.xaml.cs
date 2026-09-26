using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The storm-cell controls (see the XAML header): a card and one toggle per mark with the displayed scan's
	/// count. Bound to <see cref="StormCellsViewModel"/>; hosted by both the Now and Past windows.
	/// </summary>
	public sealed partial class StormCellsInput : UserControl
	{
		public StormCellsInput()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the counts.</summary>
		public string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		// Collapse a card line with nothing to say (same helper as the other overlay controls).
		public Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>The view model; bound from the host (NowCastTab / PastCastTab → ViewModel.StormCells).</summary>
		public StormCellsViewModel ViewModel
		{
			get => (StormCellsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(StormCellsViewModel), typeof(StormCellsInput), new PropertyMetadata(null));
	}
}
