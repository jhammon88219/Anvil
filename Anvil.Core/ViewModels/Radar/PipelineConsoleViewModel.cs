// PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit).
// A read-only "glass cockpit" over the Level-2 loop: one mini-scrubber per radar product (all seven at
// once, regardless of what the main viewer is showing) plus the VWP/storm-motion state, so you can watch
// the on-demand build pipeline fill in real time ("Duo fills, Trio completes"; docs/radar-loop-flow.md).
//
// It is a PASSIVE observer: it polls the WebView's read-only RadarLayer.pipelineSnapshot() ONLY while the
// console card is open (IsOpen), touches no decode/render hot path, and drives no map state. Removal =
// delete this file + the PIPELINE CONSOLE-tagged blocks elsewhere (grep "PIPELINE CONSOLE").
//
// Threading mirrors RadarValidationViewModel: IsOpen is set on the UI thread, so the poll loop starts and
// resumes on the UI thread (no ConfigureAwait(false)) — required, since it mutates WinUI-bound props and
// calls the WebView (ExecuteScriptAsync is UI-thread only).
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>Per-frame, per-product build state of one product cell in the console's mini-scrubber.</summary>
	public enum PipelineCellState
	{
		/// <summary>Not built yet, and not queued/in-flight.</summary>
		Unbuilt,
		/// <summary>This frame is queued for an upgrade decode (the whole frame, so this product will follow).</summary>
		Queued,
		/// <summary>This frame's upgrade decode is running now.</summary>
		InFlight,
		/// <summary>Built, but the product has no data on this frame (a genuine null moment).</summary>
		NoData,
		/// <summary>Built with data — geometry is ready to render.</summary>
		Built,
	}

	/// <summary>One cell in a product row's mini-scrubber (one loop frame).</summary>
	public sealed class PipelineCell : ObservableObject
	{
		private PipelineCellState _state;
		public PipelineCellState State
		{
			get => _state;
			set => SetProperty(ref _state, value);
		}
	}

	/// <summary>One product row: its option (label + ramp swatch) and a cell per loop frame.</summary>
	public sealed class PipelineProductRow : ObservableObject
	{
		public PipelineProductRow(RadarProductOption option)
		{
			Option = option;
		}

		/// <summary>The product option — the UI binds ShortLabel + Option.Ramp (drawn as the row swatch).</summary>
		public RadarProductOption Option { get; }
		public string ShortLabel => Option.ShortLabel;
		public string Label => Option.Label;

		/// <summary>One cell per loop frame, reconciled in place from the snapshot.</summary>
		public ObservableCollection<PipelineCell> Cells { get; } = new();
	}

	/// <summary>
	/// The Pipeline Console view-model — see the file header. Constructed once (ships in Release), driven by
	/// polling <see cref="IMapService.GetPipelineSnapshotAsync"/> while <see cref="IsOpen"/>.
	/// </summary>
	public sealed class PipelineConsoleViewModel : ObservableObject
	{
		private const int PollIntervalMs = 400;
		private const double KnotsPerMs = 0.514444;

		private static readonly JsonSerializerOptions SnapJson = new() { PropertyNameCaseInsensitive = true };

		private readonly IMapService _map;
		private readonly RadarViewModel _radar;
		private readonly Dictionary<string, int> _colByProduct = new(StringComparer.OrdinalIgnoreCase);

		public PipelineConsoleViewModel(IMapService map, RadarViewModel radar)
		{
			_map = map;
			_radar = radar;

			// One fixed row per product, in registry order (matches RadarProductOptions / the JS registry).
			foreach (var option in radar.RadarProductOptions)
			{
				Rows.Add(new PipelineProductRow(option));
			}

			_radar.PropertyChanged += OnRadarPropertyChanged;
			_currentIndex = _radar.CurrentFrameIndex;
		}

		/// <summary>The seven product rows (each a mini-scrubber). Fixed set; only their cells change.</summary>
		public ObservableCollection<PipelineProductRow> Rows { get; } = new();

		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			// Mirror the main viewer's frame so the shared playhead tracks it (observe-only, no seek).
			if (e.PropertyName == nameof(RadarViewModel.CurrentFrameIndex))
			{
				CurrentIndex = _radar.CurrentFrameIndex;
			}
		}

		private double _currentIndex;
		/// <summary>The frame the main viewer is on — drives the console's shared playhead.</summary>
		public double CurrentIndex
		{
			get => _currentIndex;
			private set => SetProperty(ref _currentIndex, value);
		}

		private int _frameCount;
		/// <summary>Number of loop frames (cells per row). Drives playhead positioning.</summary>
		public int FrameCount
		{
			get => _frameCount;
			private set => SetProperty(ref _frameCount, value);
		}

		private bool _hasLoop;
		/// <summary>Whether a loop is loaded (a snapshot came back). The card shows an empty state otherwise.</summary>
		public bool HasLoop
		{
			get => _hasLoop;
			private set => SetProperty(ref _hasLoop, value);
		}

		private string _vwpStatusText = "No loop loaded.";
		/// <summary>VWP lifecycle line: computing / resolved / insufficient / idle.</summary>
		public string VwpStatusText
		{
			get => _vwpStatusText;
			private set => SetProperty(ref _vwpStatusText, value);
		}

		private string _stormMotionText = string.Empty;
		/// <summary>Resolved storm motion (bearing @ kt + source/cuts/top), or empty when not resolved.</summary>
		public string StormMotionText
		{
			get => _stormMotionText;
			private set => SetProperty(ref _stormMotionText, value);
		}

		private string _pipelineFlagsText = string.Empty;
		/// <summary>What the pipeline is trying to build right now: active product + prefetch flags + wanted set.</summary>
		public string PipelineFlagsText
		{
			get => _pipelineFlagsText;
			private set => SetProperty(ref _pipelineFlagsText, value);
		}

		private bool _isOpen;
		/// <summary>Drives the poll loop — polling runs ONLY while the console card is open (zero cost closed).</summary>
		public bool IsOpen
		{
			get => _isOpen;
			set
			{
				if (SetProperty(ref _isOpen, value))
				{
					if (value) StartPolling();
					else StopPolling();
				}
			}
		}

		private CancellationTokenSource? _cts;

		private void StartPolling()
		{
			if (_cts is not null) return;
			_cts = new CancellationTokenSource();
			_ = PollLoopAsync(_cts.Token);
		}

		private void StopPolling()
		{
			_cts?.Cancel();
			_cts?.Dispose();
			_cts = null;
		}

		private async Task PollLoopAsync(CancellationToken ct)
		{
			try
			{
				while (!ct.IsCancellationRequested)
				{
					try
					{
						Apply(ParseSnapshot(await _map.GetPipelineSnapshotAsync()));
					}
					catch
					{
						// One bad cycle (a transient JS/parse hiccup) must not kill the loop.
					}
					await Task.Delay(PollIntervalMs, ct);
				}
			}
			catch (OperationCanceledException)
			{
				// Card closed — stop quietly.
			}
		}

		// Reconcile the console UI to a snapshot IN PLACE (grow/shrink cells, mutate State) so WinUI diffs
		// cheaply instead of rebuilding seven ItemsControls every 400 ms.
		private void Apply(Snap? snap)
		{
			if (snap is null || snap.N <= 0)
			{
				HasLoop = false;
				FrameCount = 0;
				foreach (var row in Rows) row.Cells.Clear();
				VwpStatusText = "No loop loaded.";
				StormMotionText = string.Empty;
				PipelineFlagsText = string.Empty;
				return;
			}

			HasLoop = true;
			FrameCount = snap.N;

			// Map each product id to its column in the snapshot's per-frame state array `s`.
			_colByProduct.Clear();
			for (var i = 0; i < snap.Ids.Count; i++) _colByProduct[snap.Ids[i]] = i;

			foreach (var row in Rows)
			{
				var col = _colByProduct.TryGetValue(row.Option.Id, out var c) ? c : -1;
				ReconcileCount(row.Cells, snap.N);
				for (var i = 0; i < snap.N; i++)
				{
					var fr = i < snap.Frames.Count ? snap.Frames[i] : null;
					var code = (fr is not null && col >= 0 && col < fr.S.Count) ? fr.S[col] : 0;
					row.Cells[i].State = code switch
					{
						2 => PipelineCellState.Built,
						1 => PipelineCellState.NoData,
						_ when fr is not null && fr.F => PipelineCellState.InFlight,
						_ when fr is not null && fr.Q => PipelineCellState.Queued,
						_ => PipelineCellState.Unbuilt,
					};
				}
			}

			ApplyVwp(snap);
			PipelineFlagsText =
				$"active: {snap.Active} · velPrefetch {(snap.VelPrefetch ? "on" : "off")} · " +
				$"fullPrefetch {(snap.FullPrefetch ? "on" : "off")} · " +
				$"wanted: {(snap.Wanted.Count == 0 ? "—" : string.Join("+", snap.Wanted))}";
		}

		private void ApplyVwp(Snap snap)
		{
			var v = snap.Vwp;
			if (v is null)
			{
				VwpStatusText = "Storm motion: idle.";
				StormMotionText = string.Empty;
				return;
			}

			if (v.HasMotion)
			{
				var kt = v.SpeedMs / KnotsPerMs;
				VwpStatusText = "Storm motion: resolved.";
				StormMotionText = string.Format(
					CultureInfo.InvariantCulture,
					"{0:F0}° @ {1:F0} kt · {2} · {3} cuts · top {4:F1} km",
					v.DirDeg, kt, string.IsNullOrEmpty(v.Source) ? "auto" : v.Source, v.Cuts, v.TopM / 1000.0);
			}
			else if (v.Insufficient)
			{
				VwpStatusText = string.Format(
					CultureInfo.InvariantCulture,
					"Storm motion: insufficient (top {0:F1} km) — SRV shows base velocity.", v.TopM / 1000.0);
				StormMotionText = string.Empty;
			}
			else if (v.InFlight)
			{
				VwpStatusText = "Storm motion: computing… (SRV gated)";
				StormMotionText = string.Empty;
			}
			else
			{
				VwpStatusText = "Storm motion: idle.";
				StormMotionText = string.Empty;
			}
		}

		private static void ReconcileCount(ObservableCollection<PipelineCell> cells, int n)
		{
			while (cells.Count < n) cells.Add(new PipelineCell());
			while (cells.Count > n) cells.RemoveAt(cells.Count - 1);
		}

		private static Snap? ParseSnapshot(string? json)
		{
			if (string.IsNullOrWhiteSpace(json) || json.Trim() is "null" or "\"null\"") return null;
			try
			{
				return JsonSerializer.Deserialize<Snap>(json, SnapJson);
			}
			catch
			{
				return null;
			}
		}

		// Shape of RadarLayer.pipelineSnapshot() (radar.js). Single-letter keys match case-insensitively.
		private sealed class Snap
		{
			public int N { get; set; }
			public int Cf { get; set; }
			public string Active { get; set; } = string.Empty;
			public List<string> Ids { get; set; } = new();
			public bool VelPrefetch { get; set; }
			public bool FullPrefetch { get; set; }
			public List<string> Wanted { get; set; } = new();
			public VwpSnap? Vwp { get; set; }
			public List<FrameSnap> Frames { get; set; } = new();
		}

		private sealed class VwpSnap
		{
			public bool InFlight { get; set; }
			public bool HasMotion { get; set; }
			public bool Insufficient { get; set; }
			public double SpeedMs { get; set; }
			public double DirDeg { get; set; }
			public string Source { get; set; } = string.Empty;
			public int Cuts { get; set; }
			public double TopM { get; set; }
		}

		private sealed class FrameSnap
		{
			public List<int> S { get; set; } = new();
			public bool Q { get; set; }
			public bool F { get; set; }
			public string R { get; set; } = string.Empty;
		}
	}
}
