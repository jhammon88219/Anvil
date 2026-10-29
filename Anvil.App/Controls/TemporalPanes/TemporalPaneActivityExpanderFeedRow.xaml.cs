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
	/// One row of the activity expander's FEED table, or its labels (<see cref="IsHeader"/>) — see the XAML header.
	/// </summary>
	public sealed partial class TemporalWindowActivityExpanderFeedRow : UserControl
	{
		// State-dot literals (DATA colours, the expander's tone family): ok green · checking blue · failed red · waiting grey.
		private static readonly SolidColorBrush OkBrush = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush CheckingBrush = new(ColorHelper.FromArgb(0xFF, 0x5B, 0x8D, 0xEF));
		private static readonly SolidColorBrush FailedBrush = new(ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49));
		private static readonly SolidColorBrush WaitingBrush = new(ColorHelper.FromArgb(0xFF, 0x8A, 0x8F, 0x98));

		private FeedStatus? _watched;

		public TemporalWindowActivityExpanderFeedRow()
		{
			InitializeComponent();
			Loaded += (_, _) => ApplyLastState();
			Unloaded += (_, _) => Watch(null);
		}

		/// <summary>The feed this row shows (null on the label row).</summary>
		public FeedStatus? Status
		{
			get => (FeedStatus?)GetValue(StatusProperty);
			set => SetValue(StatusProperty, value);
		}

		public static readonly DependencyProperty StatusProperty =
			DependencyProperty.Register(nameof(Status), typeof(FeedStatus), typeof(TemporalWindowActivityExpanderFeedRow),
				new PropertyMetadata(null, (d, e) => ((TemporalWindowActivityExpanderFeedRow)d).Watch(e.NewValue as FeedStatus)));

		/// <summary>True = the table's fixed column labels (FEED · EVERY · LAST · NEXT); never lifts on hover.</summary>
		public bool IsHeader
		{
			get => (bool)GetValue(IsHeaderProperty);
			set => SetValue(IsHeaderProperty, value);
		}

		public static readonly DependencyProperty IsHeaderProperty =
			DependencyProperty.Register(nameof(IsHeader), typeof(bool), typeof(TemporalWindowActivityExpanderFeedRow), new PropertyMetadata(false));

		// ── x:Bind helpers ──

		public Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		public Visibility RowShown(bool isHeader, FeedStatus? status) => !isHeader && status is not null ? Visibility.Visible : Visibility.Collapsed;

		public Brush StateBrush(FeedState state) => state switch
		{
			FeedState.Ok => OkBrush,
			FeedState.Checking => CheckingBrush,
			FeedState.Failed => FailedBrush,
			_ => WaitingBrush,
		};

		// ── Hover lift (data rows only) ──

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
		{
			if (!IsHeader) VisualStateManager.GoToState(this, "PointerOver", true);
		}

		private void OnPointerExited(object sender, PointerRoutedEventArgs e) =>
			VisualStateManager.GoToState(this, "Normal", true);

		// ── A failed feed's LAST goes red (LastStates) ──

		private void Watch(FeedStatus? status)
		{
			if (_watched is not null) _watched.PropertyChanged -= OnStatusChanged;
			_watched = status;
			if (_watched is not null) _watched.PropertyChanged += OnStatusChanged;
			ApplyLastState();
		}

		private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(FeedStatus.State)) ApplyLastState();
		}

		private void ApplyLastState() =>
			VisualStateManager.GoToState(this, _watched?.State == FeedState.Failed ? "LastFailed" : "LastNormal", false);
	}
}
