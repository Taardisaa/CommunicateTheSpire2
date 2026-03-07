using System.Collections.Generic;

namespace CommunicateTheSpire2.Choice;

public sealed class ChoiceRequestMessage
{
	public string type { get; set; } = "choice_request";
	public string choice_id { get; set; } = "";
	public string choice_type { get; set; } = ""; // "card_select" | "card_reward"
	public int min_select { get; set; }
	public int max_select { get; set; }
	public List<ChoiceOptionSummary> options { get; set; } = new List<ChoiceOptionSummary>();
	public List<string> alternatives { get; set; } = new List<string>(); // e.g. "Skip", "Reroll"
}

public sealed class ChoiceOptionSummary
{
	public int index { get; set; }
	public string? id { get; set; }  // Card ID or similar
	public string? name { get; set; }
}
