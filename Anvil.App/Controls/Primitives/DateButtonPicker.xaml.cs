using System;
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

		// ── The body: one of three grid shapes, rebuilt whole on every change ──

		private void Render()
		{
			MonthLabel.Text = Inv.DateTimeFormat.GetMonthName(_viewMonth);
			YearLabel.Text = _viewYear.ToString(Inv);
			Body.Children.Clear();
			Body.RowDefinitions.Clear();
			Body.ColumnDefinitions.Clear();
			switch (_view)
			{
				case View.Days: RenderDays(); break;
				case View.Months: RenderMonths(); break;
				default: RenderYears(); break;
			}
		}

		private void RenderDays()
		{
			Shape(7, 7, headerRow: true);
			var names = Inv.DateTimeFormat.ShortestDayNames; // Su Mo Tu …
			for (var c = 0; c < 7; c++)
			{
				Place(new TextBlock { Text = names[c], Style = (Style)Resources["WeekdayTextStyle"] }, 0, c);
			}

			var first = new DateOnly(_viewYear, _viewMonth, 1);
			var lead = (int)first.DayOfWeek;
			var start = first.AddDays(-lead);
			for (var i = 0; i < 42; i++)
			{
				var day = start.AddDays(i);
				var r = 1 + i / 7;
				var c = i % 7;
				if (day.Month != _viewMonth)
				{
					// Neighbouring months' days: shown for the calendar's shape, never clickable.
					Place(new TextBlock { Text = day.Day.ToString(Inv), Style = (Style)Resources["OtherMonthTextStyle"] }, r, c);
					continue;
				}
				var cell = Cell(day.Day.ToString(Inv), day == _draft, day >= Min && day <= Max);
				cell.Click += (_, _) => { _draft = day; WriteTyped(); Render(); };
				Place(cell, r, c);
			}
		}

		private void RenderMonths()
		{
			Shape(4, 3, headerRow: false);
			for (var m = 1; m <= 12; m++)
			{
				var month = m;
				var inRange = new DateOnly(_viewYear, m, DateTime.DaysInMonth(_viewYear, m)) >= Min
					&& new DateOnly(_viewYear, m, 1) <= Max;
				var cell = Cell(Inv.DateTimeFormat.GetAbbreviatedMonthName(m), m == _viewMonth, inRange);
				cell.Click += (_, _) => { _viewMonth = month; _view = View.Days; Render(); };
				Place(cell, (m - 1) / 3, (m - 1) % 3);
			}
		}

		private void RenderYears()
		{
			var first = Min.Year;
			var count = Max.Year - first + 1;
			Shape((count + 3) / 4, 4, headerRow: false);
			for (var i = 0; i < count; i++)
			{
				var year = first + i;
				var cell = Cell(year.ToString(Inv), year == _viewYear, true);
				cell.Click += (_, _) =>
				{
					_viewYear = year;
					// A month outside the range in the new year would show a page of disabled days — pull it in.
					if (new DateOnly(year, _viewMonth, 1) > Max) _viewMonth = Max.Month;
					if (new DateOnly(year, _viewMonth, DateTime.DaysInMonth(year, _viewMonth)) < Min) _viewMonth = Min.Month;
					_view = View.Days;
					Render();
				};
				Place(cell, i / 4, i % 4);
			}
		}

		private void Shape(int rows, int cols, bool headerRow)
		{
			for (var r = 0; r < rows; r++)
			{
				Body.RowDefinitions.Add(new RowDefinition
				{
					Height = headerRow && r == 0 ? new GridLength(22) : new GridLength(1, GridUnitType.Star),
				});
			}
			for (var c = 0; c < cols; c++)
			{
				Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			}
		}

		private Button Cell(string text, bool selected, bool enabled) => new()
		{
			Content = text,
			IsEnabled = enabled,
			Style = (Style)Resources[selected ? "SelectedCellStyle" : "CellStyle"],
		};

		private void Place(FrameworkElement e, int row, int col)
		{
			Grid.SetRow(e, row);
			Grid.SetColumn(e, col);
			Body.Children.Add(e);
		}
	}
}
