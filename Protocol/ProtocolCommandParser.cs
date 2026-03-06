using System;
using System.Text.Json;

namespace CommunicateTheSpire2.Protocol;

public static class ProtocolCommandParser
{
	public static bool TryParse(string line, out string command, out string? requestId, out ErrorMessage? error)
	{
		command = "";
		requestId = null;
		error = null;

		if (line == null)
		{
			error = new ErrorMessage { error = "InvalidCommand", details = "Line was null." };
			return false;
		}

		string trimmed = line.Trim();
		if (trimmed.Length == 0)
		{
			error = new ErrorMessage { error = "InvalidCommand", details = "Line was empty." };
			return false;
		}

		// Accept either a plain-text command line, or JSON: {"type":"command","command":"STATE","request_id":"..."}
		if (trimmed[0] == '{')
		{
			try
			{
				using JsonDocument doc = JsonDocument.Parse(trimmed);
				JsonElement root = doc.RootElement;

				string? type = null;
				if (root.TryGetProperty("type", out JsonElement typeEl) && typeEl.ValueKind == JsonValueKind.String)
					type = typeEl.GetString();

				if (!string.Equals(type, "command", StringComparison.OrdinalIgnoreCase))
				{
					error = new ErrorMessage { error = "InvalidCommand", details = "JSON must have type='command'." };
					return false;
				}

				if (!root.TryGetProperty("command", out JsonElement cmdEl) || cmdEl.ValueKind != JsonValueKind.String)
				{
					error = new ErrorMessage { error = "InvalidCommand", details = "JSON must include string field 'command'." };
					return false;
				}

				command = (cmdEl.GetString() ?? "").Trim();
				if (command.Length == 0)
				{
					error = new ErrorMessage { error = "InvalidCommand", details = "'command' was empty." };
					return false;
				}

				if (root.TryGetProperty("request_id", out JsonElement ridEl) && ridEl.ValueKind == JsonValueKind.String)
					requestId = ridEl.GetString();

				return true;
			}
			catch (Exception ex)
			{
				error = new ErrorMessage { error = "InvalidJson", details = ex.Message };
				return false;
			}
		}

		// Plain-text: take first token as the command.
		int space = trimmed.IndexOf(' ');
		command = (space < 0 ? trimmed : trimmed.Substring(0, space)).Trim();
		if (command.Length == 0)
		{
			error = new ErrorMessage { error = "InvalidCommand", details = "No command token found." };
			return false;
		}

		return true;
	}
}

