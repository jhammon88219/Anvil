using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalWindows
{
	/// <summary>
	/// One row of PastCast's LAYER · RESULT · SOURCE table, or its labels (<see cref="IsHeader"/>) — see the XAML header.
	/// </summary>
	public sealed partial class TemporalWindowActivityExpanderLoadRow : UserControl
	{
		// State-dot literals (DATA colours, the activity rows' family): loaded green · loading blue · failed red · else grey.
		private static readonly SolidColorBrush LoadedBrush = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush LoadingBrush = new(ColorHelper.FromArgb(0xFF, 0x5B, 0x8D, 0xEF));
		private static readonly SolidColorBrush FailedBrush = new(ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49));
		private static readonly SolidColorBrush QuietBrush = new(ColorHelper.FromArgb(0xFF, 0x5A, 0x5F, 0x66));

		private PastLayerStatus? _watched;

		public TemporalWindowActivityExpanderLoadRow()
		{
			InitializeComponent();
			Loaded += (_, _) => ApplyResultState();
			Unloaded += (_, _) => Watch(null);
		}

		/// <summary>The layer this row shows (null on the label row).</summary>
		public PastLayerStatus? Status
		{
			get => (PastLayerStatus?)GetValue(StatusProperty);
			set => SetValue(StatusProperty, value);
		}

		public static readonly DependencyProperty StatusProperty =
			DependencyProperty.Register(nameof(Status), typeof(PastLayerStatus), typeof(TemporalWindowActivityExpanderLoadRow),
				new PropertyMetadata(null, (d, e) => ((TemporalWindowActivityExpanderLoadRow)d).Watch(e.NewValue as PastLayerStatus)));

		/// <summary>True = the table's fixed column labels (LAYER · RESULT · SOURCE); never lifts on hover.</summary>
		public bool IsHeader
		{
			get => (bool)GetValue(IsHeaderProperty);
			set => SetValue(IsHeaderProperty, value);
		}

		public static readonly DependencyProperty IsHeaderProperty =
			DependencyProperty.Register(nameof(IsHeader), typeof(bool), typeof(TemporalWindowActivityExpanderLoadRow), new PropertyMetadata(false));

		// ── x:Bind helpers (FLAT — a nested call surfaces only as WMC9999, docs/bugs.md S20) ──

		public Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		public Visibility RowShown(bool isHeader, PastLayerStatus? status) => !isHeader && status is not null ? Visibility.Visible : Visibility.Collapsed;

		public Visibility SourceShown(bool isHeader, bool canRetry) => !isHeader && !canRetry && Status is not null ? Visibility.Visible : Visibility.Collapsed;

		public Visibility RetryShown(bool isHeader, bool canRetry) => !isHeader && canRetry ? Visibility.Visible : Visibility.Collapsed;

		// An empty tooltip would still pop an empty box — null shows none.
		public object? TipOf(string reason) => string.IsNullOrEmpty(reason) ? null : reason;

		public Brush StateBrush(PastLayerState state) => state switch
		{
			PastLayerState.Loaded => LoadedBrush,
			PastLayerState.Loading => LoadingBrush,
			PastLayerState.Failed => FailedBrush,
			_ => QuietBrush,
		};

		private void OnRetryClick(object sender, RoutedEventArgs e) => Status?.Retry();

		// ── Hover lift (data rows only) ──

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
		{
			if (!IsHeader) VisualStateManager.GoToState(this, "PointerOver", true);
		}

		private void OnPointerExited(object sender, PointerRoutedEventArgs e) =>
			VisualStateManager.GoToState(this, "Normal", true);

		// ── RESULT colour (ResultStates): failed red · nothing-in-it secondary · else primary ──

		private void Watch(PastLayerStatus? status)
		{
			if (_watched is not null) _watched.PropertyChanged -= OnStatusChanged;
			_watched = status;
			if (_watched is not null) _watched.PropertyChanged += OnStatusChanged;
			ApplyResultState();
		}

		private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(PastLayerStatus.State)) ApplyResultState();
		}

		private void ApplyResultState() =>
			VisualStateManager.GoToState(this, _watched?.State switch
			{
				PastLayerState.Failed => "ResultFailed",
				PastLayerState.Loaded or PastLayerState.Loading => "ResultNormal",
				_ => "ResultQuiet",
			}, false);
	}
}
