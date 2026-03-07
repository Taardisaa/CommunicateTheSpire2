using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.TestSupport;

namespace CommunicateTheSpire2.Patches;

/// <summary>
/// When our IpcCardSelector is active, return null from ShowScreen so CardReward uses
/// GetSelectedCardReward instead of the UI. This allows the controller to choose card rewards.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.ShowScreen))]
public static class PatchNCardRewardSelectionScreenShowScreen
{
	[HarmonyPrefix]
	public static bool Prefix(
		ref NCardRewardSelectionScreen? __result,
		IReadOnlyList<CardCreationResult> options,
		IReadOnlyList<CardRewardAlternative> extraOptions)
	{
		var selector = CardSelectCmd.Selector;
		if (selector != null && selector.GetType().FullName == "CommunicateTheSpire2.Choice.IpcCardSelector")
		{
			__result = null;
			return false; // skip original
		}
		return true;
	}
}
