using System;
using System.IO;
using System.Text.Json;

namespace CommunicateTheSpire2.Config;

public sealed class CommunicateTheSpire2Config
{
	public bool Enabled { get; set; } = false;

	/// <summary>
	/// Command line to start the controller process. Example: "python C:\path\controller.py"
	/// </summary>
	public string ControllerCommand { get; set; } = "";

	public string? ControllerWorkingDirectory { get; set; } = null;

	public int HandshakeTimeoutSeconds { get; set; } = 10;

	public bool VerboseProtocolLogs { get; set; } = false;

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
			TryWrite(cfg);
			return cfg;
		}

		try
		{
			string json = File.ReadAllText(ConfigPath);
			var cfg = JsonSerializer.Deserialize<CommunicateTheSpire2Config>(json, JsonOptions);
			return cfg ?? new CommunicateTheSpire2Config();
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

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};
}

