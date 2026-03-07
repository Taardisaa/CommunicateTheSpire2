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

	/// <summary>Current screen: "combat" | "event" | "rest_site" | "map" | "shop" | "treasure" | "unknown" | null (main menu).</summary>
	public string? screen { get; set; }

	public RunSummary? run { get; set; }
	public CombatSummary? combat { get; set; }

	public List<EventOptionSummary> event_options { get; set; } = new List<EventOptionSummary>();
	public List<RestSiteOptionSummary> rest_site_options { get; set; } = new List<RestSiteOptionSummary>();
	public MapSummary? map { get; set; }

	/// <summary>Local player's potion slots (when in run). Index matches POTION use/discard slot.</summary>
	public List<PotionSummary> potions { get; set; } = new List<PotionSummary>();

	/// <summary>Commands currently valid for this state. E.g. ["STATE","PING","PLAY","END"].</summary>
	public List<string> available_commands { get; set; } = new List<string>();
}

public sealed class PotionSummary
{
	public int index { get; set; }
	public string? id { get; set; }
	public string? target_type { get; set; }
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
	public List<HandCardSummary> hand_cards { get; set; } = new List<HandCardSummary>();
}

public sealed class HandCardSummary
{
	public int index { get; set; }
	public string? id { get; set; }
	public int energy_cost { get; set; }
	public string? target_type { get; set; }
	public bool playable { get; set; }
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

public sealed class EventOptionSummary
{
	public int index { get; set; }
	public string? text_key { get; set; }
	public string? title { get; set; }
	public bool is_locked { get; set; }
	public bool is_proceed { get; set; }
}

public sealed class RestSiteOptionSummary
{
	public int index { get; set; }
	public string? option_id { get; set; }
	public string? title { get; set; }
	public bool is_enabled { get; set; }
}

public sealed class MapCoordSummary
{
	public int col { get; set; }
	public int row { get; set; }
	public string? point_type { get; set; }
}

public sealed class MapSummary
{
	public MapCoordSummary? current_coord { get; set; }
	public List<MapCoordSummary> reachable { get; set; } = new List<MapCoordSummary>();
}

