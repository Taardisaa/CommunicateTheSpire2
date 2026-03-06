using System;
using System.Collections.Generic;

namespace CommunicateTheSpire2.Protocol;

public static class ProtocolConstants
{
	public const int ProtocolVersion = 1;
}

public sealed class HelloMessage
{
	public string type { get; set; } = "hello";
	public int protocol_version { get; set; } = ProtocolConstants.ProtocolVersion;
	public string mod_version { get; set; } = "0.1.0";
	public string transport { get; set; } = "stdio";
	public List<string> capabilities { get; set; } = new List<string>();
}

public sealed class ErrorMessage
{
	public string type { get; set; } = "error";
	public string error { get; set; } = "Unknown error";
	public string? details { get; set; } = null;
}

public sealed class CommandMessage
{
	public string type { get; set; } = "command";
	public string command { get; set; } = "";
	public string? request_id { get; set; } = null;
}

public sealed class PongMessage
{
	public string type { get; set; } = "pong";
	public long timestamp_unix_ms { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class StateMessage
{
	public string type { get; set; } = "state";
	public long timestamp_unix_ms { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	public bool in_run { get; set; }
	public bool in_combat { get; set; }

	public RunSummary? run { get; set; }
	public CombatSummary? combat { get; set; }
}

public sealed class RunSummary
{
	public int act_index { get; set; }
	public int act_floor { get; set; }
	public int total_floor { get; set; }
	public int ascension { get; set; }
	public int gold { get; set; }
	public string? room_type { get; set; }
}

public sealed class CombatSummary
{
	public int round_number { get; set; }
	public string? current_side { get; set; }

	public PlayerSummary? local_player { get; set; }
	public List<EnemySummary> enemies { get; set; } = new List<EnemySummary>();
}

public sealed class PlayerSummary
{
	public ulong net_id { get; set; }
	public int current_hp { get; set; }
	public int max_hp { get; set; }
	public int block { get; set; }
	public int energy { get; set; }
	public int stars { get; set; }
}

public sealed class EnemySummary
{
	public uint? combat_id { get; set; }
	public string? name { get; set; }
	public int current_hp { get; set; }
	public int max_hp { get; set; }
	public int block { get; set; }
}

