using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Voltage.Editor.Gateway;

/// <summary>A request failure reported to the caller as a normal error response.</summary>
public sealed class GatewayException : Exception
{
	public GatewayException(string message) : base(message)
	{
	}
}

/// <summary>Typed access to a request's params object.</summary>
public readonly struct GatewayArgs
{
	private readonly JsonElement _element;

	public GatewayArgs(JsonElement element)
	{
		_element = element;
	}

	/// <summary>True for a present, non-null value.</summary>
	public bool Has(string name) => TryGet(name, out _);

	/// <summary>True when the key was sent at all, even as JSON null.</summary>
	public bool HasProperty(string name) => _element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(name, out _);

	/// <summary>The raw element for a key that must be present; null is allowed and returned as a Null element.</summary>
	public JsonElement RequireProperty(string name)
	{
		if (_element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(name, out var value))
			return value;
		throw new GatewayException($"missing parameter '{name}'");
	}

	public bool TryGet(string name, out JsonElement value)
	{
		value = default;
		return _element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
	}

	public string String(string name, string fallback = null)
	{
		if (!TryGet(name, out var v))
			return fallback;
		return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
	}

	public string Require(string name)
	{
		var value = String(name);
		if (string.IsNullOrWhiteSpace(value))
			throw new GatewayException($"missing parameter '{name}'");
		return value;
	}

	public bool Bool(string name, bool fallback = false)
	{
		if (!TryGet(name, out var v))
			return fallback;
		return v.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
			JsonValueKind.Number => v.GetDouble() != 0,
			_ => fallback
		};
	}

	public int Int(string name, int fallback = 0) => (int)(Double(name) ?? fallback);

	public float Float(string name, float fallback = 0f) => (float)(Double(name) ?? fallback);

	public float? OptFloat(string name)
	{
		var d = Double(name);
		return d.HasValue ? (float)d.Value : null;
	}

	public double? Double(string name)
	{
		if (!TryGet(name, out var v))
			return null;
		if (v.ValueKind == JsonValueKind.Number)
			return v.GetDouble();
		if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
			return d;
		return null;
	}

	/// <summary>Reads a string array, also accepting a single scalar as a one-item list.</summary>
	public List<string> Strings(string name)
	{
		var result = new List<string>();
		if (!TryGet(name, out var v))
			return result;

		if (v.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in v.EnumerateArray())
				result.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText());
		}
		else
		{
			result.Add(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText());
		}

		return result;
	}
}
