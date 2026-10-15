using System;
using System.IO;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>Level2RadarService.TrimDiagnosticsFolder: keep diagnostics by AGE under a size cap, oldest out first,
	/// never the files this run is writing. (It used to keep the newest 15 runs — about one development day.)</summary>
	public sealed class DiagnosticsTrimTests : IDisposable
	{
		private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
		private readonly string _dir = Path.Combine(Path.GetTempPath(), "anvil-diagtrim-" + Guid.NewGuid().ToString("N"));

		public DiagnosticsTrimTests() => Directory.CreateDirectory(_dir);

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
		}

		private string File(string name, int bytes, int daysOld)
		{
			var p = Path.Combine(_dir, name);
			System.IO.File.WriteAllBytes(p, new byte[bytes]);
			System.IO.File.SetLastWriteTimeUtc(p, Now.AddDays(-daysOld));
			return p;
		}

		private string Suspect(string stamp, int bytes, int daysOld)
		{
			var d = Path.Combine(_dir, "_suspect", stamp);
			Directory.CreateDirectory(d);
			var p = Path.Combine(d, "KTLX_x.V06");
			System.IO.File.WriteAllBytes(p, new byte[bytes]);
			System.IO.File.SetLastWriteTimeUtc(p, Now.AddDays(-daysOld));
			Directory.SetLastWriteTimeUtc(d, Now.AddDays(-daysOld));
			return d;
		}

		[Fact]
		public void Many_recent_runs_are_all_kept()
		{
			// A busy development day: far more than the old 15-run limit, all young and small.
			for (int i = 0; i < 40; i++) File($"radar-diag-20261014-{i:D6}.jsonl", 100, 0);
			var (removed, _) = Level2RadarService.TrimDiagnosticsFolder(_dir, "20261015-080000", null,
				TimeSpan.FromDays(180), 1_000_000, Now);
			Assert.Equal(0, removed);
			Assert.Equal(40, Directory.GetFiles(_dir).Length);
		}

		[Fact]
		public void Files_and_suspect_folders_past_the_age_are_removed()
		{
			var oldLog = File("radar-diag-20260301-100000.jsonl", 100, 200);
			var oldReport = File("radar-report-20260301-100000.md", 100, 200);
			var oldSuspect = Suspect("20260301-100000", 100, 200);
			var young = File("radar-diag-20261001-100000.jsonl", 100, 14);

			var (removed, _) = Level2RadarService.TrimDiagnosticsFolder(_dir, "20261015-080000", null,
				TimeSpan.FromDays(180), 1_000_000, Now);

			Assert.Equal(3, removed);
			Assert.False(System.IO.File.Exists(oldLog));
			Assert.False(System.IO.File.Exists(oldReport));
			Assert.False(Directory.Exists(oldSuspect));
			Assert.True(System.IO.File.Exists(young));
		}

		[Fact]
		public void Over_the_size_cap_the_oldest_go_first()
		{
			var a = File("radar-diag-20261001-100000.jsonl", 400, 10);
			var b = Suspect("20261005-100000", 400, 8);
			var c = File("radar-diag-20261010-100000.jsonl", 400, 3);

			var (removed, bytes) = Level2RadarService.TrimDiagnosticsFolder(_dir, "20261015-080000", null,
				TimeSpan.FromDays(180), 700, Now);

			Assert.Equal(2, removed);
			Assert.False(System.IO.File.Exists(a));
			Assert.False(Directory.Exists(b));
			Assert.True(System.IO.File.Exists(c));
			Assert.Equal(400, bytes);
		}

		[Fact]
		public void The_current_runs_files_are_never_removed()
		{
			const string stamp = "20260101-090000"; // a run left up for months: even its own files are "old"
			var live = File($"radar-diag-{stamp}-d20261015.jsonl", 500, 200);
			var report = File($"radar-report-{stamp}.md", 500, 200);
			var suspect = Suspect(stamp, 500, 200);
			var earlierDay = File($"radar-diag-{stamp}.jsonl", 500, 200);

			Level2RadarService.TrimDiagnosticsFolder(_dir, stamp, Path.GetFileName(live),
				TimeSpan.FromDays(180), 10, Now);

			Assert.True(System.IO.File.Exists(live));
			Assert.True(System.IO.File.Exists(report));
			Assert.True(Directory.Exists(suspect));
			Assert.False(System.IO.File.Exists(earlierDay)); // the same run's earlier days DO age out
		}
	}
}
