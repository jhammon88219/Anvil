using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Anvil
{
	/// <summary>
	/// LAUNCH TIMING (2026-10-19): where a launch's seconds go, counted from PROCESS START (so the CLR and XAML
	/// startup before App's ctor are in it too). Each step <see cref="Mark"/>s itself; map-ready logs ONE line —
	/// "Launch: app 0.4 s · launched 0.9 s · main window 2.1 s · browser 2.6 s · navigate 2.7 s · page 5.0 s ·
	/// map-ready 5.4 s" — and the home radar's first frame + each window drawn in the first minute log their own
	/// "Launch: … at N s" lines. Instrumentation only; nothing reads it back.
	/// </summary>
	internal static class LaunchTiming
	{
		private static readonly Stopwatch Clock = Stopwatch.StartNew();
		private static readonly TimeSpan BeforeClock = SafeProcessAge();
		private static readonly List<(string Name, double Sec)> Marks = new();

		/// <summary>Lines after this long are ordinary use, not launch.</summary>
		private static readonly TimeSpan LaunchWindow = TimeSpan.FromSeconds(60);

		/// <summary>Seconds since the process started.</summary>
		public static double Seconds => (BeforeClock + Clock.Elapsed).TotalSeconds;

		/// <summary>True for the first minute of the run.</summary>
		public static bool IsLaunching => BeforeClock + Clock.Elapsed < LaunchWindow;

		/// <summary>Record that a launch step finished now.</summary>
		public static void Mark(string name)
		{
			lock (Marks) { Marks.Add((name, Seconds)); }
		}

		/// <summary>The one launch line: every mark so far, in order.</summary>
		public static void LogMarks(ILogger logger)
		{
			string line;
			lock (Marks) { line = string.Join(" · ", Marks.Select(m => $"{m.Name} {m.Sec:0.00} s")); }
			logger.LogInformation("Launch: {Marks} (debugger {Debugger})", line,
				Debugger.IsAttached ? "attached" : "not attached");
		}

		/// <summary>A single launch moment ("home radar first frame", "window temporal.Now drawn") — first minute only.</summary>
		public static void LogMoment(ILogger logger, string what)
		{
			if (!IsLaunching) return;
			logger.LogInformation("Launch: {What} at {Sec:0.00} s", what, Seconds);
		}

		private static TimeSpan SafeProcessAge()
		{
			try
			{
				using var self = Process.GetCurrentProcess();
				var age = DateTime.Now - self.StartTime;
				return age > TimeSpan.Zero ? age : TimeSpan.Zero;
			}
			catch
			{
				return TimeSpan.Zero; // marks then count from first use of this class instead
			}
		}
	}
}
