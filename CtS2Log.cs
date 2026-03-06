using System;
using System.IO;

namespace CommunicateTheSpire2;

/// <summary>
/// Writes demo mod messages to a .log file in the game's user data area
/// (e.g. %APPDATA%\SlayTheSpire2\CommunicateTheSpire2.log on Windows).
/// </summary>
public static class CommunicateTheSpireLog
{
	private static readonly object Lock = new object();
	private static string? _logPath;
	private static string? _controllerErrorLogPath;

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

	public static string ControllerErrorLogPath
	{
		get
		{
			if (_controllerErrorLogPath != null)
				return _controllerErrorLogPath;
			string dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"SlayTheSpire2");
			try
			{
				Directory.CreateDirectory(dir);
			}
			catch
			{
				dir = Path.GetTempPath();
			}
			_controllerErrorLogPath = Path.Combine(dir, "communicate_the_spire2_controller_errors.log");
			return _controllerErrorLogPath;
		}
	}

	public static void Write(string message)
	{
		string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
		WriteToPath(LogPath, line, "CommunicateTheSpire2.log");
	}

	public static void WriteControllerError(string message)
	{
		string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
		WriteToPath(ControllerErrorLogPath, line, "communicate_the_spire2_controller_errors.log");
	}

	private static void WriteToPath(string targetPath, string line, string fallbackFileName)
	{
		lock (Lock)
		{
			try
			{
				File.AppendAllText(targetPath, line);
			}
			catch
			{
				TryFallback(line, fallbackFileName);
			}
		}
	}

	private static void TryFallback(string line, string fallbackFileName)
	{
		try
		{
			string fallback = Path.Combine(Path.GetTempPath(), fallbackFileName);
			File.AppendAllText(fallback, line);
			File.AppendAllText(fallback, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] (Primary path failed; using fallback: {fallback}){Environment.NewLine}");
		}
		catch
		{
			// ignore
		}
	}
}

