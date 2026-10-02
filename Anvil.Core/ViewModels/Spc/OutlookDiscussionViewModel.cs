using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The Outlook Discussion window's content: SPC's discussion text, parsed (OutlookDiscussionParser) and laid
	/// out as labelled rows, each label with its one-sentence hint (OutlookGlossary). Fed by
	/// <see cref="MapViewModel"/> with whichever discussion the running outlook mode shows (live or PastCast).
	/// </summary>
	/// <remarks>
	/// ⚠️ The text it is handed is sometimes a STATUS line, not a discussion ("Loading forecast discussion…", "pick
	/// Latest to read it"). Anything without an SPC product id is shown as <see cref="Message"/> instead of parts.
	/// </remarks>
	public sealed class OutlookDiscussionViewModel : ObservableObject
	{
		private string _text = string.Empty;
		private OutlookDiscussion _parsed = OutlookDiscussionParser.Parse(string.Empty);
		private bool _isPreviousExpanded;

		private string? _sourceUrl;
		private bool _isArchive;

		/// <summary>Takes the current discussion text and the SPC page it came from (<paramref name="isArchive"/> =
		/// PastCast's per-issuance archive copy). A new text collapses the previous discussion again.</summary>
		public void Load(string? text, string? sourceUrl = null, bool isArchive = false,
			IReadOnlyList<OutlookRiskArea>? riskAreas = null, string? areasFile = null, string? distanceUnit = null)
		{
			text ??= string.Empty;
			riskAreas ??= Array.Empty<OutlookRiskArea>();
			_distanceUnit = distanceUnit;
			if (text == _text && sourceUrl == _sourceUrl && isArchive == _isArchive && ReferenceEquals(riskAreas, _riskAreas)
				&& areasFile == _areasFile) return;
			_sourceUrl = sourceUrl;
			_isArchive = isArchive;
			_riskAreas = riskAreas;
			_areasFile = areasFile;
			if (text == _text) { Locate(); BuildCards(); OnPropertyChanged(string.Empty); return; } // only the source/table moved
			_text = text;
			_parsed = OutlookDiscussionParser.Parse(text);
			Headlines = _parsed.Headlines.Select(h => new OutlookHeadlineRow(h)).ToArray();
			Locate();
			BuildCards();
			Rows =_parsed.Sections.Select(s => new OutlookSectionRow(s)).ToArray();
			PreviousRows = _parsed.PreviousSections.Select(s => new OutlookSectionRow(s)).ToArray();
			_isPreviousExpanded = false;
			OnPropertyChanged(string.Empty); // every property below derives from the one parse
		}

		// ── The parts ──

		public bool IsDiscussion => _parsed.ProductId is not null;
		public bool IsMessage => !IsDiscussion;
		/// <summary>The status line shown in place of the parts (empty text → nothing to say).</summary>
		public string Message => IsDiscussion ? string.Empty : _text.Trim();

		public string Title => IsDiscussion ? _parsed.Title : "Outlook discussion";
		public string Issued => _parsed.Issued ?? string.Empty;
		public string Valid => _parsed.Valid ?? string.Empty;
		public string Forecaster => _parsed.Forecaster ?? string.Empty;
		public string NextOutlook => _parsed.NextOutlook ?? string.Empty;
		public bool HasForecaster => _parsed.Forecaster is not null;
		public bool HasNextOutlook => _parsed.NextOutlook is not null;

		public IReadOnlyList<OutlookHeadlineRow> Headlines { get; private set; } = Array.Empty<OutlookHeadlineRow>();
		public bool HasHeadlines => Headlines.Count > 0;

		// ── RISK CARDS (option B, the user's call 2026-10-02): ALL FIVE severe levels, always, MARGINAL on the LEFT →
		// High on the right (low → high, the user's call), each with SPC's own 1-5 number; a level not in this outlook
		// is dimmed, and YOUR level gets a ring. Numbers come from SPC's risk-area table on the same page
		// (SpcRiskTableParser); without a table (older archive pages) presence comes from the headlines and the cards
		// carry no figures. General thunder is left out (SPC publishes no figures for it).
		// ⚠️ The card ORDER on screen is the XAML's column order; CardOrder only maps codes to cards.

		private IReadOnlyList<OutlookRiskArea> _riskAreas = Array.Empty<OutlookRiskArea>();
		private static readonly string[] CardOrder = { "HIGH", "MDT", "ENH", "SLGT", "MRGL" };

		public OutlookRiskCard HighCard { get; private set; } = OutlookRiskCard.Absent("HIGH");
		public OutlookRiskCard ModerateCard { get; private set; } = OutlookRiskCard.Absent("MDT");
		public OutlookRiskCard EnhancedCard { get; private set; } = OutlookRiskCard.Absent("ENH");
		public OutlookRiskCard SlightCard { get; private set; } = OutlookRiskCard.Absent("SLGT");
		public OutlookRiskCard MarginalCard { get; private set; } = OutlookRiskCard.Absent("MRGL");

		/// <summary>The cards row shows on every Day 1-3 discussion (headlines or a table); not on Day 4-8.</summary>
		public bool HasRiskCards => IsDiscussion && (HasHeadlines || _riskAreas.Count > 0);

		private void BuildCards()
		{
			var cards = CardOrder.Select(code =>
			{
				var you = _fix?.Code == code;
				if (_riskAreas.Count > 0)
				{
					var area = _riskAreas.FirstOrDefault(a => a.Code == code);
					return area is null ? OutlookRiskCard.Absent(code) : OutlookRiskCard.WithFigures(area, you);
				}
				return _parsed.Headlines.Any(h => h.Code == code) ? OutlookRiskCard.NoFigures(code, you) : OutlookRiskCard.Absent(code);
			}).ToArray();
			(HighCard, ModerateCard, EnhancedCard, SlightCard, MarginalCard) = (cards[0], cards[1], cards[2], cards[3], cards[4]);
		}

		// ── "ARE YOU IN THE RISK AREA" — one line under the cards (C + D, the user's call 2026-10-02) ──
		// ⚠️ The position comes from the LOCATION SERVICE (Windows), asked ONCE at app open (MapViewModel) — never the
		// map's location marker (the user's call). Every outlook change re-tests that position against the drawn
		// issuance's categorical areas (RiskAreaLocator over the cached GeoJSON). PastCast shows it too.

		private enum Position { Unasked, Locating, Off, Known }
		private Position _position = Position.Unasked;
		private double _lat, _lon;
		private string? _place;
		private string? _areasFile;
		private string? _distanceUnit;
		private RiskAreaFix? _fix;

		public void SetLocating() { _position = Position.Locating; Refresh(); }
		public void SetLocationOff() { _position = Position.Off; Refresh(); }

		public void SetPosition(double latitude, double longitude, string? place)
		{
			(_position, _lat, _lon, _place) = (Position.Known, latitude, longitude, place);
			Refresh();
		}

		private void Refresh()
		{
			Locate();
			BuildCards();
			OnPropertyChanged(string.Empty);
		}

		private void Locate()
		{
			_fix = null;
			if (_position != Position.Known || _areasFile is null) return;
			try { _fix = RiskAreaLocator.Locate(System.IO.File.ReadAllText(_areasFile), _lat, _lon); }
			catch (System.IO.IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		private bool NoSevereToday => _parsed.Headlines.Count > 0 && _parsed.Headlines.All(h => h.IsNone);

		/// <summary>The line shows wherever the cards do.</summary>
		public bool HasYouLine => HasRiskCards;

		// ⚠️ ONE wording for live AND PastCast (the user's call, 2026-10-02): "for this outlook" is the reminder that a
		// replayed outlook isn't today's. Inside, the category is a PILL in the card's own band colours (option C):
		// "You are in the [High risk area · 5 of 5] for this outlook." — band shade behind, SPC colour words, white number.

		private bool IsInside => _position == Position.Known && !NoSevereToday && _fix is { IsInside: true };

		/// <summary>The sentence, or (inside) the part BEFORE the pill: "You are in the ".</summary>
		public string YouText => _position switch
		{
			Position.Off => "Location is off for Anvil, so it can't check.",
			Position.Known when NoSevereToday => "No risk areas in this outlook, so nothing to check.",
			Position.Known when _fix is { IsInside: true } => "You are in the",
			Position.Known when _fix is not null => "You are outside the risk areas for this outlook.",
			Position.Known => "Can't check: this outlook's areas couldn't be read.",
			_ => "Finding your location…",
		};

		public bool HasYouPill => IsInside;
		/// <summary>The pill's words, in SPC's colour: "High risk area ·" (the XAML puts the space before the number).</summary>
		public string YouPillWords => IsInside ? $"{OutlookRiskCard.Short(_fix!.Code!)} risk area ·" : string.Empty;
		/// <summary>The pill's number, in white: "5 of 5".</summary>
		public string YouPillNumber => IsInside ? OutlookRiskCard.NumeralFor(_fix!.Code!).Replace("/", " of ") : string.Empty;
		/// <summary>SPC's colour (the pill's words) and its deep shade (the pill) — the card band's pair. DATA colours.</summary>
		public string YouPillInk => IsInside ? Card(_fix!.Code!).Fill : "#FF8A8A8A";
		public string YouPillFill => IsInside ? Card(_fix!.Code!).BandFill : "#FF3A3A3A";
		/// <summary>After the pill: "for this outlook.".</summary>
		public string YouTrail => IsInside ? "for this outlook." : string.Empty;

		private OutlookRiskCard Card(string code) => code switch
		{
			"HIGH" => HighCard, "MDT" => ModerateCard, "ENH" => EnhancedCard, "SLGT" => SlightCard, _ => MarginalCard,
		};

		/// <summary>The dimmed tail: the distance to the nearest edge when outside, then the place.</summary>
		public string YouDetail
		{
			get
			{
				if (_position != Position.Known || NoSevereToday || _fix is null) return string.Empty;
				var parts = new List<string>();
				if (_fix.NearestEdgeMeters is { } m)
				{
					parts.Add($"the nearest edge is {Anvil.Models.DistanceUnits.Format(m, _distanceUnit)} {_fix.Bearing} of you");
				}
				if (_place is not null) parts.Add(_place);
				return parts.Count == 0 ? string.Empty : "· " + string.Join(" · ", parts);
			}
		}

		public bool IsLocationOff => _position == Position.Off;

		public string Alert => _parsed.Alert ?? string.Empty;
		public bool HasAlert => _parsed.Alert is not null;

		/// <summary>The summary's paragraphs as one text, a blank line between.</summary>
		public string Summary => string.Join("\n\n", _parsed.Summary);
		public bool HasSummary => _parsed.Summary.Count > 0;

		public IReadOnlyList<OutlookSectionRow> Rows { get; private set; } = Array.Empty<OutlookSectionRow>();

		public IReadOnlyList<OutlookSectionRow> PreviousRows { get; private set; } = Array.Empty<OutlookSectionRow>();
		public bool HasPrevious => PreviousRows.Count > 0;

		/// <summary>"issued 1630Z · 11:30 AM CDT · 3 sections" — the box's right-hand caption.
		/// ⚠️ The PREVIOUS row is ALWAYS shown (the user's call, 2026-10-02): SPC repeats the earlier discussion only
		/// under an UPDATE (Day 1's 20Z; some older updates), so a full issuance says so instead of hiding the row.</summary>
		public string PreviousCaption
		{
			get
			{
				if (!HasPrevious) return "None in this issuance";
				var n = PreviousRows.Count;
				var when = _parsed.PreviousIssuedTime is { } p ? $"issued {p} · " : string.Empty;
				return $"{when}{n} section{(n == 1 ? string.Empty : "s")}";
			}
		}

		/// <summary>The previous discussion's box: COLLAPSED by default (the user's call, 2026-10-02).</summary>
		public bool IsPreviousExpanded
		{
			get => _isPreviousExpanded;
			set => SetProperty(ref _isPreviousExpanded, value);
		}

		// ── SOURCE: the SPC page this text came from (the band's last row; option B, the user's call 2026-10-02) ──

		/// <summary>The page's full address — the link target and its tooltip.</summary>
		public string SourceUrl => _sourceUrl ?? string.Empty;
		public bool HasSource => IsDiscussion && !string.IsNullOrEmpty(_sourceUrl);

		/// <summary>"· spc.noaa.gov · archive copy" / "· spc.noaa.gov · latest issuance" — dimmed after the link.</summary>
		public string SourceNote
		{
			get
			{
				if (!HasSource) return string.Empty;
				var host = Uri.TryCreate(_sourceUrl, UriKind.Absolute, out var u) ? u.Host.Replace("www.", string.Empty) : "spc.noaa.gov";
				return $"· {host} · {(_isArchive ? "archive copy" : "latest issuance")}";
			}
		}

		public RadarGlossaryCard SourceHint => OutlookGlossary.Source;

		/// <summary>SPC's original text. ⚠️ Kept but NOT shown (the user's call, 2026-10-02: "keep but hide").</summary>
		public string Raw => _text;

		// ── The hints ──

		public RadarGlossaryCard IssuedHint => OutlookGlossary.Issued;
		public RadarGlossaryCard ValidHint => OutlookGlossary.Valid;
		public RadarGlossaryCard ForecasterHint => OutlookGlossary.Forecaster;
		public RadarGlossaryCard NextOutlookHint => OutlookGlossary.NextOutlook;
		public RadarGlossaryCard RiskHint => OutlookGlossary.Risk;
		public RadarGlossaryCard AlertHint => OutlookGlossary.Alert;
		public RadarGlossaryCard SummaryHint => OutlookGlossary.Summary;
		public RadarGlossaryCard PreviousHint => OutlookGlossary.Previous;
	}

	/// <summary>A risk headline as the window draws it: a bar in the category's SPC colour, the category, the place.</summary>
	public sealed class OutlookHeadlineRow
	{
		// ⚠️ DATA colour (the SPC categorical catalog), never themed. "No severe areas" has no category: a neutral grey.
		private const string NoneFill = "#FF8A8A8A";

		public OutlookHeadlineRow(OutlookHeadline headline)
		{
			Category = headline.Category;
			Text = headline.Text;
			Fill = headline.IsNone
				? NoneFill
				: SpcRiskCatalog.Level(SpcOutlookType.Categorical, headline.Code)?.Fill ?? NoneFill;
		}

		public string Category { get; }
		public string Text { get; }
		public bool HasText => Text.Length > 0;
		public string Fill { get; }

		/// <summary>"HIGH RISK FROM CENTRAL AND NORTHERN OKLAHOMA…" — the one line under the cards.</summary>
		public string Line => HasText ? $"{Category} {Text}" : Category;
	}

	/// <summary>
	/// One risk card: the category's SPC colour strip, its name, and — when SPC's table has the row — the people in
	/// it ("1.9M people"), its area and its largest places, one per line. Absent = dimmed, "Not in this outlook".
	/// </summary>
	public sealed class OutlookRiskCard
	{
		private OutlookRiskCard(string code, bool isPresent, OutlookRiskArea? area, bool isYou = false)
		{
			var level = SpcRiskCatalog.Level(SpcOutlookType.Categorical, code);
			Name = code switch { "MDT" => "MODERATE", "ENH" => "ENHANCED", "SLGT" => "SLIGHT", "MRGL" => "MARGINAL", _ => code };
			Numeral = NumeralFor(code);
			Fill = level?.Fill ?? "#FF8A8A8A"; // ⚠️ DATA colour, never themed
			BandFill = Deep(Fill);
			IsPresent = isPresent;
			IsYou = isYou && isPresent;
			HasFigures = area is not null;
			People = area is null ? string.Empty : Compact(area.Population);
			Area = area is null ? string.Empty : $"{area.AreaSqMi.ToString("N0", CultureInfo.InvariantCulture)} sq mi";
			Places = area?.Places ?? Array.Empty<string>();
		}

		public static OutlookRiskCard WithFigures(OutlookRiskArea area, bool isYou = false) => new(area.Code, true, area, isYou);
		public static OutlookRiskCard NoFigures(string code, bool isYou = false) => new(code, true, null, isYou);
		public static OutlookRiskCard Absent(string code) => new(code, false, null);

		/// <summary>SPC's own 1-5 number for a category: Marginal 1/5 … High 5/5.</summary>
		public static string NumeralFor(string code) => code switch
		{
			"MRGL" => "1/5", "SLGT" => "2/5", "ENH" => "3/5", "MDT" => "4/5", "HIGH" => "5/5", _ => string.Empty,
		};

		/// <summary>"High", "Moderate"… — the category in the "are you in the risk area" pill.</summary>
		public static string Short(string code) => code switch
		{
			"MRGL" => "Marginal", "SLGT" => "Slight", "ENH" => "Enhanced", "MDT" => "Moderate", "HIGH" => "High", _ => code,
		};

		public string Name { get; }
		public string Numeral { get; }
		/// <summary>Your location is in this category (the card gets a ring).</summary>
		public bool IsYou { get; }
		/// <summary>SPC's colour: the card's NAME (and the you-line pill's words) are written in it.</summary>
		public string Fill { get; }
		/// <summary>The title band behind the name: a deep shade of <see cref="Fill"/> (option X, the user's call 2026-10-02).</summary>
		public string BandFill { get; }
		public bool IsPresent { get; }
		public bool IsAbsent => !IsPresent;

		// "#66A366" / "#FF66A366" → the same hue at 38% brightness, so SPC's colour reads ON it (≈3.5-5:1 for all five).
		internal static string Deep(string hex)
		{
			var h = hex.TrimStart('#');
			if (h.Length == 8) h = h[2..];
			if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return "#FF3A3A3A";
			static int Dim(int c) => (int)Math.Round(c * 0.38);
			return $"#FF{Dim(rgb >> 16 & 0xFF):X2}{Dim(rgb >> 8 & 0xFF):X2}{Dim(rgb & 0xFF):X2}";
		}
		public bool HasFigures { get; }
		/// <summary>In the outlook, but this page had no table (older archive pages).</summary>
		public bool IsPresentWithoutFigures => IsPresent && !HasFigures;
		public string People { get; }
		public string Area { get; }
		public IReadOnlyList<string> Places { get; }

		// 1,896,303 → "1.9M"; 31,343,171 → "31.3M"; 280,571 → "281K"; under a thousand as is.
		internal static string Compact(long n) => n switch
		{
			>= 1_000_000 => $"{(n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)}M",
			>= 1_000 => $"{(n / 1_000.0).ToString("0", CultureInfo.InvariantCulture)}K",
			_ => n.ToString(CultureInfo.InvariantCulture),
		};
	}

	/// <summary>A body section as the window draws it: dimmed label + hint, an optional heading, the paragraphs.</summary>
	public sealed class OutlookSectionRow
	{
		public OutlookSectionRow(OutlookSection section)
		{
			Label = OutlookGlossary.LabelFor(section);
			Hint = OutlookGlossary.For(section.Kind);
			// The heading is the section's own title — shown only when it says more than the label does
			// ("Lower Mississippi Valley" under REGION; not "Synopsis" under SYNOPSIS, not "20Z update").
			Heading = section.Kind == OutlookSectionKind.Update && Label != "CHANGES"
				|| string.Equals(section.Title, Label, StringComparison.OrdinalIgnoreCase)
					? string.Empty
					: section.Title;
			Paragraphs = section.Paragraphs;
		}

		public string Label { get; }
		public RadarGlossaryCard Hint { get; }
		public string Heading { get; }
		public bool HasHeading => Heading.Length > 0;
		public IReadOnlyList<string> Paragraphs { get; }
	}
}
