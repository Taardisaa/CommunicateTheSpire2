using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunicateTheSpire2.Config;

public sealed class CommunicateTheSpire2Config
{
	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = false;

	/// <summary>
	/// Command line to start the controller process. Example: "python C:\path\controller.py"
	/// </summary>
	[JsonPropertyName("command")]
	public string Command { get; set; } = "";

	[JsonPropertyName("working_directory")]
	public string? WorkingDirectory { get; set; } = null;

	[JsonPropertyName("handshake_timeout_seconds")]
	public int HandshakeTimeoutSeconds { get; set; } = 10;

	[JsonPropertyName("verbose_protocol_logs")]
	public bool VerboseProtocolLogs { get; set; } = false;

	/// <summary>
	/// IPC startup mode. "spawn" starts a child process; "connect" is reserved for future endpoint attach.
	/// </summary>
	[JsonPropertyName("mode")]
	public string Mode { get; set; } = "spawn";

	public static string ConfigPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"SlayTheSpire2",
		"CommunicateTheSpire2.config.json");

	public static CommunicateTheSpire2Config LoadOrCreateDefault()
	{
		try
		{
			string? dir = Path.GetDirectoryName(ConfigPath);
			if (!string.IsNullOrWhiteSpace(dir))
			{
				Directory.CreateDirectory(dir);
			}
		}
		catch
		{
			// ignore; we'll attempt to proceed (may fall back to defaults)
		}

		if (!File.Exists(ConfigPath))
		{
			var cfg = new CommunicateTheSpire2Config();
			cfg.Normalize();
			TryWrite(cfg);
			return cfg;
		}

		try
		{
			string json = File.ReadAllText(ConfigPath);
			var cfg = JsonSerializer.Deserialize<CommunicateTheSpire2Config>(json, JsonOptions);
			cfg ??= new CommunicateTheSpire2Config();
			cfg.Normalize();
			return cfg;
		}
		catch
		{
			return new CommunicateTheSpire2Config();
		}
	}

	public static void TryWrite(CommunicateTheSpire2Config cfg)
	{
		try
		{
			string json = JsonSerializer.Serialize(cfg, JsonOptions);
			File.WriteAllText(ConfigPath, json);
		}
		catch
		{
			// ignore
		}
	}

	public bool IsSpawnMode => string.Equals(Mode, "spawn", StringComparison.OrdinalIgnoreCase);

	private void Normalize()
	{
		Command = (Command ?? "").Trim();
		WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim();
		HandshakeTimeoutSeconds = Math.Max(1, HandshakeTimeoutSeconds);

		string mode = (Mode ?? "").Trim().ToLowerInvariant();
		if (mode != "spawn" && mode != "connect")
		{
			mode = "spawn";
		}
		Mode = mode;
	}

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};
}

