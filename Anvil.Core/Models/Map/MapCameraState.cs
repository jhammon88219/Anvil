using System.Text.Json;

namespace Anvil.Models
{
	/// <summary>
	/// Where the map looks: centre, zoom (the web-map convention, each step doubles the scale), bearing (degrees clockwise
	/// from north) and pitch (degrees of tilt). NATIVE-MAP (bolt-on): what is handed across when the map engine switches.
	/// </summary>
	public sealed record MapCameraState(double Longitude, double Latitude, double Zoom, double Bearing, double Pitch)
	{
		/// <summary>
		/// Reads the page's <c>window.getCamera()</c> answer, a JSON array <c>[lng, lat, zoom, bearing, pitch]</c>. Null for
		/// anything else (an empty string before the page is up, <c>null</c> before its first map exists).
		/// </summary>
		public static MapCameraState? Parse(string? json)
		{
			if (string.IsNullOrWhiteSpace(json)) return null;
			try
			{
				using var doc = JsonDocument.Parse(json);
				var a = doc.RootElement;
				if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 5) return null;
				return new MapCameraState(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble(), a[4].GetDouble());
			}
			catch (JsonException)
			{
				return null;
			}
			catch (System.InvalidOperationException)
			{
				return null; // an element that is not a number
			}
		}
	}
}
