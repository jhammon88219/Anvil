using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>
	/// The LIVE-POLL TIMING LOG: one JSON line per new live frame / volume change (<see cref="LivePollTimingRecord"/>) in
	/// <c>%LocalAppData%\Anvil\Usage\live-poll-timing.jsonl</c>, kept for good (not swept like RadarDiagnostics' per-run
	/// files) — a permanent fixture to check regime-aware polling against reality from time to time
	/// (<c>py -3 tools/live_poll_report.py</c>). Written by <see cref="Anvil.ViewModels.LivePollTimingRecorder"/>. On in
	/// EVERY build (unlike the Debug-only load-time log): a line per frame is a few hundred lines a day.
	/// </summary>
	public sealed class LivePollTimingLog
	{
		private static readonly JsonSerializerOptions Json = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		};

		private readonly ILogger<LivePollTimingLog> _logger;
		private readonly object _gate = new();

		/// <param name="directory">TESTS ONLY — where the file lives. Null (the DI default) = <c>%LocalAppData%\Anvil\Usage</c>.</param>
		public LivePollTimingLog(ILogger<LivePollTimingLog> logger, string? directory = null)
		{
			_logger = logger;
			var dir = directory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "Usage");
			Directory.CreateDirectory(dir);
			FilePath = Path.Combine(dir, "live-poll-timing.jsonl");
		}

		public string FilePath { get; }

		/// <summary>Append one record. A write failure is logged, never thrown — losing a sample mustn't break a poll.</summary>
		public void Append(LivePollTimingRecord record)
		{
			try
			{
				var line = JsonSerializer.Serialize(record, Json) + Environment.NewLine;
				lock (_gate)
				{
					File.AppendAllText(FilePath, line);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				_logger.LogWarning(ex, "Couldn't append to the live-poll timing log {Path}", FilePath);
			}
		}
	}
}
