using System;
using System.IO;

namespace CommunicateTheSpire2;

/// <summary>
/// Writes demo mod messages to a .log file in the game's user data area
/// (e.g. %APPDATA%\SlayTheSpire2\CommunicateTheSpire2.log on Windows).
/// </summary>
public static class DemoModLog
{
	private static readonly object Lock = new object();
	private static string? _logPath;

	public static string LogPath
	{
		get
		{
			if (_logPath != null)
				return _logPath;
			string dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"SlayTheSpire2");
			try
			{
				Directory.CreateDirectory(dir);
			}
			catch
			{
				// fallback to temp
				dir = Path.GetTempPath();
			}
			_logPath = Path.Combine(dir, "CommunicateTheSpire2.log");
			return _logPath;
		}
	}

	public static void Write(string message)
	{
		string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
		lock (Lock)
		{
			try
			{
				File.AppendAllText(LogPath, line);
			}
			catch
			{
				TryFallback(line);
			}
		}
	}

	private static void TryFallback(string line)
	{
		try
		{
			string fallback = Path.Combine(Path.GetTempPath(), "DemoMod.log");
			File.AppendAllText(fallback, line);
			File.AppendAllText(fallback, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] (Primary path failed; using fallback: {fallback}){Environment.NewLine}");
		}
		catch
		{
			// ignore
		}
	}
}

