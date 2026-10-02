using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>
	/// The PastCast LOAD-TIME LOG: one JSON line per load (<see cref="LoopLoadRecord"/>) in
	/// <c>%LocalAppData%\Anvil\Usage\loop-load-times.jsonl</c>, kept for good (not swept like RadarDiagnostics' per-run
	/// files) so loads can be averaged into a "ready in about X" estimate. Written by
	/// <see cref="Anvil.ViewModels.LoopLoadRecorder"/>.
	/// </summary>
	public sealed class LoopLoadLog
	{
		internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

		private readonly ILogger<LoopLoadLog> _logger;
		private readonly object _gate = new();

		/// <param name="logger">Injected by the container (Serilog-backed).</param>
		/// <param name="directory">TESTS ONLY — where the file lives. Null (the DI default) uses
		/// <c>%LocalAppData%\Anvil\Usage</c> (beside site-usage.json).</param>
		public LoopLoadLog(ILogger<LoopLoadLog> logger, string? directory = null)
		{
			_logger = logger;
			var dir = directory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "Usage");
			Directory.CreateDirectory(dir);
			FilePath = Path.Combine(dir, "loop-load-times.jsonl");
		}

		public string FilePath { get; }

		/// <summary>Append one load. A write failure is logged, never thrown — losing a sample mustn't break a load.</summary>
		public void Append(LoopLoadRecord record)
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
				_logger.LogWarning(ex, "Couldn't append to the load-time log {Path}", FilePath);
			}
		}

		/// <summary>Every readable record, oldest first (a damaged line is skipped). For the future estimate.</summary>
		public IReadOnlyList<LoopLoadRecord> ReadAll()
		{
			var records = new List<LoopLoadRecord>();
			try
			{
				if (!File.Exists(FilePath)) return records;
				foreach (var line in File.ReadLines(FilePath))
				{
					if (string.IsNullOrWhiteSpace(line)) continue;
					try
					{
						if (JsonSerializer.Deserialize<LoopLoadRecord>(line, Json) is { } r) records.Add(r);
					}
					catch (JsonException) { }
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				_logger.LogWarning(ex, "Couldn't read the load-time log {Path}", FilePath);
			}
			return records;
		}
	}
}
