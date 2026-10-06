using System.Collections.Generic;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="MapService"/>'s PREVIEW MIRROR (the Settings window's map preview): which commands reach a
	/// mirror, and what a late-opened preview is replayed.
	/// </summary>
	/// <remarks>
	/// ⚠️ The replay is in SEND order because applyTheme carries a style and applyStyle replaces it — a fixed
	/// order would put a preview on the wrong basemap whenever the two were sent the other way round.
	/// </remarks>
	public class MapPreviewMirrorTests
	{
		private sealed class RecordingView : IMapView
		{
			public List<string> Scripts { get; } = new();

			public Task<string> RunScriptAsync(string javaScript)
			{
				Scripts.Add(javaScript);
				return Task.FromResult(string.Empty);
			}
		}

		private static readonly AppTheme Dark = new("dark", "Dark", ThemeBase.Dark, "dataVizBlack", "#000000");
		private static readonly MapStyle StyleA = new("a", "A", "a.json", "https://mapassets/a.json");
		private static readonly MapStyle StyleB = new("b", "B", "b.json", "https://mapassets/b.json");

		private static (MapService Map, RecordingView Main) Create()
		{
			var map = new MapService();
			var main = new RecordingView();
			map.Attach(main);
			return (map, main);
		}

		[Fact]
		public async Task Replay_KeepsTheLatestOfEachCommand_InSendOrder()
		{
			var (map, _) = Create();
			await map.ApplyThemeAsync(Dark, StyleA);
			await map.SetDistanceUnitsAsync("mi");
			await map.ApplyStyleAsync(StyleB);           // after the theme → the preview must end on B
			await map.SetDistanceUnitsAsync("nm");       // replaces "mi", and moves to the end

			var preview = new RecordingView();
			await map.ReplayToMirrorAsync(preview);

			Assert.Equal(new[]
			{
				"window.applyTheme('dark','https://mapassets/a.json');",
				"window.applyStyle('https://mapassets/b.json');",
				"window.setDistanceUnits('nm');",
			}, preview.Scripts);
		}

		[Fact]
		public async Task OnlyLookCommands_ReachAMirror()
		{
			var (map, main) = Create();
			var preview = new RecordingView();
			map.AddMirror(preview);

			await map.SetRangeRingsAsync(true, true, false, true, 0, true);
			await map.ClearRadarAsync();           // radar is not a look — the preview never sees it

			Assert.Equal(2, main.Scripts.Count);
			Assert.Single(preview.Scripts);
			Assert.StartsWith("window.setRangeRings(", preview.Scripts[0]);
		}

		[Fact]
		public async Task MirrorOnlyStyle_SkipsTheMainMap_ButIsReplayed()
		{
			var (map, main) = Create();
			await map.SetRangeRingStyleAsync("{\"bearing\":0}");
			await map.MirrorRangeRingStyleAsync("{\"bearing\":90}"); // the main map's handle was dragged

			var preview = new RecordingView();
			await map.ReplayToMirrorAsync(preview);

			Assert.Single(main.Scripts);
			Assert.Equal(new[] { "window.setRangeRingStyle('{\"bearing\":90}');" }, preview.Scripts);
		}

		[Fact]
		public async Task RemovedMirror_HearsNothingMore()
		{
			var (map, _) = Create();
			var preview = new RecordingView();
			map.AddMirror(preview);
			map.RemoveMirror(preview);

			await map.SetScopeColorAsync("#112233");

			Assert.Empty(preview.Scripts);
		}
	}
}
