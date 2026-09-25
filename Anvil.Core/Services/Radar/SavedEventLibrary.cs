using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="ISavedEventLibrary"/>: the curated built-in events from the embedded
	/// <c>Assets/saved-events.json</c>, merged with the user's own from <c>SavedEvents.json</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ <b>BUILT-INS ARE CURATED, NOT GUESSED.</b> Each carries a <c>source</c> naming where its times came
	/// from, and <c>tools/check_saved_events.py</c> confirms the archive really holds volumes for every leg's
	/// site and window before one ships — so a built-in cannot point at an empty loop.
	/// <para>⚠️ <b>An invalid built-in is SKIPPED, not fatal</b>, and recorded in <see cref="Problems"/>;
	/// <c>SavedEventLibraryTests</c> asserts the shipped file has none, which is where a bad edit should be
	/// caught. A user file that fails to parse is moved aside (<c>SavedEvents.corrupt-*.json</c>) rather than
	/// silently overwritten by the next save.</para>
	/// <para>⚠️ Built-in vs user is decided by WHICH FILE an event came from, never by a flag inside it — a
	/// hand-edited user file cannot promote its own events to undeletable.</para>
	/// </remarks>
	public sealed class SavedEventLibrary : ISavedEventLibrary
	{
		private const string ResourceName = "Anvil.Assets.saved-events.json";
		private const string UserFileName = "SavedEvents.json";

		/// <summary>The earliest start a leg may have — the archive's first day (UTC midnight), same
		/// bound as the Timeframe calendar.</summary>
		private static readonly DateTimeOffset ArchiveStart =
			new(Level2RadarService.ArchiveFirstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

		private readonly string _userDirectory;
		private readonly List<SavedEvent> _builtIn;
		private readonly List<SavedEvent> _user;
		private readonly List<string> _problems = new();

		/// <param name="userDirectory">TESTS ONLY — where <c>SavedEvents.json</c> lives. DI uses the default
		/// <c>%LocalAppData%\Anvil</c>, same as the settings service's override.</param>
		public SavedEventLibrary(string? userDirectory = null)
			: this(userDirectory, ReadEmbeddedBuiltIns())
		{
		}

		/// <summary>TESTS ONLY — substitute the built-in JSON.</summary>
		internal SavedEventLibrary(string? userDirectory, string builtInJson)
		{
			_userDirectory = userDirectory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil");
			_builtIn = Parse(builtInJson, builtIn: true, _problems);
			_user = LoadUserEvents();
		}

		/// <summary>Why any built-in or user event was skipped on load. Empty when everything parsed.</summary>
		public IReadOnlyList<string> Problems => _problems;

		/// <summary>The user file's full path.</summary>
		public string UserFilePath => Path.Combine(_userDirectory, UserFileName);

		/// <remarks>⚠️ The ORDER is the list's grouping (the view model starts a header wherever the group
		/// changes): by <see cref="SavedEventKind"/> (Other last), newest first within each. The user's own sit
		/// IN their kind's group (the Atlas tags them Custom) — agreed in the Anvil Atlas redesign.</remarks>
		public IReadOnlyList<SavedEvent> GetEvents() =>
			_builtIn.Concat(_user)
				.OrderBy(e => e.Kind == SavedEventKind.Other ? int.MaxValue : (int)e.Kind)
				.ThenByDescending(e => e.StartUtc)
				.ToList();

		public SavedEvent Add(string name, IReadOnlyList<SavedEventLeg> legs, string notes, SavedEventKind kind = SavedEventKind.Other)
		{
			var ev = new SavedEvent(
				"user-" + Guid.NewGuid().ToString("N"),
				(name ?? string.Empty).Trim(),
				legs.ToList(),
				0,
				(notes ?? string.Empty).Trim(),
				string.Empty,
				IsBuiltIn: false,
				kind);

			if (Validate(ev, DateTimeOffset.UtcNow) is { } problem)
			{
				throw new ArgumentException(problem);
			}

			_user.Add(ev);
			SaveUserEvents();
			return ev;
		}

		public SavedEvent? SetLegKey(string id, int legIndex, SavedEventKey? key)
		{
			// Same membership rule as Remove: only an event from the USER file can be edited.
			var index = _user.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
			if (index < 0 || legIndex < 0 || legIndex >= _user[index].Legs.Count)
			{
				return null;
			}

			var ev = _user[index];
			var legs = ev.Legs.ToList();
			legs[legIndex] = legs[legIndex] with { Key = key };
			var updated = ev with { Legs = legs };
			if (Validate(updated, DateTimeOffset.UtcNow) is { } problem)
			{
				throw new ArgumentException(problem);
			}

			_user[index] = updated;
			SaveUserEvents();
			return updated;
		}

		public SavedEvent? SetKind(string id, SavedEventKind kind)
		{
			var index = _user.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
			if (index < 0)
			{
				return null;
			}
			if (_user[index].Kind != kind)
			{
				_user[index] = _user[index] with { Kind = kind };
				SaveUserEvents();
			}
			return _user[index];
		}

		public bool Remove(string id)
		{
			// ⚠️ The built-in check is by LIST MEMBERSHIP. A built-in id can never be in _user (user ids are
			// minted "user-<guid>" and loaded from the user file only), so this can't delete one.
			var index = _user.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
			if (index < 0)
			{
				return false;
			}

			_user.RemoveAt(index);
			SaveUserEvents();
			return true;
		}

		// ── Validation ──────────────────────────────────────────────────────────────────────────────

		/// <summary>
		/// Why an event can't be used, in words for the user — or null when it's fine.
		/// </summary>
		/// <remarks>
		/// ⚠️ The five-minute rule is load-bearing: the Timeframe TimePicker steps in 5 minutes, and a start
		/// it cannot represent risks the picker writing a rounded value back — which would clear the event's
		/// highlight the moment it was picked.
		/// </remarks>
		public static string? Validate(SavedEvent ev, DateTimeOffset nowUtc)
		{
			if (string.IsNullOrWhiteSpace(ev.Name))
			{
				return "Enter a name for the event.";
			}
			if (ev.Name.Length > 80)
			{
				return "Keep the name under 80 characters.";
			}
			if (ev.Legs.Count == 0)
			{
				return "An event needs at least one radar leg.";
			}
			if (ev.DefaultLegIndex < 0 || ev.DefaultLegIndex >= ev.Legs.Count)
			{
				return "The default leg doesn't exist.";
			}

			foreach (var leg in ev.Legs)
			{
				if (!SavedEventLeg.AllowedDurationMinutes.Contains(leg.DurationMinutes))
				{
					return $"A window of {leg.DurationMinutes} minutes isn't one the Timeframe picker offers.";
				}
				if (leg.StartUtc < ArchiveStart || leg.StartUtc > nowUtc)
				{
					return "A leg starts outside the radar archive.";
				}
				if (leg.StartUtc.Second != 0 || leg.StartUtc.Millisecond != 0 || leg.StartUtc.Minute % 5 != 0)
				{
					return "Leg start times must fall on a 5-minute mark.";
				}
				if (leg.SiteId is { } site && (site.Length != 4 || !site.All(char.IsLetterOrDigit)))
				{
					return $"'{site}' isn't a radar site id.";
				}
				if (leg.Key is { } key && ValidateKey(leg, key) is { } keyProblem)
				{
					return keyProblem;
				}
			}

			return null;
		}

		/// <summary>Why a leg's key time can't be used, in words for the user — or null when it's fine.</summary>
		/// <remarks>⚠️ OVERLAP, not containment — see <see cref="SavedEventKey"/>.</remarks>
		public static string? ValidateKey(SavedEventLeg leg, SavedEventKey key)
		{
			if (key.EndUtc is { } end && end <= key.StartUtc)
			{
				return "The end time must come after the start.";
			}
			var last = key.EndUtc ?? key.StartUtc;
			if (key.StartUtc > leg.EndUtc || last < leg.StartUtc)
			{
				return "The time must fall within the replay window.";
			}
			return null;
		}

		// ── JSON ───────────────────────────────────────────────────────────────────────────────────
		// Shape (both files):
		// { "events": [ { "id", "type" (tornado|hurricane|derecho, optional), "name", "notes", "source", "defaultLeg",
		//                 "legs": [ { "site": "KTLX"|null, "startUtc": "2013-05-31T22:30:00Z", "minutes": 120,
		//                             "key": { "startUtc", "endUtc" (optional), "place" (optional) } (optional) } ] } ] }

		internal static List<SavedEvent> Parse(string json, bool builtIn, List<string> problems)
		{
			var events = new List<SavedEvent>();
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("events", out var array) || array.ValueKind != JsonValueKind.Array)
			{
				return events;
			}

			var now = DateTimeOffset.UtcNow;
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var element in array.EnumerateArray())
			{
				SavedEvent ev;
				try
				{
					ev = ReadEvent(element, builtIn);
				}
				catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException)
				{
					problems.Add($"{(builtIn ? "Built-in" : "User")} event skipped: {ex.Message}");
					continue;
				}

				if (Validate(ev, now) is { } problem)
				{
					problems.Add($"'{ev.Name}' skipped: {problem}");
					continue;
				}
				if (!seen.Add(ev.Id))
				{
					problems.Add($"'{ev.Name}' skipped: duplicate id '{ev.Id}'.");
					continue;
				}

				events.Add(ev);
			}

			return events;
		}

		private static SavedEvent ReadEvent(JsonElement e, bool builtIn)
		{
			var legs = e.GetProperty("legs").EnumerateArray().Select(l => new SavedEventLeg(
				l.TryGetProperty("site", out var s) && s.ValueKind == JsonValueKind.String
					? s.GetString()!.Trim().ToUpperInvariant()
					: null,
				ParseUtc(l.GetProperty("startUtc").GetString()),
				l.GetProperty("minutes").GetInt32(),
				ReadKey(l))).ToList();

			return new SavedEvent(
				e.GetProperty("id").GetString() ?? throw new FormatException("an event has no id"),
				OptionalString(e, "name"),
				legs,
				e.TryGetProperty("defaultLeg", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0,
				OptionalString(e, "notes"),
				OptionalString(e, "source"),
				builtIn,
				ReadKind(OptionalString(e, "type")));
		}

		private static SavedEventKey? ReadKey(JsonElement leg)
		{
			if (!leg.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.Object)
			{
				return null;
			}
			return new SavedEventKey(
				ParseUtc(k.GetProperty("startUtc").GetString()),
				k.TryGetProperty("endUtc", out var e) && e.ValueKind == JsonValueKind.String ? ParseUtc(e.GetString()) : null,
				OptionalString(k, "place").Trim());
		}

		private static DateTimeOffset ParseUtc(string? text) =>
			DateTimeOffset.Parse(text ?? throw new FormatException("a time is missing"), CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal).ToUniversalTime();

		private static string FormatUtc(DateTimeOffset t) =>
			t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

		// The JSON "type" a kind writes back as — the inverse of ReadKind. Other writes nothing.
		private static string? KindToken(SavedEventKind kind) => kind switch
		{
			SavedEventKind.Tornado => "tornado",
			SavedEventKind.Hurricane => "hurricane",
			SavedEventKind.Derecho => "derecho",
			_ => null,
		};

		private static SavedEventKind ReadKind(string type) => type.Trim().ToLowerInvariant() switch
		{
			"" => SavedEventKind.Other,
			"tornado" => SavedEventKind.Tornado,
			"hurricane" => SavedEventKind.Hurricane,
			"derecho" => SavedEventKind.Derecho,
			_ => throw new FormatException($"unknown event type '{type}'"),
		};

		private static string OptionalString(JsonElement e, string name) =>
			e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

		private static string ReadEmbeddedBuiltIns()
		{
			// An embedded resource can't go missing at runtime, so a failure is a broken BUILD — say so.
			using var stream = typeof(SavedEventLibrary).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
				?? throw new InvalidOperationException(
					$"Embedded saved-event list '{ResourceName}' is missing. It is declared as an EmbeddedResource in Anvil.Core.csproj.");
			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}

		private List<SavedEvent> LoadUserEvents()
		{
			var path = UserFilePath;
			if (!File.Exists(path))
			{
				return new List<SavedEvent>();
			}

			try
			{
				return Parse(File.ReadAllText(path), builtIn: false, _problems);
			}
			catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
			{
				// ⚠️ Move it ASIDE, don't drop it: the next save would otherwise overwrite the user's events
				// with an empty list, and a corrupt file is still recoverable by hand.
				_problems.Add($"Your saved events file couldn't be read ({ex.Message}); it was set aside.");
				try
				{
					File.Move(path, Path.Combine(_userDirectory,
						$"SavedEvents.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json"));
				}
				catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
				{
					// Leave it where it is; the save below still writes via a temp file.
				}
				return new List<SavedEvent>();
			}
		}

		private void SaveUserEvents()
		{
			var root = new JsonObject
			{
				["events"] = new JsonArray(_user.Select(e => (JsonNode)new JsonObject
				{
					["id"] = e.Id,
					["type"] = KindToken(e.Kind),
					["name"] = e.Name,
					["notes"] = e.Notes,
					["defaultLeg"] = e.DefaultLegIndex,
					["legs"] = new JsonArray(e.Legs.Select(l => (JsonNode)new JsonObject
					{
						["site"] = l.SiteId,
						["startUtc"] = FormatUtc(l.StartUtc),
						["minutes"] = l.DurationMinutes,
						["key"] = l.Key is { } key
							? new JsonObject
							{
								["startUtc"] = FormatUtc(key.StartUtc),
								["endUtc"] = key.EndUtc is { } end ? FormatUtc(end) : null,
								["place"] = key.Place,
							}
							: null,
					}).ToArray()),
				}).ToArray()),
			};

			Directory.CreateDirectory(_userDirectory);
			var path = UserFilePath;
			// Unique temp name + move, same reasoning as CachingHttpService.AtomicWriteAsync: a crash mid-write
			// must leave the previous file intact, never a truncated one.
			var temp = $"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
			File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			File.Move(temp, path, overwrite: true);
		}
	}
}
