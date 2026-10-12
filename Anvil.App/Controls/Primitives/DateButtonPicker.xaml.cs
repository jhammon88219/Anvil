using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A calendar date behind one button (see the XAML header). Host contract: <see cref="Date"/> two-way,
	/// bounded by <see cref="MinDate"/>/<see cref="MaxDate"/>. The flyout edits a DRAFT; only Set writes
	/// <see cref="Date"/>.
	/// </summary>
	public sealed partial class DateButtonPicker : UserControl
	{
		private enum View { Days, Months, Years }

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private static readonly string[] Formats = { "MM/dd/yyyy", "M/d/yyyy" };

		// The draft (what Set would commit) and the page being shown — separate, so paging the month or
		// year never moves the selection.
		private DateOnly _draft;
		private int _viewYear, _viewMonth;
		private View _view;
		private bool _writingTyped;

		public DateButtonPicker()
		{
			InitializeComponent();
			UpdateFace();
		}

		/// <summary>The committed date. Written ONLY by Set (as local midnight); a write from the host
		/// just refreshes the face.</summary>
		public DateTimeOffset? Date
		{
			get => (DateTimeOffset?)GetValue(DateProperty);
			set => SetValue(DateProperty, value);
		}

		public static readonly DependencyProperty DateProperty =
			DependencyProperty.Register(nameof(Date), typeof(DateTimeOffset?), typeof(DateButtonPicker),
				new PropertyMetadata(null, (d, _) => ((DateButtonPicker)d).UpdateFace()));

		/// <summary>Earliest selectable day (its own Y/M/D are used).</summary>
		public DateTimeOffset MinDate
		{
			get => (DateTimeOffset)GetValue(MinDateProperty);
			set => SetValue(MinDateProperty, value);
		}

		public static readonly DependencyProperty MinDateProperty =
			DependencyProperty.Register(nameof(MinDate), typeof(DateTimeOffset), typeof(DateButtonPicker),
				new PropertyMetadata(new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero)));

		/// <summary>Latest selectable day (its own Y/M/D are used).</summary>
		public DateTimeOffset MaxDate
		{
			get => (DateTimeOffset)GetValue(MaxDateProperty);
			set => SetValue(MaxDateProperty, value);
		}

		public static readonly DependencyProperty MaxDateProperty =
			DependencyProperty.Register(nameof(MaxDate), typeof(DateTimeOffset), typeof(DateButtonPicker),
				new PropertyMetadata(new DateTimeOffset(2100, 12, 31, 0, 0, 0, TimeSpan.Zero)));

		private DateOnly Min => new(MinDate.Year, MinDate.Month, MinDate.Day);
		private DateOnly Max => new(MaxDate.Year, MaxDate.Month, MaxDate.Day);

		private void UpdateFace() =>
			FaceText.Text = Date is { } d ? d.ToString("MM/dd/yyyy", Inv) : "—";

		// Every open starts from the COMMITTED value: a dismissed draft is gone.
		private void OnFlyoutOpening(object? sender, object e)
		{
			var seed = Date is { } d ? new DateOnly(d.Year, d.Month, d.Day) : Max;
			_draft = Clamp(seed);
			_viewYear = _draft.Year;
			_viewMonth = _draft.Month;
			_view = View.Days;
			WriteTyped();
			Render();
		}

		private void OnSetClick(object sender, RoutedEventArgs e)
		{
			// ⚠️ LOCAL MIDNIGHT FROM PARTS — RadarViewModel.LocalMidnight's rule; never via a DateTime.
			Date = new DateTimeOffset(_draft.Year, _draft.Month, _draft.Day, 0, 0, 0,
				TimeZoneInfo.Local.GetUtcOffset(new DateTime(_draft.Year, _draft.Month, _draft.Day)));
			PickerFlyout.Hide();
		}

		private void OnMonthHeaderClick(object sender, RoutedEventArgs e)
		{
			_view = _view == View.Months ? View.Days : View.Months;
			Render();
		}

		private void OnYearHeaderClick(object sender, RoutedEventArgs e)
		{
			_view = _view == View.Years ? View.Days : View.Years;
			Render();
		}

		// A valid, in-range typed date moves the draft and the page; anything else is left alone (mid-typing).
		private void OnTypedChanged(object sender, TextChangedEventArgs e)
		{
			if (_writingTyped) return;
			if (!DateOnly.TryParseExact(Typed.Text.Trim(), Formats, Inv, DateTimeStyles.None, out var typed)) return;
			if (typed < Min || typed > Max) return;
			_draft = typed;
			_viewYear = typed.Year;
			_viewMonth = typed.Month;
			_view = View.Days;
			Render();
		}

		private void WriteTyped()
		{
			_writingTyped = true;
			Typed.Text = _draft.ToString("MM/dd/yyyy", Inv);
			_writingTyped = false;
		}

		private DateOnly Clamp(DateOnly d) => d < Min ? Min : d > Max ? Max : d;

		// ── The body: three declared pages; Render lists the shown page's cells and picks the page ──

		private void Render()
		{
			MonthLabel.Text = Inv.DateTimeFormat.GetMonthName(_viewMonth);
			YearLabel.Text = _viewYear.ToString(Inv);
			DaysPage.Visibility = _view == View.Days ? Visibility.Visible : Visibility.Collapsed;
			MonthCells.Visibility = _view == View.Months ? Visibility.Visible : Visibility.Collapsed;
			YearCells.Visibility = _view == View.Years ? Visibility.Visible : Visibility.Collapsed;
			switch (_view)
			{
				case View.Days: RenderDays(); break;
				case View.Months: RenderMonths(); break;
				default: RenderYears(); break;
			}
		}

		private void RenderDays()
		{
			WeekdayCells.ItemsSource ??= Inv.DateTimeFormat.ShortestDayNames; // Su Mo Tu …

			var first = new DateOnly(_viewYear, _viewMonth, 1);
			var lead = (int)first.DayOfWeek;
			var start = first.AddDays(-lead);
			var cells = new List<CalendarCell>(42);
			for (var i = 0; i < 42; i++)
			{
				var day = start.AddDays(i);
				cells.Add(day.Month != _viewMonth
					// Neighbouring months' days: shown for the calendar's shape, never clickable.
					? CalendarCell.Inert(day.Day.ToString(Inv))
					: new CalendarCell(day.Day.ToString(Inv), day == _draft, day >= Min && day <= Max,
						() => { _draft = day; WriteTyped(); Render(); }));
			}
			DayCells.ItemsSource = cells;
		}

		private void RenderMonths()
		{
			var cells = new List<CalendarCell>(12);
			for (var m = 1; m <= 12; m++)
			{
				var month = m;
				var inRange = new DateOnly(_viewYear, m, DateTime.DaysInMonth(_viewYear, m)) >= Min
					&& new DateOnly(_viewYear, m, 1) <= Max;
				cells.Add(new CalendarCell(Inv.DateTimeFormat.GetAbbreviatedMonthName(m), m == _viewMonth, inRange,
					() => { _viewMonth = month; _view = View.Days; Render(); }));
			}
			MonthCells.ItemsSource = cells;
		}

		private void RenderYears()
		{
			var first = Min.Year;
			var count = Max.Year - first + 1;
			var cells = new List<CalendarCell>(Math.Max(0, count));
			for (var i = 0; i < count; i++)
			{
				var year = first + i;
				cells.Add(new CalendarCell(year.ToString(Inv), year == _viewYear, true, () =>
				{
					_viewYear = year;
					// A month outside the range in the new year would show a page of disabled days — pull it in.
					if (new DateOnly(year, _viewMonth, 1) > Max) _viewMonth = Max.Month;
					if (new DateOnly(year, _viewMonth, DateTime.DaysInMonth(year, _viewMonth)) < Min) _viewMonth = Min.Month;
					_view = View.Days;
					Render();
				}));
			}
			YearCells.ItemsSource = cells;
		}
	}
}

namespace Anvil.Controls.Primitives
{
	/// <summary>One cell of a <see cref="DateButtonPicker"/> page (a day, a month, a year): its text, its state —
	/// which of the three declared cell templates it wears — and what a click on it does.</summary>
	public sealed class CalendarCell
	{
		private readonly Action? _pick;

		public CalendarCell(string text, bool isSelected, bool isEnabled, Action pick)
		{
			Text = text;
			IsSelected = isSelected;
			IsEnabled = isEnabled;
			_pick = pick;
		}

		private CalendarCell(string text)
		{
			Text = text;
			IsInert = true;
		}

		/// <summary>A neighbouring month's day: shown, never clickable.</summary>
		public static CalendarCell Inert(string text) => new(text);

		public string Text { get; }
		public bool IsSelected { get; }
		public bool IsEnabled { get; }
		public bool IsInert { get; }

		/// <summary>The cell's Click (bound with x:Bind).</summary>
		public void Pick() => _pick?.Invoke();
	}

	/// <summary>Picks a calendar cell's template by its state; the templates are declared in DateButtonPicker.xaml.</summary>
	public sealed class CalendarCellTemplateSelector : DataTemplateSelector
	{
		public DataTemplate? Plain { get; set; }
		public DataTemplate? Selected { get; set; }
		public DataTemplate? Inert { get; set; }

		protected override DataTemplate? SelectTemplateCore(object item) => Pick(item);
		protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => Pick(item);

		private DataTemplate? Pick(object item) => item is CalendarCell c
			? c.IsInert ? Inert : c.IsSelected ? Selected : Plain
			: Plain;
	}
}
